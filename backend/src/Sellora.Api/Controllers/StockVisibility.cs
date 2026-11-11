using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Phase 8 (D6): which stock locations a read may see, as an explicit predicate (NOT a global query filter: posting code must never be
/// narrowed). Visible = locations of branches the caller may access (<see cref="BranchContext"/>), further narrowed to the branches where the
/// required permission is held (<see cref="EffectivePermissionSet.PermittedBranches"/>), plus company-level locations (BranchId NULL and not a vehicle assigned to a branch; Phase 3/7
/// decision). Inactive locations stay visible for history. Also serves as the id → code/name/type lookup (location FKs are navigation-less).
/// </summary>
internal sealed class StockVisibility
{
    private readonly Dictionary<Guid, StockLocationLabel> _visible;

    private StockVisibility(Dictionary<Guid, StockLocationLabel> visible) => _visible = visible;

    /// <summary>Ids of every visible location (never null; empty = nothing visible).</summary>
    public IReadOnlyList<Guid> Ids => _visible.Keys.ToList();

    public bool IsVisible(Guid locationId) => _visible.ContainsKey(locationId);

    public string? Code(Guid id) => _visible.TryGetValue(id, out var l) ? l.Code : null;
    public string? Name(Guid id) => _visible.TryGetValue(id, out var l) ? l.Name : null;
    public string? Type(Guid id) => _visible.TryGetValue(id, out var l) ? l.Type : null;

    /// <summary>
    /// <paramref name="permissionCode"/> null = branch access only (POS catalog: the cashier holds Sales.*, not Inventory.*).
    /// </summary>
    public static async Task<StockVisibility> LoadAsync(
        SelloraDbContext db, IEffectivePermissionService effectivePermissions, string? permissionCode, CancellationToken ct)
    {
        IReadOnlySet<Guid>? permitted = null;
        if (permissionCode is not null)
        {
            var set = await effectivePermissions.GetCurrentAsync(ct);
            permitted = set.PermittedBranches(permissionCode);
        }

        var companyId = db.CompanyContext.RequireCompanyId();
        var rows = await db.StockLocations.AsNoTracking()
            .Where(l => l.CompanyId == companyId)
            .Select(l => new { l.Id, l.Code, l.Name, l.BranchId, l.LocationType })
            .ToListAsync(ct);

        // A vehicle's location row is company-level (BranchId NULL); its owner is the branch it is assigned to (D3). Only central
        // (unassigned) vehicles and company warehouses are company-level for visibility.
        var vehicleOwner = await db.Vehicles.AsNoTracking()
            .Where(v => v.CompanyId == companyId && v.AssignedBranchId != null)
            .Select(v => new { v.StockLocationId, v.AssignedBranchId })
            .ToDictionaryAsync(v => v.StockLocationId, v => v.AssignedBranchId, ct);

        // Phase 9 (D7): the company Transit location holds goods in flight between branches. It is visible only to a company-level
        // reader of the permission; branch-scoped readers (and the permission-less POS catalog) never get it in their sums and see the
        // in-transit quantity on the transfer document instead.
        // Company-wide membership as well: an AssignedBranches member with a company-scope role must not see other branches' goods in flight.
        var companyLevel = permissionCode is not null && permitted is null && db.BranchContext.AllowedBranchIds is null;

        var visible = rows
            .Where(l => companyLevel || l.LocationType != StockLocationType.Transit)
            .Select(l => new { l.Id, l.Code, l.Name, l.LocationType, Owner = l.BranchId ?? vehicleOwner.GetValueOrDefault(l.Id) })
            .Where(l => l.Owner is not { } b || (db.BranchContext.CanAccess(b) && (permitted is null || permitted.Contains(b))))
            .ToDictionary(l => l.Id, l => new StockLocationLabel(l.Code, l.Name, l.LocationType.ToString()));
        return new StockVisibility(visible);
    }

    /// <summary>403 <c>branch_access_denied</c> for an explicit location filter that is unknown, foreign or not visible (one answer, no existence leak).</summary>
    public ObjectResult? DenyUnlessVisible(ControllerBase controller, Guid? stockLocationId)
    {
        if (stockLocationId is not { } id || IsVisible(id)) return null;
        return controller.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "You do not have access to this stock location.",
            extensions: new Dictionary<string, object?> { ["code"] = CompanyContextMiddleware.BranchAccessDeniedCode });
    }

    /// <summary>Visible locations, or exactly the one requested (already checked by <see cref="DenyUnlessVisible"/>).</summary>
    public List<Guid> Scope(Guid? stockLocationId) => stockLocationId is { } id ? [id] : Ids.ToList();

    /// <summary>Per-product, per-location balances (existing rows only) of the given products inside <paramref name="scope"/>.</summary>
    public async Task<Dictionary<Guid, List<StockLocationBalanceDto>>> BalancesAsync(
        SelloraDbContext db, IReadOnlyCollection<Guid> productIds, List<Guid> scope, CancellationToken ct)
    {
        var rows = await db.StockItems.AsNoTracking()
            .Where(s => productIds.Contains(s.ProductId) && scope.Contains(s.StockLocationId))
            .Select(s => new { s.ProductId, s.StockLocationId, s.QuantityOnHand, s.AverageCost })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g
                .Select(r => new StockLocationBalanceDto(
                    r.StockLocationId, Code(r.StockLocationId) ?? "", Name(r.StockLocationId) ?? "", Type(r.StockLocationId) ?? "",
                    r.QuantityOnHand, r.AverageCost, r.QuantityOnHand * r.AverageCost))
                .OrderBy(b => b.Code, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>Company weighted average cost of a set of per-location rows: Σ(qty × avg) / Σ qty; with nothing on hand, the highest stored average (display only).</summary>
    public static decimal WeightedAverage(decimal qty, decimal value, decimal fallbackAverage) => qty > 0 ? value / qty : fallbackAverage;

    private sealed record StockLocationLabel(string Code, string Name, string Type);
}
