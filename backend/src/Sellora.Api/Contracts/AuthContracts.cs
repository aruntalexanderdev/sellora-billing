using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record LoginRequest(
    [Required] string UsernameOrEmail,
    [Required] string Password
);

public record RefreshRequest(
    [Required] string RefreshToken
);

public record AllowedBranchDto(Guid Id, string Code, string Name, bool IsPrimary, bool IsDefault);

public record UserSummaryDto(
    Guid Id,
    string FirstName,
    string LastName,
    string Username,
    string Email,
    string RoleName,
    string[] Permissions,
    Guid CompanyId,
    string CompanyName,
    CompanyAccessScope AccessScope,
    Guid? PrimaryBranchId,
    AllowedBranchDto[] AllowedBranches
)
{
    public static UserSummaryDto From(AuthenticatedUserSummary s) =>
        new(s.Id, s.FirstName, s.LastName, s.Username, s.Email, s.RoleName, s.Permissions.ToArray(), s.CompanyId, s.CompanyName,
            s.AccessScope, s.PrimaryBranchId,
            (s.AllowedBranches ?? []).Select(b => new AllowedBranchDto(b.Id, b.Code, b.Name, b.IsPrimary, b.IsDefault)).ToArray());
}

public record CompanyOptionDto(Guid Id, string Name);

/// <summary>GET auth/me: the user summary plus every company the user may switch to.</summary>
public record MeResponse(
    Guid Id,
    string FirstName,
    string LastName,
    string Username,
    string Email,
    string RoleName,
    string[] Permissions,
    Guid CompanyId,
    string CompanyName,
    CompanyAccessScope AccessScope,
    Guid? PrimaryBranchId,
    AllowedBranchDto[] AllowedBranches,
    CompanyOptionDto[] Companies
) : UserSummaryDto(Id, FirstName, LastName, Username, Email, RoleName, Permissions, CompanyId, CompanyName, AccessScope, PrimaryBranchId, AllowedBranches);

public record SwitchCompanyRequest(
    [Required] Guid CompanyId,
    [Required] string RefreshToken
);

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
