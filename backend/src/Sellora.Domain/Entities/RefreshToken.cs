namespace Sellora.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 hash of the opaque token — the raw value is only ever returned to the client, never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The company this token is bound to (refresh never silently switches tenant). Null for tokens issued before multi-company.</summary>
    public Guid? CompanyId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
