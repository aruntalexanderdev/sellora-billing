using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Role definition writes (Phase 7, D1/D9/D10 R1-R3). Scope fields (CompanyId, ScopeType, BranchId) are immutable after insert. A branch role holds
/// only branch-eligible codes. Codes granted must be held by the actor at the role's scope (Administrator: all). The caller owns the transaction
/// and advisory lock (AccessManagementScope).
/// </summary>
public sealed class RoleService(SelloraDbContext db) : IRoleService
{
    private static readonly HashSet<string> ValidCodes = PermissionCatalog.All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

    public async Task<Role> CreateAsync(RoleActor actor, CreateRoleInput input, CancellationToken ct = default)
    {
        var scope = input.ScopeType ?? RoleScope.Company;
        var branchId = await ValidateScopeAsync(scope, input.BranchId, ct);
        await RequireManageAsync(actor, scope, branchId, "Roles.Create", ct);

        var name = ValidateName(input.Name);
        var codes = ValidateCodes(input.PermissionCodes, scope);
        await RequireUniqueNameAsync(name, scope, branchId, null, ct);
        if (RoleRules.NotHeld(actor, scope, branchId, codes) is { Count: > 0 } notHeld)
        {
            throw new DomainValidationException("permissionCodes", RoleRules.NotHeldMessage(notHeld));
        }

        var role = new Role
        {
            Id = Guid.NewGuid(),
            CompanyId = db.CompanyContext.RequireCompanyId(),
            Name = name,
            Description = Clean(input.Description),
            ScopeType = scope,
            BranchId = branchId,
            IsSystem = false,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = actor.UserId,
        };
        await AttachPermissionsAsync(role, codes, ct);
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task<Role> UpdateAsync(RoleActor actor, Guid id, UpdateRoleInput input, CancellationToken ct = default)
    {
        var role = await LoadVisibleAsync(actor, id, ct);
        await RequireManageAsync(actor, role.ScopeType, role.BranchId, "Roles.Edit", ct);
        if (role.IsSystem)
        {
            throw new RoleRuleException(RoleErrorCodes.SystemRoleProtected, "The Administrator role is a system role and cannot be modified.");
        }
        if (input.ScopeType is { } s && s != role.ScopeType)
        {
            throw new DomainValidationException("scopeType", "The scope of a role cannot be changed.");
        }
        if (input.BranchId is { } b && b != role.BranchId)
        {
            throw new DomainValidationException("branchId", "The branch of a role cannot be changed.");
        }

        var name = ValidateName(input.Name);
        var codes = ValidateCodes(input.PermissionCodes, role.ScopeType);
        await RequireUniqueNameAsync(name, role.ScopeType, role.BranchId, id, ct);

        // Only codes being added must be held by the actor; keeping or removing existing ones is allowed (R1).
        var existing = role.RolePermissions.Select(rp => rp.Permission.Code).ToHashSet(StringComparer.Ordinal);
        if (RoleRules.NotHeld(actor, role.ScopeType, role.BranchId, codes.Except(existing)) is { Count: > 0 } notHeld)
        {
            throw new DomainValidationException("permissionCodes", RoleRules.NotHeldMessage(notHeld));
        }

        var changed = !existing.SetEquals(codes);
        role.Name = name;
        role.Description = Clean(input.Description);
        role.UpdatedAt = DateTimeOffset.UtcNow;
        role.UpdatedByUserId = actor.UserId;
        if (changed)
        {
            role.RolePermissions.Clear();
            await AttachPermissionsAsync(role, codes, ct);
        }
        await db.SaveChangesAsync(ct);
        if (changed)
        {
            await RoleRules.BumpHoldersAsync(db, role.Id, actor.UserId, ct);
            await db.SaveChangesAsync(ct);
        }
        return role;
    }

    public async Task<Role> DuplicateAsync(RoleActor actor, Guid id, CancellationToken ct = default)
    {
        var source = await LoadVisibleAsync(actor, id, ct);
        await RequireManageAsync(actor, source.ScopeType, source.BranchId, "Roles.Duplicate", ct);

        var sourceCodes = source.IsSystem ? RoleRules.AllCodes.ToList() : source.RolePermissions.Select(rp => rp.Permission.Code).ToList();
        if (RoleRules.NotHeld(actor, source.ScopeType, source.BranchId, sourceCodes) is { Count: > 0 } notHeld)
        {
            throw RoleRules.Forbidden(RoleErrorCodes.RoleEscalationForbidden, $"You cannot duplicate this role. {RoleRules.NotHeldMessage(notHeld)}");
        }

        var baseName = $"{source.Name} copy";
        var name = baseName;
        var suffix = 2;
        while (await NameTakenAsync(name, source.ScopeType, source.BranchId, null, ct))
        {
            name = $"{baseName} {suffix++}";
        }

        var copy = new Role
        {
            Id = Guid.NewGuid(),
            CompanyId = source.CompanyId,
            Name = name,
            Description = source.Description,
            ScopeType = source.ScopeType,
            BranchId = source.BranchId,
            IsSystem = false,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = actor.UserId,
        };
        await AttachPermissionsAsync(copy, sourceCodes, ct);
        db.Roles.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public async Task SetActiveAsync(RoleActor actor, Guid id, bool isActive, CancellationToken ct = default)
    {
        var role = await LoadVisibleAsync(actor, id, ct);
        await RequireManageAsync(actor, role.ScopeType, role.BranchId, "Roles.Disable", ct);
        if (role.IsSystem)
        {
            throw new RoleRuleException(RoleErrorCodes.SystemRoleProtected, "The Administrator role is a system role and cannot be disabled.");
        }
        if (role.IsActive == isActive) return;

        if (!isActive)
        {
            var companyId = db.CompanyContext.RequireCompanyId();
            var holders = await db.UserRoleAssignments.CountAsync(a =>
                a.CompanyId == companyId && a.RoleId == id && a.Status == AssignmentStatus.Active
                && db.UserCompanyMemberships.Any(m => m.Id == a.UserCompanyMembershipId && m.Status != MembershipStatus.Revoked
                    && m.User.Status != UserStatus.Disabled), ct);
            if (holders > 0)
            {
                throw new RoleRuleException("role_in_use", $"This role is assigned to {holders} active user(s). Reassign them to another role first.");
            }
        }

        role.IsActive = isActive;
        role.UpdatedAt = DateTimeOffset.UtcNow;
        role.UpdatedByUserId = actor.UserId;
        await db.SaveChangesAsync(ct);
        // Reactivation (or deactivation with only disabled holders) changes the effective set of remaining holders.
        await RoleRules.BumpHoldersAsync(db, role.Id, actor.UserId, ct);
        await db.SaveChangesAsync(ct);
    }

    // ---- helpers ----

    /// <summary>Role of this company the actor may see; otherwise 404 (others' branch roles, unknown ids, no view right).</summary>
    private async Task<Role> LoadVisibleAsync(RoleActor actor, Guid id, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null || !await RoleRules.CanViewAsync(db, actor, role.ScopeType, role.BranchId, ct))
        {
            throw new AccessTargetNotFoundException();
        }
        return role;
    }

    private async Task RequireManageAsync(RoleActor actor, RoleScope scope, Guid? branchId, string companyCode, CancellationToken ct)
    {
        if (scope == RoleScope.Branch && branchId is { } b && !await RoleRules.ReachesAsync(db, actor, b, ct))
        {
            throw new AccessTargetNotFoundException();
        }
        if (!await RoleRules.CanManageAsync(db, actor, scope, branchId, companyCode, ct))
        {
            throw RoleRules.Forbidden(RoleErrorCodes.RoleManagementForbidden, scope == RoleScope.Company
                ? "Only company-wide users holding the permission can manage company roles."
                : "You are not allowed to manage roles of this branch.");
        }
    }

    private async Task<Guid?> ValidateScopeAsync(RoleScope scope, Guid? branchId, CancellationToken ct)
    {
        if (scope == RoleScope.Company)
        {
            if (branchId is not null) throw new DomainValidationException("branchId", "A company role has no branch.");
            return null;
        }
        if (branchId is not { } b) throw new DomainValidationException("branchId", "Select the branch of the role.");
        var companyId = db.CompanyContext.RequireCompanyId();
        if (!await db.Branches.AnyAsync(x => x.Id == b && x.CompanyId == companyId && x.IsActive, ct))
        {
            throw new DomainValidationException("branchId", "Select a valid, active branch of this company.");
        }
        return b;
    }

    private static string ValidateName(string? raw)
    {
        var name = (raw ?? string.Empty).Trim();
        if (name.Length is 0 or > 80) throw new DomainValidationException("name", "Enter a role name of up to 80 characters.");
        return name;
    }

    private static List<string> ValidateCodes(IReadOnlyList<string>? raw, RoleScope scope)
    {
        var codes = (raw ?? []).Select(c => c?.Trim() ?? "").Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var unknown = codes.Where(c => !ValidCodes.Contains(c)).ToList();
        if (unknown.Count > 0) throw new DomainValidationException("permissionCodes", $"Unknown permission code(s): {string.Join(", ", unknown)}");
        if (scope == RoleScope.Branch && codes.Where(c => !PermissionCatalog.IsBranchEligible(c)).OrderBy(c => c, StringComparer.Ordinal).ToList() is { Count: > 0 } bad)
        {
            throw new DomainValidationException("permissionCodes", $"A branch role cannot hold company-wide permissions: {string.Join(", ", bad)}.");
        }
        return codes;
    }

    private Task<bool> NameTakenAsync(string name, RoleScope scope, Guid? branchId, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLower();
        var companyId = db.CompanyContext.RequireCompanyId();
        return db.Roles.AnyAsync(r => r.CompanyId == companyId && r.ScopeType == scope && r.BranchId == branchId
            && (exceptId == null || r.Id != exceptId) && r.Name.ToLower() == lower, ct);
    }

    private async Task RequireUniqueNameAsync(string name, RoleScope scope, Guid? branchId, Guid? exceptId, CancellationToken ct)
    {
        if (await NameTakenAsync(name, scope, branchId, exceptId, ct))
        {
            throw new DomainValidationException("name", "A role with this name already exists.");
        }
    }

    private async Task AttachPermissionsAsync(Role role, IEnumerable<string> codes, CancellationToken ct)
    {
        var codeSet = codes.ToHashSet(StringComparer.Ordinal);
        if (codeSet.Count == 0) return;
        var permissions = await db.Permissions.Where(p => codeSet.Contains(p.Code)).ToListAsync(ct);
        foreach (var permission in permissions)
        {
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id, Permission = permission });
        }
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
