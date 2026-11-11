using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Per-request id → code/name dictionary of the company's branches (Phase 2 D3). Branch foreign keys are navigation-less by
/// design (a filtered required navigation reached through an optional one would become an INNER JOIN and drop rows, Phase 1 §8),
/// so DTOs resolve the branch label from this dictionary. Branches are few; inactive ones are included for history.
/// </summary>
internal sealed class BranchLabels
{
    private readonly Dictionary<Guid, (string Code, string Name)> _byId;

    private BranchLabels(Dictionary<Guid, (string Code, string Name)> byId) => _byId = byId;

    public static async Task<BranchLabels> LoadAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var rows = await db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == companyId)
            .Select(b => new { b.Id, b.Code, b.Name })
            .ToListAsync(ct);
        return new BranchLabels(rows.ToDictionary(b => b.Id, b => (b.Code, b.Name)));
    }

    public string? Code(Guid? branchId) => branchId is { } id && _byId.TryGetValue(id, out var label) ? label.Code : null;

    public string? Name(Guid? branchId) => branchId is { } id && _byId.TryGetValue(id, out var label) ? label.Name : null;
}

internal static class BranchFilter
{
    /// <summary>Validates an optional <c>branchId</c> list/report filter: it must be a branch of the company (inactive allowed, for
    /// history) the caller may access. Unknown, foreign and inaccessible ids share one 403 <c>branch_access_denied</c> answer.
    /// Returns null when the filter is absent or valid.</summary>
    public static async Task<ObjectResult?> DenyUnlessAllowedAsync(this ControllerBase controller, SelloraDbContext db, Guid? branchId, CancellationToken ct)
    {
        if (branchId is not { } id) return null;

        var companyId = db.CompanyContext.RequireCompanyId();
        if (db.BranchContext.CanAccess(id) && await db.Branches.AnyAsync(b => b.CompanyId == companyId && b.Id == id, ct))
        {
            return null;
        }

        return controller.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "You do not have access to this branch.",
            extensions: new Dictionary<string, object?> { ["code"] = CompanyContextMiddleware.BranchAccessDeniedCode });
    }

    /// <summary>403 <c>branch_access_denied</c> when the caller may not work in <paramref name="branchId"/> (null passes). Unknown
    /// branches are left to the service, which answers 400 for administrators.</summary>
    public static ObjectResult? DenyIfInaccessible(this ControllerBase controller, SelloraDbContext db, Guid? branchId)
    {
        if (branchId is not { } id || db.BranchContext.CanAccess(id)) return null;

        return controller.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "You do not have access to this branch.",
            extensions: new Dictionary<string, object?> { ["code"] = CompanyContextMiddleware.BranchAccessDeniedCode });
    }
}
