using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Query helpers for the governance rules R1-R5 (Phase 6 D10). All read the current state from the database; callers run
/// them inside the access-management lock so they see a consistent state.</summary>
public static class GovernanceRules
{
    /// <summary>Governance codes the user holds now in the current company (active member, active membership).</summary>
    public static async Task<HashSet<string>> HeldCodesAsync(SelloraDbContext db, Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var codes = await db.GovernanceMemberPermissions.AsNoTracking()
            .Where(p => p.CompanyId == companyId
                && p.Member.Status == GovernanceMemberStatus.Active
                && p.Member.Membership.UserId == userId
                && p.Member.Membership.Status == MembershipStatus.Active)
            .Select(p => p.GovernancePermission.Code)
            .ToListAsync(ct);
        return codes.ToHashSet();
    }

    public static Task<bool> IsActivePlatformAdminAsync(SelloraDbContext db, Guid userId, CancellationToken ct) =>
        db.PlatformAdministrators.AsNoTracking().AnyAsync(p => p.UserId == userId && p.Status == PlatformAdminStatus.Active, ct);

    /// <summary>
    /// R5: operational APIs (edit, status, role, reset-password, access changes) refuse protected targets. An active platform
    /// administrator is never changeable from a company session; an active governance member only by an actor holding ManageMembers
    /// (Owner target: ManageOwners).
    /// </summary>
    public static async Task EnsureOperationalTargetAllowedAsync(SelloraDbContext db, Guid actorUserId, Guid targetUserId, CancellationToken ct)
    {
        if (await IsActivePlatformAdminAsync(db, targetUserId, ct))
        {
            throw new AccessManagementForbiddenException(GovernanceErrorCodes.PlatformAccountProtected,
                "Platform administrator accounts cannot be changed from a company session.");
        }

        var companyId = db.CompanyContext.RequireCompanyId();
        var title = await db.CompanyGovernanceMembers.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.Status == GovernanceMemberStatus.Active && m.Membership.UserId == targetUserId)
            .Select(m => (GovernanceTitle?)m.Title)
            .FirstOrDefaultAsync(ct);
        if (title is null) return;

        var required = title == GovernanceTitle.Owner ? GovernancePermissionCatalog.ManageOwners : GovernancePermissionCatalog.ManageMembers;
        var held = await HeldCodesAsync(db, actorUserId, ct);
        if (!held.Contains(required))
        {
            throw new AccessManagementForbiddenException(GovernanceErrorCodes.MemberProtected,
                "This account is a governance member and cannot be changed by ordinary user administration.");
        }
    }

    /// <summary>True when the user is an active governance member with the Owner title in the current company.</summary>
    public static Task<bool> IsActiveOwnerAsync(SelloraDbContext db, Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return db.CompanyGovernanceMembers.AsNoTracking().AnyAsync(m =>
            m.CompanyId == companyId && m.Status == GovernanceMemberStatus.Active && m.Title == GovernanceTitle.Owner && m.Membership.UserId == userId, ct);
    }

    /// <summary>
    /// R4: the company keeps at least one root owner = Active Owner member, Active membership, user not Disabled, holding View +
    /// ManageOwners + AssignPermissions. Reads the saved state: call after SaveChanges, inside the transaction (the caller's scope
    /// rolls back when this throws).
    /// </summary>
    public static async Task EnsureRootOwnerRemainsAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var ownerIds = await db.CompanyGovernanceMembers.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.Status == GovernanceMemberStatus.Active && m.Title == GovernanceTitle.Owner
                && m.Membership.Status == MembershipStatus.Active && m.Membership.User.Status != UserStatus.Disabled)
            .Select(m => m.Id)
            .ToListAsync(ct);

        string[] required = [GovernancePermissionCatalog.View, GovernancePermissionCatalog.ManageOwners, GovernancePermissionCatalog.AssignPermissions];
        var grants = ownerIds.Count == 0
            ? []
            : await db.GovernanceMemberPermissions.AsNoTracking()
                .Where(p => p.CompanyId == companyId && ownerIds.Contains(p.GovernanceMemberId) && required.Contains(p.GovernancePermission.Code))
                .Select(p => new { p.GovernanceMemberId, p.GovernancePermission.Code })
                .ToListAsync(ct);

        var hasRoot = grants.GroupBy(g => g.GovernanceMemberId).Any(g => g.Select(x => x.Code).Distinct().Count() == required.Length);
        if (!hasRoot)
        {
            throw new GovernanceRuleException(GovernanceErrorCodes.LastOwnerProtected,
                "The company must keep at least one active Owner holding View, ManageOwners and AssignPermissions.");
        }
    }
}
