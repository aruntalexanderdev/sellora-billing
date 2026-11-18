using Microsoft.AspNetCore.Authorization;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>
/// Server-side permission check (Phase 7, E5). Token claims are ignored: the effective set of the request's membership is evaluated at the working
/// branch (X-Branch-Id, else the primary branch, else the company's default branch). A company-level grant passes for every accessible branch; a
/// branch-only grant passes only when the working branch is one of the branches whose roles hold the code, and then narrows the request's branch
/// scope to those branches so other branches' documents are invisible. Scoped (reads the membership context and database).
/// </summary>
public sealed class PermissionAuthorizationHandler(
    IEffectivePermissionService permissions, IMembershipContext membership, BranchContext branchContext)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (membership.MembershipId is null) return;

        var ct = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        var set = await permissions.GetCurrentAsync(ct);
        // Any-of: a company-wide grant of any listed code passes; otherwise the branch scopes of every held code are unioned (one code = the single-code rule).
        HashSet<Guid>? union = null;
        foreach (var code in requirement.PermissionCodes)
        {
            var held = set.PermittedBranches(code);
            if (held is null)
            {
                context.Succeed(requirement);
                return;
            }
            (union ??= []).UnionWith(held);
        }
        IReadOnlySet<Guid> permitted = union ?? [];
        if (permitted.Count == 0) return;

        // Active branch is resolved once by CompanyContextMiddleware (header, primary, company default).
        var working = branchContext.ActiveBranchId;
        if (working is not { } id || !permitted.Contains(id) || !branchContext.CanAccess(id)) return;

        branchContext.Narrow(permitted);
        context.Succeed(requirement);
    }
}
