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
internal static class StockLocks
{
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAsync(SelloraDbContext db, CancellationToken ct) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;

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
