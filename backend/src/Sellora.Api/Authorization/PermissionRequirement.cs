using Microsoft.AspNetCore.Authorization;

namespace Sellora.Api.Authorization;

public class PermissionRequirement(string permissionCode) : IAuthorizationRequirement
{
    public string PermissionCode { get; } = permissionCode;
}
