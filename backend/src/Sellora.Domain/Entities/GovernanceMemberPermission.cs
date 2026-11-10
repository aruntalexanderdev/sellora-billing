using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>One governance code granted to a member (Phase 6 D7). Revocation deletes the row (history = audit before/after).</summary>
public class GovernanceMemberPermission : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    public Guid GovernanceMemberId { get; set; }
    public CompanyGovernanceMember Member { get; set; } = null!;

    public Guid GovernancePermissionId { get; set; }
    public GovernancePermission GovernancePermission { get; set; } = null!;

    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? GrantedByUserId { get; set; }
}
