using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Effective permissions per membership (Phase 7, D6/D7). Cached in IMemoryCache by ("perm", membershipId, accessVersion), absolute TTL 5 minutes;
/// every membership change bumps AccessVersion (read from the database on each request), so a stale entry is simply never looked up again.
/// Works before the company context exists (login/refresh/switch): every query bypasses the Company filter with explicit CompanyId predicates.
/// </summary>
public sealed class EffectivePermissionService(SelloraDbContext db, IMemoryCache cache, IMembershipContext membership) : IEffectivePermissionService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly string[] CompanyFilter = [CompanyQueryFilter.Name];

    public async Task<EffectivePermissionSet> GetCurrentAsync(CancellationToken ct = default) =>
        membership.MembershipId is { } id ? await GetAsync(id, membership.AccessVersion, ct) : EffectivePermissionSet.None;

    public async Task<EffectivePermissionSet> GetAsync(Guid membershipId, long accessVersion, CancellationToken ct = default)
    {
        var key = ("perm", membershipId, accessVersion);
        if (cache.TryGetValue(key, out EffectivePermissionSet? hit) && hit is not null) return hit;

        var set = await LoadAsync(db, membershipId, ct);
        cache.Set(key, set, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
        return set;
    }

    /// <summary>Uncached evaluation (also used by access-management writes that must see the current state).</summary>
    public static async Task<EffectivePermissionSet> LoadAsync(SelloraDbContext db, Guid membershipId, CancellationToken ct)
    {
        var companyId = await db.UserCompanyMemberships
            .Where(m => m.Id == membershipId && m.Status == MembershipStatus.Active)
            .Select(m => (Guid?)m.CompanyId)
            .FirstOrDefaultAsync(ct);
        if (companyId is not { } cid) return EffectivePermissionSet.None;

        var roles = await (
            from a in db.UserRoleAssignments.IgnoreQueryFilters(CompanyFilter)
            join r in db.Roles.IgnoreQueryFilters(CompanyFilter) on a.RoleId equals r.Id
            where a.CompanyId == cid && a.UserCompanyMembershipId == membershipId && a.Status == AssignmentStatus.Active
                && r.CompanyId == cid && r.IsActive
            select new { RoleId = r.Id, r.IsSystem, r.ScopeType, a.BranchId })
            .ToListAsync(ct);
        if (roles.Count == 0) return EffectivePermissionSet.None;

        var isAdministrator = roles.Any(r => r.IsSystem && r.ScopeType == RoleScope.Company);

        // Branch roles apply only while the member holds an active allocation to an active branch (E2/E4).
        var branchRoleBranches = roles.Where(r => r.ScopeType == RoleScope.Branch && r.BranchId != null).Select(r => r.BranchId!.Value).Distinct().ToArray();
        var usableBranches = new HashSet<Guid>();
        if (branchRoleBranches.Length > 0)
        {
            var allocated = await db.UserBranchAssignments.IgnoreQueryFilters(CompanyFilter)
                .Where(a => a.CompanyId == cid && a.UserCompanyMembershipId == membershipId && a.Status == AssignmentStatus.Active
                    && branchRoleBranches.Contains(a.BranchId))
                .Select(a => a.BranchId)
                .ToListAsync(ct);
            var active = await db.Branches.IgnoreQueryFilters(CompanyFilter)
                .Where(b => b.CompanyId == cid && b.IsActive && allocated.Contains(b.Id))
                .Select(b => b.Id)
                .ToListAsync(ct);
            usableBranches.UnionWith(active);
        }

        var roleIds = roles.Select(r => r.RoleId).Distinct().ToArray();
        var grants = await db.RolePermissions.IgnoreQueryFilters(CompanyFilter)
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => new { rp.RoleId, rp.Permission.Code })
            .ToListAsync(ct);
        var codesByRole = grants.GroupBy(g => g.RoleId).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToList());

        var companyCodes = new HashSet<string>(StringComparer.Ordinal);
        var branchCodes = new Dictionary<Guid, HashSet<string>>();
        foreach (var role in roles)
        {
            var codes = codesByRole.TryGetValue(role.RoleId, out var c) ? c : [];
            if (role.ScopeType == RoleScope.Company)
            {
                companyCodes.UnionWith(codes);
            }
            else if (role.BranchId is { } branchId && usableBranches.Contains(branchId))
            {
                if (!branchCodes.TryGetValue(branchId, out var set)) branchCodes[branchId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(codes.Where(PermissionCatalog.IsBranchEligible)); // defensive: CompanyOnly codes never apply at a branch
            }
        }

        return new EffectivePermissionSet(
            isAdministrator,
            companyCodes,
            branchCodes.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value));
    }
}
