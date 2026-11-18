using Microsoft.AspNetCore.Authorization;

namespace Sellora.Api.Authorization;

/// <summary>
/// A permission the caller must hold. <see cref="PermissionCodes"/> normally has one code; an any-of policy ("CodeA|CodeB", see
/// <see cref="PermissionPolicyProvider"/>) lists several and passes when ANY of them is held (company-wide or at the working branch).
/// </summary>
public class PermissionRequirement(IReadOnlyList<string> permissionCodes) : IAuthorizationRequirement
{
    public PermissionRequirement(string permissionCode) : this([permissionCode]) { }

    public IReadOnlyList<string> PermissionCodes { get; } = permissionCodes;

    public string PermissionCode => PermissionCodes[0];
}
