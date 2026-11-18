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
    private static readonly HashSet<string> KnownGovernanceCodes = GovernancePermissionCatalog.All.Select(p => p.Code).ToHashSet();
    /// <summary>Separates the codes of an any-of policy name, e.g. "StockTransfers.Receive|StockTransfers.ApproveReceipt".</summary>
    public const char AnyOfSeparator = '|';
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName == PlatformPolicies.Admin)
        {
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformAdminRequirement())
                .Build());
        }

        // Governance policies ("Governance:<code>") are evaluated against the database by GovernanceAuthorizationHandler;
        // governance codes are never operational permissions or token claims (ADR-13).
        if (policyName.StartsWith(GovernancePolicy.Prefix, StringComparison.Ordinal))
        {
            var code = policyName[GovernancePolicy.Prefix.Length..];
            if (KnownGovernanceCodes.Contains(code))
            {
                return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new GovernanceRequirement(code))
                    .Build());
            }
        }

        if (KnownPermissionCodes.Contains(policyName))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        // Any-of permission policy: "CodeA|CodeB" (every part a known permission code); the caller needs at least one.
        if (policyName.Contains(AnyOfSeparator))
        {
            var codes = policyName.Split(AnyOfSeparator);
            if (codes.Length > 1 && codes.All(KnownPermissionCodes.Contains))
            {
                return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(codes))
                    .Build());
            }
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
