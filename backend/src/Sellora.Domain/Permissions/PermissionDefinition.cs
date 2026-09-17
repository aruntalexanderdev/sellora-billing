namespace Sellora.Domain.Permissions;

public sealed record PermissionDefinition(string Code, string Module, string Action, string Description);
