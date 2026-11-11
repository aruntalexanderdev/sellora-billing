namespace Sellora.Domain.Entities;

/// <summary>Lifecycle of a <see cref="PlatformAdministrator"/> row.</summary>
public enum PlatformAdminStatus
{
    Active,
    Revoked,
}

/// <summary>
/// Platform operator, outside company RBAC (Phase 6 D1). One row per user (the user id is the key). Not company-owned and not
/// query-filtered: a platform session has no company context. Granted only by the ops script (owner decision O3), revoked by the
/// platform API. Active &lt;=&gt; RevokedAt is NULL (CHECK).
/// </summary>
public class PlatformAdministrator
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public PlatformAdminStatus Status { get; set; } = PlatformAdminStatus.Active;

    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? GrantedByUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? Reason { get; set; }
}
