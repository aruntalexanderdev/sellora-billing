using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Role assignment writes (Phase 7, D3/D9/D10 R4). History is close-only; every change bumps the membership AccessVersion and appends one audit event.
/// Rules: company role = Company.Users.AssignRoles + CompanyWide actor (Q1); branch role at B = that path or Branch.Users.AssignRoles at B; the role's
/// codes must be held by the actor at that scope; never the actor's own membership; the Administrator only by an Administrator; a branch role needs
/// the target allocated to the branch; the last Administrator is kept. The caller owns the transaction and advisory lock.
/// </summary>
public sealed class RoleAssignmentService(SelloraDbContext db, IUserAccessService userAccess) : IRoleAssignmentService
{
    private const int ReasonMax = 500;

    public async Task<UserRoleAssignment> AssignAsync(RoleActor actor, Guid userId, Guid roleId, string? reason, CancellationToken ct = default)
    {
        var note = Reason(reason, required: false);
        var role = await LoadRoleAsync(actor, roleId, "roleId", ct);
        var m = await LoadTargetAsync(actor, userId, ct);
        await AuthorizeAsync(actor, role, ct);
        RequireActiveRole(role);
        await RequireTargetNotAdministratorAsync(actor, m, ct);

        if (await db.UserRoleAssignments.AnyAsync(a =>
                a.CompanyId == m.CompanyId && a.UserCompanyMembershipId == m.Id && a.RoleId == role.Id && a.Status == AssignmentStatus.Active, ct))
        {
            throw new DomainValidationException("roleId", "The user already holds this role.");
        }
        await RequireBranchAllocationAsync(m, role, ct);

        var row = Stage(actor, m, role, note);
        if (role.IsSystem)
        {
            // Administrators are always company-wide (phase-5 D2): promotion forces it in the same transaction.
            await db.SaveChangesAsync(ct);
            await userAccess.EnsureCompanyWideForAdministratorAsync(RoleRules.ToAccess(actor), userId, ct);
        }
        RoleRules.Bump(m, actor.UserId);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task EndAsync(RoleActor actor, Guid userId, Guid assignmentId, string? reason, CancellationToken ct = default)
    {
        var note = Reason(reason, required: true);
        var m = await LoadTargetAsync(actor, userId, ct);
        var row = await db.UserRoleAssignments.SingleOrDefaultAsync(a =>
            a.Id == assignmentId && a.CompanyId == m.CompanyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active, ct)
            ?? throw new AccessTargetNotFoundException();
        var role = await LoadRoleAsync(actor, row.RoleId, "assignmentId", ct);
        await AuthorizeAsync(actor, role, ct);
        await RequireTargetNotAdministratorAsync(actor, m, ct);

        await EndRowAsync(actor, m, row, role, note, ct);
        RoleRules.Bump(m, actor.UserId);
        await db.SaveChangesAsync(ct);
        if (role.IsSystem) await RoleRules.EnsureAdministratorRemainsAsync(db, ct);
    }

    public async Task StageInitialCompanyRoleAsync(RoleActor actor, UserCompanyMembership membership, Guid roleId, CancellationToken ct = default)
    {
        var role = await LoadRoleAsync(actor, roleId, "roleId", ct);
        if (role.ScopeType != RoleScope.Company)
        {
            throw new DomainValidationException("roleId", "Select a company role.");
        }
        await AuthorizeAsync(actor, role, ct);
        RequireActiveRole(role);
        Stage(actor, membership, role, "Initial role");
    }

    public async Task StageInitialBranchRoleAsync(RoleActor actor, UserCompanyMembership membership, Guid roleId, Guid branchId, CancellationToken ct = default)
    {
        var role = await LoadRoleAsync(actor, roleId, "roleId", ct);
        if (role.ScopeType != RoleScope.Branch || role.BranchId != branchId)
        {
            throw new DomainValidationException("roleId", "Select a role of the active branch.");
        }
        // Same R4 checks as AssignAsync (Branch.Users.AssignRoles at the branch, codes held, FI-D45). The target's allocation at the branch is staged
        // in the same save by IUserAccessService.InitializeNewMembershipAsync, so RequireBranchAllocationAsync (which reads saved rows) is not used here.
        await AuthorizeAsync(actor, role, ct);
        RequireActiveRole(role);
        Stage(actor, membership, role, "Initial role");
    }

    public async Task SetSingleCompanyRoleAsync(RoleActor actor, Guid userId, Guid roleId, CancellationToken ct = default)
    {
        var role = await LoadRoleAsync(actor, roleId, "roleId", ct);
        if (role.ScopeType != RoleScope.Company)
        {
            throw new DomainValidationException("roleId", "Select a company role.");
        }
        var m = await LoadTargetAsync(actor, userId, ct);
        await RequireTargetNotAdministratorAsync(actor, m, ct);

        var companyId = m.CompanyId;
        var current = await db.UserRoleAssignments.Where(a =>
            a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active && a.ScopeType == RoleScope.Company).ToListAsync(ct);
        if (current.Count == 1 && current[0].RoleId == role.Id) return;

        await AuthorizeAsync(actor, role, ct);
        RequireActiveRole(role);

        var endedAdministrator = false;
        foreach (var row in current.Where(a => a.RoleId != role.Id))
        {
            var old = await db.Roles.SingleAsync(r => r.Id == row.RoleId, ct);
            await AuthorizeAsync(actor, old, ct);
            await EndRowAsync(actor, m, row, old, "Role replaced", ct);
            endedAdministrator |= old.IsSystem;
        }
        // Close first (partial unique index is per role, but keep one save order like the allocation history).
        await db.SaveChangesAsync(ct);

        if (current.All(a => a.RoleId != role.Id))
        {
            Stage(actor, m, role, "Role set through the user form");
        }
        RoleRules.Bump(m, actor.UserId);
        await db.SaveChangesAsync(ct);
        if (role.IsSystem) await userAccess.EnsureCompanyWideForAdministratorAsync(RoleRules.ToAccess(actor), userId, ct);
        if (endedAdministrator) await RoleRules.EnsureAdministratorRemainsAsync(db, ct);
    }

    // ---- helpers ----

    private async Task<Role> LoadRoleAsync(RoleActor actor, Guid roleId, string field, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null)
        {
            // Another company's role id is filtered out: indistinguishable from unknown.
            throw field == "assignmentId" ? new AccessTargetNotFoundException() : new DomainValidationException(field, "Select a valid role.");
        }
        if (role.ScopeType == RoleScope.Branch && (role.BranchId is not { } b || !await RoleRules.ReachesAsync(db, actor, b, ct)))
        {
            throw new AccessTargetNotFoundException();
        }
        return role;
    }

    private async Task<UserCompanyMembership> LoadTargetAsync(RoleActor actor, Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var m = await db.UserCompanyMemberships
            .SingleOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId && x.Status != MembershipStatus.Revoked, ct)
            ?? throw new AccessTargetNotFoundException();
        if (userId == actor.UserId)
        {
            throw RoleRules.Forbidden(RoleErrorCodes.SelfRoleChangeForbidden, "You cannot change your own roles.");
        }
        // Phase 6 R5: platform administrators and governance members are protected from ordinary user administration.
        await GovernanceRules.EnsureOperationalTargetAllowedAsync(db, actor.UserId, userId, ct);
        return m;
    }

    /// <summary>R4: the path right for the role's scope, the Administrator restriction (H1) and "codes held" at that scope.</summary>
    private async Task AuthorizeAsync(RoleActor actor, Role role, CancellationToken ct)
    {
        if (!await RoleRules.CanAssignAsync(db, actor, role, ct))
        {
            throw RoleRules.Forbidden(RoleErrorCodes.RoleAssignmentForbidden, role.ScopeType == RoleScope.Company
                ? "Only company-wide users holding Company.Users.AssignRoles can assign company roles."
                : "You are not allowed to assign roles of this branch.");
        }
        if (role.IsSystem && !actor.IsAdministrator)
        {
            throw RoleRules.Forbidden(RoleErrorCodes.AdministratorAssignmentForbidden, "Only Administrators can assign or end the Administrator role.");
        }
        var codes = await RoleRules.CodesOfAsync(db, role, ct);
        // FI-D45: roles holding branch administration codes are assigned and ended only by a company administrator.
        if (role.ScopeType == RoleScope.Branch)
        {
            RoleRules.RequireCompanyPathForBranchAdministration(actor, PermissionCatalog.CompanyUsersAssignRoles, codes);
        }
        if (RoleRules.NotHeld(actor, role.ScopeType, role.BranchId, codes) is { Count: > 0 } notHeld)
        {
            throw RoleRules.Forbidden(RoleErrorCodes.RoleEscalationForbidden,
                $"You can only assign a role whose permissions you hold yourself. Not held: {string.Join(", ", notHeld)}.");
        }
    }

    private async Task RequireTargetNotAdministratorAsync(RoleActor actor, UserCompanyMembership m, CancellationToken ct)
    {
        if (!actor.IsAdministrator && await RoleRules.HoldsAdministratorAsync(db, m.Id, ct))
        {
            throw RoleRules.Forbidden(RoleErrorCodes.AdministratorAssignmentForbidden, "Only Administrators can change Administrator accounts.");
        }
    }

    private static void RequireActiveRole(Role role)
    {
        if (!role.IsActive) throw new DomainValidationException("roleId", "This role is inactive. Activate it or choose another role.");
    }

    /// <summary>E2: a branch role needs an AssignedBranches target with an active allocation at the role's branch.</summary>
    private async Task RequireBranchAllocationAsync(UserCompanyMembership m, Role role, CancellationToken ct)
    {
        if (role.ScopeType != RoleScope.Branch) return;
        var allocated = m.AccessScope == CompanyAccessScope.AssignedBranches
            && await db.UserBranchAssignments.AnyAsync(a =>
                a.CompanyId == m.CompanyId && a.UserCompanyMembershipId == m.Id && a.BranchId == role.BranchId && a.Status == AssignmentStatus.Active, ct);
        if (!allocated)
        {
            throw new DomainValidationException("branchId", "The user must be allocated to the role's branch (AssignedBranches) to hold a branch role.");
        }
    }

    private UserRoleAssignment Stage(RoleActor actor, UserCompanyMembership m, Role role, string? note)
    {
        var row = RoleRules.NewAssignment(m, role, note, actor.UserId, DateTimeOffset.UtcNow);
        db.UserRoleAssignments.Add(row);
        RoleRules.AuditCreated(db, RoleRules.ToAccess(actor), row, role.Name, m.UserId);
        return row;
    }

    private Task EndRowAsync(RoleActor actor, UserCompanyMembership m, UserRoleAssignment row, Role role, string? note, CancellationToken ct)
    {
        var before = RoleRules.Snap(row, role.Name);
        RoleRules.Close(row, actor.UserId, note);
        RoleRules.AuditEnded(db, RoleRules.ToAccess(actor), row, role.Name, m.UserId, before);
        return Task.CompletedTask;
    }

    private static string? Reason(string? reason, bool required)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null && required) throw new DomainValidationException("reason", "A reason is required.");
        if (text is { Length: > ReasonMax }) throw new DomainValidationException("reason", $"The reason can have at most {ReasonMax} characters.");
        return text;
    }
}
