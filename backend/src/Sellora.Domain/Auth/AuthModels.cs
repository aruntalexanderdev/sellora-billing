using Sellora.Domain.Entities;

namespace Sellora.Domain.Auth;

public sealed record AuthenticatedUserSummary(
    Guid Id,
    string FirstName,
    string LastName,
    string Username,
    string Email,
    string RoleName,
    IReadOnlyCollection<string> Permissions
);

public sealed record AuthResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    AuthenticatedUserSummary User
);

public enum LoginFailureReason
{
    InvalidCredentials,
    AccountDisabled,
}

public sealed class LoginResult
{
    public AuthResult? Auth { get; private init; }
    public LoginFailureReason? FailureReason { get; private init; }
    public bool Succeeded => Auth is not null;

    public static LoginResult Success(AuthResult auth) => new() { Auth = auth };
    public static LoginResult Failure(LoginFailureReason reason) => new() { FailureReason = reason };
}

public sealed class RefreshResult
{
    public AuthResult? Auth { get; private init; }
    public bool Succeeded => Auth is not null;

    public static RefreshResult Success(AuthResult auth) => new() { Auth = auth };
    public static readonly RefreshResult Failed = new();
}

public static class PermissionResolver
{
    public static IReadOnlyCollection<string> Resolve(Role role)
    {
        if (role.IsSystem)
        {
            return Permissions.PermissionCatalog.All.Select(p => p.Code).ToArray();
        }

        return role.RolePermissions.Select(rp => rp.Permission.Code).ToArray();
    }
}
