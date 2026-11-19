using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

/// <summary>
/// Role assignments of a company membership (Phase 7, D3/D10 R4). Authorization is evaluated per role scope in <see cref="IRoleAssignmentService"/>
/// (company role: Company.Users.AssignRoles + CompanyWide actor; branch role: that path or Branch.Users.AssignRoles at the branch). History is
/// close-only; each write returns the refreshed <see cref="UserRoleAssignmentsDto"/>. A user without a membership in this company is 404.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public class RoleAssignmentsController(
    SelloraDbContext db, IRoleAssignmentService assignments, IEffectivePermissionService effective, IMembershipContext membership) : ControllerBase
{
    [HttpGet("users/{id:guid}/roles")]
    public async Task<ActionResult<UserRoleAssignmentsDto>> Get(Guid id, CancellationToken ct)
    {
        var actor = await RoleActorReader.ReadAsync(db, effective, membership, User, HttpContext.TraceIdentifier, ct);
        var companyView = actor.Effective.HasCompanyLevel("Users.View") || actor.Effective.HasCompanyLevel(PermissionCatalog.CompanyUsersAssignRoles);
        if (!companyView && !actor.Effective.HasCompanyLevel(PermissionCatalog.BranchUsersAssignRoles)
            && !actor.Effective.BranchCodes.Values.Any(c => c.Contains(PermissionCatalog.BranchUsersAssignRoles)))
        {
            return NotFound();
        }

        // Phase 01b read rule: a branch-path viewer reaches only users with an active allocation at a branch where it may assign roles (and that it
        // reaches); users only in other branches, company-wide members and unallocated users are 404. The view itself stays filtered below (Phase 7).
        if (!companyView)
        {
            IReadOnlyCollection<Guid>? assignBranches = null;
            if (actor.Effective.PermittedBranches(PermissionCatalog.BranchUsersAssignRoles) is { } held)
            {
                var reachable = new List<Guid>();
                foreach (var b in held) if (await RoleRules.ReachesAsync(db, actor, b, ct)) reachable.Add(b);
                assignBranches = reachable;
            }
            else if (db.BranchContext.AllowedBranchIds is { } allowedBranches)
            {
                assignBranches = allowedBranches.ToList();
            }
            if (!await UserVisibility.IsAllocatedWithinAsync(db, id, assignBranches, ct)) return NotFound();
        }
        var view = await BuildViewAsync(id, ct);
        if (view is null) return NotFound();
        if (companyView) return Ok(view);

        // Branch administrators see only the branch roles of branches they may assign at.
        var allowed = new HashSet<Guid>();
        foreach (var branchId in view.Current.Concat(view.History).Where(r => r.BranchId != null).Select(r => r.BranchId!.Value).Distinct())
        {
            if (actor.Effective.HasAtBranch(PermissionCatalog.BranchUsersAssignRoles, branchId) && await RoleRules.ReachesAsync(db, actor, branchId, ct)) allowed.Add(branchId);
        }
        bool Visible(RoleAssignmentDto r) => r.ScopeType == RoleScope.Branch && r.BranchId is { } b && allowed.Contains(b);
        return Ok(view with { Current = view.Current.Where(Visible).ToList(), History = view.History.Where(Visible).ToList() });
    }

    [HttpPost("users/{id:guid}/roles")]
    public async Task<ActionResult<UserRoleAssignmentsDto>> Assign(Guid id, AssignRoleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await WriteAsync(id, actor => assignments.AssignAsync(actor, id, request.RoleId!.Value, request.Reason, ct), created: true, ct);
    }

    [HttpPost("users/{id:guid}/roles/{assignmentId:guid}/end")]
    public async Task<ActionResult<UserRoleAssignmentsDto>> End(Guid id, Guid assignmentId, EndRoleAssignmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await WriteAsync(id, actor => assignments.EndAsync(actor, id, assignmentId, request.Reason, ct), created: false, ct);
    }

    /// <summary>Members allocated to a branch, for branch administrators picking who to assign (Users.View, or Branch.Users.AssignRoles at the branch).</summary>
    [HttpGet("branches/{branchId:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<BranchMemberDto>>> Members(Guid branchId, CancellationToken ct)
    {
        var actor = await RoleActorReader.ReadAsync(db, effective, membership, User, HttpContext.TraceIdentifier, ct);
        var companyId = db.CompanyContext.RequireCompanyId();
        if (!await RoleRules.ReachesAsync(db, actor, branchId, ct)
            || !(actor.Effective.HasCompanyLevel("Users.View") || actor.Effective.HasAtBranch(PermissionCatalog.BranchUsersAssignRoles, branchId)))
        {
            return NotFound();
        }
        if (!await db.Branches.AnyAsync(b => b.Id == branchId && b.CompanyId == companyId, ct)) return NotFound();

        var rows = await db.UserBranchAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.BranchId == branchId && a.Status == AssignmentStatus.Active)
            .Select(a => new { a.UserCompanyMembershipId, a.IsPrimary }).ToListAsync(ct);
        var membershipIds = rows.Select(r => r.UserCompanyMembershipId).ToList();
        var members = await db.UserCompanyMemberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId && membershipIds.Contains(m.Id) && m.Status != MembershipStatus.Revoked)
            .Select(m => new { m.Id, m.UserId, m.Status }).ToListAsync(ct);
        var userIds = members.Select(m => m.UserId).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username }).ToDictionaryAsync(u => u.Id, ct);
        var primary = rows.ToDictionary(r => r.UserCompanyMembershipId, r => r.IsPrimary);

        var result = members.Where(m => users.ContainsKey(m.UserId))
            .Select(m => new BranchMemberDto(m.UserId, m.Id, users[m.UserId].FirstName, users[m.UserId].LastName, users[m.UserId].Username, m.Status, primary[m.Id]))
            .OrderBy(m => m.FirstName).ThenBy(m => m.LastName).ToList();
        return Ok(result);
    }

    private async Task<ActionResult<UserRoleAssignmentsDto>> WriteAsync(Guid userId, Func<RoleActor, Task> operation, bool created, CancellationToken ct)
    {
        await using (var scope = await AccessManagementScope.BeginAsync(db, User, ct))
        {
            try
            {
                await operation(scope.Actor.ToRoleActor(HttpContext.TraceIdentifier));
                await scope.CommitAsync(ct);
            }
            catch (AccessManagementForbiddenException ex) { return AccessProblems.Forbidden(ex); }
            catch (RoleRuleException ex) { return AccessProblems.Rule(ex); }
            catch (AccessTargetNotFoundException) { return NotFound(); }
        }

        var view = await BuildViewAsync(userId, ct);
        if (view is null) return NotFound();
        return created ? Created($"/api/v1/users/{userId}/roles", view) : Ok(view);
    }

    private async Task<UserRoleAssignmentsDto?> BuildViewAsync(Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var m = await db.UserCompanyMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId, ct);
        if (m is null) return null;

        var rows = await db.UserRoleAssignments.AsNoTracking().Where(a => a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id).ToListAsync(ct);
        var roleIds = rows.Select(r => r.RoleId).Distinct().ToList();
        var roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).Select(r => new { r.Id, r.Name, r.IsSystem }).ToDictionaryAsync(r => r.Id, ct);
        var branchIds = rows.Where(r => r.BranchId != null).Select(r => r.BranchId!.Value).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId && branchIds.Contains(b.Id)).Select(b => new { b.Id, b.Name }).ToDictionaryAsync(b => b.Id, ct);
        var userIdsToName = rows.SelectMany(r => new[] { r.CreatedByUserId, r.EndedByUserId }).Where(u => u != null).Select(u => u!.Value).Distinct().ToList();
        var names = (await db.Users.AsNoTracking().Where(u => userIdsToName.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim());

        RoleAssignmentDto ToDto(UserRoleAssignment r) => new(
            r.Id, r.RoleId, roles.TryGetValue(r.RoleId, out var role) ? role.Name : "", r.ScopeType, r.BranchId,
            r.BranchId is { } b && branches.TryGetValue(b, out var branch) ? branch.Name : null, role0(r.RoleId),
            r.Status, r.StartAt, r.EndAt, r.AssignmentReason, r.EndReason,
            r.CreatedByUserId is { } c && names.TryGetValue(c, out var cn) ? cn : null,
            r.EndedByUserId is { } e && names.TryGetValue(e, out var en) ? en : null);
        bool role0(Guid id) => roles.TryGetValue(id, out var x) && x.IsSystem;

        var current = rows.Where(r => r.Status == AssignmentStatus.Active).OrderBy(r => r.ScopeType).ThenBy(r => roles.TryGetValue(r.RoleId, out var x) ? x.Name : "").Select(ToDto).ToList();
        var history = rows.Where(r => r.Status == AssignmentStatus.Ended).OrderByDescending(r => r.EndAt).ThenByDescending(r => r.StartAt).Select(ToDto).ToList();
        return new UserRoleAssignmentsDto(m.Id, m.UserId, current, history);
    }
}
