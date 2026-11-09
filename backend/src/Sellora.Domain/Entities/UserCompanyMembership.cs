namespace Sellora.Domain.Entities;

/// <summary>Explicit membership of a user in a company. Never inferred; the company context is only established from an Active membership.</summary>
public class UserCompanyMembership
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    public CompanyAccessScope AccessScope { get; set; } = CompanyAccessScope.CompanyWide;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
