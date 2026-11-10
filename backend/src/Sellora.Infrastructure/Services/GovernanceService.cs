using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Company governance writes (Phase 6 D7-D11). The caller owns the transaction and the access-management advisory lock, so the actor's
/// governance codes (read here from the database) and the target are a consistent state. Rules: R1 no self change, R2 delegation
/// ceiling, R3 Owner changes need ManageOwners, R4 last root owner, reason on every write. Every write bumps the subject membership's
/// AccessVersion and appends an audit event.
/// </summary>
public sealed class GovernanceService(SelloraDbContext db, IUserAccessService userAccess) : IGovernanceService
{
    private const int ReasonMax = 500;

    public async Task<Guid> AddMemberAsync(GovernanceActor actor, AddGovernanceMemberInput input, CancellationToken ct = default)
    {
        var note = Reason(input.Reason);
        if (!Enum.IsDefined(input.Title)) throw new DomainValidationException("title", "Select a valid title.");
        var held = await GovernanceRules.HeldCodesAsync(db, actor.UserId, ct);
        RequireCode(held, input.Title == GovernanceTitle.Owner ? GovernancePermissionCatalog.ManageOwners : GovernancePermissionCatalog.ManageMembers);
        if (input.UserId == actor.UserId) throw SelfChange();

        var companyId = db.CompanyContext.RequireCompanyId();
        var membership = await db.UserCompanyMemberships.Include(m => m.User)
            .SingleOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == input.UserId, ct)
            ?? throw new AccessTargetNotFoundException();
        if (membership.Status != MembershipStatus.Active || membership.User.Status == UserStatus.Disabled)
        {
            throw new DomainValidationException("userId", "The user must be an active member of this company.");
        }
        if (await GovernanceRules.IsActivePlatformAdminAsync(db, input.UserId, ct))
        {
            throw new AccessManagementForbiddenException(GovernanceErrorCodes.PlatformAccountProtected,
                "Platform administrators cannot hold company governance roles.");
        }
        if (await db.CompanyGovernanceMembers.AnyAsync(m => m.CompanyId == companyId && m.UserCompanyMembershipId == membership.Id && m.Status == GovernanceMemberStatus.Active, ct))
        {
            throw new DomainValidationException("userId", "The user is already an active governance member.");
        }

        var codes = NormalizeCodes(input.PermissionCodes);
        if (codes.Count > 0)
        {
            RequireCode(held, GovernancePermissionCatalog.AssignPermissions);
            RequireHeld(held, codes);
        }
        var catalogue = await LoadCatalogueAsync(codes, ct);

        var now = DateTimeOffset.UtcNow;
        var member = new CompanyGovernanceMember
        {
            Id = Guid.NewGuid(), CompanyId = companyId, UserCompanyMembershipId = membership.Id, Title = input.Title,
            Status = GovernanceMemberStatus.Active, StartAt = now, CreatedAt = now, CreatedByUserId = actor.UserId,
        };
        db.CompanyGovernanceMembers.Add(member);
        foreach (var code in codes)
        {
            db.GovernanceMemberPermissions.Add(NewGrant(member, catalogue[code], actor, now));
        }

        Bump(membership, actor);
        AuditLog.Append(db, ToAccess(actor), AuditActions.GovernanceMemberAdded, AuditTargets.GovernanceMember, member.Id, input.UserId, note,
            null, Snap(member, codes));
        await db.SaveChangesAsync(ct);
        await GovernanceRules.EnsureRootOwnerRemainsAsync(db, ct);
        return member.Id;
    }

    public async Task ChangeTitleAsync(GovernanceActor actor, Guid memberId, GovernanceTitle title, string? reason, CancellationToken ct = default)
    {
        var note = Reason(reason);
        if (!Enum.IsDefined(title)) throw new DomainValidationException("title", "Select a valid title.");
        var held = await GovernanceRules.HeldCodesAsync(db, actor.UserId, ct);
        var member = await LoadActiveMemberAsync(memberId, ct);
        RequireNotSelf(actor, member);
        RequireCode(held, member.Title == GovernanceTitle.Owner || title == GovernanceTitle.Owner
            ? GovernancePermissionCatalog.ManageOwners : GovernancePermissionCatalog.ManageMembers);
        if (member.Title == title) throw new DomainValidationException("title", $"The member is already {title}.");

        var codes = await CodesOfAsync(member, ct);
        var before = Snap(member, codes);
        member.Title = title;
        member.UpdatedAt = DateTimeOffset.UtcNow;
        member.UpdatedByUserId = actor.UserId;
        Bump(member.Membership, actor);
        AuditLog.Append(db, ToAccess(actor), AuditActions.GovernanceMemberTitleChanged, AuditTargets.GovernanceMember, member.Id,
            member.Membership.UserId, note, before, Snap(member, codes));
        await db.SaveChangesAsync(ct);
        await GovernanceRules.EnsureRootOwnerRemainsAsync(db, ct);
    }

    public async Task SetPermissionsAsync(GovernanceActor actor, Guid memberId, IReadOnlyList<string>? permissionCodes, string? reason, CancellationToken ct = default)
    {
        var note = Reason(reason);
        var held = await GovernanceRules.HeldCodesAsync(db, actor.UserId, ct);
        RequireCode(held, GovernancePermissionCatalog.AssignPermissions);
        var member = await LoadActiveMemberAsync(memberId, ct);
        RequireNotSelf(actor, member);
        if (member.Title == GovernanceTitle.Owner) RequireCode(held, GovernancePermissionCatalog.ManageOwners);

        var desired = NormalizeCodes(permissionCodes);
        var existing = await db.GovernanceMemberPermissions
            .Where(p => p.CompanyId == member.CompanyId && p.GovernanceMemberId == member.Id)
            .Include(p => p.GovernancePermission)
            .ToListAsync(ct);
        var current = existing.Select(p => p.GovernancePermission.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var added = desired.Except(current).ToList();
        var removed = current.Except(desired).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            throw new DomainValidationException("permissionCodes", "The grants are unchanged.");
        }
        RequireHeld(held, added.Concat(removed).ToList());

        var catalogue = await LoadCatalogueAsync(added, ct);
        var now = DateTimeOffset.UtcNow;
        db.GovernanceMemberPermissions.RemoveRange(existing.Where(p => removed.Contains(p.GovernancePermission.Code)));
        foreach (var code in added)
        {
            db.GovernanceMemberPermissions.Add(NewGrant(member, catalogue[code], actor, now));
        }

        member.UpdatedAt = now;
        member.UpdatedByUserId = actor.UserId;
        Bump(member.Membership, actor);
        AuditLog.Append(db, ToAccess(actor), AuditActions.GovernanceMemberPermissionsChanged, AuditTargets.GovernanceMember, member.Id,
            member.Membership.UserId, note, Snap(member, current), Snap(member, desired));
        await db.SaveChangesAsync(ct);
        await GovernanceRules.EnsureRootOwnerRemainsAsync(db, ct);
    }

    public async Task EndMemberAsync(GovernanceActor actor, Guid memberId, string? reason, CancellationToken ct = default)
    {
        var note = Reason(reason);
        var held = await GovernanceRules.HeldCodesAsync(db, actor.UserId, ct);
        var member = await LoadActiveMemberAsync(memberId, ct);
        RequireNotSelf(actor, member);
        RequireCode(held, member.Title == GovernanceTitle.Owner ? GovernancePermissionCatalog.ManageOwners : GovernancePermissionCatalog.ManageMembers);

        var grants = await db.GovernanceMemberPermissions
            .Where(p => p.CompanyId == member.CompanyId && p.GovernanceMemberId == member.Id)
            .Include(p => p.GovernancePermission)
            .ToListAsync(ct);
        var codes = grants.Select(g => g.GovernancePermission.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
        // Ending revokes every grant, so the delegation ceiling (R2) applies exactly as in SetPermissionsAsync.
        RequireHeld(held, codes);
        var before = Snap(member, codes);

        // Ending removes the grants in the same transaction (D7).
        db.GovernanceMemberPermissions.RemoveRange(grants);
        var now = DateTimeOffset.UtcNow;
        member.Status = GovernanceMemberStatus.Ended;
        member.EndAt = now < member.StartAt ? member.StartAt : now;
        member.EndReason = note;
        member.EndedByUserId = actor.UserId;
        Bump(member.Membership, actor);
        AuditLog.Append(db, ToAccess(actor), AuditActions.GovernanceMemberEnded, AuditTargets.GovernanceMember, member.Id,
            member.Membership.UserId, note, before, Snap(member, []));
        await db.SaveChangesAsync(ct);
        await GovernanceRules.EnsureRootOwnerRemainsAsync(db, ct);
    }

    public async Task GrantAdministratorAsync(GovernanceActor actor, Guid userId, string? reason, CancellationToken ct = default)
    {
        var note = Reason(reason);
        var held = await GovernanceRules.HeldCodesAsync(db, actor.UserId, ct);
        RequireCode(held, GovernancePermissionCatalog.ManageAdministrators);
        if (userId == actor.UserId) throw SelfChange();

        var membership = await LoadAdminTargetAsync(actor, userId, ct);
        if (membership.Status != MembershipStatus.Active || membership.User.Status == UserStatus.Disabled)
        {
            throw new DomainValidationException("userId", "The user must be an active member of this company.");
        }
        if (membership.User.Role.IsSystem) throw new DomainValidationException("userId", "The user is already an Administrator.");

        var admin = await db.Roles.AsNoTracking().Where(r => r.IsSystem && r.IsActive).OrderBy(r => r.Name).FirstOrDefaultAsync(ct)
            ?? throw new DomainValidationException("userId", "The Administrator role is not available.");

        var before = new { roleId = membership.User.RoleId, role = membership.User.Role.Name, accessScope = membership.AccessScope.ToString() };
        membership.User.RoleId = admin.Id;
        await db.SaveChangesAsync(ct);
        await userAccess.EnsureCompanyWideForAdministratorAsync(new AccessActor(actor.UserId, false, false, actor.CorrelationId), userId, ct);

        Bump(membership, actor);
        AuditLog.Append(db, ToAccess(actor), AuditActions.GovernanceAdministratorGranted, AuditTargets.Membership, membership.Id, userId, note,
            before, new { roleId = admin.Id, role = admin.Name, accessScope = CompanyAccessScope.CompanyWide.ToString() });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAdministratorAsync(GovernanceActor actor, Guid userId, Guid replacementRoleId, string? reason, CancellationToken ct = default)
    {
        var note = Reason(reason);
        var held = await GovernanceRules.HeldCodesAsync(db, actor.UserId, ct);
        RequireCode(held, GovernancePermissionCatalog.ManageAdministrators);
        if (userId == actor.UserId) throw SelfChange();

        var membership = await LoadAdminTargetAsync(actor, userId, ct);
        if (!membership.User.Role.IsSystem) throw new DomainValidationException("userId", "The user is not an Administrator.");

        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == replacementRoleId, ct);
        if (role is null || role.IsSystem || !role.IsActive)
        {
            throw new DomainValidationException("replacementRoleId", "Select an active, non-system role.");
        }

        var before = new { roleId = membership.User.RoleId, role = membership.User.Role.Name };
        membership.User.RoleId = role.Id;
        Bump(membership, actor);
        AuditLog.Append(db, ToAccess(actor), AuditActions.GovernanceAdministratorRemoved, AuditTargets.Membership, membership.Id, userId, note,
            before, new { roleId = role.Id, role = role.Name });
        await db.SaveChangesAsync(ct);

        var companyId = db.CompanyContext.RequireCompanyId();
        var remaining = await db.UserCompanyMemberships.AsNoTracking().AnyAsync(m =>
            m.CompanyId == companyId && m.Status == MembershipStatus.Active && m.AccessScope == CompanyAccessScope.CompanyWide
            && m.User.Status != UserStatus.Disabled && m.User.Role.IsSystem && m.User.Role.IsActive, ct);
        if (!remaining)
        {
            throw new GovernanceRuleException("last_administrator_protected", "The company must keep at least one active company-wide Administrator.");
        }
    }

    // ---- helpers ----

    private async Task<UserCompanyMembership> LoadAdminTargetAsync(GovernanceActor actor, Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var membership = await db.UserCompanyMemberships.Include(m => m.User).ThenInclude(u => u.Role)
            .SingleOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId, ct)
            ?? throw new AccessTargetNotFoundException();
        if (await db.UserCompanyMemberships.AnyAsync(m => m.UserId == userId && m.CompanyId != companyId, ct))
        {
            throw new AccessManagementForbiddenException(GovernanceErrorCodes.SharedAccountProtected,
                "This user also belongs to another company and cannot be changed from here.");
        }
        await GovernanceRules.EnsureOperationalTargetAllowedAsync(db, actor.UserId, userId, ct);
        return membership;
    }

    private async Task<CompanyGovernanceMember> LoadActiveMemberAsync(Guid memberId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return await db.CompanyGovernanceMembers.Include(m => m.Membership)
            .SingleOrDefaultAsync(m => m.CompanyId == companyId && m.Id == memberId && m.Status == GovernanceMemberStatus.Active, ct)
            ?? throw new AccessTargetNotFoundException();
    }

    private async Task<List<string>> CodesOfAsync(CompanyGovernanceMember member, CancellationToken ct) =>
        await db.GovernanceMemberPermissions
            .Where(p => p.CompanyId == member.CompanyId && p.GovernanceMemberId == member.Id)
            .Select(p => p.GovernancePermission.Code)
            .OrderBy(c => c)
            .ToListAsync(ct);

    private async Task<Dictionary<string, GovernancePermission>> LoadCatalogueAsync(IReadOnlyList<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0) return [];
        var rows = await db.GovernancePermissions.AsNoTracking().Where(p => codes.Contains(p.Code)).ToListAsync(ct);
        return rows.ToDictionary(r => r.Code, StringComparer.Ordinal);
    }

    private static List<string> NormalizeCodes(IReadOnlyList<string>? codes)
    {
        var list = (codes ?? []).Select(c => c?.Trim() ?? "").Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var unknown = list.Where(c => GovernancePermissionCatalog.All.All(d => d.Code != c)).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainValidationException("permissionCodes", $"Unknown governance permission: {string.Join(", ", unknown)}.");
        }
        return list;
    }

    private static GovernanceMemberPermission NewGrant(CompanyGovernanceMember member, GovernancePermission permission, GovernanceActor actor, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), CompanyId = member.CompanyId, GovernanceMemberId = member.Id, GovernancePermissionId = permission.Id,
        GrantedAt = now, GrantedByUserId = actor.UserId,
    };

    private static void RequireCode(HashSet<string> held, string code)
    {
        if (!held.Contains(code))
        {
            throw new AccessManagementForbiddenException(GovernanceErrorCodes.PermissionRequired, $"You need the {code} governance permission.");
        }
    }

    /// <summary>R2: only codes the actor holds can be granted or revoked.</summary>
    private static void RequireHeld(HashSet<string> held, IReadOnlyList<string> codes)
    {
        var notHeld = codes.Where(c => !held.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (notHeld.Count > 0)
        {
            throw new AccessManagementForbiddenException(GovernanceErrorCodes.DelegationExceeded,
                $"You can only grant or revoke governance permissions you hold yourself. Not held: {string.Join(", ", notHeld)}.");
        }
    }

    private static AccessManagementForbiddenException SelfChange() =>
        new(GovernanceErrorCodes.SelfGovernanceChangeForbidden, "You cannot change your own governance membership or grants.");

    private static void RequireNotSelf(GovernanceActor actor, CompanyGovernanceMember member)
    {
        if (member.Membership.UserId == actor.UserId) throw SelfChange();
    }

    private static string Reason(string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null) throw new DomainValidationException("reason", "A reason is required.");
        if (text.Length > ReasonMax) throw new DomainValidationException("reason", $"The reason can have at most {ReasonMax} characters.");
        return text;
    }

    private static AccessActor ToAccess(GovernanceActor actor) => new(actor.UserId, false, false, actor.CorrelationId);

    private static void Bump(UserCompanyMembership m, GovernanceActor actor)
    {
        m.AccessVersion++;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        m.UpdatedByUserId = actor.UserId;
    }

    private static object Snap(CompanyGovernanceMember m, IEnumerable<string> codes) => new
    {
        id = m.Id, title = m.Title.ToString(), status = m.Status.ToString(), startAt = m.StartAt, endAt = m.EndAt, permissions = codes.ToList(),
    };
}
