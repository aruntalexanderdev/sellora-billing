namespace Sellora.Domain.Entities;

public class User
{
    public Guid Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
    public string? PhoneNumber { get; set; }
    public required string PasswordHash { get; set; }

    /// <summary>Deprecated (Phase 7, D4): nullable, no longer used for authorization or written for new users; dropped in a later release. Evaluation reads <see cref="UserRoleAssignment"/>.</summary>
    public Guid? RoleId { get; set; }
    public Role Role { get; set; } = null!;

    public UserStatus Status { get; set; } = UserStatus.Active;
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
