using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A company-owned role (Phase 7, D1). <see cref="ScopeType"/>, <see cref="BranchId"/> and <see cref="CompanyId"/> are immutable after insert
/// (the service rejects changes). A Branch role may only hold branch-eligible permission codes (PermissionCatalog scope metadata); that rule
/// is a service invariant - SQL cannot see the catalogue. Exactly one active system (Administrator) role exists per company.
/// </summary>
public class Role : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    public RoleScope ScopeType { get; set; } = RoleScope.Company;

    /// <summary>Set iff <see cref="ScopeType"/> is Branch (CK_Roles_Scope). Same-company composite FK; navigation-less by design (phase-2 D3).</summary>
    public Guid? BranchId { get; set; }

    /// <summary>Administrator only. System roles are not editable/deletable and always hold every permission. Always Company-scoped.</summary>
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Legacy holders through the deprecated Users.RoleId column (read only by the compat path; evaluation uses <see cref="UserRoleAssignment"/>).</summary>
    public ICollection<User> Users { get; set; } = new List<User>();
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
