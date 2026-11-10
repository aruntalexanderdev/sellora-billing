using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>Policy name of <see cref="PlatformAdminRequirement"/>.</summary>
public static class PlatformPolicies
{
    public const string Admin = "Platform.Admin";
    public const string SessionRequiredCode = "platform_session_required";
    public const string AdminRequiredCode = "platform_admin_required";
}

/// <summary>The caller must hold a platform session AND still be an active platform administrator (re-read per request).</summary>
public sealed class PlatformAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Scoped handler (it needs the request's DbContext; registered with AddScoped - the framework resolves handlers per
/// authorization call from the request scope, unlike the singleton permission handler). Revocation or disabling takes effect on
/// the very next request, whatever the token's remaining lifetime.
/// </summary>
public sealed class PlatformAdminHandler(SelloraDbContext db) : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        if (context.User.FindFirstValue(AuthClaimTypes.SessionKind) != AuthClaimTypes.PlatformSession) return;
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var userId)) return;

        var ct = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        var active = await db.PlatformAdministrators.AsNoTracking()
            .AnyAsync(p => p.UserId == userId && p.Status == PlatformAdminStatus.Active && p.User.Status != UserStatus.Disabled, ct);
        if (active) context.Succeed(requirement);
    }
}
