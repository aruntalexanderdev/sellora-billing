using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The only code that mutates <see cref="StockItem.QuantityOnHand"/>, <see cref="StockItem.AverageCost"/>,
/// <see cref="BatchLocationBalance.RemainingBaseQty"/> and the dual-written <see cref="PurchaseBatch.RemainingBaseQty"/>
/// (ADR-12; the batch total is retired in a later release). Every operation takes the locked, tracked StockItem of one
/// (location, product); the caller holds <see cref="StockLocks"/> and writes the movement/document rows (stamping
/// StockLocationId from the same item). Quantities are base units, already validated storable.
/// </summary>
public static class StockPosting
{
    /// <summary>Stock arrives in a new batch at the item's location: batch remainder, balance row, moving-average cost, quantity.
    /// The batch must be new (added to the context by the caller with PurchasedBaseQty = qty).</summary>
    public static void Receive(SelloraDbContext db, StockItem item, PurchaseBatch batch, decimal qty, decimal unitCost)
    {
        batch.OriginStockLocationId = item.StockLocationId;
        batch.RemainingBaseQty = qty;
        db.BatchLocationBalances.Add(new BatchLocationBalance
        {
            PurchaseBatchId = batch.Id,
            StockLocationId = item.StockLocationId,
            ProductId = item.ProductId,
            RemainingBaseQty = qty,
        });

        AddValue(item, qty, unitCost);
    }

    /// <summary>FIFO issue at the item's location (sale, adjustment decrease). Returns the batches drawn; average cost unchanged.
    /// The caller has checked availability; a shortage in the batches is raised through <paramref name="shortageError"/>.</summary>
    public static async Task<IReadOnlyList<BatchAllocator.Allocation>> IssueFifoAsync(
        SelloraDbContext db, StockItem item, decimal qty,
        Func<decimal, Sellora.Domain.Abstractions.DomainValidationException> shortageError, CancellationToken ct)
    {
        var allocations = await BatchAllocator.ConsumeFifoAsync(db, item.StockLocationId, item.ProductId, qty, shortageError, ct);
        item.QuantityOnHand -= qty;
        return allocations;
    }

    /// <summary>Issue from one named batch (purchase return), valued at the batch cost. The caller has checked
    /// <see cref="BalanceAsync"/> covers <paramref name="qty"/>.</summary>
    public static async Task IssueFromBatchAsync(SelloraDbContext db, StockItem item, PurchaseBatch batch, decimal qty, CancellationToken ct)
    {
        var balance = await FindBalanceAsync(db, batch.Id, item.StockLocationId, ct)
            ?? throw new InvalidOperationException($"Batch {batch.Id} has no balance at location {item.StockLocationId}.");
        balance.RemainingBaseQty -= qty;
        batch.RemainingBaseQty -= qty;
        RemoveValue(item, qty, batch.UnitCost);
    }

    /// <summary>Goods go back into the batch they were drawn from at the item's location (sale return / cancellation).
    /// Quantity rises; average cost is untouched (as before Phase 8).</summary>
    public static async Task RestoreAsync(SelloraDbContext db, StockItem item, PurchaseBatch batch, decimal qty, CancellationToken ct)
    {
        var balance = await FindBalanceAsync(db, batch.Id, item.StockLocationId, ct);
        if (balance is null)
        {
            balance = new BatchLocationBalance
            {
                PurchaseBatchId = batch.Id,
                StockLocationId = item.StockLocationId,
                ProductId = item.ProductId,
                RemainingBaseQty = 0,
            };
            db.BatchLocationBalances.Add(balance);
        }
        balance.RemainingBaseQty += qty;
        batch.RemainingBaseQty += qty;
        item.QuantityOnHand += qty;
    }

    /// <summary>Phase 9 transfer of one named batch between two locations (dispatch, receipt, return to source): the batch's
    /// balance moves from <paramref name="from"/> to <paramref name="to"/>; <see cref="PurchaseBatch.RemainingBaseQty"/> (the
    /// all-location total) is unchanged. The source loses value at the batch cost; the target takes it in at the same cost
    /// (moving average), so company value is conserved. The caller holds both StockItems locks.</summary>
    public static async Task MoveBatchAsync(SelloraDbContext db, StockItem from, StockItem to, PurchaseBatch batch, decimal qty, CancellationToken ct)
    {
        var source = await FindBalanceAsync(db, batch.Id, from.StockLocationId, ct)
            ?? throw new InvalidOperationException($"Batch {batch.Id} has no balance at location {from.StockLocationId}.");
        if (source.RemainingBaseQty < qty)
        {
            throw new InvalidOperationException($"Batch {batch.Id} holds {source.RemainingBaseQty} at location {from.StockLocationId}, cannot move {qty}.");
        }

        var target = await FindBalanceAsync(db, batch.Id, to.StockLocationId, ct);
        if (target is null)
        {
            target = new BatchLocationBalance
            {
                PurchaseBatchId = batch.Id,
                StockLocationId = to.StockLocationId,
                ProductId = to.ProductId,
                RemainingBaseQty = 0,
            };
            db.BatchLocationBalances.Add(target);
        }

        source.RemainingBaseQty -= qty;
        target.RemainingBaseQty += qty;
        RemoveValue(from, qty, batch.UnitCost);
        AddValue(to, qty, batch.UnitCost);
    }

    /// <summary>FIFO transfer from <paramref name="from"/> to <paramref name="to"/> (dispatch): plans over the source's balances
    /// (see <see cref="BatchAllocator.PlanFifoAsync"/>), then moves each drawn batch with <see cref="MoveBatchAsync"/>.</summary>
    public static async Task<IReadOnlyList<BatchAllocator.Allocation>> MoveFifoAsync(
        SelloraDbContext db, StockItem from, StockItem to, decimal qty,
        Func<decimal, Sellora.Domain.Abstractions.DomainValidationException> shortageError, CancellationToken ct)
    {
        var plan = await BatchAllocator.PlanFifoAsync(db, from.StockLocationId, from.ProductId, qty, shortageError, ct);
        foreach (var draw in plan)
        {
            await MoveBatchAsync(db, from, to, draw.Allocation.Batch, draw.Allocation.QtyBaseUnits, ct);
        }
        return plan.Select(p => p.Allocation).ToList();
    }

    /// <summary>Purchase cancellation: the whole untouched batch leaves the item's location. The caller has checked that the
    /// balance and the batch total both equal PurchasedBaseQty.</summary>
    public static async Task ReverseReceiptAsync(SelloraDbContext db, StockItem item, PurchaseBatch batch, CancellationToken ct)
    {
        var balance = await FindBalanceAsync(db, batch.Id, item.StockLocationId, ct)
            ?? throw new InvalidOperationException($"Batch {batch.Id} has no balance at location {item.StockLocationId}.");
        RemoveValue(item, batch.PurchasedBaseQty, batch.UnitCost);
        balance.RemainingBaseQty = 0;
        batch.RemainingBaseQty = 0;
    }

    /// <summary>What is left of a batch at one location (0 if it never was there).</summary>
    public static async Task<decimal> BalanceAsync(SelloraDbContext db, Guid batchId, Guid locationId, CancellationToken ct) =>
        (await FindBalanceAsync(db, batchId, locationId, ct))?.RemainingBaseQty ?? 0;

    private static async Task<BatchLocationBalance?> FindBalanceAsync(SelloraDbContext db, Guid batchId, Guid locationId, CancellationToken ct) =>
        db.BatchLocationBalances.Local.FirstOrDefault(b => b.PurchaseBatchId == batchId && b.StockLocationId == locationId)
        ?? await db.BatchLocationBalances.SingleOrDefaultAsync(b => b.PurchaseBatchId == batchId && b.StockLocationId == locationId, ct);

    // Quantity arrives at a known cost: moving-average cost.
    private static void AddValue(StockItem item, decimal qty, decimal unitCost)
    {
        var newQty = item.QuantityOnHand + qty;
        item.AverageCost = newQty > 0
            ? StockCost.PerBaseUnit((item.QuantityOnHand * item.AverageCost + qty * unitCost) / newQty)
            : 0;
        item.QuantityOnHand = newQty;
    }

    // Quantity leaves at a known cost: the average is re-derived from the remaining value (never below zero).
    private static void RemoveValue(StockItem item, decimal qty, decimal unitCost)
    {
        var qtyAfter = item.QuantityOnHand - qty;
        if (qtyAfter > 0)
        {
            var valueAfter = item.QuantityOnHand * item.AverageCost - qty * unitCost;
            item.AverageCost = StockCost.PerBaseUnit(Math.Max(0, valueAfter / qtyAfter));
            item.QuantityOnHand = qtyAfter;
        }
        else
        {
            item.QuantityOnHand = 0;
            item.AverageCost = 0;
        }
    }
}
