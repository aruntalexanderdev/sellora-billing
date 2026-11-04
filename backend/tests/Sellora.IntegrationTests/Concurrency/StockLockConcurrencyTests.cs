using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Two writers compete for the same product's stock, each on its own context/connection. The race is made
/// deterministic with a gate: a separate transaction holds <c>SELECT … FOR UPDATE</c> on the product's StockItems
/// row while both writers are released, and the test waits (via pg_stat_activity / pg_blocking_pids) until the
/// writers are provably blocked inside the <c>FOR UPDATE</c> of <c>StockLocks.LockStockItemsAsync</c> before
/// opening the gate. Without that row lock the writers would read stock concurrently and only block later on their
/// UPDATE, which the wait condition rejects. Only one writer may then succeed; stock must never go negative and the
/// ledger, batches and QOH must still reconcile.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockLockConcurrencyTests(PostgresFixture postgres)
{
    private const decimal StockKg = 10m;  // one 10 kg bag
    private const decimal RequestKg = 6m; // more than half of the stock
    private static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(30); // generous for slow CI runners; the wait returns as soon as the writers are blocked

    /// <summary>The first sale blocks on the gate's row lock inside LockStockItemsAsync; the second may instead
    /// queue behind it on the INV document-number advisory lock (taken earlier in CreateSaleAsync). Both waiting
    /// is required, and at least one must be waiting in the StockItems FOR UPDATE, blocked by the gate.</summary>
    [Fact]
    public async Task CreateSaleAsync_TwoConcurrentSalesExceedingStock_ExactlyOneSucceeds()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var scenario = await SeedStockAsync(database);

        var outcomes = await RaceBehindRowLockAsync(database, scenario.ProductId,
            requiredBlockedOnRowLock: 1,
            db => scenario.SellLooseKgAsync(db, RequestKg),
            db => scenario.SellLooseKgAsync(db, RequestKg));

        await AssertExactlyOneSucceededAsync(database, outcomes);
        await using var check = database.CreateDbContext();
        Assert.Equal(1, await check.SalesInvoices.CountAsync());
    }

    /// <summary>A sale and a stock adjustment take different document-number advisory locks (INV vs ADJ), so the
    /// only thing serialising them is the StockItems <c>FOR UPDATE</c> row lock in <c>StockLocks.LockStockItemsAsync</c>.
    /// Both writers must be seen waiting in that statement (one directly behind the gate, the other queued behind it).</summary>
    [Fact]
    public async Task CreateSaleAndCreateAdjustment_ConcurrentDecreasesExceedingStock_ExactlyOneSucceeds()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var scenario = await SeedStockAsync(database);

        var outcomes = await RaceBehindRowLockAsync(database, scenario.ProductId,
            requiredBlockedOnRowLock: 2,
            db => scenario.SellLooseKgAsync(db, RequestKg),
            db => scenario.AdjustAsync(db, AdjustmentType.Damage, AdjustmentDirection.Decrease, RequestKg));

        await AssertExactlyOneSucceededAsync(database, outcomes);
    }

    private static async Task<StockScenario> SeedStockAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var scenario = await StockScenario.SeedAsync(db);
        await scenario.PurchaseBagsAsync(db, StockKg / StockScenario.BagFactor);
        return scenario;
    }

    /// <summary>
    /// Holds a gate row lock on the product's StockItems row, releases all writers, waits until every writer is
    /// waiting on a lock, at least <paramref name="requiredBlockedOnRowLock"/> of them inside the StockItems
    /// <c>FOR UPDATE</c> (at least one blocked directly by the gate), then opens the gate and collects each writer's outcome
    /// (null = success).
    /// </summary>
    private static async Task<Exception?[]> RaceBehindRowLockAsync(
        TestDatabase database, Guid productId, int requiredBlockedOnRowLock, params Func<SelloraDbContext, Task>[] writers)
    {
        var contexts = writers.Select(_ => database.CreateDbContext()).ToArray();
        await using var gate = new NpgsqlConnection(database.ConnectionString);
        await using var monitor = new NpgsqlConnection(database.ConnectionString);
        Task<Exception?>[] tasks = [];
        try
        {
            foreach (var db in contexts) await db.Database.OpenConnectionAsync();
            await monitor.OpenAsync();
            await gate.OpenAsync();

            await using var gateTx = await gate.BeginTransactionAsync();
            int gatePid;
            await using (var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", gate, gateTx))
            {
                gatePid = (int)(await cmd.ExecuteScalarAsync())!;
            }
            await using (var cmd = new NpgsqlCommand("SELECT 1 FROM \"StockItems\" WHERE \"ProductId\" = @id FOR UPDATE", gate, gateTx))
            {
                cmd.Parameters.AddWithValue("id", productId);
                Assert.Equal(1, await cmd.ExecuteScalarAsync());
            }

            tasks = writers.Select((write, i) => Task.Run(async () =>
            {
                try
                {
                    await write(contexts[i]);
                    return (Exception?)null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })).ToArray();

            await WaitUntilBlockedAsync(monitor, gatePid, writers.Length, requiredBlockedOnRowLock, tasks);

            await gateTx.RollbackAsync();
            return await Task.WhenAll(tasks);
        }
        finally
        {
            // If the wait failed, close the gate so blocked writers can finish before their contexts are disposed.
            if (gate.State == System.Data.ConnectionState.Open) await gate.CloseAsync();
            try { await Task.WhenAll(tasks).WaitAsync(BlockTimeout); } catch { /* outcome already reported */ }
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }

    private static async Task WaitUntilBlockedAsync(
        NpgsqlConnection monitor, int gatePid, int expectedWaiting, int requiredBlockedOnRowLock, Task<Exception?>[] writers)
    {
        // Each statement on the monitor connection is its own transaction, so pg_stat_activity is re-read every poll.
        // Row-lock waiters queue: the first waits on the gate's transactionid (pg_blocking_pids = gate), later ones on
        // the tuple lock held by the first waiter. So count writers waiting inside the StockItems FOR UPDATE, and
        // separately require that at least one of them is blocked directly by the gate.
        const string sql = """
            SELECT
              count(*) FILTER (WHERE wait_event_type = 'Lock'),
              count(*) FILTER (WHERE wait_event_type = 'Lock' AND query ILIKE '%"StockItems"%FOR UPDATE%'),
              count(*) FILTER (WHERE wait_event_type = 'Lock' AND query ILIKE '%"StockItems"%FOR UPDATE%'
                                 AND @gate = ANY(pg_blocking_pids(pid)))
            FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid() AND pid <> @gate
            """;

        var deadline = DateTime.UtcNow + BlockTimeout;
        long waiting = 0, onRowLock = 0, behindGate = 0;
        while (DateTime.UtcNow < deadline)
        {
            await using (var cmd = new NpgsqlCommand(sql, monitor))
            {
                cmd.Parameters.AddWithValue("gate", gatePid);
                await using var reader = await cmd.ExecuteReaderAsync();
                await reader.ReadAsync();
                waiting = reader.GetInt64(0);
                onRowLock = reader.GetInt64(1);
                behindGate = reader.GetInt64(2);
            }

            if (waiting >= expectedWaiting && onRowLock >= requiredBlockedOnRowLock && behindGate >= 1) return;

            var finished = writers.FirstOrDefault(t => t.IsCompleted);
            if (finished is not null)
            {
                Assert.Fail("Writers did not block on the StockItems row lock: a writer finished while the gate held the " +
                            $"row lock (waiting={waiting}, in FOR UPDATE={onRowLock}, directly behind gate={behindGate}). " +
                            $"Writer outcome: {(await finished)?.ToString() ?? "succeeded"}");
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Writers did not block on the StockItems row lock within {BlockTimeout.TotalSeconds:0} s: " +
                    $"{waiting} of {expectedWaiting} writers waiting on a lock, {onRowLock} of {requiredBlockedOnRowLock} " +
                    "required waiting in StockLocks.LockStockItemsAsync (SELECT … FOR UPDATE), " +
                    $"{behindGate} blocked directly by the gate transaction (need ≥ 1).");
    }

    private static async Task AssertExactlyOneSucceededAsync(TestDatabase database, Exception?[] outcomes)
    {
        Assert.Equal(1, outcomes.Count(e => e is null));
        var failure = Assert.Single(outcomes, e => e is not null);
        Assert.IsType<DomainValidationException>(failure);

        await using var db = database.CreateDbContext();
        var stock = await db.StockItems.SingleAsync();
        Assert.True(stock.QuantityOnHand >= 0, $"QuantityOnHand went negative: {stock.QuantityOnHand}");
        Assert.Equal(StockKg - RequestKg, stock.QuantityOnHand);
        await StockInvariants.AssertHoldAsync(db);
    }
}
