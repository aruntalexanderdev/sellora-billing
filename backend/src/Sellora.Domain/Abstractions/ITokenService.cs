using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

public interface ITokenService
{
    (string AccessToken, DateTimeOffset ExpiresAt) CreateAccessToken(User user, Guid companyId, IReadOnlyCollection<string>? permissionCodes = null);

    /// <summary>Platform session token (Phase 6 D2): sub/email/name and <c>sellora_session=platform</c>; no company_id, no permission claims.</summary>
    (string AccessToken, DateTimeOffset ExpiresAt) CreatePlatformAccessToken(User user, TimeSpan lifetime);

    /// <summary>Generates a new opaque refresh token and its stored hash. The raw value is returned to the client only.</summary>
    (string RawToken, string TokenHash, DateTimeOffset ExpiresAt) CreateRefreshToken();

    string HashRefreshToken(string rawToken);
}
