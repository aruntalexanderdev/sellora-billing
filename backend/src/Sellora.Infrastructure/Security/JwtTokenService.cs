using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Security;

public class JwtTokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    // Phase 7: the token carries identity and company only. Permissions are evaluated server-side per request (permissionCodes is ignored, kept for source compatibility).
    public (string AccessToken, DateTimeOffset ExpiresAt) CreateAccessToken(User user, Guid companyId, IReadOnlyCollection<string>? permissionCodes = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, $"{user.FirstName} {user.LastName}"),
            new(AuthClaimTypes.CompanyId, companyId.ToString()),
        };

        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials
        );

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public (string AccessToken, DateTimeOffset ExpiresAt) CreatePlatformAccessToken(User user, TimeSpan lifetime)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, $"{user.FirstName} {user.LastName}"),
            new(AuthClaimTypes.SessionKind, AuthClaimTypes.PlatformSession),
        };
        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey));
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public (string RawToken, string TokenHash, DateTimeOffset ExpiresAt) CreateRefreshToken()
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var expiresAt = DateTimeOffset.UtcNow.AddDays(_options.RefreshTokenDays);
        return (rawToken, HashRefreshToken(rawToken), expiresAt);
    }

    public string HashRefreshToken(string rawToken) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
}
