using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Shared rules of scoped roles (Phase 7, D9/D10): who may see/manage/assign a role at its scope (Q1: company roles need a CompanyWide actor),
/// which codes a role holds, assignment row mechanics, the last-Administrator rule. Callers hold the access-management lock.
/// </summary>
public static class RoleRules
{
    public static readonly string[] AllCodes = PermissionCatalog.All.Select(p => p.Code).ToArray();

    /// <summary>The actor may act on branch <paramref name="branchId"/>: company-wide or holds an active allocation there.</summary>
    public static async Task<bool> ReachesAsync(SelloraDbContext db, RoleActor actor, Guid branchId, CancellationToken ct)
    {
        if (actor.IsCompanyWide) return true;
        if (actor.MembershipId is not { } membershipId) return false;
        var companyId = db.CompanyContext.RequireCompanyId();
        return await db.UserBranchAssignments.AnyAsync(a =>
            a.CompanyId == companyId && a.UserCompanyMembershipId == membershipId && a.BranchId == branchId && a.Status == AssignmentStatus.Active, ct);
    }

    /// <summary>Visibility (404 otherwise): company roles need company-level Roles.View; branch roles need reach plus Roles.View (company level) or Branch.Roles.View at the branch.</summary>
    public static async Task<bool> CanViewAsync(SelloraDbContext db, RoleActor actor, RoleScope scope, Guid? branchId, CancellationToken ct)
    {
        if (scope == RoleScope.Company) return actor.Effective.HasCompanyLevel("Roles.View");
        if (branchId is not { } b || !await ReachesAsync(db, actor, b, ct)) return false;
        return actor.Effective.HasCompanyLevel("Roles.View") || actor.Effective.HasAtBranch(PermissionCatalog.BranchRolesView, b)
            || actor.Effective.HasAtBranch(PermissionCatalog.BranchRolesManage, b);
    }

    /// <summary>R2/R3: company roles need the company-level code and a CompanyWide actor; branch roles the same company path or Branch.Roles.Manage at the branch.</summary>
    public static async Task<bool> CanManageAsync(SelloraDbContext db, RoleActor actor, RoleScope scope, Guid? branchId, string companyCode, CancellationToken ct)
    {
        var companyPath = actor.IsCompanyWide && actor.Effective.HasCompanyLevel(companyCode);
        if (scope == RoleScope.Company) return companyPath;
        if (branchId is not { } b) return false;
        return companyPath || (await ReachesAsync(db, actor, b, ct) && actor.Effective.HasAtBranch(PermissionCatalog.BranchRolesManage, b));
    }

    /// <summary>R4: company role needs Company.Users.AssignRoles + CompanyWide actor; branch role the same path or Branch.Users.AssignRoles at the branch (with reach).</summary>
    public static async Task<bool> CanAssignAsync(SelloraDbContext db, RoleActor actor, Role role, CancellationToken ct)
    {
        var companyPath = actor.IsCompanyWide && actor.Effective.HasCompanyLevel(PermissionCatalog.CompanyUsersAssignRoles);
        if (role.ScopeType == RoleScope.Company) return companyPath;
        if (role.BranchId is not { } b) return false;
        return companyPath || (await ReachesAsync(db, actor, b, ct) && actor.Effective.HasAtBranch(PermissionCatalog.BranchUsersAssignRoles, b));
    }

    /// <summary>The actor holds <paramref name="code"/> at the role's scope (company-wide for company roles; at branch B for branch roles).</summary>
    public static bool Holds(RoleActor actor, RoleScope scope, Guid? branchId, string code) =>
        actor.IsAdministrator || (scope == RoleScope.Company
            ? actor.Effective.HasCompanyLevel(code)
            : branchId is { } b && actor.Effective.HasAtBranch(code, b));

    public static List<string> NotHeld(RoleActor actor, RoleScope scope, Guid? branchId, IEnumerable<string> codes) =>
        codes.Distinct(StringComparer.Ordinal).Where(c => !Holds(actor, scope, branchId, c)).OrderBy(c => c, StringComparer.Ordinal).ToList();

    public static async Task<List<string>> CodesOfAsync(SelloraDbContext db, Role role, CancellationToken ct)
    {
        if (role.IsSystem) return AllCodes.ToList();
        return await db.RolePermissions.Where(rp => rp.RoleId == role.Id).Select(rp => rp.Permission.Code).ToListAsync(ct);
    }

    public static AccessManagementForbiddenException Forbidden(string code, string message) => new(code, message);

    public static string NotHeldMessage(IReadOnlyCollection<string> codes) =>
        $"You can only grant permissions you hold yourself. Not held: {string.Join(", ", codes)}.";

    public static AccessActor ToAccess(RoleActor actor) => new(actor.UserId, actor.IsAdministrator, false, actor.CorrelationId);

    public static void Bump(UserCompanyMembership m, Guid actorUserId)
    {
        m.AccessVersion++;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        m.UpdatedByUserId = actorUserId;
    }

    public static UserRoleAssignment NewAssignment(UserCompanyMembership m, Role role, string? reason, Guid? actorUserId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = m.CompanyId,
        UserCompanyMembershipId = m.Id,
        RoleId = role.Id,
        ScopeType = role.ScopeType,
        BranchId = role.BranchId,
        Status = AssignmentStatus.Active,
        StartAt = now,
        AssignmentReason = reason,
        CreatedAt = now,
        CreatedByUserId = actorUserId,
    };

    public static void Close(UserRoleAssignment a, Guid actorUserId, string? reason)
    {
        var now = DateTimeOffset.UtcNow;
        a.Status = AssignmentStatus.Ended;
        a.EndAt = now < a.StartAt ? a.StartAt : now;
        a.EndedByUserId = actorUserId;
        a.EndReason = reason;
    }

    public static object Snap(UserRoleAssignment a, string roleName) => new
    {
        id = a.Id, roleId = a.RoleId, role = roleName, scopeType = a.ScopeType.ToString(), branchId = a.BranchId,
        status = a.Status.ToString(), startAt = a.StartAt, endAt = a.EndAt,
    };

    public static void AuditCreated(SelloraDbContext db, AccessActor actor, UserRoleAssignment a, string roleName, Guid subjectUserId) =>
        AuditLog.Append(db, actor, AuditActions.RoleAssignmentCreated, AuditTargets.RoleAssignment, a.Id, subjectUserId, a.AssignmentReason, null, Snap(a, roleName));

    public static void AuditEnded(SelloraDbContext db, AccessActor actor, UserRoleAssignment a, string roleName, Guid subjectUserId, object before) =>
        AuditLog.Append(db, actor, AuditActions.RoleAssignmentEnded, AuditTargets.RoleAssignment, a.Id, subjectUserId, a.EndReason, before, Snap(a, roleName));

    /// <summary>True when the membership holds an Active assignment of the company's Administrator (system) role.</summary>
    public static Task<bool> HoldsAdministratorAsync(SelloraDbContext db, Guid membershipId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return db.UserRoleAssignments.AnyAsync(a =>
            a.CompanyId == companyId && a.UserCompanyMembershipId == membershipId && a.Status == AssignmentStatus.Active
            && db.Roles.Any(r => r.Id == a.RoleId && r.IsSystem), ct);
    }

    /// <summary>The company's Administrator role (one per company, IX_Roles_CompanyId_System), or null.</summary>
    public static Task<Role?> AdministratorRoleAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return db.Roles.Where(r => r.CompanyId == companyId && r.IsSystem).FirstOrDefaultAsync(ct);
    }

    /// <summary>D9: the company keeps at least one Active CompanyWide membership holding an Active Administrator assignment of a non-Disabled user (checked on saved state).</summary>
    public static async Task EnsureAdministratorRemainsAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var remains = await db.UserRoleAssignments.AsNoTracking().AnyAsync(a =>
            a.CompanyId == companyId && a.Status == AssignmentStatus.Active
            && db.Roles.Any(r => r.Id == a.RoleId && r.IsSystem && r.IsActive)
            && db.UserCompanyMemberships.Any(m => m.Id == a.UserCompanyMembershipId && m.Status == MembershipStatus.Active
                && m.AccessScope == CompanyAccessScope.CompanyWide && m.User.Status != UserStatus.Disabled), ct);
        if (!remains)
        {
            throw new RoleRuleException(RoleErrorCodes.LastAdministratorProtected, "The company must keep at least one active company-wide Administrator.");
        }
    }

    /// <summary>D8: bumps AccessVersion of every membership holding an Active assignment of the role (same transaction; loaded and tracked so InMemory works).</summary>
    public static async Task BumpHoldersAsync(SelloraDbContext db, Guid roleId, Guid actorUserId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var membershipIds = await db.UserRoleAssignments
            .Where(a => a.CompanyId == companyId && a.RoleId == roleId && a.Status == AssignmentStatus.Active)
            .Select(a => a.UserCompanyMembershipId).Distinct().ToListAsync(ct);
        if (membershipIds.Count == 0) return;
        var memberships = await db.UserCompanyMemberships.Where(m => m.CompanyId == companyId && membershipIds.Contains(m.Id)).ToListAsync(ct);
        foreach (var m in memberships) Bump(m, actorUserId);
    }
}
