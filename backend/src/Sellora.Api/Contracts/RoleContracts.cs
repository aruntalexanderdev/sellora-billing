using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <summary><c>UserCount</c> = Active assignments of the role in the current company (Phase 7: no longer a global count).</summary>
public record RoleListItemDto(
    Guid Id, string Name, string? Description, bool IsSystem, bool IsActive, int UserCount,
    RoleScope ScopeType = RoleScope.Company, Guid? BranchId = null, string? BranchName = null,
    bool HoldsBranchAdministration = false);

public record RoleDetailDto(
    Guid Id, string Name, string? Description, bool IsSystem, bool IsActive, string[] PermissionCodes,
    RoleScope ScopeType = RoleScope.Company, Guid? BranchId = null, string? BranchName = null);

/// <summary><c>ScopeType</c> defaults to Company; a Branch role needs <c>BranchId</c> and only branch-eligible codes.</summary>
public record CreateRoleRequest(
    [Required, MaxLength(80)] string Name,
    [MaxLength(400)] string? Description,
    string[] PermissionCodes,
    RoleScope? ScopeType = null,
    Guid? BranchId = null
);

/// <summary>Scope fields are immutable: when present and different from the stored values the request is rejected (400 scopeType / branchId).</summary>
public record UpdateRoleRequest(
    [Required, MaxLength(80)] string Name,
    [MaxLength(400)] string? Description,
    string[] PermissionCodes,
    RoleScope? ScopeType = null,
    Guid? BranchId = null
);

public record SetRoleActiveRequest(bool IsActive);

public record AssignRoleRequest([Required] Guid? RoleId, [MaxLength(500)] string? Reason = null);

public record EndRoleAssignmentRequest([Required, MaxLength(500)] string Reason);

public record RoleAssignmentDto(
    Guid Id, Guid RoleId, string RoleName, RoleScope ScopeType, Guid? BranchId, string? BranchName, bool IsSystem,
    AssignmentStatus Status, DateTimeOffset StartAt, DateTimeOffset? EndAt, string? AssignmentReason, string? EndReason,
    string? CreatedBy, string? EndedBy);

public record UserRoleAssignmentsDto(Guid MembershipId, Guid UserId, IReadOnlyList<RoleAssignmentDto> Current, IReadOnlyList<RoleAssignmentDto> History);

public record BranchMemberDto(Guid UserId, Guid MembershipId, string FirstName, string LastName, string Username, MembershipStatus MembershipStatus, bool IsPrimary);
