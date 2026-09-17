using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(IAuthService authService, SelloraDbContext db) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await authService.LoginAsync(request.UsernameOrEmail, request.Password, ct);

        if (!result.Succeeded)
        {
            var (status, code, message) = result.FailureReason switch
            {
                LoginFailureReason.AccountDisabled => (403, "account_disabled", "Your account has been disabled. Contact an administrator."),
                _ => (401, "invalid_credentials", "Invalid username/email or password."),
            };
            return Problem(statusCode: status, title: message, extensions: new Dictionary<string, object?> { ["code"] = code });
        }

        return Ok(AuthResponse.From(result.Auth!));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var result = await authService.RefreshAsync(request.RefreshToken, ct);
        if (!result.Succeeded)
        {
            return Problem(statusCode: 401, title: "The session has expired. Please sign in again.",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_refresh_token" });
        }

        return Ok(AuthResponse.From(result.Auth!));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        await authService.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        var user = await db.Users
            .Include(u => u.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return NotFound();

        var permissions = PermissionResolver.Resolve(user.Role);
        var summary = new AuthenticatedUserSummary(user.Id, user.FirstName, user.LastName, user.Username, user.Email, user.Role.Name, permissions);
        return Ok(UserSummaryDto.From(summary));
    }
}
