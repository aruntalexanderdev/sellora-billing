using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>Governance title (Phase 6 D7). A title only labels the member; permissions come from <see cref="GovernanceMemberPermission"/> grants.</summary>
public enum GovernanceTitle
{
    Owner,
    BoardMember,
    Director,
    DelegatedAdministrator,
}

public enum GovernanceMemberStatus
{
    Active,
    Ended,
}

/// <summary>
/// A person with a governance role in a company (Phase 6 D7, arch §5.2). Attached to the company membership (composite FK, same
/// company by construction). Ending closes the row; re-adding inserts a new one. At most one Active row per membership (partial
/// unique index). Grants nothing by itself.
/// </summary>
public class CompanyGovernanceMember : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    public Guid UserCompanyMembershipId { get; set; }
    public UserCompanyMembership Membership { get; set; } = null!;

    public GovernanceTitle Title { get; set; }
    public GovernanceMemberStatus Status { get; set; } = GovernanceMemberStatus.Active;

    public DateTimeOffset StartAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndAt { get; set; }
    public string? EndReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public Guid? EndedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public ICollection<GovernanceMemberPermission> Permissions { get; set; } = new List<GovernanceMemberPermission>();
}
