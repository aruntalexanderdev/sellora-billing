using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 8 lock engine (design D4) on PostgreSQL. StockItems are locked per (location, product) in ONE ordered statement
/// (<c>ORDER BY "StockLocationId", "ProductId" FOR UPDATE</c>), so two documents that name overlapping (location, product) sets in
/// opposite order cannot deadlock - the property the Phase 9 transfers rely on. The ordering is proven deterministically (a gate holds
/// the lowest key; the writer must wait on it WITHOUT holding the higher key: a NOWAIT probe on the higher key succeeds), then
/// hammered with real concurrency. The first-stock ensure-insert is checked per location. The 8a services only use the default store, so
/// the multi-location checks drive the <c>StockLocks</c> primitive directly; the opposite-product-order check also runs end to end
/// through <see cref="PurchaseService"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockLocationLockTests(PostgresFixture postgres)
{
    // PostgreSQL orders uuids bytewise: L1 (Main store, 0c0a...) < L2 (f000...); Pa < Pb.
    private static readonly Guid L1 = DefaultTenant.MainStoreLocationId;
    private static readonly Guid L2 = Guid.Parse("f0000000-0000-4000-8000-0000000000a2");
    private static readonly Guid Pa = Guid.Parse("00000000-0000-0000-0000-000000000a01");
    private static readonly Guid Pb = Guid.Parse("00000000-0000-0000-0000-000000000b02");
    private static readonly Guid Pc = Guid.Parse("00000000-0000-0000-0000-000000000c03");

    private sealed record World(StockScenario Scenario, Guid KgUnitId, Guid TaxId);

    /// <summary>StockScenario masters, a second location (BLR store, id sorting after Main's), and three bare products Pa/Pb/Pc (no stock rows).</summary>
    private static async Task<World> SeedAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var scenario = await StockScenario.SeedAsync(db);
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == scenario.ProductId);
        var branch = BranchBuilder.Extra("BLR");
        db.Branches.Add(branch);
        var store = BranchBuilder.Store(branch);
        store.Id = L2;
        db.StockLocations.Add(store);
        foreach (var (id, sku) in new[] { (Pa, "PA"), (Pb, "PB"), (Pc, "PC") })
        {
            db.Products.Add(new Product { Id = id, Sku = sku, Name = sku, BaseUnitId = product.BaseUnitId, TaxId = product.TaxId });
        }
        await db.SaveChangesAsync();
        return new World(scenario, product.BaseUnitId, product.TaxId);
    }

    private static async Task LockAndHoldAsync(SelloraDbContext db, TimeSpan hold, params (Guid, Guid)[] keys)
    {
        await using var tx = await StockLocksProbe.BeginAsync(db);
        await StockLocksProbe.EnsureAndLockAsync(db, keys);
        if (hold > TimeSpan.Zero) await Task.Delay(hold);
        await tx.CommitAsync();
    }

    private static async Task CommitRowsAsync(TestDatabase database, params (Guid, Guid)[] keys)
    {
        await using var db = database.CreateDbContext();
        await LockAndHoldAsync(db, TimeSpan.Zero, keys);
    }

    // ---- deterministic ordering ---------------------------------------------------------------------------------------------------

    public static TheoryData<string> OrderCases() => new() { "across locations", "same location, opposite product order" };

    [Theory]
    [MemberData(nameof(OrderCases))]
    public async Task EnsureAndLock_KeysGivenInOppositeOrder_WaitsOnTheLowestKeyWithoutHoldingTheHigherOne(string scenarioName)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedAsync(database);
        (Guid, Guid) low, high;
        if (scenarioName == "across locations") { low = (L1, Pa); high = (L2, Pb); }  // sorted location-first: (L1,Pa) then (L2,Pb)
        else { low = (L1, Pa); high = (L1, Pb); }                                      // same location: product order
        await CommitRowsAsync(database, low, high);

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            var rows = await gate.Database.SqlQueryRaw<Guid>(
                "SELECT \"ProductId\" AS \"Value\" FROM \"StockItems\" WHERE \"StockLocationId\" = {0} AND \"ProductId\" = {1} FOR UPDATE", low.Item1, low.Item2).ToListAsync();
            Assert.Single(rows);
        });
        // The writer names the keys HIGH first, then LOW.
        var writer = await race.StartAsync("writer", db => LockAndHoldAsync(db, TimeSpan.Zero, high, low));
        await race.WaitBlockedAsync(writer, "%FOR UPDATE%", "The writer must wait on the gate's lock on the lowest key:");

        // While it waits, the higher key must still be free: the writer locks in (location, product) order, not in the order given.
        await using (var probe = new NpgsqlConnection(database.ConnectionString))
        {
            await probe.OpenAsync();
            await using var tx = await probe.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1 FROM \"StockItems\" WHERE \"StockLocationId\" = @l AND \"ProductId\" = @p FOR UPDATE NOWAIT", probe, tx);
            cmd.Parameters.AddWithValue("l", high.Item1);
            cmd.Parameters.AddWithValue("p", high.Item2);
            Assert.Equal(1, await cmd.ExecuteScalarAsync());
            await tx.RollbackAsync();
        }

        var outcomes = await race.ReleaseAsync();
        Assert.All(outcomes, o => Assert.True(o is null, o?.ToString()));
    }

    // ---- no deadlock under real concurrency ----------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureAndLock_TwoDocumentsLockingOverlappingLocationProductSetsInOppositeOrder_NeverDeadlock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedAsync(database);
        (Guid, Guid)[] forward = [(L1, Pa), (L2, Pb), (L1, Pc)];
        (Guid, Guid)[] backward = [(L1, Pc), (L2, Pb), (L1, Pa)];
        await CommitRowsAsync(database, forward);

        for (var round = 0; round < 25; round++)
        {
            var start = new TaskCompletionSource();
            async Task Run((Guid, Guid)[] keys)
            {
                await using var db = database.CreateDbContext();
                await db.Database.OpenConnectionAsync();
                await start.Task;
                await LockAndHoldAsync(db, TimeSpan.FromMilliseconds(5), keys);
            }
            var a = Task.Run(() => Run(forward));
            var b = Task.Run(() => Run(backward));
            await Task.Delay(20);
            start.SetResult();

            await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30)); // a deadlock surfaces as PostgresException 40P01 here
        }
    }

    [Fact]
    public async Task CreatePurchase_TwoDocumentsWithLinesForTheSameProductsInOppositeOrder_BothSucceedNoDeadlockAndStockReconciles()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var world = await SeedAsync(database);
        Guid rowA, rowB;
        await using (var db = database.CreateDbContext())
        {
            var kg = await db.Units.SingleAsync(u => u.Id == world.KgUnitId);
            var (a, b) = (await db.Products.SingleAsync(p => p.Id == Pa), await db.Products.SingleAsync(p => p.Id == Pb));
            var ra = new ProductUnit { Id = Guid.NewGuid(), ProductId = a.Id, UnitId = kg.Id, IsBaseUnit = true, ConversionFactorToBase = 1m };
            var rb = new ProductUnit { Id = Guid.NewGuid(), ProductId = b.Id, UnitId = kg.Id, IsBaseUnit = true, ConversionFactorToBase = 1m };
            db.ProductUnits.AddRange(ra, rb);
            await db.SaveChangesAsync();
            (rowA, rowB) = (ra.Id, rb.Id);
        }
        Task<PurchaseOrder> Buy(SelloraDbContext db, bool aFirst, decimal qty)
        {
            PurchaseLineInput[] lines = aFirst
                ? [new(Pa, rowA, qty, 10m, 0, 0), new(Pb, rowB, qty, 10m, 0, 0)]
                : [new(Pb, rowB, qty, 10m, 0, 0), new(Pa, rowA, qty, 10m, 0, 0)];
            return new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
                new CreatePurchaseInput(world.Scenario.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0, lines), world.Scenario.UserId);
        }

        const int rounds = 12;
        for (var i = 0; i < rounds; i++)
        {
            await using var d1 = database.CreateDbContext();
            await using var d2 = database.CreateDbContext();
            var t1 = Task.Run(() => Buy(d1, true, 1m));
            var t2 = Task.Run(() => Buy(d2, false, 2m));
            await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(60));
        }

        await using var check = database.CreateDbContext();
        Assert.Equal(rounds * 3m, (await check.StockItems.SingleAsync(s => s.ProductId == Pa)).QuantityOnHand);
        Assert.Equal(rounds * 3m, (await check.StockItems.SingleAsync(s => s.ProductId == Pb)).QuantityOnHand);
        await StockInvariants.AssertHoldAsync(check);
        await StockInvariants.AssertAllAtDefaultStoreAsync(check);
    }

    // ---- first stock (ensure-insert) at a location -----------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureAndLock_ConcurrentFirstStockAtANeverStockedLocationProduct_OneRowNoLostUpdate()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedAsync(database);
        const int writers = 8;
        var start = new TaskCompletionSource();

        async Task Run()
        {
            await using var db = database.CreateDbContext();
            await db.Database.OpenConnectionAsync();
            await start.Task;
            await using var tx = await StockLocksProbe.BeginAsync(db);
            await StockLocksProbe.EnsureAndLockAsync(db, (L2, Pc));
            var item = await db.StockItems.SingleAsync(s => s.StockLocationId == L2 && s.ProductId == Pc);
            item.QuantityOnHand += 1; // would lose updates (or hit the PK) without the row lock
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(Run)).ToArray();
        await Task.Delay(50);
        start.SetResult();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        await using var check = database.CreateDbContext();
        var rows = await check.StockItems.AsNoTracking().Where(s => s.ProductId == Pc).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal((L2, (decimal)writers, DefaultTenant.CompanyId), (row.StockLocationId, row.QuantityOnHand, row.CompanyId));
    }

    [Fact]
    public async Task EnsureAndLock_SameProductAtTwoLocations_AreIndependentRows_AndASecondLocationDoesNotWaitOnTheFirst()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedAsync(database);
        await CommitRowsAsync(database, (L1, Pc), (L2, Pc));

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            var rows = await gate.Database.SqlQueryRaw<Guid>(
                "SELECT \"ProductId\" AS \"Value\" FROM \"StockItems\" WHERE \"StockLocationId\" = {0} AND \"ProductId\" = {1} FOR UPDATE", L1, Pc).ToListAsync();
            Assert.Single(rows);
        });
        var other = await race.StartAsync("other location", db => LockAndHoldAsync(db, TimeSpan.Zero, (L2, Pc)));
        var same = await race.StartAsync("same location", db => LockAndHoldAsync(db, TimeSpan.Zero, (L1, Pc)));

        Assert.False(await race.WaitBlockedOrFinishedAsync(other), "Another location's row must not wait on the first location's lock.");
        await race.WaitBlockedAsync(same, "%FOR UPDATE%");
        var outcomes = await race.ReleaseAsync();
        Assert.All(outcomes, o => Assert.True(o is null, o?.ToString()));
    }

    [Fact]
    public async Task EnsureAndLock_UnknownProductOrLocation_SkippedWithoutForeignKeyErrorAndNothingInserted()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedAsync(database);
        IReadOnlyList<(Guid, Guid)> locked;

        await using (var db = database.CreateDbContext())
        {
            await using var tx = await StockLocksProbe.BeginAsync(db);
            locked = await StockLocksProbe.EnsureAndLockAsync(db, (L1, Guid.NewGuid()), (Guid.NewGuid(), Pa), (L1, Pa));
            await tx.CommitAsync();
        }

        Assert.Equal(new[] { (L1, Pa) }, locked);
        await using var check = database.CreateDbContext();
        Assert.Equal(new[] { (L1, Pa) }, (await check.StockItems.AsNoTracking().ToListAsync()).Select(s => (s.StockLocationId, s.ProductId)));
    }

    [Fact]
    public async Task LockLocationsKeyShare_OfALocationAdminRowLock_WaitsAndTwoPostingsDoNotBlockEachOther()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedAsync(database);

        // Two postings take FOR KEY SHARE on the same location together.
        await using (var a = database.CreateDbContext())
        await using (var b = database.CreateDbContext())
        {
            await using var txA = await StockLocksProbe.BeginAsync(a);
            await using var txB = await StockLocksProbe.BeginAsync(b);
            await StockLocksProbe.LockLocationsKeyShareAsync(a, L1, L2);
            await StockLocksProbe.LockLocationsKeyShareAsync(b, L2, L1);
            await txA.CommitAsync();
            await txB.CommitAsync();
        }

        // A location admin operation (FOR UPDATE on the location row) makes a posting wait.
        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockLocations", "Id", L1));
        var keyShare = await race.StartAsync("key share", async db =>
        {
            await using var tx = await StockLocksProbe.BeginAsync(db);
            await StockLocksProbe.LockLocationsKeyShareAsync(db, L1);
            await tx.CommitAsync();
        });
        await race.WaitBlockedAsync(keyShare, "%FOR KEY SHARE%");
        var outcomes = await race.ReleaseAsync();
        Assert.All(outcomes, o => Assert.True(o is null, o?.ToString()));
    }
}
