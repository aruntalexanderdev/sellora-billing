using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The one FIFO batch-consumption implementation, shared by sales and stock-adjustment decreases (and later
/// transfers and damage). Batches are drawn oldest-first: <see cref="PurchaseBatch.CreatedAt"/>, then
/// <see cref="PurchaseBatch.BatchSequence"/>. Since Phase 8 it draws over the <see cref="BatchLocationBalance"/> rows of one
/// (location, product), so stock at another location is never consumed. All quantities are base units. Reached through
/// <see cref="StockPosting.IssueFifoAsync"/>; it keeps the balance and the batch's dual-written total in step.
/// </summary>
public static class BatchAllocator
{
    /// <summary>One draw on a batch: how much was taken and the batch's cost per base unit at that time.</summary>
    public sealed record Allocation(PurchaseBatch Batch, decimal QtyBaseUnits, decimal UnitCost);

    /// <summary>
    /// Caller must hold the (location, product) StockItems lock. Plans oldest-first (CreatedAt, BatchSequence) over the
    /// balances at the location with RemainingBaseQty &gt; 0 (re-checked on the tracked value, so a batch an earlier line of
    /// the same document already drained is skipped rather than producing a zero-quantity draw), throws
    /// <paramref name="shortageError"/>(shortBy) without mutating anything if they cannot cover <paramref name="baseQty"/>,
    /// otherwise decrements the balances and the batches' RemainingBaseQty and returns the allocations.
    /// </summary>
    public static async Task<IReadOnlyList<Allocation>> ConsumeFifoAsync(
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

        var plan = new List<(BatchLocationBalance Balance, Allocation Allocation)>();
        var remaining = baseQty;
        foreach (var candidate in candidates)
        {
            if (remaining <= 0) break;
            if (candidate.Balance.RemainingBaseQty <= 0) continue;

            var take = Math.Min(candidate.Balance.RemainingBaseQty, remaining);
            plan.Add((candidate.Balance, new Allocation(candidate.Batch, take, candidate.Batch.UnitCost)));
            remaining -= take;
        }

        if (remaining > 0)
        {
            throw shortageError(remaining);
        }

        foreach (var (balance, allocation) in plan)
        {
            balance.RemainingBaseQty -= allocation.QtyBaseUnits;
            allocation.Batch.RemainingBaseQty -= allocation.QtyBaseUnits;
        }

        return plan.Select(p => p.Allocation).ToList();
    }
}
