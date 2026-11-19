using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>Phase 01b (FI-D44/FI-D45): which company users an actor may see/administer. Shared by the users and user-access controllers.</summary>
internal static class UserVisibility
{
    /// <summary>Users that have a membership (any status) in the current company. Users is a platform table, so every
    /// action that reads or changes ANOTHER user goes through this scope (L1): other tenants' users are 404.</summary>
    public static IQueryable<User> CompanyUsers(SelloraDbContext db)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return db.Users.Where(u => db.UserCompanyMemberships.Any(m => m.UserId == u.Id && m.CompanyId == companyId));
    }

    /// <summary>
    /// Phase 01 (FI-D14) + Phase 01b (FI-D44/FI-D45): company users the actor may see/administer. A CompanyWide actor in company mode (allowed set
    /// null) sees every company user. Otherwise - a branch-limited actor, or any actor on the branch path (<paramref name="branchMode"/>: only the
    /// Branch.Users.* code, no company-level Users.* code) - only users that work wholly inside the actor's branches and are not company administration
    /// (else 404). The branch set is the request's allowed set (already narrowed to the branches where the branch code is held), or every company
    /// branch for a company-wide actor holding the branch code company-level.
    /// </summary>
    public static async Task<IQueryable<User>> VisibleUsersAsync(SelloraDbContext db, bool branchMode, Guid actorUserId, CancellationToken ct)
    {
        var query = CompanyUsers(db);
        var allowedSet = db.BranchContext.AllowedBranchIds;
        if (allowedSet is null && !branchMode) return query;

        var companyId = db.CompanyContext.RequireCompanyId();
        var allowed = allowedSet?.ToArray() ?? await db.Branches.Where(b => b.CompanyId == companyId).Select(b => b.Id).ToArrayAsync(ct);
        var adminCodes = PermissionCatalog.BranchAdministrationCodes.ToArray();
        // FI-D14: a branch-limited actor reaches only users that work wholly inside its branches - an AssignedBranches membership with at least
        // one active allocation, every active allocation and every active branch role inside the actor's branches, and no company role.
        // Company-wide members and users without any allocation are company administration (404 for the branch actor).
        // FI-D45 (owner-confirmed): holders of a branch administration code are company administration too, and the actor never administers itself
        // here (self-service endpoints are separate).
        return query.Where(u => u.Id != actorUserId && db.UserCompanyMemberships.Any(m => m.UserId == u.Id && m.CompanyId == companyId
            && m.AccessScope == CompanyAccessScope.AssignedBranches
            && db.UserBranchAssignments.Any(a => a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active)
            && !db.UserBranchAssignments.Any(a => a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active && !allowed.Contains(a.BranchId))
            && !db.UserRoleAssignments.Any(r => r.CompanyId == companyId && r.UserCompanyMembershipId == m.Id && r.Status == AssignmentStatus.Active
                && (r.ScopeType == RoleScope.Company || (r.BranchId != null && !allowed.Contains(r.BranchId.Value))
                    || db.RolePermissions.Any(rp => rp.RoleId == r.RoleId && adminCodes.Contains(rp.Permission.Code))))));
    }

    /// <summary>
    /// Phase 01b read rule for the access view (GET users/{id}/access) and the role-assignment view (GET users/{id}/roles): a non-company-wide caller
    /// reaches only users with at least one ACTIVE allocation in <paramref name="branchIds"/> (null = every company branch). Users only in other
    /// branches, company-wide members and unallocated users are 404. Unlike <see cref="VisibleUsersAsync"/> (the administration target rule), this
    /// keeps the approved Phase 5/7 read semantics: a multi-branch member or a company-role holder working in the caller's branch stays readable
    /// (the role view is still filtered to the caller's branches); writes keep the FI-D14/FI-D45 rules.
    /// </summary>
    public static async Task<bool> IsAllocatedWithinAsync(SelloraDbContext db, Guid userId, IReadOnlyCollection<Guid>? branchIds, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var set = branchIds?.ToArray() ?? await db.Branches.Where(b => b.CompanyId == companyId).Select(b => b.Id).ToArrayAsync(ct);
        return await db.UserCompanyMemberships.AnyAsync(m => m.UserId == userId && m.CompanyId == companyId
            && db.UserBranchAssignments.Any(a => a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id
                && a.Status == AssignmentStatus.Active && set.Contains(a.BranchId)), ct);
    }

    /// <summary>
    /// Phase 01b: the branch path applies when the caller lacks <paramref name="companyCode"/> company-level but holds <paramref name="branchCode"/>
    /// (at some branch, or company-level). A caller holding neither keeps the company-path checks (the policy refuses it before this point anyway).
    /// </summary>
    public static bool IsBranchMode(EffectivePermissionSet set, string companyCode, string branchCode) =>
        !set.HasCompanyLevel(companyCode) && (set.HasCompanyLevel(branchCode) || set.PermittedBranches(branchCode) is { Count: > 0 });
}
