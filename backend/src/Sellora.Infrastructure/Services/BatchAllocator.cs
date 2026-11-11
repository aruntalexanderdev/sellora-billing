using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The one FIFO batch-consumption implementation, shared by sales, stock-adjustment decreases and transfer dispatch. Batches are drawn oldest-first: <see cref="PurchaseBatch.CreatedAt"/>, then
/// <see cref="PurchaseBatch.BatchSequence"/>. Since Phase 8 it draws over the <see cref="BatchLocationBalance"/> rows of one
/// (location, product), so stock at another location is never consumed. All quantities are base units. Reached through
/// <see cref="StockPosting.IssueFifoAsync"/>; it keeps the balance and the batch's dual-written total in step.
/// </summary>
public static class BatchAllocator
{
    /// <summary>One draw on a batch: how much was taken and the batch's cost per base unit at that time.</summary>
    public sealed record Allocation(PurchaseBatch Batch, decimal QtyBaseUnits, decimal UnitCost);

    /// <summary>One planned draw: the balance row it comes off and the allocation it yields.</summary>
    public sealed record PlannedDraw(BatchLocationBalance Balance, Allocation Allocation);

    /// <summary>
    /// Caller must hold the (location, product) StockItems lock. Plans oldest-first (CreatedAt, BatchSequence) over the
    /// balances at the location with RemainingBaseQty &gt; 0 (re-checked on the tracked value, so a batch an earlier line of
    /// the same document already drained is skipped rather than producing a zero-quantity draw) and throws
    /// <paramref name="shortageError"/>(shortBy) if they cannot cover <paramref name="baseQty"/>. Mutates nothing: the
    /// consume and move operations in <see cref="StockPosting"/> apply the plan.
    /// </summary>
    public static async Task<IReadOnlyList<PlannedDraw>> PlanFifoAsync(
        SelloraDbContext db, Guid locationId, Guid productId, decimal baseQty,
        Func<decimal, DomainValidationException> shortageError, CancellationToken ct)
    {
        // The DB filter sees saved values; EF hands back tracked instances, whose remainder may already be lower.
        var candidates = await (
            from balance in db.BatchLocationBalances
            join batch in db.PurchaseBatches on balance.PurchaseBatchId equals batch.Id
            where balance.StockLocationId == locationId && balance.ProductId == productId && balance.RemainingBaseQty > 0
            orderby batch.CreatedAt, batch.BatchSequence
            select new { Balance = balance, Batch = batch }).ToListAsync(ct);

        var plan = new List<PlannedDraw>();
        var remaining = baseQty;
        foreach (var candidate in candidates)
        {
            if (remaining <= 0) break;
            if (candidate.Balance.RemainingBaseQty <= 0) continue;

            var take = Math.Min(candidate.Balance.RemainingBaseQty, remaining);
            plan.Add(new PlannedDraw(candidate.Balance, new Allocation(candidate.Batch, take, candidate.Batch.UnitCost)));
            remaining -= take;
        }

        if (remaining > 0)
        {
            throw shortageError(remaining);
        }

        return plan;
    }

    /// <summary>Plan (see <see cref="PlanFifoAsync"/>) and apply: decrements the balances and the batches' RemainingBaseQty
    /// and returns the allocations.</summary>
    public static async Task<IReadOnlyList<Allocation>> ConsumeFifoAsync(
        SelloraDbContext db, Guid locationId, Guid productId, decimal baseQty,
        Func<decimal, DomainValidationException> shortageError, CancellationToken ct)
    {
        var plan = await PlanFifoAsync(db, locationId, productId, baseQty, shortageError, ct);

        foreach (var draw in plan)
        {
            draw.Balance.RemainingBaseQty -= draw.Allocation.QtyBaseUnits;
            draw.Allocation.Batch.RemainingBaseQty -= draw.Allocation.QtyBaseUnits;
        }

        return plan.Select(p => p.Allocation).ToList();
    }
}
