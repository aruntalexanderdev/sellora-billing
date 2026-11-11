using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PurchaseReturnAtB_AndDispatchOfTheSameBatchAtB_ExactlyOneTakesIt_NeverNegative(bool dispatchQueuesFirst)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var (a, b, bStore) = (t.Main, t.Blr, t.BlrStore);
        var purchase = await t.BuyAsync(null, t.MainStore, t.Pc, 100m, 8m);
        await t.MoveAsync(t.MainStore, bStore, 40m, a, b); // the whole batch quantity at B is 40
        var transfer = await t.ApprovedAtAsync(b, bStore, t.L.BlrWarehouse, t.Pen(t.Pc, 40m));
        var loaded = await t.ReloadAsync(transfer.Id);
        var input = new DispatchStockTransferInput(loaded.Lines.OrderBy(l => l.LineNo).Select(l => new DispatchStockTransferLineInput(l.Id, l.ProductUnitId, l.RequestedQty)).ToList(), null);
        var permissions = new FakePermissions("Purchase.Return");

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            var rows = await gate.Database.SqlQueryRaw<decimal>(Gate(bStore, t.PenId)).ToListAsync();
            Assert.Single(rows);
        });
        Func<Task<LockRace.Writer>> dispatch = () => race.StartAsync("dispatch", db => new StockTransferService(db, null).DispatchAsync(transfer.Id, input, t.UserId), branchId: b);
        Func<Task<LockRace.Writer>> purchaseReturn = () => race.StartAsync("purchase return", async db =>
            await new PurchaseService(db, new ExchangeRateService(db), permissions).CreateReturnAsync(
                new Sellora.Domain.Purchasing.CreatePurchaseReturnInput(purchase.Id, VanOps.Day, "race", [new(purchase.Lines.Single().Id, t.Pc, 40m)], null, bStore), t.UserId), branchId: a);
        var (first, second) = dispatchQueuesFirst ? (dispatch, purchaseReturn) : (purchaseReturn, dispatch);
        var w1 = await first();
        await race.WaitBlockedAsync(w1, """%"StockItems"%""");
        var w2 = await second();
        await race.WaitBlockedAsync(w2, """%"StockItems"%""");
        var outcomes = await race.ReleaseAsync();

        AssertNoDeadlock(outcomes);
        Assert.Null(outcomes[0]); // the first in the queue wins
        Assert.IsType<DomainValidationException>(outcomes[1]); // the second finds the batch gone from B
        var (dispatched, returned) = dispatchQueuesFirst ? (outcomes[0] is null, outcomes[1] is null) : (outcomes[1] is null, outcomes[0] is null);
        Assert.NotEqual(dispatched, returned);
        Assert.Equal(0m, await t.QohAsync(bStore));
        await using (var check = t.Db())
        {
            Assert.Empty(await check.StockItems.AsNoTracking().Where(i => i.QuantityOnHand < 0).ToListAsync());
            Assert.Empty(await check.BatchLocationBalances.AsNoTracking().Where(x => x.RemainingBaseQty < 0).ToListAsync());
            Assert.Equal(returned ? 1 : 0, await check.PurchaseReturns.CountAsync());
        }
        Assert.Equal(dispatched ? 40m : 0m, await t.QohAsync(t.Transit));
        await t.AssertAllAsync($"purchase return vs dispatch (dispatch first: {dispatchQueuesFirst})");
    }
}
