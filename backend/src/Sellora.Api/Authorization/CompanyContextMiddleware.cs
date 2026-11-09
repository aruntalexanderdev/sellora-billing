using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>
/// Establishes the company context for every authenticated request (ADR-05, fail closed). The token's <c>company_id</c>
/// claim is never trusted on its own: one indexed query confirms an Active membership of the user in an active company
/// and that the user is not disabled; only then is <see cref="CompanyContext"/> set. A missing/invalid claim or a failed
/// check answers 401 <c>company_context_invalid</c> (the client refreshes its token, which carries the claim).
/// Anonymous endpoints (login, refresh, logout) never need a company and are skipped. Registered between
/// UseAuthentication and UseAuthorization.
/// </summary>
public sealed class CompanyContextMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "company_context_invalid";

    public async Task InvokeAsync(HttpContext context, SelloraDbContext db, CompanyContext companyContext)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        var companyIdValue = context.User.FindFirstValue(AuthClaimTypes.CompanyId);

        if (!Guid.TryParse(userIdValue, out var userId)
            || !Guid.TryParse(companyIdValue, out var companyId)
            || companyId == Guid.Empty
            || !await HasActiveMembershipAsync(db, userId, companyId, context.RequestAborted))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Your company session is no longer valid. Please sign in again.",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCode })
                .ExecuteAsync(context);
            return;
        }

        companyContext.Set(companyId);
        await next(context);
    }

    private static Task<bool> HasActiveMembershipAsync(SelloraDbContext db, Guid userId, Guid companyId, CancellationToken ct) =>
        db.UserCompanyMemberships.AnyAsync(m =>
            m.UserId == userId
            && m.CompanyId == companyId
            && m.Status == MembershipStatus.Active
            && m.Company.IsActive
            && m.User.Status != UserStatus.Disabled, ct);
}
