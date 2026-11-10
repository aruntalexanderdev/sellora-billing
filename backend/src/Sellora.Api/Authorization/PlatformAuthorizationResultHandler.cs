using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Sellora.Api.Authorization;

/// <summary>Adds the <c>platform_admin_required</c> code to a 403 caused by the Platform.Admin requirement; every other outcome
/// is the framework default.</summary>
public sealed class PlatformAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<PlatformAdminRequirement>().Any() == true)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Platform administrator access is required.",
                extensions: new Dictionary<string, object?> { ["code"] = PlatformPolicies.AdminRequiredCode })
                .ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
