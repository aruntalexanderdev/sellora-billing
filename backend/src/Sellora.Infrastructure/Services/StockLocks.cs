using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Row locks that serialise concurrent stock changes for the same products. Every operation that consumes
/// batches FIFO takes the product's StockItem lock first, so two sales (or a sale and an adjustment) can never
/// read the same batch quantities and both decrement them. Locks are taken in product-id order so two
/// multi-product operations cannot deadlock, and they are released when the surrounding transaction ends. The
/// in-memory test provider has no row locks, hence the relational check.
/// </summary>
internal static class StockQuantity
{
    /// <summary>Stock is stored to 3 decimal places (numeric(18,3)). A quantity that needs more would be rounded
    /// on save while the arithmetic that produced it was not, letting batch and stock totals drift apart — so it is
    /// refused up front instead.</summary>
    public static void EnsureStorable(decimal baseQty, string field)
    {
        if (Math.Round(baseQty, 3, MidpointRounding.AwayFromZero) != baseQty)
        {
            throw new Sellora.Domain.Abstractions.DomainValidationException(field, "Quantity has too many decimal places — stock is tracked to 3 decimals in the base unit.");
        }
    }
}

internal static class StockLocks
{
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAsync(SelloraDbContext db, CancellationToken ct) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;

    /// <summary>
    /// Serialises generation of one kind of document number (PUR, INV, ADJ…) until the surrounding transaction
    /// ends. Numbers are derived from a row count, so two concurrent creations would otherwise pick the same
    /// number and one would fail on the unique index. Callers must be inside a transaction.
    /// </summary>
    public static async Task LockDocumentNumberAsync(SelloraDbContext db, string series, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"sellora:docno:" + series}))", ct);
    }

    public static async Task LockStockItemsAsync(SelloraDbContext db, IEnumerable<Guid> productIds, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;

        var ids = productIds.Distinct().Order().ToArray();
        if (ids.Length == 0) return;

        await db.StockItems
            .FromSqlInterpolated($"SELECT * FROM \"StockItems\" WHERE \"ProductId\" = ANY({ids}) ORDER BY \"ProductId\" FOR UPDATE")
            .ToListAsync(ct);
    }
}
