using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Auth;

namespace Sellora.Api.Contracts;

public record LoginRequest(
    [Required] string UsernameOrEmail,
    [Required] string Password
);

public record RefreshRequest(
    [Required] string RefreshToken
);

public record UserSummaryDto(
    Guid Id,
    string FirstName,
    string LastName,
    string Username,
    string Email,
    string RoleName,
    string[] Permissions
)
{
    public static UserSummaryDto From(AuthenticatedUserSummary s) =>
        new(s.Id, s.FirstName, s.LastName, s.Username, s.Email, s.RoleName, s.Permissions.ToArray());
}

public record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserSummaryDto User
)
{
    public static AuthResponse From(AuthResult r) =>
        new(r.AccessToken, r.AccessTokenExpiresAt, r.RefreshToken, r.RefreshTokenExpiresAt, UserSummaryDto.From(r.User));
}
