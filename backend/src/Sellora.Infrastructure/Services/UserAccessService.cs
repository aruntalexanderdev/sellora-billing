using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Membership and branch-allocation writes (Phase 5, design D1/D2/D8/D9/D15). The caller owns the transaction and the Phase H
/// advisory lock (AccessManagementScope), so every rule below runs against a consistent, re-read state. History is close-only:
/// a primary change closes the affected rows (first save) and inserts successors (second save) so the partial unique indexes
/// never see two primaries. Every change bumps <see cref="UserCompanyMembership.AccessVersion"/> and appends one audit event.
/// </summary>
public sealed class UserAccessService(SelloraDbContext db) : IUserAccessService
{
    private const int ReasonMax = 500;

    public async Task<UserBranchAssignment> AddAssignmentAsync(
        AccessActor actor, Guid userId, Guid branchId, bool isPrimary, string? reason, CancellationToken ct = default)
    {
        var m = await LoadTargetAsync(actor, userId, ct);
        RequireAssignable(m);
        var note = Reason(reason, required: false);
        var current = await LoadActiveAsync(m, ct);

        await RequireActiveBranchesAsync([branchId], "branchId", ct);
        if (current.Any(a => a.BranchId == branchId))
        {
            throw new DomainValidationException("branchId", "The user already has an active allocation at this branch.");
        }

        var makePrimary = isPrimary || current.Count == 0;
        var oldPrimary = current.SingleOrDefault(a => a.IsPrimary);
        var oldPrimarySnapshot = oldPrimary is null ? null : Snap(oldPrimary);

        var desired = current.Select(a => (a.BranchId, IsPrimary: !makePrimary && a.IsPrimary)).ToList();
        desired.Add((branchId, makePrimary));
        var created = await ApplyAsync(m, current, desired, note ?? "Primary branch changed", note, actor, ct);

        var row = created.Single(a => a.BranchId == branchId);
        Bump(m, actor);
        if (makePrimary && oldPrimary is not null)
        {
            AuditLog.Append(db, actor, AuditActions.BranchAssignmentPrimaryChanged, AuditTargets.BranchAssignment, row.Id, m.UserId, note, oldPrimarySnapshot, Snap(row));
        }
        else
        {
            AuditLog.Append(db, actor, AuditActions.BranchAssignmentCreated, AuditTargets.BranchAssignment, row.Id, m.UserId, note, null, Snap(row));
        }
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task EndAssignmentAsync(AccessActor actor, Guid userId, Guid assignmentId, string? reason, CancellationToken ct = default)
    {
        var m = await LoadTargetAsync(actor, userId, ct);
        var note = Reason(reason, required: true);
        var current = await LoadActiveAsync(m, ct);

        var target = current.SingleOrDefault(a => a.Id == assignmentId) ?? throw new AccessTargetNotFoundException();
        if (target.IsPrimary)
        {
            throw new DomainValidationException("assignmentId", "The primary allocation cannot be ended. Set another primary branch first.");
        }
        if (current.Count <= 1)
        {
            throw new DomainValidationException("assignmentId", "The last allocation cannot be ended. Switch the user to company-wide or revoke the membership instead.");
        }

        var before = Snap(target);
        var desired = current.Where(a => a.Id != assignmentId).Select(a => (a.BranchId, a.IsPrimary)).ToList();
        await ApplyAsync(m, current, desired, note, null, actor, ct);

        Bump(m, actor);
        AuditLog.Append(db, actor, AuditActions.BranchAssignmentEnded, AuditTargets.BranchAssignment, target.Id, m.UserId, note, before, Snap(target));
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPrimaryAsync(AccessActor actor, Guid userId, Guid branchId, string? reason, CancellationToken ct = default)
    {
        var m = await LoadTargetAsync(actor, userId, ct);
        RequireAssignable(m);
        var note = Reason(reason, required: false);
        var current = await LoadActiveAsync(m, ct);

        var target = current.SingleOrDefault(a => a.BranchId == branchId)
            ?? throw new DomainValidationException("branchId", "The user has no active allocation at this branch. Add it first.");
        if (target.IsPrimary)
        {
            throw new DomainValidationException("branchId", "This branch is already the primary branch.");
        }
        await RequireActiveBranchesAsync([branchId], "branchId", ct);

        var oldPrimary = current.SingleOrDefault(a => a.IsPrimary);
        var oldSnapshot = oldPrimary is null ? null : Snap(oldPrimary);
        var desired = current.Select(a => (a.BranchId, IsPrimary: a.BranchId == branchId)).ToList();
        var created = await ApplyAsync(m, current, desired, note ?? "Primary branch changed", note, actor, ct);

        var row = created.Single(a => a.BranchId == branchId);
        Bump(m, actor);
        AuditLog.Append(db, actor, AuditActions.BranchAssignmentPrimaryChanged, AuditTargets.BranchAssignment, row.Id, m.UserId, note, oldSnapshot, Snap(row));
        await db.SaveChangesAsync(ct);
    }

    public async Task SetScopeAsync(AccessActor actor, Guid userId, SetAccessScopeInput input, CancellationToken ct = default)
    {
        var m = await LoadTargetAsync(actor, userId, ct);
        if (m.Status == MembershipStatus.Revoked)
        {
            throw new DomainValidationException("accessScope", "A revoked membership has no access scope. Reactivate it through the status endpoint.");
        }
        var note = Reason(input.Reason, required: true);
        if (input.AccessScope == m.AccessScope)
        {
            throw new DomainValidationException("accessScope", $"The membership is already {input.AccessScope}. Use the allocation endpoints to change branches.");
        }

        var current = await LoadActiveAsync(m, ct);
        var desired = await DesiredForScopeAsync(m, input.AccessScope, input.BranchIds, input.PrimaryBranchId, ct);
        var before = State(m, current);

        m.AccessScope = input.AccessScope;
        var created = await ApplyAsync(m, current, desired, note, note, actor, ct);
        Bump(m, actor);
        AuditLog.Append(db, actor, AuditActions.MembershipScopeChanged, AuditTargets.Membership, m.Id, m.UserId, note, before, State(m, Remaining(current, created)));
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(AccessActor actor, Guid userId, SetMembershipStatusInput input, CancellationToken ct = default)
    {
        var m = await LoadTargetAsync(actor, userId, ct);
        var note = Reason(input.Reason, required: true);
        if (input.Status == m.Status)
        {
            throw new DomainValidationException("status", $"The membership is already {input.Status}.");
        }

        var current = await LoadActiveAsync(m, ct);
        var before = State(m, current);
        List<(Guid BranchId, bool IsPrimary)> desired;

        if (input.Status == MembershipStatus.Revoked)
        {
            desired = [];
        }
        else if (m.Status == MembershipStatus.Revoked)
        {
            // Revoked -> Active: access must be decided again.
            if (input.Status != MembershipStatus.Active)
            {
                throw new DomainValidationException("status", "A revoked membership can only be reactivated (Active).");
            }
            var scope = input.AccessScope ?? throw new DomainValidationException("accessScope", "Choose the access scope for the reactivated membership.");
            desired = await DesiredForScopeAsync(m, scope, input.BranchIds, input.PrimaryBranchId, ct);
            m.AccessScope = scope;
        }
        else
        {
            // Active <-> Suspended keeps the allocations; an Active AssignedBranches membership needs its primary.
            desired = current.Select(a => (a.BranchId, a.IsPrimary)).ToList();
            if (input.Status == MembershipStatus.Active && m.AccessScope == CompanyAccessScope.AssignedBranches && !current.Any(a => a.IsPrimary))
            {
                throw new DomainValidationException("status", "The membership has no active primary branch. Set the access scope with branches first.");
            }
        }

        m.Status = input.Status;
        var created = await ApplyAsync(m, current, desired, note, note, actor, ct);
        Bump(m, actor);
        AuditLog.Append(db, actor, AuditActions.MembershipStatusChanged, AuditTargets.Membership, m.Id, m.UserId, note, before, State(m, Remaining(current, created)));

        // Phase 6 D10: revoking a membership ends its governance member (grants removed, audited, same transaction);
        // suspending or revoking an Owner must leave a root owner (R4).
        var govMember = m.Status == MembershipStatus.Active
            ? null
            : await db.CompanyGovernanceMembers.SingleOrDefaultAsync(
                g => g.CompanyId == m.CompanyId && g.UserCompanyMembershipId == m.Id && g.Status == GovernanceMemberStatus.Active, ct);
        if (govMember is not null && m.Status == MembershipStatus.Revoked)
        {
            var grants = await db.GovernanceMemberPermissions
                .Where(p => p.CompanyId == m.CompanyId && p.GovernanceMemberId == govMember.Id)
                .Include(p => p.GovernancePermission).ToListAsync(ct);
            var codes = grants.Select(g => g.GovernancePermission.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var ended = DateTimeOffset.UtcNow;
            db.GovernanceMemberPermissions.RemoveRange(grants);
            govMember.Status = GovernanceMemberStatus.Ended;
            govMember.EndAt = ended < govMember.StartAt ? govMember.StartAt : ended;
            govMember.EndReason = note;
            govMember.EndedByUserId = actor.UserId;
            AuditLog.Append(db, actor, AuditActions.GovernanceMemberEnded, AuditTargets.GovernanceMember, govMember.Id, m.UserId, note,
                new { title = govMember.Title.ToString(), status = "Active", permissions = codes },
                new { title = govMember.Title.ToString(), status = "Ended", permissions = Array.Empty<string>() });
        }
        await db.SaveChangesAsync(ct);
        if (govMember is { Title: GovernanceTitle.Owner })
        {
            await GovernanceRules.EnsureRootOwnerRemainsAsync(db, ct);
        }
    }

    public async Task ReassignAsync(AccessActor actor, Guid userId, ReassignInput input, CancellationToken ct = default)
    {
        var m = await LoadTargetAsync(actor, userId, ct);
        RequireAssignable(m);
        var note = Reason(input.Reason, required: true);
        var current = await LoadActiveAsync(m, ct);

        var fromIds = (input.FromAssignmentIds ?? []).Distinct().ToList();
        if (fromIds.Count == 0)
        {
            throw new DomainValidationException("fromAssignmentIds", "Select at least one allocation to move.");
        }
        var from = current.Where(a => fromIds.Contains(a.Id)).ToList();
        if (from.Count != fromIds.Count)
        {
            throw new DomainValidationException("fromAssignmentIds", "Every allocation to move must be an active allocation of this user.");
        }
        if (from.Any(a => a.BranchId == input.ToBranchId))
        {
            throw new DomainValidationException("toBranchId", "The destination branch is one of the allocations being moved.");
        }
        if (current.Any(a => a.BranchId == input.ToBranchId))
        {
            throw new DomainValidationException("toBranchId", "The user already has an active allocation at the destination branch.");
        }
        if (!input.MakePrimary && from.Any(a => a.IsPrimary))
        {
            throw new DomainValidationException("makePrimary", "The primary allocation is being moved: make the destination the primary branch.");
        }
        await RequireActiveBranchesAsync([input.ToBranchId], "toBranchId", ct);

        var before = State(m, current);
        var desired = current.Where(a => !fromIds.Contains(a.Id))
            .Select(a => (a.BranchId, IsPrimary: !input.MakePrimary && a.IsPrimary)).ToList();
        desired.Add((input.ToBranchId, input.MakePrimary));
        var created = await ApplyAsync(m, current, desired, note, note, actor, ct);

        Bump(m, actor);
        AuditLog.Append(db, actor, AuditActions.AccessReassigned, AuditTargets.Membership, m.Id, m.UserId, note, before, State(m, Remaining(current, created)));
        await db.SaveChangesAsync(ct);
    }

    public async Task InitializeNewMembershipAsync(
        AccessActor actor, UserCompanyMembership membership, bool isAdministratorRole, AccessSelection? selection, CancellationToken ct = default)
    {
        var explicitSelection = selection is not null
            && (selection.AccessScope is not null || selection.BranchIds is { Count: > 0 } || selection.PrimaryBranchId is not null);
        if (explicitSelection)
        {
            await RequireManagerAsync(actor, db.CompanyContext.RequireCompanyId(), ct);
        }

        var scope = selection?.AccessScope ?? (isAdministratorRole ? CompanyAccessScope.CompanyWide : CompanyAccessScope.AssignedBranches);
        if (isAdministratorRole && scope != CompanyAccessScope.CompanyWide)
        {
            throw new DomainValidationException("accessScope", "Administrator accounts are always company-wide.");
        }

        var desired = new List<(Guid BranchId, bool IsPrimary)>();
        if (scope == CompanyAccessScope.CompanyWide)
        {
            if (selection?.BranchIds is { Count: > 0 } || selection?.PrimaryBranchId is not null)
            {
                throw new DomainValidationException("branchIds", "Branches cannot be set for a company-wide user.");
            }
        }
        else if (selection?.BranchIds is { Count: > 0 })
        {
            desired = await BuildDesiredAsync(selection.BranchIds, selection.PrimaryBranchId, ct);
        }
        else
        {
            if (selection?.PrimaryBranchId is not null)
            {
                throw new DomainValidationException("branchIds", "Select the branches the primary branch belongs to.");
            }
            // Default (O1): one primary allocation at the creator's own working branch - never wider than the creator's reach.
            var working = await BranchRules.ResolveWorkingBranchAsync(db, ct);
            desired.Add((working.Id, true));
        }

        membership.AccessScope = scope;
        var now = DateTimeOffset.UtcNow;
        var created = desired.Select(d => NewRow(membership, d.BranchId, d.IsPrimary, "Initial allocation", actor, now)).ToList();
        db.UserBranchAssignments.AddRange(created);

        AuditLog.Append(db, actor, AuditActions.MembershipCreated, AuditTargets.Membership, membership.Id, membership.UserId, null, null, State(membership, created));
        foreach (var row in created)
        {
            AuditLog.Append(db, actor, AuditActions.BranchAssignmentCreated, AuditTargets.BranchAssignment, row.Id, membership.UserId, row.AssignmentReason, null, Snap(row));
        }
    }

    public async Task EnsureCompanyWideForAdministratorAsync(AccessActor actor, Guid userId, CancellationToken ct = default)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var m = await db.UserCompanyMemberships.SingleOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId, ct);
        if (m is null || m.AccessScope == CompanyAccessScope.CompanyWide) return;

        const string note = "Promoted to Administrator";
        var current = await LoadActiveAsync(m, ct);
        var before = State(m, current);
        m.AccessScope = CompanyAccessScope.CompanyWide;
        var created = await ApplyAsync(m, current, [], note, note, actor, ct);
        Bump(m, actor);
        AuditLog.Append(db, actor, AuditActions.MembershipScopeChanged, AuditTargets.Membership, m.Id, m.UserId, note, before, State(m, Remaining(current, created)));
        await db.SaveChangesAsync(ct);
    }

    // ---- guards ----

    private async Task RequireManagerAsync(AccessActor actor, Guid companyId, CancellationToken ct)
    {
        if (!actor.CanManageBranchAccess)
        {
            throw new AccessManagementForbiddenException(AccessErrorCodes.AccessManagementForbidden, "You are not allowed to manage branch access.");
        }
        // Branch-restricted actors (branch admins) cannot manage access: the actor's own scope is re-read inside the lock.
        var companyWide = await db.UserCompanyMemberships.AnyAsync(m =>
            m.CompanyId == companyId && m.UserId == actor.UserId && m.Status == MembershipStatus.Active && m.AccessScope == CompanyAccessScope.CompanyWide, ct);
        if (!companyWide)
        {
            throw new AccessManagementForbiddenException(AccessErrorCodes.AccessManagementForbidden, "Only company-wide users can manage branch access.");
        }
    }

    private async Task<UserCompanyMembership> LoadTargetAsync(AccessActor actor, Guid userId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        await RequireManagerAsync(actor, companyId, ct);

        var m = await db.UserCompanyMemberships
            .Include(x => x.User).ThenInclude(u => u.Role)
            .SingleOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == userId, ct)
            ?? throw new AccessTargetNotFoundException();

        if (userId == actor.UserId)
        {
            throw new AccessManagementForbiddenException(AccessErrorCodes.SelfAccessChangeForbidden, "You cannot change your own access.");
        }
        if (m.User.Role.IsSystem && !actor.IsAdministrator)
        {
            throw new AccessManagementForbiddenException(AccessErrorCodes.AccessManagementForbidden, "Only Administrators can change Administrator accounts.");
        }
        // Phase 6 R5: platform administrators and governance members are protected from ordinary user administration.
        await GovernanceRules.EnsureOperationalTargetAllowedAsync(db, actor.UserId, userId, ct);
        return m;
    }

    private static void RequireAssignable(UserCompanyMembership m)
    {
        if (m.Status == MembershipStatus.Revoked)
        {
            throw new DomainValidationException("status", "The membership is revoked. Reactivate it through the status endpoint first.");
        }
        if (m.AccessScope != CompanyAccessScope.AssignedBranches)
        {
            throw new DomainValidationException("accessScope", "The user is company-wide. Switch the access scope to AssignedBranches first.");
        }
    }

    private async Task<List<(Guid BranchId, bool IsPrimary)>> DesiredForScopeAsync(
        UserCompanyMembership m, CompanyAccessScope scope, IReadOnlyList<Guid>? branchIds, Guid? primaryBranchId, CancellationToken ct)
    {
        if (scope == CompanyAccessScope.CompanyWide) return [];
        if (m.User.Role.IsSystem)
        {
            throw new DomainValidationException("accessScope", "Administrator accounts are always company-wide.");
        }
        return await BuildDesiredAsync(branchIds, primaryBranchId, ct);
    }

    private async Task<List<(Guid BranchId, bool IsPrimary)>> BuildDesiredAsync(IReadOnlyList<Guid>? branchIds, Guid? primaryBranchId, CancellationToken ct)
    {
        if (branchIds is not { Count: > 0 })
        {
            throw new DomainValidationException("branchIds", "Select at least one branch.");
        }
        var ids = branchIds.Distinct().ToList();
        if (primaryBranchId is null)
        {
            throw new DomainValidationException("primaryBranchId", "Select the primary branch.");
        }
        if (!ids.Contains(primaryBranchId.Value))
        {
            throw new DomainValidationException("primaryBranchId", "The primary branch must be one of the selected branches.");
        }
        await RequireActiveBranchesAsync(ids, "branchIds", ct);
        return ids.Select(id => (id, id == primaryBranchId.Value)).ToList();
    }

    private async Task RequireActiveBranchesAsync(IReadOnlyList<Guid> ids, string field, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var found = await db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.IsActive && ids.Contains(b.Id))
            .Select(b => b.Id).ToListAsync(ct);
        if (found.Count != ids.Distinct().Count())
        {
            throw new DomainValidationException(field, "Select valid, active branches of this company.");
        }
    }

    private static string? Reason(string? reason, bool required)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null && required)
        {
            throw new DomainValidationException("reason", "A reason is required.");
        }
        if (text is { Length: > ReasonMax })
        {
            throw new DomainValidationException("reason", $"The reason can have at most {ReasonMax} characters.");
        }
        return text;
    }

    // ---- allocation mechanics ----

    private async Task<List<UserBranchAssignment>> LoadActiveAsync(UserCompanyMembership m, CancellationToken ct) =>
        await db.UserBranchAssignments
            .Where(a => a.CompanyId == m.CompanyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active)
            .ToListAsync(ct);

    /// <summary>Closes every active row that is not in <paramref name="desired"/> (first save), then inserts the missing ones
    /// (staged; the caller's save writes them with the audit row). Returns the inserted rows.</summary>
    private async Task<List<UserBranchAssignment>> ApplyAsync(
        UserCompanyMembership m, List<UserBranchAssignment> current, IReadOnlyList<(Guid BranchId, bool IsPrimary)> desired,
        string? endReason, string? createReason, AccessActor actor, CancellationToken ct)
    {
        var assigned = m.AccessScope == CompanyAccessScope.AssignedBranches && m.Status != MembershipStatus.Revoked;
        if (desired.Select(d => d.BranchId).Distinct().Count() != desired.Count
            || desired.Count(d => d.IsPrimary) > 1
            || (assigned && desired.Count > 0 && desired.Count(d => d.IsPrimary) != 1)
            || (m.AccessScope == CompanyAccessScope.CompanyWide && desired.Count > 0))
        {
            throw new InvalidOperationException("Branch allocation invariant violated (one primary, distinct branches, none for company-wide).");
        }

        var now = DateTimeOffset.UtcNow;
        var closing = current.Where(c => !desired.Contains((c.BranchId, c.IsPrimary))).ToList();
        foreach (var row in closing)
        {
            row.Status = AssignmentStatus.Ended;
            row.EndAt = now < row.StartAt ? row.StartAt : now;
            row.EndedByUserId = actor.UserId;
            row.EndReason = endReason;
        }
        if (closing.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var created = desired
            .Where(d => !current.Any(c => c.Status == AssignmentStatus.Active && c.BranchId == d.BranchId && c.IsPrimary == d.IsPrimary))
            .Select(d => NewRow(m, d.BranchId, d.IsPrimary, createReason, actor, now))
            .ToList();
        db.UserBranchAssignments.AddRange(created);
        return created;
    }

    private static UserBranchAssignment NewRow(UserCompanyMembership m, Guid branchId, bool isPrimary, string? reason, AccessActor actor, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = m.CompanyId,
        UserCompanyMembershipId = m.Id,
        BranchId = branchId,
        IsPrimary = isPrimary,
        Status = AssignmentStatus.Active,
        StartAt = now,
        AssignmentReason = reason,
        CreatedAt = now,
        CreatedByUserId = actor.UserId,
    };

    private static void Bump(UserCompanyMembership m, AccessActor actor)
    {
        m.AccessVersion++;
        m.UpdatedAt = DateTimeOffset.UtcNow;
        m.UpdatedByUserId = actor.UserId;
    }

    private static IEnumerable<UserBranchAssignment> Remaining(List<UserBranchAssignment> current, List<UserBranchAssignment> created) =>
        current.Where(a => a.Status == AssignmentStatus.Active).Concat(created);

    // ---- audit snapshots (no secrets) ----

    private static object Snap(UserBranchAssignment a) => new
    {
        id = a.Id,
        branchId = a.BranchId,
        isPrimary = a.IsPrimary,
        status = a.Status.ToString(),
        startAt = a.StartAt,
        endAt = a.EndAt,
    };

    private static object State(UserCompanyMembership m, IEnumerable<UserBranchAssignment> active) => new
    {
        status = m.Status.ToString(),
        accessScope = m.AccessScope.ToString(),
        accessVersion = m.AccessVersion,
        assignments = active.OrderBy(a => a.BranchId).Select(a => new { id = a.Id, branchId = a.BranchId, isPrimary = a.IsPrimary }).ToList(),
    };
}
