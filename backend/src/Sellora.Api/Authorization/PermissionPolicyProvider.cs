using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Sellora.Domain.Permissions;

namespace Sellora.Api.Authorization;

/// <summary>
/// Lets controllers write [Authorize(Policy = "Inventory.View")] directly against a permission code
/// instead of registering one static policy per permission. Unrecognized policy names fall through to
/// the default provider.
/// </summary>
public class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private static readonly HashSet<string> KnownPermissionCodes = PermissionCatalog.All.Select(p => p.Code).ToHashSet();
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (KnownPermissionCodes.Contains(policyName))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
