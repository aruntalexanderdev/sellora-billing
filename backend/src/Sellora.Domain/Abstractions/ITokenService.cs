using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

public interface ITokenService
{
    (string AccessToken, DateTimeOffset ExpiresAt) CreateAccessToken(User user, Guid companyId, IReadOnlyCollection<string> permissionCodes);

    /// <summary>Generates a new opaque refresh token and its stored hash. The raw value is returned to the client only.</summary>
    (string RawToken, string TokenHash, DateTimeOffset ExpiresAt) CreateRefreshToken();

    string HashRefreshToken(string rawToken);
}
