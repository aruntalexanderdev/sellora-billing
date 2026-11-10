using Sellora.Domain.Entities;

namespace Sellora.Domain.Auth;

/// <summary>A company the user is an active member of (auth/me).</summary>
public sealed record CompanyOption(Guid Id, string Name);

/// <summary>A branch the user may work in (login/me). <c>IsPrimary</c> = the user's primary allocation, <c>IsDefault</c> = the company's default branch.</summary>
public sealed record AllowedBranchSummary(Guid Id, string Code, string Name, bool IsPrimary, bool IsDefault);

/// <summary>A role the membership holds (Phase 7): company scope, or branch scope with its branch.</summary>
public sealed record AuthRoleSummary(Guid Id, string Name, RoleScope ScopeType, Guid? BranchId);

public sealed record AuthenticatedUserSummary(
    Guid Id,
    string FirstName,
    string LastName,
    string Username,
    string Email,
    string RoleName,
    IReadOnlyCollection<string> Permissions,
    Guid CompanyId,
    string CompanyName,
    CompanyAccessScope AccessScope = CompanyAccessScope.CompanyWide,
    Guid? PrimaryBranchId = null,
    IReadOnlyCollection<AllowedBranchSummary>? AllowedBranches = null,
    IReadOnlyCollection<AuthRoleSummary>? Roles = null
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
    /// <summary>Active membership(s) exist but none is usable: every one is AssignedBranches with no active branch allocation.</summary>
    NoBranchAccess,
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
    public bool NoBranchAccess { get; private init; }
    public static readonly RefreshResult Failed = new();
    /// <summary>The bound membership has no usable branch allocation (403 no_branch_access, not a session expiry).</summary>
    public static readonly RefreshResult NoBranch = new() { NoBranchAccess = true };
}

public enum SwitchCompanyFailureReason
{
    InvalidRefreshToken,
    /// <summary>Not an active member of an active company with this id — identical for unknown and foreign ids (no existence leak).</summary>
    CompanyAccessDenied,
    /// <summary>The membership exists but is AssignedBranches with no active allocation.</summary>
    NoBranchAccess,
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
