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
/// Company and branch roles (Phase 7). Authorization is evaluated per scope in the services (R1-R3) because a branch administrator holds
/// only <c>Branch.Roles.*</c> at one branch: the endpoints require a signed-in user and answer 404 for roles outside the caller's reach.
/// GET defaults to company roles so the existing SPA keeps working. Writes run in the access-management scope (advisory lock, actor re-read).
/// </summary>
[ApiController]
[Route("api/v1/roles")]
[Authorize]
public class RolesController(
    SelloraDbContext db, IRoleService roles, IEffectivePermissionService effective, IMembershipContext membership) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoleListItemDto>>> List(
        [FromQuery] RoleScope scope = RoleScope.Company, [FromQuery] Guid? branchId = null, CancellationToken ct = default)
    {
        var actor = await RoleActorReader.ReadAsync(db, effective, membership, User, HttpContext.TraceIdentifier, ct);
        List<Role> rows;
        if (scope == RoleScope.Company)
        {
            if (branchId is not null) return ValidationFailure("branchId", "A company role has no branch.");
            if (!actor.Effective.HasCompanyLevel("Roles.View")) return Forbidden("You are not allowed to view company roles.");
            rows = await db.Roles.AsNoTracking().Where(r => r.ScopeType == RoleScope.Company).OrderBy(r => r.Name).ToListAsync(ct);
        }
        else
        {
            var candidates = await db.Roles.AsNoTracking()
                .Where(r => r.ScopeType == RoleScope.Branch && (branchId == null || r.BranchId == branchId))
                .OrderBy(r => r.Name).ToListAsync(ct);
            rows = [];
            foreach (var r in candidates)
            {
                if (await RoleRules.CanViewAsync(db, actor, r.ScopeType, r.BranchId, ct)) rows.Add(r);
            }
            if (branchId is not null && rows.Count == 0 && !await RoleRules.CanViewAsync(db, actor, RoleScope.Branch, branchId, ct))
            {
                return NotFound();
            }
        }
        return Ok(await ToListItemsAsync(rows, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RoleDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var actor = await RoleActorReader.ReadAsync(db, effective, membership, User, HttpContext.TraceIdentifier, ct);
        var role = await db.Roles.AsNoTracking().Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null || !await RoleRules.CanViewAsync(db, actor, role.ScopeType, role.BranchId, ct)) return NotFound();
        return Ok(await ToDetailAsync(role, ct));
    }

    [HttpPost]
    public async Task<ActionResult<RoleDetailDto>> Create(CreateRoleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await WriteAsync(
            (actor, _) => roles.CreateAsync(actor, new CreateRoleInput(request.Name, request.Description, request.PermissionCodes ?? [], request.ScopeType, request.BranchId), ct),
            created: true, ct);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RoleDetailDto>> Update(Guid id, UpdateRoleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await WriteAsync(
            (actor, _) => roles.UpdateAsync(actor, id, new UpdateRoleInput(request.Name, request.Description, request.PermissionCodes ?? [], request.ScopeType, request.BranchId), ct),
            created: false, ct);
    }

    [HttpPost("{id:guid}/duplicate")]
    public Task<ActionResult<RoleDetailDto>> Duplicate(Guid id, CancellationToken ct) =>
        WriteAsync((actor, _) => roles.DuplicateAsync(actor, id, ct), created: true, ct);

    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> SetActive(Guid id, SetRoleActiveRequest request, CancellationToken ct)
    {
        await using var access = await AccessManagementScope.BeginAsync(db, User, ct);
        try
        {
            await roles.SetActiveAsync(access.Actor.ToRoleActor(HttpContext.TraceIdentifier), id, request.IsActive, ct);
            await access.CommitAsync(ct);
            return NoContent();
        }
        catch (AccessManagementForbiddenException ex) { return AccessProblems.Forbidden(ex); }
        catch (RoleRuleException ex) { return AccessProblems.Rule(ex); }
        catch (AccessTargetNotFoundException) { return NotFound(); }
    }

    private async Task<ActionResult<RoleDetailDto>> WriteAsync(Func<RoleActor, AccessManagementScope, Task<Role>> operation, bool created, CancellationToken ct)
    {
        Role role;
        await using (var access = await AccessManagementScope.BeginAsync(db, User, ct))
        {
            try
            {
                role = await operation(access.Actor.ToRoleActor(HttpContext.TraceIdentifier), access);
                await access.CommitAsync(ct);
            }
            catch (AccessManagementForbiddenException ex) { return AccessProblems.Forbidden(ex); }
            catch (RoleRuleException ex) { return AccessProblems.Rule(ex); }
            catch (AccessTargetNotFoundException) { return NotFound(); }
        }

        var detail = await ToDetailAsync(role, ct);
        return created ? CreatedAtAction(nameof(Get), new { id = role.Id }, detail) : Ok(detail);
    }

    private ObjectResult Forbidden(string message) => AccessProblems.Create(StatusCodes.Status403Forbidden, message, RoleErrorCodes.RoleManagementForbidden);

    private ActionResult ValidationFailure(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    private async Task<IReadOnlyList<RoleListItemDto>> ToListItemsAsync(List<Role> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var companyId = db.CompanyContext.RequireCompanyId();
        var counts = await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.Status == AssignmentStatus.Active && ids.Contains(a.RoleId))
            .GroupBy(a => a.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var countById = counts.ToDictionary(c => c.RoleId, c => c.Count);
        var branchNames = await BranchNamesAsync(rows, ct);
        var adminCodes = PermissionCatalog.BranchAdministrationCodes.ToArray();
        var holders = (await db.RolePermissions.AsNoTracking()
            .Where(rp => ids.Contains(rp.RoleId) && adminCodes.Contains(rp.Permission.Code))
            .Select(rp => rp.RoleId).Distinct().ToListAsync(ct)).ToHashSet();
        return rows.Select(r => new RoleListItemDto(r.Id, r.Name, r.Description, r.IsSystem, r.IsActive, countById.GetValueOrDefault(r.Id),
            r.ScopeType, r.BranchId, r.BranchId is { } b ? branchNames.GetValueOrDefault(b) : null,
            r.IsSystem || holders.Contains(r.Id))).ToList();
    }

    private async Task<RoleDetailDto> ToDetailAsync(Role role, CancellationToken ct)
    {
        // Reload the grants (a created/updated role may carry tracked navigations only partially).
        var codes = role.IsSystem
            ? PermissionCatalog.All.Select(p => p.Code).ToArray()
            : (await db.RolePermissions.AsNoTracking().Where(rp => rp.RoleId == role.Id).Select(rp => rp.Permission.Code).ToListAsync(ct)).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var names = await BranchNamesAsync([role], ct);
        return new RoleDetailDto(role.Id, role.Name, role.Description, role.IsSystem, role.IsActive, codes,
            role.ScopeType, role.BranchId, role.BranchId is { } b ? names.GetValueOrDefault(b) : null);
    }

    private async Task<Dictionary<Guid, string>> BranchNamesAsync(IEnumerable<Role> rows, CancellationToken ct)
    {
        var branchIds = rows.Where(r => r.BranchId != null).Select(r => r.BranchId!.Value).Distinct().ToList();
        if (branchIds.Count == 0) return [];
        var companyId = db.CompanyContext.RequireCompanyId();
        var branches = await db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId && branchIds.Contains(b.Id)).Select(b => new { b.Id, b.Name }).ToListAsync(ct);
        return branches.ToDictionary(b => b.Id, b => b.Name);
    }
}
