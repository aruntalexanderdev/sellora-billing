using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The one availability check (FI-D7 / FI-D23) used by sales, adjustment decreases, purchase returns from a named batch and
/// transfer dispatch (including van loading): available = quantity on hand - reserved, per (location, product), and per
/// (batch, location) where a batch is named. Callers run it under the existing stock-row locks (FOR UPDATE on the StockItems,
/// <see cref="StockLocks"/>), on the locked rows, so the quantities read here cannot change before the caller posts. It never
/// locks or posts itself. Messages and field keys stay with the caller (shortage factory).
/// </summary>
public static class StockAvailability
{
    /// <summary>A requested base quantity against one (location, product).</summary>
    public readonly record struct Requirement(Guid LocationId, Guid ProductId, decimal Qty);

    /// <summary>A requirement that does not fit: what was asked, what is available, and the quantity on hand it came from.</summary>
    public readonly record struct Shortage(Guid LocationId, Guid ProductId, decimal Requested, decimal Available);

    // Test seam: a scope-local replacement for the reserved-quantity source (AsyncLocal, so parallel tests never see each other's).
    private static readonly AsyncLocal<ReservedSource?> Override = new();

    /// <summary>Reserved quantity for a (location, product) and optionally a batch at that location. Null batch = the whole product.</summary>
    internal delegate Task<decimal> ReservedSource(SelloraDbContext db, Guid locationId, Guid productId, Guid? batchId, CancellationToken ct);

    /// <summary>Replaces the reserved-quantity source for the current async flow until the returned scope is disposed. Tests only.</summary>
    /// <remarks>Test-only seam (internal; visible to the test assemblies only). Requests can never set it.</remarks>
    internal static IDisposable UseReservedSource(ReservedSource source)
    {
        var previous = Override.Value;
        Override.Value = source;
        return new Scope(previous);
    }

    /// <summary>The single reservation seam (FI-D30). With no batch: <c>StockItem.ReservedQuantity</c> of the (location, product), read from the
    /// tracked row when the caller holds it under the StockItem lock (the locked, current value), else from the database. Reservations are per
    /// product and location, never per batch (batches stay FIFO, chosen at dispatch), so a batch argument reserves nothing by itself: the
    /// batch cap is applied through the product's <c>on hand - reserved</c> in <see cref="AvailableInBatchAsync"/>.</summary>
    public static async Task<decimal> ReservedAsync(SelloraDbContext db, Guid locationId, Guid productId, Guid? batchId, CancellationToken ct)
    {
        if (Override.Value is { } source) return await source(db, locationId, productId, batchId, ct);
        if (batchId is not null) return 0m;

        var tracked = db.StockItems.Local.FirstOrDefault(s => s.StockLocationId == locationId && s.ProductId == productId);
        if (tracked is not null) return tracked.ReservedQuantity;
        return await db.StockItems.AsNoTracking()
            .Where(s => s.StockLocationId == locationId && s.ProductId == productId)
            .Select(s => s.ReservedQuantity)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Available base quantity of one locked StockItem (a missing row counts as 0 on hand).</summary>
    public static async Task<decimal> AvailableAsync(SelloraDbContext db, Guid locationId, Guid productId, StockItem? lockedItem, CancellationToken ct) =>
        (lockedItem?.QuantityOnHand ?? 0) - (lockedItem is not null && Override.Value is null
            ? lockedItem.ReservedQuantity   // the locked row's own value: nothing else can change it before the caller posts
            : await ReservedAsync(db, locationId, productId, null, ct));

    /// <summary>First requirement (requests for the same location and product are summed, first-seen order) that exceeds its
    /// available quantity, else null. <paramref name="lockedItems"/> are the rows returned by
    /// <see cref="StockLocks.EnsureAndLockStockItemsAsync"/>.</summary>
    public static async Task<Shortage?> FindShortageAsync(
        SelloraDbContext db, IReadOnlyDictionary<(Guid LocationId, Guid ProductId), StockItem> lockedItems,
        IEnumerable<Requirement> requirements, CancellationToken ct)
    {
        var totals = new Dictionary<(Guid, Guid), decimal>();
        var order = new List<(Guid, Guid)>();
        foreach (var r in requirements)
        {
            var key = (r.LocationId, r.ProductId);
            if (!totals.TryAdd(key, r.Qty)) totals[key] += r.Qty; else order.Add(key);
        }

        foreach (var key in order)
        {
            var available = await AvailableAsync(db, key.Item1, key.Item2, lockedItems.GetValueOrDefault(key), ct);
            if (totals[key] > available) return new Shortage(key.Item1, key.Item2, totals[key], available);
        }
        return null;
    }

    /// <summary>Throws the caller's exception for the first shortage.</summary>
    public static async Task EnsureAsync(
        SelloraDbContext db, IReadOnlyDictionary<(Guid LocationId, Guid ProductId), StockItem> lockedItems,
        IEnumerable<Requirement> requirements, Func<Shortage, DomainValidationException> shortageError, CancellationToken ct)
    {
        if (await FindShortageAsync(db, lockedItems, requirements, ct) is { } shortage) throw shortageError(shortage);
    }

    /// <summary>What a named batch can still give at a location: its balance there minus what is reserved against it.</summary>
    public static async Task<decimal> AvailableInBatchAsync(SelloraDbContext db, Guid batchId, Guid locationId, Guid productId, CancellationToken ct)
    {
        var available = await StockPosting.BalanceAsync(db, batchId, locationId, ct) - await ReservedAsync(db, locationId, productId, batchId, ct);

        // FI-D30: batch availability = min(batch balance, on hand - reserved). Reservations are not batch-specific, so what is reserved
        // at the location for the product can never be taken from any single batch. Nothing reserved = the batch balance alone.
        var reserved = await ReservedAsync(db, locationId, productId, null, ct);
        if (reserved > 0)
        {
            var tracked = db.StockItems.Local.FirstOrDefault(s => s.StockLocationId == locationId && s.ProductId == productId);
            var onHand = tracked?.QuantityOnHand ?? await db.StockItems.AsNoTracking()
                .Where(s => s.StockLocationId == locationId && s.ProductId == productId)
                .Select(s => s.QuantityOnHand).SingleOrDefaultAsync(ct);
            available = Math.Min(available, onHand - reserved);
        }
        return available;
    }

    /// <summary>Throws the caller's exception when <paramref name="qty"/> exceeds the named batch's availability at the location.
    /// The caller holds the StockItem lock of (location, product), which also serialises batch balances.</summary>
    public static async Task EnsureInBatchAsync(
        SelloraDbContext db, Guid batchId, Guid locationId, Guid productId, decimal qty,
        Func<decimal, DomainValidationException> shortageError, CancellationToken ct)
    {
        var available = await AvailableInBatchAsync(db, batchId, locationId, productId, ct);
        if (qty > available) throw shortageError(available);
    }

    private sealed class Scope(ReservedSource? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
