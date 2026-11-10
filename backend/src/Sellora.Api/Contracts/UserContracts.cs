using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record UserListItemDto(
    Guid Id, string FirstName, string LastName, string Username, string Email,
    Guid RoleId, string RoleName, UserStatus Status, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt
);

public record UserDetailDto(
    Guid Id, string FirstName, string LastName, string Username, string Email, string? PhoneNumber,
    Guid RoleId, string RoleName, UserStatus Status, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt
);

public record CreateUserRequest(
    [Required, MaxLength(80)] string FirstName,
    [Required, MaxLength(80)] string LastName,
    [Required, MaxLength(60)] string Username,
    [Required, EmailAddress, MaxLength(200)] string Email,
    [MaxLength(30)] string? PhoneNumber,
    [Required, MinLength(8)] string Password,
    [Required] string ConfirmPassword,
    [Required] Guid? RoleId,
    // Optional initial access (Phase 5, D12). Omitted: AssignedBranches with one primary allocation at the creator's working
    // branch (company-wide for the Administrator role). Supplying any of them needs Company.Users.ManageBranchAccess and a company-wide actor.
    CompanyAccessScope? AccessScope = null,
    Guid[]? BranchIds = null,
    Guid? PrimaryBranchId = null
);

public record UpdateUserRequest(
    [Required, MaxLength(80)] string FirstName,
    [Required, MaxLength(80)] string LastName,
    [Required, MaxLength(60)] string Username,
    [Required, EmailAddress, MaxLength(200)] string Email,
    [MaxLength(30)] string? PhoneNumber,
    [Required] Guid? RoleId
);

public record SetUserStatusRequest(UserStatus Status);

public record ResetPasswordResponse(string TemporaryPassword);

public record UpdateOwnUsernameRequest(
    [Required, MaxLength(60)] string Username
);

public record ChangeOwnPasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword,
    [Required] string ConfirmPassword
);
