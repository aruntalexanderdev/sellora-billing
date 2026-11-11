using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Platform;

namespace Sellora.Api.Controllers;

/// <summary>Platform operator sign-in (Phase 6 D2): a separate flow from company login. Issues a 30-minute (Platform:SessionMinutes)
/// access token with no company and no permission claims and no refresh token.</summary>
[ApiController]
[Route("api/v1/auth/platform")]
public class PlatformAuthController(IPlatformService platform) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(PlatformLoginRequest request, CancellationToken ct)
    {
        var result = await platform.LoginAsync(request.UsernameOrEmail, request.Password, HttpContext.TraceIdentifier, ct);
        if (!result.Succeeded)
        {
            var (status, code, message) = result.FailureReason switch
            {
                PlatformLoginFailureReason.AccountDisabled => (403, "account_disabled", "Your account has been disabled."),
                PlatformLoginFailureReason.NotPlatformAdministrator => (403, "platform_admin_required", "This account is not a platform administrator."),
                _ => (401, "invalid_credentials", "Invalid username/email or password."),
            };
            return Problem(statusCode: status, title: message, extensions: new Dictionary<string, object?> { ["code"] = code });
        }

        var s = result.Session!;
        return Ok(new PlatformLoginResponse(s.AccessToken, s.ExpiresAt, new PlatformUserDto(s.UserId, s.Name)));
    }
}
