using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Which product unit rows are immutable (Phase 2 design §5.2, Phase 3 §5). A row is <em>directly locked</em> when a
/// purchase line, purchase return line, sales line, sales return line, stock adjustment, stock movement or selling price references it: its factor is part of history (lines
/// and adjustments snapshot it) or of a price that was set for that pack size. Adjustments share the
/// <see cref="Transactions"/> reason. Every row contained in a locked row, transitively, is locked as well, because
/// changing it would change the locked row's factor. Locked rows keep their unit, contained unit, quantity and factor;
/// a change means deactivate and add a new row. Shared by <see cref="ProductService"/> and the products controller so
/// the editor shows exactly what the save enforces. Movements (Phase 6a) share the reason; Phase 6b adds transfer lines.
/// </summary>
public static class ProductUnitUsage
{
    private static readonly string[] BranchBypass = [BranchQueryFilter.Name];

    public const string Transactions = "Transactions";
    public const string Prices = "Prices";
    public const string ContainedInLockedUnit = "ContainedInLockedUnit";

    /// <summary>Lock reason per locked row id (rows that are not locked are absent). Transactions win over prices,
    /// direct locks over <see cref="ContainedInLockedUnit"/>.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> GetLockReasonsAsync(
        SelloraDbContext db, IReadOnlyCollection<ProductUnit> units, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        if (units.Count == 0) return result;

        // Documented "Branch"-filter bypass (D7): ProductUnit immutability is company-wide, so the usage checks must see the
        // documents of every branch (the Company filter stays). Applies to every document query below.
        var ids = units.Select(u => u.Id).ToList();
        var productIds = units.Select(u => u.ProductId).Distinct().ToList();
        var inTransactions = (await db.PurchaseOrderLines.IgnoreQueryFilters(BranchBypass).Where(l => ids.Contains(l.ProductUnitId)).Select(l => l.ProductUnitId).Distinct().ToListAsync(ct))
            .Concat(await db.SalesInvoiceLines.IgnoreQueryFilters(BranchBypass).Where(l => ids.Contains(l.ProductUnitId)).Select(l => l.ProductUnitId).Distinct().ToListAsync(ct))
            .Concat(await db.PurchaseReturnLines.IgnoreQueryFilters(BranchBypass).Where(r => ids.Contains(r.ProductUnitId)).Select(r => r.ProductUnitId).Distinct().ToListAsync(ct))
            .Concat(await db.SalesReturnLines.IgnoreQueryFilters(BranchBypass).Where(r => ids.Contains(r.ProductUnitId)).Select(r => r.ProductUnitId).Distinct().ToListAsync(ct))
            .Concat(await db.StockAdjustments.IgnoreQueryFilters(BranchBypass).Where(a => ids.Contains(a.ProductUnitId)).Select(a => a.ProductUnitId).Distinct().ToListAsync(ct))
            .Concat(await db.StockMovements.Where(m => productIds.Contains(m.ProductId) && m.EnteredProductUnitId != null && ids.Contains(m.EnteredProductUnitId.Value)).Select(m => m.EnteredProductUnitId!.Value).Distinct().ToListAsync(ct))
            .ToHashSet();
        var priced = (await db.SellingPrices.Where(p => ids.Contains(p.ProductUnitId)).Select(p => p.ProductUnitId).Distinct().ToListAsync(ct))
            .ToHashSet();

        foreach (var id in inTransactions) result[id] = Transactions;
        foreach (var id in priced) result.TryAdd(id, Prices);

        // Lock closure: walk down from every directly locked row.
        var innerOf = units.ToDictionary(u => u.Id, u => u.InnerProductUnitId);
        foreach (var start in result.Keys.ToList())
        {
            var seen = new HashSet<Guid> { start };
            var current = innerOf.GetValueOrDefault(start);
            while (current is { } inner && seen.Add(inner))
            {
                result.TryAdd(inner, ContainedInLockedUnit);
                current = innerOf.GetValueOrDefault(inner);
            }
        }

        return result;
    }

    /// <summary>Stock quantities, batches and costs are stored in the base unit, so once any exist the base unit
    /// cannot be re-labelled.</summary>
    public static async Task<bool> HasStockHistoryAsync(SelloraDbContext db, Guid productId, CancellationToken ct) =>
        await db.StockMovements.AnyAsync(m => m.ProductId == productId, ct)
        || await db.PurchaseBatches.AnyAsync(b => b.ProductId == productId, ct);

    /// <summary>True when the product's base unit cannot change: stock history exists or any unit row is locked.</summary>
    public static async Task<bool> IsBaseUnitLockedAsync(SelloraDbContext db, Guid productId, IReadOnlyDictionary<Guid, string> lockReasons, CancellationToken ct) =>
        lockReasons.Count > 0 || await HasStockHistoryAsync(db, productId, ct);
}
