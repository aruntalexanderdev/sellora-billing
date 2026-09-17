using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

public record RoleListItemDto(Guid Id, string Name, string? Description, bool IsSystem, bool IsActive, int UserCount);

public record RoleDetailDto(Guid Id, string Name, string? Description, bool IsSystem, bool IsActive, string[] PermissionCodes);

public record CreateRoleRequest(
    [Required, MaxLength(80)] string Name,
    [MaxLength(400)] string? Description,
    string[] PermissionCodes
);

public record UpdateRoleRequest(
    [Required, MaxLength(80)] string Name,
    [MaxLength(400)] string? Description,
    string[] PermissionCodes
);

public record SetRoleActiveRequest(bool IsActive);
