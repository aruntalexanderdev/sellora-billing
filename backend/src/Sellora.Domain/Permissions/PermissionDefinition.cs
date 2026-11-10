namespace Sellora.Domain.Permissions;

/// <summary>Where a permission code may be granted (Phase 7, D5): CompanyOnly codes can only be held by company-scoped roles; CompanyOrBranch codes may also be held by a branch-scoped role.</summary>
public enum PermissionScope
{
    CompanyOnly,
    CompanyOrBranch,
}

public sealed record PermissionDefinition(string Code, string Module, string Action, string Description, PermissionScope Scope = PermissionScope.CompanyOnly);
