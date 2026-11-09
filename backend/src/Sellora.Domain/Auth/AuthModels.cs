using Sellora.Domain.Entities;

namespace Sellora.Domain.Auth;

/// <summary>A company the user is an active member of (auth/me).</summary>
public sealed record CompanyOption(Guid Id, string Name);

public sealed record AuthenticatedUserSummary(
    Guid Id,
    string FirstName,
    string LastName,
    string Username,
    string Email,
    string RoleName,
    IReadOnlyCollection<string> Permissions,
    Guid CompanyId,
    string CompanyName
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
    /// <summary>Credentials are valid but the user has no active membership in an active company.</summary>
    NoCompanyAccess,
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

public enum SwitchCompanyFailureReason
{
    InvalidRefreshToken,
    /// <summary>Not an active member of an active company with this id — identical for unknown and foreign ids (no existence leak).</summary>
    CompanyAccessDenied,
}

public sealed class SwitchCompanyResult
{
    public AuthResult? Auth { get; private init; }
    public SwitchCompanyFailureReason? FailureReason { get; private init; }
    public bool Succeeded => Auth is not null;

    public static SwitchCompanyResult Success(AuthResult auth) => new() { Auth = auth };
    public static SwitchCompanyResult Failure(SwitchCompanyFailureReason reason) => new() { FailureReason = reason };
}

public static class PermissionResolver
{
    public static IReadOnlyCollection<string> Resolve(Role role)
    {
        // A deactivated role grants nothing; deactivation is refused while active users hold the role,
        // so this is a safety net for roles deactivated before that rule existed.
        if (!role.IsActive)
        {
            return [];
        }

        if (role.IsSystem)
        {
            return Permissions.PermissionCatalog.All.Select(p => p.Code).ToArray();
        }

        return role.RolePermissions.Select(rp => rp.Permission.Code).ToArray();
    }
}
