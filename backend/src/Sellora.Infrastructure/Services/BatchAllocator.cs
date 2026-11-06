using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The one FIFO batch-consumption implementation, shared by sales and stock-adjustment decreases (and later
/// transfers and damage). Batches are drawn oldest-first: <see cref="PurchaseBatch.CreatedAt"/>, then
/// <see cref="PurchaseBatch.BatchSequence"/>. All quantities are base units.
/// </summary>
public static class BatchAllocator
{
    /// <summary>One draw on a batch: how much was taken and the batch's cost per base unit at that time.</summary>
    public sealed record Allocation(PurchaseBatch Batch, decimal QtyBaseUnits, decimal UnitCost);

    /// <summary>
    /// Caller must hold the product's StockItems lock. Plans oldest-first (CreatedAt, BatchSequence) over batches with
    /// RemainingBaseQty &gt; 0 (re-checked on the tracked value, so a batch an earlier line of the same document already
    /// drained is skipped rather than producing a zero-quantity draw), throws <paramref name="shortageError"/>(shortBy)
    /// without mutating anything if the batches cannot cover <paramref name="baseQty"/>, otherwise decrements
    /// RemainingBaseQty and returns the allocations.
    /// </summary>
    public static async Task<IReadOnlyList<Allocation>> ConsumeFifoAsync(
        SelloraDbContext db, Guid productId, decimal baseQty,
        Func<decimal, DomainValidationException> shortageError, CancellationToken ct)
    {
        // The DB filter sees saved values; EF hands back tracked instances, whose remainder may already be lower.
        var batches = await db.PurchaseBatches
            .Where(b => b.ProductId == productId && b.RemainingBaseQty > 0)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.BatchSequence)
            .ToListAsync(ct);

        var plan = new List<Allocation>();
        var remaining = baseQty;
        foreach (var batch in batches)
        {
            if (remaining <= 0) break;
            if (batch.RemainingBaseQty <= 0) continue;

            var take = Math.Min(batch.RemainingBaseQty, remaining);
            plan.Add(new Allocation(batch, take, batch.UnitCost));
            remaining -= take;
        }

        if (remaining > 0)
        {
            throw shortageError(remaining);
        }

        foreach (var allocation in plan)
        {
            allocation.Batch.RemainingBaseQty -= allocation.QtyBaseUnits;
        }

        return plan;
    }
}
