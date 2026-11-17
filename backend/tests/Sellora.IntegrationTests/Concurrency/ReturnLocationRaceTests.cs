using System.Net;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using H = Sellora.IntegrationTests.TestData.ShipmentHttp;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 10 locks on PostgreSQL (design D1, D2). A sales return / cancellation re-resolves its restock location under the location KEY SHARE lock, so a vehicle
/// reassignment or deactivation committed in the window is seen (return: falls back to the branch store; cancel: refused) and no stock is ever left at an inactive
/// or foreign location; a purchase return and a transfer dispatch of the same batch at the same location cannot both take it (never a negative balance). After every
/// race: the stock invariants, V1-V8 and R1-R5.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReturnLocationRaceTests(PostgresFixture postgres)
{
    private const string LocationsPattern = """%"StockLocations"%""";

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

    /// <summary>A van of A, stocked with 10 pieces by a real transfer and emptied by a sale of all 10.</summary>
    private static async Task<(TransferScenario T, Guid VehicleId, Guid Van, SalesInvoice Sale)> EmptiedVanAsync(TestDatabase database, string number)
    {
        var t = await TransferScenario.CreateAsync(database);
        var (vehicleId, van) = await t.CreateVanAsync(number, t.Main);
        await t.BuyAsync(null, t.MainStore, t.Pc, 50m, 8m);
        await t.MoveAsync(t.MainStore, van, 10m, t.Main, t.Main);
        var sale = await t.SellPiecesAsync(t.Main, van, 10m);
        Assert.Equal(0m, await t.QohAsync(van));
        return (t, vehicleId, van, sale);
    }

    private static Func<SelloraDbContext, Task> Reassign(Guid van, Guid branch) => async gate =>
    {
        await LockRace.LockRow("StockLocations", "Id", van)(gate);
        await gate.Database.ExecuteSqlRawAsync("""UPDATE "Vehicles" SET "AssignedBranchId" = {0} WHERE "StockLocationId" = {1}""", branch, van);
    };

    private static Func<SelloraDbContext, Task> Deactivate(Guid van) => async gate =>
    {
        await LockRace.LockRow("StockLocations", "Id", van)(gate);
        await gate.Database.ExecuteSqlRawAsync("""UPDATE "StockLocations" SET "IsActive" = false WHERE "Id" = {0}""", van);
        await gate.Database.ExecuteSqlRawAsync("""UPDATE "Vehicles" SET "IsActive" = false WHERE "StockLocationId" = {0}""", van);
    };

    private static Task ReturnAsync(SelloraDbContext db, TransferScenario t, SalesInvoice sale, Action<SalesReturn> capture) =>
        new SalesService(db, null).CreateReturnAsync(new CreateSaleReturnInput(sale.Id, VanOps.Day, "race", [new SaleReturnLineInput(sale.Lines.Single().Id, t.Pc, 3m)]), t.UserId)
            .ContinueWith(r => capture(r.Result), TaskContinuationOptions.OnlyOnRanToCompletion);

    // ---- sales return vs vehicle reassignment / deactivation (deterministic) -------------------------------------------------------------

    [Fact]
    public async Task Return_VanReassignedBetweenCheckAndLock_FallsBackToTheStore_NothingLeftAtTheVan()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, _, van, sale) = await EmptiedVanAsync(database, "RC1");
        SalesReturn? created = null;

        await using var race = await LockRace.BeginAsync(database, Reassign(van, t.Blr));
        var writer = await race.StartAsync("return", db => ReturnAsync(db, t, sale, r => created = r), branchId: t.Main);
        await race.WaitBlockedAsync(writer, LocationsPattern, "the return must wait on the location KEY SHARE behind the reassignment.");
        var outcomes = await race.ReleaseAsync(commit: true);

        Assert.Null(outcomes.Single());
        Assert.Equal(t.MainStore, created!.StockLocationId);
        Assert.Equal((0m, 43m), (await t.QohAsync(van), await t.QohAsync(t.MainStore))); // 50 - 10 moved + 3 returned
        await t.AssertAllAsync("return vs reassignment");
    }

    [Fact]
    public async Task Return_VanDeactivatedBetweenCheckAndLock_FallsBackToTheStore_NothingLeftAtTheVan()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, _, van, sale) = await EmptiedVanAsync(database, "RC2");
        SalesReturn? created = null;

        await using var race = await LockRace.BeginAsync(database, Deactivate(van));
        var writer = await race.StartAsync("return", db => ReturnAsync(db, t, sale, r => created = r), branchId: t.Main);
        await race.WaitBlockedAsync(writer, LocationsPattern);
        var outcomes = await race.ReleaseAsync(commit: true);

        Assert.Null(outcomes.Single());
        Assert.Equal(t.MainStore, created!.StockLocationId);
        Assert.Equal(0m, await t.QohAsync(van));
        await using (var check = t.Db()) Assert.Empty(await check.StockItems.Where(i => i.StockLocationId == van && i.QuantityOnHand != 0).ToListAsync());
        await t.AssertAllAsync("return vs deactivation");
    }

    [Fact]
    public async Task Cancel_VanDeactivatedBetweenCheckAndLock_IsRefusedWithNothingMoved()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (t, _, van, sale) = await EmptiedVanAsync(database, "RC3");
        var before = await t.MovementCountAsync();

        await using var race = await LockRace.BeginAsync(database, Deactivate(van));
        var writer = await race.StartAsync("cancel", db => new SalesService(db, null).CancelSaleAsync(sale.Id, t.UserId), branchId: t.Main);
        await race.WaitBlockedAsync(writer, LocationsPattern);
        var outcomes = await race.ReleaseAsync(commit: true);

        Assert.Equal("stockLocationId", Assert.IsType<DomainValidationException>(outcomes.Single()).Field);
        Assert.Equal((before, 0m), (await t.MovementCountAsync(), await t.QohAsync(van)));
        await using (var check = t.Db()) Assert.Equal(SalesStatus.Completed, (await check.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == sale.Id)).Status);
        await t.AssertAllAsync("cancel vs deactivation");
    }

    // ---- sales return vs the real vehicle service (parallel) --------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Return_ParallelWithVehicleReassignOrDeactivate_IsConsistent_NoDeadlock_NoStockAtAnInvalidLocation(bool deactivate)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 100m, 8m);
        for (var round = 0; round < 4; round++)
        {
            var (vehicleId, van) = await t.CreateVanAsync($"RP{(deactivate ? "D" : "R")}{round}", t.Main);
            await t.MoveAsync(t.MainStore, van, 10m, t.Main, t.Main);
            var sale = await t.SellPiecesAsync(t.Main, van, 10m);
            SalesReturn? created = null;

            var outcomes = await ParallelAsync(
                async () => { await using var db = t.Db(t.Main); await ReturnAsync(db, t, sale, r => created = r); },
                async () =>
                {
                    await using var db = t.Db();
                    var service = new VehicleService(db, new StockLocationUsageService(db));
                    if (deactivate) await service.SetStatusAsync(vehicleId, false, "test", t.UserId);
                    else await service.UpdateAsync(vehicleId, new Sellora.Domain.Inventory.UpdateVehicleInput("Van", Sellora.Domain.Inventory.VehicleType.Van, t.Blr), t.UserId);
                });

            AssertNoDeadlock(outcomes);
            Assert.Null(outcomes[0]); // the return always succeeds: at the van while it is still usable, else at the store
            var atVan = created!.StockLocationId == van;
            // The goods are at the van only if the van change was refused (it then holds stock); they are at the store only if the change went through.
            Assert.Equal(atVan, outcomes[1] is DomainValidationException);
            Assert.True(atVan || outcomes[1] is null, $"round {round}: unexpected outcome {outcomes[1]}");
            await using (var check = t.Db())
            {
                var stocked = await check.StockItems.AsNoTracking().Where(i => i.StockLocationId == van && i.QuantityOnHand != 0).CountAsync();
                var vehicle = await check.Vehicles.AsNoTracking().SingleAsync(x => x.Id == vehicleId);
                Assert.True(stocked == 0 || (vehicle.IsActive && vehicle.AssignedBranchId == t.Main), $"round {round}: stock left at a deactivated or reassigned van");
            }
            await t.AssertAllAsync($"return vs vehicle change, round {round}");
        }
    }

    // ---- purchase return at B vs transfer dispatch of the same batch at B ----------------------------------------------------------------------

    private static string Gate(Guid location, Guid product) =>
        $"""SELECT "QuantityOnHand" AS "Value" FROM "StockItems" WHERE "StockLocationId" = '{location}' AND "ProductId" = '{product}' FOR UPDATE""";

    // ---- purchase return vs a SHIPMENT dispatch of the same batch (FI-D34, FI-D38 e: replaces PurchaseReturnAtB_AndDispatchOfTheSameBatchAtB) -----------------------

    [Fact]
    public async Task PurchaseReturnVsShipmentDispatch_OfTheSameOldestBatch_NeverNegative_ReturnEitherFitsOrIsACleanRefusal()
    {
        for (var round = 0; round < 3; round++)
        {
            // Two batches at the source: 6 (oldest) and 4. The shipment of 6 draws FIFO from the oldest; the return wants 4 of that same oldest batch.
            var s = await H.NewSceneAsync(postgres, 0m);
            var oldest = await H.BuyAsync(s, 6m);
            await H.BuyAsync(s, 4m);
            var request = await H.RequestAsync(s, s.AStore, 6m);
            var sid = H.IdOf(await H.ReadyShipmentAsync(s, (request, 6m)));
            var transitBefore = await H.TransitQohAsync(s);

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid), () => H.PurchaseReturnAsync(s, oldest, 4m));

            await H.AssertNo500Async($"round {round}: purchase return vs dispatch", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            // Return first: the oldest batch keeps 2 and the dispatch tops up from the next batch. Dispatch first: the oldest batch is gone and the return is refused.
            var returned = responses[1].IsSuccessStatusCode;
            if (!returned) Assert.Equal(HttpStatusCode.BadRequest, responses[1].StatusCode);
            Assert.Equal((returned ? 0m : 4m, 0m, transitBefore + 6m), (await H.QohAsync(s), await H.ReservedAsync(s), await H.TransitQohAsync(s)));
            await H.AssertNothingNegativeAsync(s, $"round {round}: purchase return vs dispatch (returned: {returned})");
        }
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task PurchaseReturnVsShipmentDispatch_OfASingleBatch_OnlyTheUnreservedRemainderCanGoBack(int pieces, bool fits)
    {
        for (var round = 0; round < 3; round++)
        {
            var s = await H.NewSceneAsync(postgres, 0m);
            var purchase = await H.BuyAsync(s, 10m);
            var request = await H.RequestAsync(s, s.AStore, 6m);
            var sid = H.IdOf(await H.ReadyShipmentAsync(s, (request, 6m)));

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid), () => H.PurchaseReturnAsync(s, purchase, pieces));

            await H.AssertNo500Async($"round {round}: single-batch return vs dispatch", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Equal(fits, responses[1].IsSuccessStatusCode);
            if (!fits) Assert.Equal(HttpStatusCode.BadRequest, responses[1].StatusCode);
            Assert.Equal((fits ? 0m : 4m, 0m), (await H.QohAsync(s), await H.ReservedAsync(s)));
            await H.AssertNothingNegativeAsync(s, $"round {round}: return {pieces} vs dispatch");
        }
    }
}
