using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record GovernancePermissionDto(string Code, string Description);

public record GovernanceMemberDto(
    Guid Id, Guid UserId, string DisplayName, GovernanceTitle Title, GovernanceMemberStatus Status,
    DateTimeOffset StartAt, DateTimeOffset? EndAt, string? EndReason, IReadOnlyList<string> Permissions);

public record AddGovernanceMemberRequest(
    Guid UserId, GovernanceTitle Title, string[]? PermissionCodes, [MaxLength(500)] string? Reason);

public record ChangeGovernanceTitleRequest(GovernanceTitle Title, [MaxLength(500)] string? Reason);

public record SetGovernancePermissionsRequest(string[]? PermissionCodes, [MaxLength(500)] string? Reason);

public record EndGovernanceMemberRequest([MaxLength(500)] string? Reason);

public record GrantAdministratorRequest(Guid UserId, [MaxLength(500)] string? Reason);

public record RemoveAdministratorRequest(Guid ReplacementRoleId, [MaxLength(500)] string? Reason);
