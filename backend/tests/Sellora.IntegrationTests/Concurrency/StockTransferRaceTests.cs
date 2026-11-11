using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 9 locks on PostgreSQL (design D6), deterministic with <see cref="LockRace"/> where order matters and parallel hammers where the invariant is the point:
/// double dispatch with / without an Idempotency-Key, two receivers over-receiving, opposite-direction transfers of the same product and batch (no deadlock),
/// first dispatches racing for the Transit stock row, dispatch vs a source sale (never negative stock), dispatch / receipt vs a location deactivation
/// committed in the window. After every race the stock invariants and the validation script V1-V8 hold.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockTransferRaceTests(PostgresFixture postgres)
{
    private const string TransfersPattern = """%"StockTransfers"%""";

    private static DispatchStockTransferInput DispatchInput(StockTransfer transfer, string? key = null) =>
        new(transfer.Lines.OrderBy(l => l.LineNo).Select(l => new DispatchStockTransferLineInput(l.Id, l.ProductUnitId, l.RequestedQty)).ToList(), key);

    private static async Task<Exception?[]> ParallelAsync(params Func<Task>[] work)
    {
        var start = new TaskCompletionSource();
        var tasks = work.Select(w => Task.Run(async () =>
        {
            await start.Task;
            try { await w(); return (Exception?)null; }
            catch (Exception ex) { return ex; }
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
    }

    private static void AssertNoDeadlock(IEnumerable<Exception?> outcomes) =>
        Assert.DoesNotContain(outcomes, o => o is Npgsql.PostgresException { SqlState: "40P01" or "40001" } || o?.InnerException is Npgsql.PostgresException { SqlState: "40P01" or "40001" });

    private static string Field(Exception? ex) => Assert.IsType<DomainValidationException>(ex).Field;

    private static async Task<(TransferScenario T, StockTransfer Transfer)> ApprovedAsync(TestDatabase database, decimal pieces = 100m, decimal stock = 200m)
    {
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, stock, 5m);
        return (t, await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Pc, pieces)));
    }

    // ---- double dispatch ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DoubleDispatch_WithoutKey_BothWaitOnTheHeader_OneDispatches_TheOtherIsRefusedOnStatus()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, transfer) = await ApprovedAsync(database);
        var loaded = await t.ReloadAsync(transfer.Id);

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockTransfers", "Id", transfer.Id));
        var w1 = await race.StartAsync("dispatch 1", db => new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded), t.UserId), branchId: t.Main);
        await race.WaitBlockedAsync(w1, TransfersPattern, "dispatch must queue on the transfer header.");
        var w2 = await race.StartAsync("dispatch 2", db => new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded), t.UserId), branchId: t.Main);
        await race.WaitBlockedAsync(w2, TransfersPattern);

        var outcomes = await race.ReleaseAsync();

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Equal("status", Field(outcomes.Single(o => o is not null)));
        Assert.Equal((100m, 100m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.Transit)));
        Assert.Equal(3, await t.MovementCountAsync()); // the purchase's movement + one dispatch pair
        await t.AssertInvariantsAsync("double dispatch without key");
    }

    [Fact]
    public async Task DoubleDispatch_WithTheSameKey_SecondWaitsOnTheKey_AndReplaysTheFirst()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, transfer) = await ApprovedAsync(database);
        var loaded = await t.ReloadAsync(transfer.Id);

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockTransfers", "Id", transfer.Id));
        var w1 = await race.StartAsync("dispatch 1", db => new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded, "same-key"), t.UserId), branchId: t.Main);
        await race.WaitBlockedAsync(w1, TransfersPattern);
        var w2 = await race.StartAsync("dispatch 2", db => new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded, "same-key"), t.UserId), branchId: t.Main);
        await race.WaitBlockedAsync(w2, hint: "the second must wait on the idempotency advisory lock held by the first.");

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, o => Assert.Null(o)); // both succeed: the second returns the stored dispatch
        Assert.Equal((100m, 100m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.Transit)));
        Assert.Equal(StockTransferStatus.Dispatched, (await t.ReloadAsync(transfer.Id)).Status);
        await t.AssertInvariantsAsync("double dispatch with key");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoubleDispatch_ParallelHammer_MovesTheStockExactlyOnce(bool withKey)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, transfer) = await ApprovedAsync(database);
        var loaded = await t.ReloadAsync(transfer.Id);

        var outcomes = await ParallelAsync(Enumerable.Range(0, 6).Select<int, Func<Task>>(_ => async () =>
        {
            await using var db = t.Db(t.Main);
            await new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded, withKey ? "hammer" : null), t.UserId);
        }).ToArray());

        AssertNoDeadlock(outcomes);
        if (withKey) Assert.All(outcomes, o => Assert.Null(o));
        else
        {
            Assert.Equal(1, outcomes.Count(o => o is null));
            Assert.All(outcomes.Where(o => o is not null), o => Assert.Equal("status", Field(o)));
        }
        Assert.Equal((100m, 100m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.Transit)));
        await t.AssertInvariantsAsync("dispatch hammer");
    }

    // ---- two receivers -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoReceivers_BothOverReceiving_OneWins_TheOtherIsRefusedOnAcceptedQuantity()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, transfer) = await ApprovedAsync(database);
        await t.DispatchAsRequestedAsync(transfer.Id);
        var line = (await t.ReloadAsync(transfer.Id)).Lines.Single();
        ReceiveStockTransferInput Input() => new(null, [new ReceiveStockTransferLineInput(line.Id, t.Pc, 60m, 0m)]);

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockTransfers", "Id", transfer.Id));
        var w1 = await race.StartAsync("receive 1", db => new StockTransferService(db, null).ReceiveAsync(transfer.Id, Input(), t.UserId), branchId: t.Blr);
        await race.WaitBlockedAsync(w1, TransfersPattern);
        var w2 = await race.StartAsync("receive 2", db => new StockTransferService(db, null).ReceiveAsync(transfer.Id, Input(), t.UserId), branchId: t.Blr);
        await race.WaitBlockedAsync(w2, TransfersPattern);

        var outcomes = await race.ReleaseAsync();

        Assert.Null(outcomes[0]);                                       // queued first, wins
        Assert.Equal("lines[0].acceptedQuantity", Field(outcomes[1]));  // 60 > 40 outstanding
        Assert.Equal((60m, 40m), (await t.QohAsync(t.BlrStore), await t.QohAsync(t.Transit)));
        Assert.Single((await t.ReloadAsync(transfer.Id)).Receipts);
        await t.AssertInvariantsAsync("two receivers");
    }

    [Fact]
    public async Task EightParallelReceivers_OfTwentyEach_AgainstOneHundred_FiveSucceedThreeAreRefused_NothingOverReceived()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, transfer) = await ApprovedAsync(database);
        await t.DispatchAsRequestedAsync(transfer.Id);
        var line = (await t.ReloadAsync(transfer.Id)).Lines.Single();

        var outcomes = await ParallelAsync(Enumerable.Range(0, 8).Select<int, Func<Task>>(_ => async () =>
        {
            await using var db = t.Db(t.Blr);
            await new StockTransferService(db, null).ReceiveAsync(transfer.Id, new ReceiveStockTransferInput(null, [new ReceiveStockTransferLineInput(line.Id, t.Pc, 20m, 0m)]), t.UserId);
        }).ToArray());

        AssertNoDeadlock(outcomes);
        Assert.Equal(5, outcomes.Count(o => o is null));
        // Once complete, the losers fail on status; before that on the quantity: both are 400s, never a 500.
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<DomainValidationException>(o));
        Assert.Equal((100m, 0m), (await t.QohAsync(t.BlrStore), await t.QohAsync(t.Transit)));
        Assert.Equal(StockTransferStatus.Completed, (await t.ReloadAsync(transfer.Id)).Status);
        await t.AssertInvariantsAsync("receiver hammer");
    }

    // ---- opposite directions, same product and batch ----------------------------------------------------------------------------------------

    [Fact]
    public async Task OppositeDirectionTransfers_OfTheSameProductAndBatch_RunInParallel_WithoutDeadlock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 400m, 5m);
        // Put the SAME batch at both stores first.
        var seed = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Pc, 200m));
        await t.DispatchAsRequestedAsync(seed.Id);
        await t.ReceiveAsync(seed.Id, t.Pc, 200m);
        await t.AssertInvariantsAsync("seeded both stores");
        var value = await t.CompanyValueAsync();

        for (var round = 0; round < 8; round++)
        {
            var ab = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Pc, 10m));
            var ba = await t.CreateAsync(t.BlrStore, t.MainStore, [t.Pen(t.Pc, 10m)], branch: t.Blr);
            await t.ApproveAsync(ba.Id, t.Blr);

            var dispatches = await ParallelAsync(
                async () => await t.DispatchAsRequestedAsync(ab.Id, t.Main),
                async () => await t.DispatchAsRequestedAsync(ba.Id, t.Blr));
            AssertNoDeadlock(dispatches);
            Assert.All(dispatches, o => Assert.Null(o));

            var receipts = await ParallelAsync(
                async () => await t.ReceiveAsync(ab.Id, t.Pc, 10m, branch: t.Blr),
                async () => await t.ReceiveAsync(ba.Id, t.Pc, 10m, branch: t.Main));
            AssertNoDeadlock(receipts);
            Assert.All(receipts, o => Assert.Null(o));
            await t.AssertInvariantsAsync($"opposite round {round}");
        }
        Assert.Equal((200m, 200m, 0m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.BlrStore), await t.QohAsync(t.Transit)));
        await t.AssertValueConservedAsync(value, "opposite hammer");
    }

    [Fact]
    public async Task OppositeDispatchesMixedWithReceipts_OfTwoTransfers_AreDeadlockFree()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 100m, 5m);
        await t.BuyAsync(t.Blr, t.BlrStore, t.Pc, 100m, 6m);

        for (var round = 0; round < 6; round++)
        {
            var ab = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Pc, 7m));
            var ba = await t.CreateAsync(t.BlrStore, t.MainStore, [t.Pen(t.Pc, 7m)], branch: t.Blr);
            await t.ApproveAsync(ba.Id, t.Blr);
            await t.DispatchAsRequestedAsync(ab.Id, t.Main); // ab is in flight while ba dispatches and ab is received
            var mixed = await ParallelAsync(
                async () => await t.DispatchAsRequestedAsync(ba.Id, t.Blr),
                async () => await t.ReceiveAsync(ab.Id, t.Pc, 7m, branch: t.Blr));
            AssertNoDeadlock(mixed);
            Assert.All(mixed, o => Assert.Null(o));
            await t.ReceiveAsync(ba.Id, t.Pc, 7m, branch: t.Main);
            await t.AssertInvariantsAsync($"mixed round {round}");
        }
    }

    [Fact]
    public async Task FirstDispatchesOfTheSameProduct_FromTwoSources_RaceForTheTransitRow_WithoutErrors()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 50m, 5m);
        await t.BuyAsync(t.Blr, t.BlrStore, t.Pc, 50m, 6m);
        var fromMain = await t.ApprovedAtAsync(t.Main, t.MainStore, t.BlrStore, t.Pen(t.Pc, 20m));
        var fromBlr = await t.ApprovedAtAsync(t.Blr, t.BlrStore, t.MainStore, t.Pen(t.Pc, 15m));
        await using (var db = t.Db()) Assert.Equal(0, await db.StockItems.CountAsync(i => i.StockLocationId == t.Transit)); // no Transit row exists yet

        var outcomes = await ParallelAsync(
            async () => await t.DispatchAsRequestedAsync(fromMain.Id, t.Main),
            async () => await t.DispatchAsRequestedAsync(fromBlr.Id, t.Blr));

        AssertNoDeadlock(outcomes);
        Assert.All(outcomes, o => Assert.Null(o));
        Assert.Equal(35m, await t.QohAsync(t.Transit));
        await t.AssertInvariantsAsync("first dispatches");
    }

    // ---- dispatch vs a sale at the source -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DispatchQueuedBeforeASourceSale_Wins_TheSaleIsRefused_NoNegativeStock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, transfer) = await ApprovedAsync(database, pieces: 40m, stock: 40m);
        var loaded = await t.ReloadAsync(transfer.Id);

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            var rows = await gate.Database.SqlQueryRaw<decimal>("""SELECT "QuantityOnHand" AS "Value" FROM "StockItems" WHERE "StockLocationId" = {0} AND "ProductId" = {1} FOR UPDATE""", t.MainStore, t.PenId).ToListAsync();
            Assert.Single(rows);
        });
        var dispatch = await race.StartAsync("dispatch", db => new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded), t.UserId), branchId: t.Main);
        await race.WaitBlockedAsync(dispatch, """%"StockItems"%""");
        var sale = await race.StartAsync("sale", db => new SalesService(db, LocationScenario.CompanyLevel).CreateSaleAsync(
            new Sellora.Domain.Sales.CreateSaleInput(t.L.S.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0,
                [new Sellora.Domain.Sales.SaleLineInput(t.PenId, t.Pc, 1m, 10m, 0)]), t.UserId), branchId: t.Main);
        await race.WaitBlockedAsync(sale, """%"StockItems"%""");

        var outcomes = await race.ReleaseAsync();

        Assert.Null(outcomes[0]);
        Assert.IsType<DomainValidationException>(outcomes[1]); // nothing left at the source
        Assert.Equal((0m, 40m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.Transit)));
        await t.AssertInvariantsAsync("dispatch before sale");
    }

    [Fact]
    public async Task DispatchVsSourceSale_ParallelHammer_ExactlyOneWins_NeverNegativeStock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        for (var round = 0; round < 6; round++)
        {
            await t.BuyAsync(null, t.MainStore, t.Pc, 30m, 5m);
            var onHand = await t.QohAsync(t.MainStore);
            var transfer = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Pc, onHand)); // the whole stock
            var loaded = await t.ReloadAsync(transfer.Id);

            var outcomes = await ParallelAsync(
                async () => { await using var db = t.Db(t.Main); await new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded), t.UserId); },
                async () => await t.SellAsync(null, t.MainStore, t.Pc, 1m, 10m));

            AssertNoDeadlock(outcomes);
            Assert.Equal(1, outcomes.Count(o => o is null)); // all of it moves, or one piece sells and the dispatch of "all" is refused: never both
            Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<DomainValidationException>(o));
            await t.AssertInvariantsAsync($"dispatch vs sale round {round}");
            if (outcomes[0] is null) // clean up: return the shipment so the next round starts from a known state
            {
                await t.ReceiveAsync(transfer.Id, t.Pc, onHand, branch: t.Blr);
            }
        }
    }

    // ---- dispatch / receipt vs a deactivation committed in the window --------------------------------------------------------------------

    private static Func<SelloraDbContext, Task> DeactivateLocation(Guid location) => async gate =>
    {
        await LockRace.LockRow("StockLocations", "Id", location)(gate);
        await gate.Database.ExecuteSqlRawAsync("""UPDATE "StockLocations" SET "IsActive" = false WHERE "Id" = {0}""", location);
    };

    [Fact]
    public async Task Dispatch_SourceDeactivatedBetweenCheckAndLock_IsRefusedWithNothingMoved()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(t.Blr, t.L.BlrWarehouse, t.Pc, 50m, 5m);
        var transfer = await t.ApprovedAtAsync(t.Blr, t.L.BlrWarehouse, t.MainStore, t.Pen(t.Pc, 20m));
        var loaded = await t.ReloadAsync(transfer.Id);
        var before = await t.MovementCountAsync();

        await using var race = await LockRace.BeginAsync(database, DeactivateLocation(t.L.BlrWarehouse));
        var writer = await race.StartAsync("dispatch", db => new StockTransferService(db, null).DispatchAsync(transfer.Id, DispatchInput(loaded), t.UserId), branchId: t.Blr);
        await race.WaitBlockedAsync(writer, """%"StockLocations"%""", "the dispatch must wait on the location KEY SHARE behind the deactivation.");

        var outcomes = await race.ReleaseAsync(commit: true);

        Assert.Equal("stockLocationId", Field(outcomes.Single()));
        Assert.Equal((50m, 0m, before), (await t.QohAsync(t.L.BlrWarehouse), await t.QohAsync(t.Transit), await t.MovementCountAsync()));
        Assert.Equal(StockTransferStatus.Approved, (await t.ReloadAsync(transfer.Id)).Status);
        await t.AssertInvariantsAsync("dispatch vs deactivation");
    }

    [Fact]
    public async Task Receive_DestinationDeactivatedBetweenCheckAndLock_IsRefused_TheGoodsStayInTransit()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 50m, 5m);
        var transfer = await t.ApprovedAsync(t.MainStore, t.L.BlrWarehouse, t.Pen(t.Pc, 20m));
        await t.DispatchAsRequestedAsync(transfer.Id);
        var line = (await t.ReloadAsync(transfer.Id)).Lines.Single();

        await using var race = await LockRace.BeginAsync(database, DeactivateLocation(t.L.BlrWarehouse));
        var writer = await race.StartAsync("receive", db => new StockTransferService(db, null).ReceiveAsync(transfer.Id,
            new ReceiveStockTransferInput(null, [new ReceiveStockTransferLineInput(line.Id, t.Pc, 20m, 0m)]), t.UserId), branchId: t.Blr);
        await race.WaitBlockedAsync(writer, """%"StockLocations"%""");

        var outcomes = await race.ReleaseAsync(commit: true);

        Assert.Equal("stockLocationId", Field(outcomes.Single()));
        Assert.Equal((0m, 20m), (await t.QohAsync(t.L.BlrWarehouse), await t.QohAsync(t.Transit)));
        Assert.Empty((await t.ReloadAsync(transfer.Id)).Receipts);
        await t.AssertInvariantsAsync("receive vs deactivation");
    }

    [Fact]
    public async Task WarehouseDeactivation_AlongsideADispatch_IsAlwaysRefused_WhileTheTransferIsOpen()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(t.Blr, t.L.BlrWarehouse, t.Pc, 50m, 5m);
        var transfer = await t.ApprovedAtAsync(t.Blr, t.L.BlrWarehouse, t.MainStore, t.Pen(t.Pc, 20m));
        Guid warehouseId;
        await using (var db = t.Db()) warehouseId = await db.Warehouses.Where(w => w.StockLocationId == t.L.BlrWarehouse).Select(w => w.Id).SingleAsync();

        var outcomes = await ParallelAsync(
            async () => await t.DispatchAsRequestedAsync(transfer.Id, t.Blr),
            async () => { await using var db = t.Db(); await new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(warehouseId, false, "test", t.UserId); });

        AssertNoDeadlock(outcomes);
        Assert.Null(outcomes[0]);
        Assert.IsType<DomainValidationException>(outcomes[1]); // stock (or the open transfer) keeps the warehouse in use
        await using var check = t.Db();
        Assert.True((await check.StockLocations.SingleAsync(l => l.Id == t.L.BlrWarehouse)).IsActive);
        await t.AssertInvariantsAsync("warehouse deactivation vs dispatch");
    }
}
