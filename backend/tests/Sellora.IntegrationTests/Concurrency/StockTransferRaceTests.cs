using System.Net;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using H = Sellora.IntegrationTests.TestData.ShipmentHttp;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

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

    // ---- SHIPMENT dispatch races (FI-D34, FI-D38 e). These replace the seven legacy StockTransferService.DispatchAsync races deleted with the legacy dispatch. ------------
    // Dispatch vs a source sale / adjustment / purchase return / allocation live in AvailabilityRaceTests, ReturnLocationRaceTests and ReservationRaceTests; the
    // close-of-request race is ShipmentDispatchApiTests.CloseOfTheRequest_RacingTheShipmentDispatch_*; the plain same-key / no-key hammer is ParallelDispatches_*.

    // replaces DoubleDispatch_WithoutKey_* and DoubleDispatch_WithTheSameKey_*: two callers with DIFFERENT keys cannot each replay-and-move; the loser is a status refusal.
    [Fact]
    public async Task DoubleShipmentDispatch_WithDifferentKeys_MovesOnce_TheOtherIsRefusedOnStatus()
    {
        for (var round = 0; round < 3; round++)
        {
            var (s, sid, _) = await H.ReadySceneAsync(postgres, 100m, 40m);
            var transitBefore = await H.TransitQohAsync(s);

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid, "k1-" + Guid.NewGuid()), () => H.DispatchShipmentAsync(s, sid, "k2-" + Guid.NewGuid()));

            await H.AssertNo500Async($"round {round}: double dispatch", responses);
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(HttpStatusCode.BadRequest, responses.Single(r => r.StatusCode != HttpStatusCode.OK).StatusCode);
            Assert.Equal((60m, 0m, transitBefore + 40m), (await H.QohAsync(s), await H.ReservedAsync(s), await H.TransitQohAsync(s)));
            var shipmentNo = await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "ShipmentNo" FROM "Shipments" WHERE "Id" = '{sid}'""");
            Assert.Equal(1L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'TransferOut' AND "StockLocationId" = '{s.SrcStore}' AND "ProductId" = '{s.W.ProductId}' AND "Notes" LIKE '{shipmentNo}%'"""));
            await H.AssertNothingNegativeAsync(s, $"round {round}: double dispatch with different keys");
        }
    }

    // replaces FirstDispatchesOfTheSameProduct_FromTwoSources_RaceForTheTransitRow_WithoutErrors (the company Transit row is shared; the first-row creation itself is covered by FirstStockConcurrencyTests).
    [Fact]
    public async Task ShipmentDispatches_OfTheSameProduct_FromTwoSources_ShareTheTransitRow_WithoutErrors()
    {
        var (s1, sid1, _) = await H.ReadySceneAsync(postgres, 50m, 20m);
        var (s2, sid2, _) = await H.ReadySceneAsync(postgres, 50m, 15m);
        var transitBefore = await H.TransitQohAsync(s1);

        var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s1, sid1), () => H.DispatchShipmentAsync(s2, sid2));

        await H.AssertNo500Async("two sources", responses);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(transitBefore + 35m, await H.TransitQohAsync(s1));
        Assert.Equal((30m, 35m), (await H.QohAsync(s1), await H.QohAsync(s2)));
        await H.AssertNothingNegativeAsync(s1, "two sources, first");
        await H.AssertNothingNegativeAsync(s2, "two sources, second");
    }

    // replaces OppositeDispatchesMixedWithReceipts_OfTwoTransfers_AreDeadlockFree (its dispatch half; the receipts half stays as OppositeReceipts_*): X to Y and Y to X dispatch together.
    [Fact]
    public async Task OppositeShipments_DispatchingTogether_AreDeadlockFree()
    {
        var x = await H.NewSceneAsync(postgres, 60m);                                       // source X, destination A = Y
        var y = new ShipScene(x.W, x.A, x.Src, x.B, x.AStore, x.SrcStore, x.BStore, x.Pc, x.Box);   // source Y, destination X
        await x.W.EnsureBranchStockAsync(y.Src, 60m);
        for (var round = 0; round < 3; round++)
        {
            var (xr, yr) = (await H.RequestAsync(x, x.AStore, 7m), await H.RequestAsync(y, y.AStore, 7m));
            var (xs, ys) = (H.IdOf(await H.ReadyShipmentAsync(x, (xr, 7m))), H.IdOf(await H.ReadyShipmentAsync(y, (yr, 7m))));
            var transitBefore = await H.TransitQohAsync(x);

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(x, xs), () => H.DispatchShipmentAsync(y, ys));

            await H.AssertNo500Async($"round {round}: opposite dispatches", responses);
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Equal(transitBefore + 14m, await H.TransitQohAsync(x));
            await H.AssertNothingNegativeAsync(x, $"round {round}: opposite dispatches (X)");
            await H.AssertNothingNegativeAsync(y, $"round {round}: opposite dispatches (Y)");
        }
    }

    // new: FI-D38 (d), shipment cancel is refused once dispatched and dispatch once cancelled: whichever commits first, the other is a clean 400.
    [Fact]
    public async Task ShipmentDispatchVsShipmentCancel_ExactlyOneWins_StockEndsConsistent()
    {
        for (var round = 0; round < 3; round++)
        {
            var (s, sid, _) = await H.ReadySceneAsync(postgres, 10m, 6m);
            var transitBefore = await H.TransitQohAsync(s);

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid), () => H.CancelShipmentAsync(s, sid, "race"));

            await H.AssertNo500Async($"round {round}: dispatch vs shipment cancel", responses);
            Assert.True(responses[0].IsSuccessStatusCode ^ responses[1].IsSuccessStatusCode, $"round {round}: dispatch {(int)responses[0].StatusCode}, cancel {(int)responses[1].StatusCode}: exactly one must win");
            Assert.Equal(HttpStatusCode.BadRequest, (responses[0].IsSuccessStatusCode ? responses[1] : responses[0]).StatusCode);
            var dispatched = responses[0].IsSuccessStatusCode;
            Assert.Equal(dispatched ? "Dispatched" : "Cancelled", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "Shipments" WHERE "Id" = '{sid}'"""));
            Assert.Equal((dispatched ? 4m : 10m, 0m, transitBefore + (dispatched ? 6m : 0m)), (await H.QohAsync(s), await H.ReservedAsync(s), await H.TransitQohAsync(s)));
            await H.AssertNothingNegativeAsync(s, $"round {round}: dispatch vs shipment cancel");
        }
    }

    // new: the request of an allocated shipment cannot be cancelled (cancel is refused while quantity is allocated, and a dispatched request is Fulfilled), so the dispatch always wins.
    [Fact]
    public async Task ShipmentDispatchVsCancelOfTheRequest_DispatchWins_TheCancelIsRefused()
    {
        for (var round = 0; round < 3; round++)
        {
            var (s, sid, request) = await H.ReadySceneAsync(postgres, 10m, 6m);

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid),
                () => H.SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{H.IdOf(request)}/cancel", new StockTransferReasonRequest("race"), s.Src));

            await H.AssertNo500Async($"round {round}: dispatch vs request cancel", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, responses[1].StatusCode);
            Assert.Equal("Fulfilled", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{H.IdOf(request)}'"""));
            await H.AssertNothingNegativeAsync(s, $"round {round}: dispatch vs request cancel");
        }
    }

    // replaces WarehouseDeactivation_AlongsideADispatch_IsAlwaysRefused_WhileTheTransferIsOpen for shipments, and the deactivation-window dispatch race: a warehouse source holding
    // stock for a ready shipment can never be deactivated, whichever order the two commit in; the dispatch always succeeds, nothing is stranded.
    [Fact]
    public async Task ShipmentDispatchVsDeactivationOfTheSourceWarehouse_TheDispatchWins_TheWarehouseStaysActive()
    {
        var s0 = await H.NewSceneAsync(postgres, 0m);
        var created = await H.OkAsync(await s0.W.SendAsync(HttpMethod.Post, "/api/v1/warehouses", new CreateWarehouseRequest("WH" + Guid.NewGuid().ToString("N")[..8], "Race warehouse", s0.Src)));
        var (warehouseId, store) = (H.IdOf(created), created.GetProperty("stockLocationId").GetGuid());
        await using (var db = s0.W.Host.Database.CreateDbContext(s0.W.CompanyId)) await StockSeed.AddAsync(db, store, s0.W.ProductId, 10m, 5m, s0.W.AdminId);
        var s = s0 with { SrcStore = store };
        var request = await H.RequestAsync(s, s.AStore, 6m);
        var sid = H.IdOf(await H.ReadyShipmentAsync(s, (request, 6m)));

        var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid),
            () => H.SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/warehouses/{warehouseId}/status", new SetStatusRequest(false, "race"), s.Src));

        await H.AssertNo500Async("dispatch vs warehouse deactivation", responses);
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, responses[1].StatusCode);                  // 4 pieces stay on hand: stock keeps the warehouse in use
        Assert.True(await ScalarAsync<bool>(s.W.Host.Database, $"""SELECT "IsActive" FROM "StockLocations" WHERE "Id" = '{store}'"""));
        Assert.Equal((4m, 0m), (await H.QohAsync(s), await H.ReservedAsync(s)));
        await H.AssertNothingNegativeAsync(s, "dispatch vs warehouse deactivation");
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

            // FI-D3: the dispatch itself is a shipment concern (ShipmentDispatchApiTests); the legacy-dispatched state is seeded, the receipts race below.
            await t.DispatchAsRequestedAsync(ab.Id, t.Main);
            await t.DispatchAsRequestedAsync(ba.Id, t.Blr);

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
    public async Task OppositeReceipts_OfTwoInFlightTransfers_AreDeadlockFree()
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
            await t.DispatchAsRequestedAsync(ab.Id, t.Main); // legacy-dispatched state is seeded (FI-D3); both are in flight when the receipts race
            await t.DispatchAsRequestedAsync(ba.Id, t.Blr);
            var mixed = await ParallelAsync(
                async () => await t.ReceiveAsync(ba.Id, t.Pc, 7m, branch: t.Main),
                async () => await t.ReceiveAsync(ab.Id, t.Pc, 7m, branch: t.Blr));
            AssertNoDeadlock(mixed);
            Assert.All(mixed, o => Assert.Null(o));
            await t.AssertInvariantsAsync($"mixed round {round}");
        }
    }

    // ---- dispatch vs a sale at the source -----------------------------------------------------------------------------------------------------

    // ---- dispatch / receipt vs a deactivation committed in the window --------------------------------------------------------------------

    private static Func<SelloraDbContext, Task> DeactivateLocation(Guid location) => async gate =>
    {
        await LockRace.LockRow("StockLocations", "Id", location)(gate);
        await gate.Database.ExecuteSqlRawAsync("""UPDATE "StockLocations" SET "IsActive" = false WHERE "Id" = {0}""", location);
    };

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
