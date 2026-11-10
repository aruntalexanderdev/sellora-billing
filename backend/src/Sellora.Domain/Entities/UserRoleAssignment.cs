using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// Assignment of a role to a company membership (Phase 7, D3; replaces Users.RoleId for evaluation). History rule as
/// <see cref="UserBranchAssignment"/>: a row is written once and closed once (Status/EndAt/EndedByUserId/EndReason); the role and branch of a
/// row never change. <see cref="ScopeType"/> and <see cref="BranchId"/> mirror the role's (composite FKs prove it). Service invariants not
/// expressible in SQL: branch roles only for AssignedBranches members allocated to the branch, last-Administrator rule, escalation rules.
/// </summary>
public class UserRoleAssignment : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    public Guid UserCompanyMembershipId { get; set; }
    public UserCompanyMembership Membership { get; set; } = null!;

    /// <summary>Same-company role whose ScopeType equals <see cref="ScopeType"/> (FK_URA_Role) and whose BranchId equals <see cref="BranchId"/> (FK_URA_RoleBranch).</summary>
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    public RoleScope ScopeType { get; set; } = RoleScope.Company;

    /// <summary>Set iff <see cref="ScopeType"/> is Branch (CK_URA_Scope_Branch); navigation-less (phase-2 D3).</summary>
    public Guid? BranchId { get; set; }

    public AssignmentStatus Status { get; set; } = AssignmentStatus.Active;
    public DateTimeOffset StartAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndAt { get; set; }

    public string? AssignmentReason { get; set; }
    public string? EndReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public Guid? EndedByUserId { get; set; }
}
