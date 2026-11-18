using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Authorization;

/// <summary>
/// Phase 01 (FI-D11): marks a read endpoint that may be widened from the active branch to the whole company scope with <c>?scope=company</c>
/// (reports, accounts reports, stock overview, dashboard). The widening is honoured only when EVERY permission policy of the endpoint (plus the
/// optional <see cref="Permission"/>, e.g. the dashboard's <c>Reports.Sales</c>) is held company-level; otherwise 403 <c>company_scope_denied</c>.
/// Any other <c>scope</c> value answers 400 <c>scope_invalid</c>. Runs as an action filter, i.e. after authorization has narrowed the allowed set,
/// so the widened read scope is allowed ∩ permitted. Without the parameter the read scope stays the active branch.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class CompanyScopeReadAttribute : Attribute, IAsyncActionFilter
{
    public const string ScopeParameter = "scope";
    public const string CompanyScope = "company";
    public const string DeniedCode = "company_scope_denied";
    public const string InvalidCode = "scope_invalid";

    /// <summary>Extra permission code that must be company-level for the company view (in addition to the endpoint's policies).</summary>
    public string? Permission { get; init; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!http.Request.Query.TryGetValue(ScopeParameter, out var values) || values.Count == 0 || string.IsNullOrEmpty(values[0]))
        {
            await next();
            return;
        }

        if (values.Count != 1 || !string.Equals(values[0], CompanyScope, StringComparison.Ordinal))
        {
            context.Result = Problem(http, StatusCodes.Status400BadRequest, "The scope parameter must be 'company' or omitted.", InvalidCode);
            return;
        }

        var codes = (http.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>() ?? [])
            .Select(a => a.Policy)
            .Where(p => p is not null && PermissionCatalog.All.Any(c => c.Code == p))
            .Select(p => p!)
            .ToList();
        if (Permission is not null) codes.Add(Permission);

        var set = await http.RequestServices.GetRequiredService<IEffectivePermissionService>().GetCurrentAsync(http.RequestAborted);
        if (codes.Count == 0 || codes.Any(c => set.PermittedBranches(c) is not null))
        {
            context.Result = Problem(http, StatusCodes.Status403Forbidden, "The company view requires the permission at company level.", DeniedCode);
            return;
        }

        http.RequestServices.GetRequiredService<BranchContext>().WidenToCompanyScope();
        await next();
    }

    private static ObjectResult Problem(HttpContext http, int status, string title, string code)
    {
        var details = http.RequestServices.GetRequiredService<ProblemDetailsFactory>().CreateProblemDetails(http, status, title);
        details.Extensions["code"] = code;
        return new ObjectResult(details) { StatusCode = status };
    }
}
