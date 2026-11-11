using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// Historical allocation of a company membership to a branch (Phase 5, arch §6.2). A row is written once and only closed once
/// (Status/EndAt/EndedByUserId/EndReason); <see cref="BranchId"/> and <see cref="IsPrimary"/> never change on a row - a primary
/// change closes the affected rows and inserts successors. Service invariants (>= 1 active row and exactly one active primary for
/// an Active AssignedBranches membership; none for CompanyWide) are not expressible in SQL and live in the access service.
/// </summary>
public class UserBranchAssignment : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    public Guid UserCompanyMembershipId { get; set; }
    public UserCompanyMembership Membership { get; set; } = null!;

    /// <summary>Same-company branch (composite FK); navigation-less by design (phase-2 D3).</summary>
    public Guid BranchId { get; set; }

    public bool IsPrimary { get; set; }
    public AssignmentStatus Status { get; set; } = AssignmentStatus.Active;

    /// <summary>Set to the write instant; allocations are never future-dated.</summary>
    public DateTimeOffset StartAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndAt { get; set; }

    public string? AssignmentReason { get; set; }
    public string? EndReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public Guid? EndedByUserId { get; set; }
}
