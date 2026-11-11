using Sellora.Domain.Permissions;

namespace Sellora.Api.Contracts;

/// <summary>A catalogue permission. <c>Scope</c> (Phase 11, additive): CompanyOnly or CompanyOrBranch — whether a branch role may hold it.</summary>
public record PermissionDto(string Code, string Module, string Action, string Description, PermissionScope Scope = PermissionScope.CompanyOnly)
{
    public static PermissionDto From(PermissionDefinition d) => new(d.Code, d.Module, d.Action, d.Description, d.Scope);
}
