using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 8b posting locks on PostgreSQL (design D4, D7), made deterministic with <see cref="LockRace"/> where order matters:
/// <list type="bullet">
/// <item>Lock-time re-check: the location is deactivated between the writer's pre-check and its FOR KEY SHARE; once the deactivation
/// commits the posting is refused (400 stockLocationId) and nothing is written. Purchase, sale and adjustment.</item>
/// <item>A lock on one (location, product) stock row blocks that location's posting and NOT the same product's posting at another location.</item>
/// <item>Location admin vs posting: the usage check takes FOR UPDATE on the location, so it waits for an in-flight posting (which holds
/// KEY SHARE and then writes stock) and then sees the stock - for warehouse/vehicle deactivation, vehicle reassignment and branch
/// deactivation. A hammer of postings against a deactivation never leaves an inactive location holding stock.</item>
/// <item>Postings at two locations in parallel are deadlock-free and keep every invariant.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockLocationPostingRaceTests(PostgresFixture postgres)
{
    private const string StockLocationsPattern = """%"StockLocations"%""";

    private static Func<SelloraDbContext, Task> DeactivateLocation(Guid location) => async gate =>
    {
        await LockRace.LockRow("StockLocations", "Id", location)(gate);
        await gate.Database.ExecuteSqlRawAsync("""UPDATE "StockLocations" SET "IsActive" = false WHERE "Id" = {0}""", location);
    };

    private static async Task<(int Documents, int Movements, int Items)> CountsAsync(LocationScenario l)
    {
        await using var db = l.Db();
        return (await db.PurchaseOrders.CountAsync() + await db.SalesInvoices.CountAsync() + await db.StockAdjustments.CountAsync(), await db.StockMovements.CountAsync(), await db.StockItems.CountAsync());
    }

    // ---- lock-time re-check ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("purchase into a branch warehouse")]
    [InlineData("sale at the branch store")]
    [InlineData("adjustment at the branch store")]
    public async Task Posting_LocationDeactivatedBetweenPreCheckAndLock_IsRefusedWithNothingWritten(string kind)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        await l.BuyAsync(l.Blr, l.BlrStore, 20m);
        var target = kind.StartsWith("purchase", StringComparison.Ordinal) ? l.BlrWarehouse : l.BlrStore;
        var before = await CountsAsync(l);

        await using var race = await LockRace.BeginAsync(database, DeactivateLocation(target));
        var writer = await race.StartAsync(kind, db => kind switch
        {
            "purchase into a branch warehouse" => new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreatePurchaseAsync(
                new Sellora.Domain.Purchasing.CreatePurchaseInput(l.S.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0,
                    [new Sellora.Domain.Purchasing.PurchaseLineInput(l.S.ProductId, l.S.LooseKgModeId, 5m, 10m, 0, 0)], StockLocationId: target), l.S.UserId),
            "sale at the branch store" => new SalesService(db, LocationScenario.CompanyLevel).CreateSaleAsync(
                new Sellora.Domain.Sales.CreateSaleInput(l.S.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0,
                    [new Sellora.Domain.Sales.SaleLineInput(l.S.ProductId, l.S.LooseKgModeId, 1m, StockScenario.LooseKgPrice, 0)], StockLocationId: target), l.S.UserId),
            _ => new StockAdjustmentService(db, LocationScenario.CompanyLevel).CreateAdjustmentAsync(
                new Sellora.Domain.Stock.CreateAdjustmentInput(l.S.ProductId, l.S.LooseKgModeId, AdjustmentType.Correction, AdjustmentDirection.Increase, 3m, 12m, "race", StockLocationId: target), l.S.UserId),
        }, branchId: l.Blr);
        await race.WaitBlockedAsync(writer, StockLocationsPattern, "the posting must wait on the location's KEY SHARE behind the deactivation.");

        var outcomes = await race.ReleaseAsync(commit: true);

        var refused = Assert.IsType<DomainValidationException>(outcomes.Single());
        Assert.Equal("stockLocationId", refused.Field);
        Assert.Equal(before, await CountsAsync(l));
        await l.AssertInvariantsAsync("refused posting");
    }

    // ---- locks are per (location, product) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task StockRowLockedAtOneLocation_BlocksThatLocationOnly_PostingAtAnotherLocationProceeds()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        await l.BuyAsync(l.Blr, l.BlrStore, 10m);
        await l.BuyAsync(l.Blr, l.CompanyWarehouse, 10m);

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            var rows = await gate.Database.SqlQueryRaw<decimal>(
                """SELECT "QuantityOnHand" AS "Value" FROM "StockItems" WHERE "StockLocationId" = {0} AND "ProductId" = {1} FOR UPDATE""", l.BlrStore, l.S.ProductId).ToListAsync();
            Assert.Single(rows);
        });
        var blocked = await race.StartAsync("purchase at the locked location", db => new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreatePurchaseAsync(
            new Sellora.Domain.Purchasing.CreatePurchaseInput(l.S.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0,
                [new Sellora.Domain.Purchasing.PurchaseLineInput(l.S.ProductId, l.S.LooseKgModeId, 5m, 10m, 0, 0)], StockLocationId: l.BlrStore), l.S.UserId), branchId: l.Blr);
        await race.WaitBlockedAsync(blocked, """%"StockItems"%""");

        // The same product at another location is not blocked: it completes while the gate still holds the other row. (A different branch: documents of one
        // branch queue on that branch's PUR number counter, which is unrelated to stock locks.)
        var other = await race.StartAsync("purchase at the other location", db => new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreatePurchaseAsync(
            new Sellora.Domain.Purchasing.CreatePurchaseInput(l.S.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0,
                [new Sellora.Domain.Purchasing.PurchaseLineInput(l.S.ProductId, l.S.LooseKgModeId, 7m, 10m, 0, 0)], StockLocationId: l.CompanyWarehouse), l.S.UserId));
        Assert.Null(await other.Outcome.WaitAsync(LockRace.BlockTimeout));
        Assert.False(blocked.Outcome.IsCompleted);

        var outcomes = await race.ReleaseAsync();
        Assert.All(outcomes, o => Assert.Null(o));
        Assert.Equal((15m, 17m), (await l.QohAsync(l.BlrStore), await l.QohAsync(l.CompanyWarehouse)));
        await l.AssertInvariantsAsync("parallel locations");
    }

    [Fact]
    public async Task ParallelPostingsAtTwoLocations_AreDeadlockFree_AndKeepEveryInvariant()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        await l.BuyAsync(l.Blr, l.BlrStore, 200m);
        await l.BuyAsync(l.Blr, l.CompanyWarehouse, 200m);
        await l.BuyAsync(null, l.MainStore, 200m);
        var start = new TaskCompletionSource();

        // 24 postings over 3 locations in all combinations of document types and orders, all released at once.
        var work = Enumerable.Range(0, 24).Select(async i =>
        {
            await start.Task;
            return await Task.Run(async () =>
            {
                try
                {
                    switch (i % 6)
                    {
                        case 0: await l.BuyAsync(l.Blr, l.BlrStore, 3m); break;
                        case 1: await l.BuyAsync(l.Blr, l.CompanyWarehouse, 4m); break;
                        case 2: await l.SellAsync(l.Blr, l.BlrStore, 2m); break;
                        case 3: await l.SellAsync(null, l.MainStore, 2m); break;
                        case 4: await l.AdjustAsync(l.Blr, l.CompanyWarehouse, AdjustmentDirection.Decrease, 1m); break;
                        default: await l.AdjustAsync(l.Blr, l.BlrStore, AdjustmentDirection.Increase, 1m); break;
                    }
                    return (Exception?)null;
                }
                catch (Exception ex) { return ex; }
            });
        }).ToArray();
        start.SetResult();

        var outcomes = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(120));

        Assert.All(outcomes, o => Assert.True(o is null, "a posting failed (deadlock or lock error?): " + o));
        await l.AssertInvariantsAsync("24 parallel postings");
        Assert.Equal(200m + 4 * 3m + 4 * 1m - 4 * 2m, await l.QohAsync(l.BlrStore));            // 4 purchases, 4 adjustments up, 4 sales
        Assert.Equal(200m + 4 * 4m - 4 * 1m, await l.QohAsync(l.CompanyWarehouse));
        Assert.Equal(200m - 4 * 2m, await l.QohAsync(l.MainStore));
    }

    // ---- location admin vs in-flight posting ---------------------------------------------------------------------------------------

    private static Func<SelloraDbContext, Task> InFlightPosting(LocationScenario l, Guid location) => async gate =>
    {
        // What a posting holds while it writes: KEY SHARE on the location, then the stock row it creates. Uncommitted until the gate is released.
        await LockRace.LockRow("StockLocations", "Id", location, "FOR KEY SHARE")(gate);
        await StockSeed.AddAsync(gate, location, l.S.ProductId, 5m, 10m, l.S.UserId);
    };

    [Theory]
    [InlineData("warehouse deactivation")]
    [InlineData("vehicle deactivation")]
    [InlineData("vehicle reassignment")]
    [InlineData("branch deactivation")]
    public async Task LocationAdmin_WaitsForAnInFlightPosting_ThenRefusesBecauseStockExists(string kind)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        var target = kind switch { "warehouse deactivation" => l.BlrWarehouse, "vehicle deactivation" or "vehicle reassignment" => l.BlrVan, _ => l.BlrStore };

        if (kind == "branch deactivation")
        {
            // Clear the branch's other blockers so deactivation reaches the store usage check (the subject of this test).
            await using var prep = l.Db();
            await prep.StockLocations.Where(x => x.BranchId == l.Blr && x.LocationType == StockLocationType.BranchWarehouse).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
            await prep.Vehicles.Where(v => v.AssignedBranchId == l.Blr).ExecuteUpdateAsync(u => u.SetProperty(v => v.IsActive, false));
            await prep.UserBranchAssignments.Where(a => a.BranchId == l.Blr && a.IsPrimary).ExecuteUpdateAsync(u => u.SetProperty(a => a.IsPrimary, false));
        }

        await using var race = await LockRace.BeginAsync(database, InFlightPosting(l, target));
        var admin = await race.StartAsync(kind, async db =>
        {
            var usage = new StockLocationUsageService(db);
            switch (kind)
            {
                case "warehouse deactivation":
                    await new WarehouseService(db, usage).SetStatusAsync(await db.Warehouses.Where(w => w.StockLocationId == target).Select(w => w.Id).SingleAsync(), false, l.S.UserId); break;
                case "vehicle deactivation":
                    await new VehicleService(db, usage).SetStatusAsync(await db.Vehicles.Where(v => v.StockLocationId == target).Select(v => v.Id).SingleAsync(), false, l.S.UserId); break;
                case "vehicle reassignment":
                    await new VehicleService(db, usage).UpdateAsync(await db.Vehicles.Where(v => v.StockLocationId == target).Select(v => v.Id).SingleAsync(), new UpdateVehicleInput("Van one", VehicleType.Van, null), l.S.UserId); break;
                default:
                    await new BranchService(db, usage).SetStatusAsync(l.Blr, false, l.S.UserId); break;
            }
        });
        // It waits behind the posting on the location row (usage check FOR UPDATE vs the posting's KEY SHARE).
        await race.WaitBlockedAsync(admin, null, "the location admin must wait for the in-flight posting.");

        var outcomes = await race.ReleaseAsync(commit: true);

        Assert.IsType<DomainValidationException>(outcomes.Single());
        await using var check = l.Db();
        Assert.True((await check.StockLocations.SingleAsync(x => x.Id == target)).IsActive);
        if (kind == "vehicle reassignment") Assert.Equal(l.Blr, (await check.Vehicles.SingleAsync(v => v.StockLocationId == target)).AssignedBranchId);
        if (kind == "branch deactivation") Assert.True((await check.Branches.SingleAsync(b => b.Id == l.Blr)).IsActive);
    }

    [Fact]
    public async Task LocationAdmin_WaitsOnTheLocationKeyShareOfAPostingThatHasNotWrittenStockYet_ThenProceeds()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);

        // A posting that has locked its location (KEY SHARE) but not written anything yet: the usage check's FOR UPDATE on the location must wait on exactly that.
        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockLocations", "Id", l.BlrWarehouse, "FOR KEY SHARE"));
        var admin = await race.StartAsync("deactivate warehouse", async db =>
            await new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(await db.Warehouses.Where(w => w.StockLocationId == l.BlrWarehouse).Select(w => w.Id).SingleAsync(), false, l.S.UserId));
        await race.WaitBlockedAsync(admin, StockLocationsPattern, "FOR UPDATE on the location must wait for the KEY SHARE of an in-flight posting.");

        Assert.All(await race.ReleaseAsync(commit: true), o => Assert.Null(o));
        await using var check = l.Db();
        Assert.False((await check.StockLocations.SingleAsync(x => x.Id == l.BlrWarehouse)).IsActive);
    }

    [Fact]
    public async Task LocationAdmin_WithNoStockAnywhere_DeactivatesNormally_AndZeroQuantityRowsDoNotBlock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        await l.BuyAsync(l.Blr, l.BlrWarehouse, 5m);
        await using (var db = l.Db(l.Blr)) await new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CancelPurchaseAsync(
            (await db.PurchaseOrders.SingleAsync()).Id, l.S.UserId); // the warehouse row stays with quantity 0

        await using var admin = l.Db();
        var warehouse = await admin.Warehouses.SingleAsync(w => w.StockLocationId == l.BlrWarehouse);
        await new WarehouseService(admin, new StockLocationUsageService(admin)).SetStatusAsync(warehouse.Id, false, l.S.UserId);

        await using var check = l.Db();
        Assert.False((await check.StockLocations.SingleAsync(x => x.Id == l.BlrWarehouse)).IsActive);
    }

    /// <summary>
    /// Lock order between location admin (Companies FOR UPDATE, then the location FOR UPDATE) and a posting (location FOR KEY SHARE, then its
    /// inserts, which take KEY SHARE on the Companies row through the CompanyId foreign keys). The gate plays the admin: it holds the company
    /// row FOR UPDATE; a real purchase posting runs into it; then the "admin" asks for the location row. A consistent order lets the admin
    /// have the location at once (the posting must have queued on the company row BEFORE locking the location); an inconsistent one is a deadlock.
    /// </summary>
    [Fact]
    public async Task LocationAdminHoldingTheCompanyRow_AndAnInFlightPosting_DoNotDeadlock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        await l.BuyAsync(l.Blr, l.BlrWarehouse, 1m); // the branch's PUR counter row and the stock row exist: the posting's first statements take no company lock
        SelloraDbContext? admin = null;

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            admin = gate;
            // What location admin holds first (CompanyLocks): FOR NO KEY UPDATE, which the posting's CompanyId FK KEY SHARE does not wait on.
            await LockRace.LockRow("Companies", "Id", Sellora.Domain.Companies.DefaultTenant.CompanyId, "FOR NO KEY UPDATE")(gate);
        });
        var posting = await race.StartAsync("purchase into the branch warehouse", db => new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreatePurchaseAsync(
            new Sellora.Domain.Purchasing.CreatePurchaseInput(l.S.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0,
                [new Sellora.Domain.Purchasing.PurchaseLineInput(l.S.ProductId, l.S.LooseKgModeId, 2m, 10m, 0, 0)], StockLocationId: l.BlrWarehouse), l.S.UserId), branchId: l.Blr);

        // The posting completes while the admin holds the company row: it never waits on it, so no cycle can form.
        var postingDone = await Task.WhenAny(posting.Outcome, Task.Delay(LockRace.BlockTimeout));
        Assert.True(postingDone == posting.Outcome, "The posting waited on the company row held by location admin (deadlock risk).");
        Assert.Null(await posting.Outcome);

        // The admin then takes the location FOR UPDATE without waiting (the posting has committed).
        var lockLocation = Task.Run(() => admin!.Database.SqlQueryRaw<Guid>("""SELECT "Id" AS "Value" FROM "StockLocations" WHERE "Id" = {0} FOR UPDATE""", l.BlrWarehouse).ToListAsync());
        var finished = await Task.WhenAny(lockLocation, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(finished == lockLocation && !lockLocation.IsFaulted,
            "The admin could not lock the location after the posting committed. " + lockLocation.Exception?.GetBaseException().Message);
        var outcomes = await race.ReleaseAsync();
        Assert.All(outcomes, o => Assert.True(o is null or DomainValidationException, "the posting failed: " + o));
    }

    [Fact]
    public async Task PostingsAgainstADeactivation_NeverLeaveAnInactiveLocationHoldingStock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);

        for (var round = 0; round < 6; round++)
        {
            Guid location, warehouseId;
            await using (var db = l.Db())
            {
                var created = await new WarehouseService(db, new StockLocationUsageService(db)).CreateAsync(new CreateWarehouseInput("R" + round, "Round " + round, l.Blr), l.S.UserId);
                (location, warehouseId) = (created.StockLocationId, created.Id);
            }
            var start = new TaskCompletionSource();
            var posters = Enumerable.Range(0, 4).Select(async _ =>
            {
                await start.Task;
                try { await l.BuyAsync(l.Blr, location, 2m); return (Exception?)null; } catch (Exception ex) { return ex; }
            }).ToArray();
            var deactivate = Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    await using var db = l.Db();
                    await new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(warehouseId, false, l.S.UserId);
                    return (Exception?)null;
                }
                catch (Exception ex) { return ex; }
            });
            start.SetResult();

            var results = (await Task.WhenAll(posters)).Append(await deactivate.WaitAsync(TimeSpan.FromSeconds(60)));

            // Every outcome is success or a clean 400: no deadlock, no constraint or lock error.
            Assert.All(results, o => Assert.True(o is null or DomainValidationException, $"round {round}: unexpected failure: {o}"));
            await using var check = l.Db();
            var active = (await check.StockLocations.SingleAsync(x => x.Id == location)).IsActive;
            var qoh = await l.QohAsync(location);
            Assert.True(active || qoh == 0m, $"round {round}: an inactive location holds {qoh} kg");
        }
        await l.AssertInvariantsAsync("hammer");
    }
}
