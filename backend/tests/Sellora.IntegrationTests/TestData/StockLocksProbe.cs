using Microsoft.EntityFrameworkCore.Storage;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Calls the internal <c>StockLocks</c> primitives (the Phase 9 transfer reuse, D4) by reflection: the production assembly exposes no
/// InternalsVisibleTo, and these tests must drive the multi-location lock primitive directly because the 8a services only ever use the
/// default store. Signatures are asserted so a rename fails loudly.
/// </summary>
public static class StockLocksProbe
{
    private static readonly Type Locks = typeof(PurchaseService).Assembly.GetType("Sellora.Infrastructure.Services.StockLocks", throwOnError: true)!;

    public static async Task<IDbContextTransaction> BeginAsync(SelloraDbContext db)
    {
        var method = Locks.GetMethod("BeginAsync") ?? throw new InvalidOperationException("StockLocks.BeginAsync not found.");
        var tx = await (Task<IDbContextTransaction?>)method.Invoke(null, [db, CancellationToken.None])!;
        return tx ?? throw new InvalidOperationException("Expected a relational transaction.");
    }

    /// <summary>EnsureAndLockStockItemsAsync: ensures the rows exist, then FOR UPDATE in (location, product) order; returns the tracked rows' keys.</summary>
    public static async Task<IReadOnlyList<(Guid LocationId, Guid ProductId)>> EnsureAndLockAsync(SelloraDbContext db, params (Guid LocationId, Guid ProductId)[] keys)
    {
        var method = Locks.GetMethod("EnsureAndLockStockItemsAsync") ?? throw new InvalidOperationException("StockLocks.EnsureAndLockStockItemsAsync not found.");
        var task = (Task)method.Invoke(null, [db, keys, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var keyList = (System.Collections.IEnumerable)result.GetType().GetProperty("Keys")!.GetValue(result)!;
        return keyList.Cast<(Guid, Guid)>().ToList();
    }

    public static async Task LockLocationsKeyShareAsync(SelloraDbContext db, params Guid[] ids)
    {
        var method = Locks.GetMethod("LockLocationsKeyShareAsync") ?? throw new InvalidOperationException("StockLocks.LockLocationsKeyShareAsync not found.");
        await (Task)method.Invoke(null, [db, ids, CancellationToken.None])!;
    }
}
