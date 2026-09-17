using Sellora.Domain.Permissions;

namespace Sellora.Api.Contracts;

public record PermissionDto(string Code, string Module, string Action, string Description)
{
    public static PermissionDto From(PermissionDefinition d) => new(d.Code, d.Module, d.Action, d.Description);
}
