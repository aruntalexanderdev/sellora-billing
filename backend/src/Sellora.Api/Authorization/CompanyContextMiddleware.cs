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
/// <para>The same pass establishes the branch scope (Phase 2/5, ADR-04): a CompanyWide membership may use every branch
/// (<c>AllowedBranchIds</c> null); an AssignedBranches one only its active allocations (none: 403 <c>branch_required</c> unless the endpoint is <c>[AllowWithoutBranch]</c>). The optional
/// <c>X-Branch-Id</c> header is validated for every authenticated request: malformed → 400 <c>branch_header_invalid</c>;
/// unknown, foreign, inactive or not accessible → 403 <c>branch_access_denied</c> (one answer for all, no existence leak).
/// Without the header the working branch is resolved by the services (primary branch, else the company's default branch).</para>
/// </summary>
public sealed class CompanyContextMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "company_context_invalid";
    public const string BranchHeaderName = "X-Branch-Id";
    public const string BranchHeaderInvalidCode = "branch_header_invalid";
    public const string BranchAccessDeniedCode = "branch_access_denied";
    public const string BranchRequiredCode = "branch_required";

    private static readonly string[] CompanyFilter = [CompanyQueryFilter.Name];

    private sealed record AssignmentRow(Guid BranchId, bool IsPrimary);
    private sealed record MembershipAccess(CompanyAccessScope Scope, List<AssignmentRow> Assignments);

    public async Task InvokeAsync(HttpContext context, SelloraDbContext db, CompanyContext companyContext, BranchContext branchContext)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        var companyIdValue = context.User.FindFirstValue(AuthClaimTypes.CompanyId);

        MembershipAccess? access = null;
        if (Guid.TryParse(userIdValue, out var userId)
            && Guid.TryParse(companyIdValue, out var companyId)
            && companyId != Guid.Empty)
        {
            access = await GetActiveMembershipAsync(db, userId, companyId, context.RequestAborted);
        }
        else
        {
            companyId = Guid.Empty;
        }

        if (access is null)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Your company session is no longer valid. Please sign in again.",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCode })
                .ExecuteAsync(context);
            return;
        }

        companyContext.Set(companyId);

        // Branch scope (D4): CompanyWide = every branch (allowed null, no primary); AssignedBranches = its active allocations
        // (inactive branches included so history stays readable). No allocation at all = 403 branch_required, except on
        // endpoints marked [AllowWithoutBranch] (they get an empty, grants-nothing set).
        IReadOnlySet<Guid>? allowed = null;
        Guid? primary = null;
        if (access.Scope != CompanyAccessScope.CompanyWide)
        {
            allowed = access.Assignments.Select(a => a.BranchId).ToHashSet();
            primary = access.Assignments.Where(a => a.IsPrimary).Select(a => (Guid?)a.BranchId).FirstOrDefault();
            if (allowed.Count == 0
                && context.GetEndpoint()?.Metadata.GetMetadata<AllowWithoutBranchAttribute>() is null)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "You have not been assigned to any branch. Contact an administrator.",
                    extensions: new Dictionary<string, object?> { ["code"] = BranchRequiredCode })
                    .ExecuteAsync(context);
                return;
            }
        }

        Guid? requested = null;
        if (context.Request.Headers.TryGetValue(BranchHeaderName, out var headerValues))
        {
            if (headerValues.Count != 1 || !Guid.TryParse(headerValues[0], out var headerBranchId) || headerBranchId == Guid.Empty)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"The {BranchHeaderName} header must be a branch id (GUID).",
                    extensions: new Dictionary<string, object?> { ["code"] = BranchHeaderInvalidCode })
                    .ExecuteAsync(context);
                return;
            }

            var usable = (allowed is null || allowed.Contains(headerBranchId))
                && await db.Branches.AnyAsync(b => b.CompanyId == companyId && b.Id == headerBranchId && b.IsActive, context.RequestAborted);
            if (!usable)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "You do not have access to this branch.",
                    extensions: new Dictionary<string, object?> { ["code"] = BranchAccessDeniedCode })
                    .ExecuteAsync(context);
                return;
            }
            requested = headerBranchId;
        }

        branchContext.Set(requested, allowed, primary);
        await next(context);
    }

    private static async Task<MembershipAccess?> GetActiveMembershipAsync(SelloraDbContext db, Guid userId, Guid companyId, CancellationToken ct)
    {
        // One round-trip: the membership check plus its active allocations (filtered, indexed, a few rows).
        var membership = await db.UserCompanyMemberships
            .Where(m =>
                m.UserId == userId
                && m.CompanyId == companyId
                && m.Status == MembershipStatus.Active
                && m.Company.IsActive
                && m.User.Status != UserStatus.Disabled)
            .Select(m => new
            {
                m.AccessScope,
                // Documented Company-filter bypass: the company context is not established until this check passes, so the
                // allocations are matched by explicit membership + company predicates instead (single round-trip).
                Assignments = db.UserBranchAssignments.IgnoreQueryFilters(CompanyFilter)
                    .Where(a => a.CompanyId == companyId && a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active)
                    .Select(a => new AssignmentRow(a.BranchId, a.IsPrimary))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        return membership is null ? null : new MembershipAccess(membership.AccessScope, membership.Assignments);
    }
}
