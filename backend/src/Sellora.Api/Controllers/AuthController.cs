using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(IAuthService authService, SelloraDbContext db, IEffectivePermissionService effectivePermissions) : ControllerBase
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
                LoginFailureReason.NoBranchAccess => (403, "no_branch_access", "You have not been assigned to any branch. Contact an administrator."),
                LoginFailureReason.NoCompanyAccess => (403, "no_company_access", "Your account is not a member of any active company. Contact an administrator."),
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
            return result.NoBranchAccess
                ? Problem(statusCode: 403, title: "You have not been assigned to any branch. Contact an administrator.",
                    extensions: new Dictionary<string, object?> { ["code"] = "no_branch_access" })
                : Problem(statusCode: 401, title: "The session has expired. Please sign in again.",
                    extensions: new Dictionary<string, object?> { ["code"] = "invalid_refresh_token" });
        }

        return Ok(AuthResponse.From(result.Auth!));
    }

    /// <summary>Re-issues the session for another company the caller is an active member of. A company that does not exist
    /// and one the caller is not a member of yield the same 403 (no existence leak).</summary>
    [HttpPost("switch-company")]
    [Authorize]
    [AllowWithoutBranch]
    public async Task<IActionResult> SwitchCompany(SwitchCompanyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        var result = await authService.SwitchCompanyAsync(userId, request.CompanyId, request.RefreshToken, ct);
        if (!result.Succeeded)
        {
            if (result.FailureReason == SwitchCompanyFailureReason.NoBranchAccess)
            {
                return Problem(statusCode: 403, title: "You have not been assigned to any branch in that company. Contact an administrator.",
                    extensions: new Dictionary<string, object?> { ["code"] = "no_branch_access" });
            }
            return result.FailureReason == SwitchCompanyFailureReason.CompanyAccessDenied
                ? Problem(statusCode: 403, title: "You do not have access to this company.",
                    extensions: new Dictionary<string, object?> { ["code"] = "company_access_denied" })
                : Problem(statusCode: 401, title: "The session has expired. Please sign in again.",
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
    [AllowWithoutBranch]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return NotFound();

        // The middleware validated the claim and set the context; the current company is whatever the token is bound to.
        var currentCompanyId = db.CompanyContext.RequireCompanyId();
        var companies = await db.UserCompanyMemberships
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && m.Company.IsActive)
            .OrderBy(m => m.Company.Name).ThenBy(m => m.CompanyId)
            .Select(m => new CompanyOptionDto(m.CompanyId, m.Company.Name))
            .ToArrayAsync(ct);
        var current = companies.SingleOrDefault(c => c.Id == currentCompanyId);
        if (current is null) return NotFound();

        // Branch scope as the middleware established it: null allowed set = CompanyWide (every active branch).
        var allowedIds = db.BranchContext.AllowedBranchIds;
        var primaryId = db.BranchContext.PrimaryBranchId;
        var branchRows = await db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == currentCompanyId && b.IsActive)
            .OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name)
            .Select(b => new { b.Id, b.Code, b.Name, b.IsDefault })
            .ToListAsync(ct);
        var allowedBranches = branchRows
            .Where(b => allowedIds is null || allowedIds.Contains(b.Id))
            .Select(b => new AllowedBranchDto(b.Id, b.Code, b.Name, b.Id == primaryId, b.IsDefault))
            .OrderByDescending(b => b.IsPrimary)
            .ToArray();
        var scope = allowedIds is null ? CompanyAccessScope.CompanyWide : CompanyAccessScope.AssignedBranches;

        // Effective permissions at the working branch (header, else primary, else the company's default branch); roles of this membership.
        var set = await effectivePermissions.GetCurrentAsync(ct);
        var working = db.BranchContext.RequestedBranchId ?? primaryId ?? branchRows.Where(b => b.IsDefault).Select(b => (Guid?)b.Id).FirstOrDefault();
        var permissions = set.CodesAtBranch(working).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        // D13 UI hint (authorization unchanged): company-level codes plus codes held via branch roles at any active branch this membership may access.
        var anyBranch = new HashSet<string>(set.CodesAtBranch(null), StringComparer.Ordinal);
        foreach (var b in allowedBranches)
        {
            anyBranch.UnionWith(set.CodesAtBranch(b.Id));
        }
        var permissionsAnyBranch = anyBranch.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var membershipId = HttpContext.RequestServices.GetRequiredService<IMembershipContext>().RequireMembershipId();
        var roleRows = await (
            from a in db.UserRoleAssignments
            join r in db.Roles on a.RoleId equals r.Id
            where a.UserCompanyMembershipId == membershipId && a.CompanyId == currentCompanyId && a.Status == AssignmentStatus.Active && r.IsActive
            select new { r.Id, r.Name, r.ScopeType, r.BranchId })
            .ToListAsync(ct);
        var roles = roleRows.OrderBy(r => r.ScopeType).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new AuthRoleSummary(r.Id, r.Name, r.ScopeType, r.BranchId)).ToArray();
        var summary = UserSummaryDto.From(new AuthenticatedUserSummary(
            user.Id, user.FirstName, user.LastName, user.Username, user.Email, Sellora.Infrastructure.Services.AuthService.PrimaryRoleName(roles),
            permissions, current.Id, current.Name, Roles: roles));
        var member = await db.CompanyGovernanceMembers.AsNoTracking()
            .Where(g => g.CompanyId == currentCompanyId && g.Status == GovernanceMemberStatus.Active && g.Membership.UserId == userId)
            .Select(g => new { g.Id, g.Title })
            .SingleOrDefaultAsync(ct);
        GovernanceHintDto? governance = null;
        if (member is not null)
        {
            var codes = await db.GovernanceMemberPermissions.AsNoTracking()
                .Where(p => p.GovernanceMemberId == member.Id)
                .Select(p => p.GovernancePermission.Code)
                .OrderBy(c => c)
                .ToArrayAsync(ct);
            governance = new GovernanceHintDto(member.Id, member.Title.ToString(), codes);
        }

        return Ok(new MeResponse(summary.Id, summary.FirstName, summary.LastName, summary.Username, summary.Email,
            summary.RoleName, summary.Permissions, summary.CompanyId, summary.CompanyName,
            scope, primaryId, allowedBranches, companies, governance, summary.Roles, permissionsAnyBranch));
    }
}
