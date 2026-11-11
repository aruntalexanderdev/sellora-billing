using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// The first-stock race (Phase 3 design §3). A product with no StockItems row has nothing for <c>FOR UPDATE</c> to lock,
/// so two concurrent first stock-ins used to both insert the row and one failed on the primary key (23505).
/// <c>StockLocks.EnsureAndLockStockItemsAsync</c> upserts the row (<c>INSERT … ON CONFLICT DO NOTHING</c>) before locking.
/// The gate stands in for a concurrent first writer: it inserts the product's StockItems row and keeps it uncommitted.
/// Writer A is started and must wait in the StockItems INSERT (on the gate's uncommitted key), then writer B; the gate
/// then commits or rolls back. Both writers must succeed, QOH must be the sum, the average the weighted average, and the
/// ledger must reconcile.
/// </summary>
[Collection(PostgresCollection.Name)]
public class FirstStockConcurrencyTests(PostgresFixture postgres)
{
    private const string StockItemsInsert = """%INSERT INTO "StockItems"%""";

    private sealed record Seed(StockScenario Scenario, Guid PenId, Guid Box, Guid FCtn);

    /// <summary>StockScenario masters (user, supplier, settings) plus a Ball Pen (pc; Box 10, Half Carton 50, Full Carton
    /// 100) that has never had stock: no StockItems row.</summary>
    private static async Task<Seed> SeedAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var scenario = await StockScenario.SeedAsync(db);
        var taxId = await db.Products.Where(p => p.Id == scenario.ProductId).Select(p => p.TaxId).SingleAsync();
        static Unit U(string name, string code, MeasurementCategory c) => new() { Id = Guid.NewGuid(), Name = name, ShortCode = code, MeasurementCategory = c };
        var (pcU, boxU, hctnU, fctnU) = (U("Piece", "pc", MeasurementCategory.Count), U("Box", "box", MeasurementCategory.Packaging),
            U("Half Carton", "hctn", MeasurementCategory.Packaging), U("Full Carton", "fctn", MeasurementCategory.Packaging));
        var pen = new Product { Id = Guid.NewGuid(), Sku = "STN-PEN", Name = "Ball Pen", BaseUnitId = pcU.Id, TaxId = taxId };
        ProductUnit Row(Unit unit, ProductUnit? inner, decimal qty, short sort)
        {
            var row = new ProductUnit
            {
                Id = Guid.NewGuid(), ProductId = pen.Id, UnitId = unit.Id, InnerProductUnitId = inner?.Id, InnerQuantity = qty,
                ConversionFactorToBase = qty * (inner?.ConversionFactorToBase ?? 1m), IsBaseUnit = inner is null, SortOrder = sort,
            };
            pen.Units.Add(row);
            return row;
        }
        var pc = Row(pcU, null, 1m, 0);
        var box = Row(boxU, pc, 10m, 1);
        var hctn = Row(hctnU, box, 5m, 2);
        var fctn = Row(fctnU, hctn, 2m, 3);
        db.AddRange(pcU, boxU, hctnU, fctnU, pen);
        await db.SaveChangesAsync();
        Assert.False(await db.StockItems.AnyAsync(s => s.ProductId == pen.Id));
        return new Seed(scenario, pen.Id, box.Id, fctn.Id);
    }

    /// <summary>A first stock-in: what it does, and the base quantity and per-base cost it adds.</summary>
    private sealed record StockIn(string Name, Func<SelloraDbContext, Seed, Task> Run, decimal BaseQty, decimal UnitCost);

    private static StockIn Opening(decimal fullCartons, decimal costPerCarton) => new($"opening {fullCartons} fctn",
        (db, s) => new StockAdjustmentService(db).CreateAdjustmentAsync(new CreateAdjustmentInput(s.PenId, s.FCtn, AdjustmentType.OpeningStock,
            AdjustmentDirection.Increase, fullCartons, costPerCarton, "First count"), s.Scenario.UserId),
        fullCartons * 100m, costPerCarton / 100m);

    private static StockIn Purchase(decimal boxes, decimal ratePerBox) => new($"purchase {boxes} box",
        (db, s) => new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(new CreatePurchaseInput(s.Scenario.SupplierId,
            StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(s.PenId, s.Box, boxes, ratePerBox, 0, 0)]), s.Scenario.UserId),
        boxes * 10m, ratePerBox / 10m);

    public static TheoryData<string, bool> Pairs() => new()
    {
        { "opening/opening", true }, { "opening/opening", false },
        { "opening/purchase", true }, { "opening/purchase", false },
        { "purchase/purchase", true }, { "purchase/purchase", false },
    };

    private static (StockIn A, StockIn B) Pair(string pair) => pair switch
    {
        "opening/opening" => (Opening(3m, 300m), Opening(1m, 700m)),     // 300 pc @ 3 + 100 pc @ 7 -> 400 @ 4
        "opening/purchase" => (Opening(3m, 300m), Purchase(10m, 50m)),   // 300 pc @ 3 + 100 pc @ 5 -> 400 @ 3.5
        "purchase/purchase" => (Purchase(10m, 30m), Purchase(10m, 50m)), // 100 pc @ 3 + 100 pc @ 5 -> 200 @ 4
        _ => throw new ArgumentOutOfRangeException(nameof(pair)),
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task FirstStockIn_TwoWritersBehindAnUncommittedStockItemInsert_BothSucceedAndStockReconciles(string pair, bool gateCommits)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var (a, b) = Pair(pair);

        await using var race = await LockRace.BeginAsync(database, gate => gate.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "StockItems" ("CompanyId", "StockLocationId", "ProductId", "QuantityOnHand", "AverageCost") VALUES ({Sellora.Domain.Companies.DefaultTenant.CompanyId}, {Sellora.Domain.Companies.DefaultTenant.MainStoreLocationId}, {seed.PenId}, 0, 0)"""));
        var writerA = await race.StartAsync(a.Name, db => a.Run(db, seed));
        await race.WaitBlockedAsync(writerA, StockItemsInsert, "Writer A did not wait on the gate's uncommitted StockItems row:");
        var writerB = await race.StartAsync(b.Name, db => b.Run(db, seed));
        // Two purchases also share the PUR document-number advisory lock, taken before the upsert: B may queue there.
        await race.WaitBlockedAsync(writerB, pair == "purchase/purchase" ? null : StockItemsInsert, "Writer B did not wait:");
        var outcomes = await race.ReleaseAsync(commit: gateCommits);

        Assert.True(outcomes[0] is null, $"{a.Name} failed: {outcomes[0]}");
        Assert.True(outcomes[1] is null, $"{b.Name} failed: {outcomes[1]}");
        await using var check = database.CreateDbContext();
        var stock = await check.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == seed.PenId);
        var expectedQty = a.BaseQty + b.BaseQty;
        Assert.Equal(expectedQty, stock.QuantityOnHand);
        Assert.Equal(Math.Round((a.BaseQty * a.UnitCost + b.BaseQty * b.UnitCost) / expectedQty, 4, MidpointRounding.AwayFromZero), stock.AverageCost);
        Assert.Equal(2, await check.PurchaseBatches.CountAsync(x => x.ProductId == seed.PenId));
        await StockInvariants.AssertHoldAsync(check);
    }

    /// <summary>Without any race the upsert is idempotent: the first stock-in creates exactly one row and later stock-ins
    /// reuse it. A purchase naming an unknown product reaches the upsert before line validation: the unknown id is
    /// skipped (no FK error) and the purchase is refused with the normal field error.</summary>
    [Fact]
    public async Task FirstStockInThenMore_OneStockItemRow_UnknownProductRefusedAsFieldErrorNotForeignKeyError()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        await using (var db = database.CreateDbContext()) await Opening(1m, 300m).Run(db, seed);
        await using (var db = database.CreateDbContext()) await Purchase(2m, 30m).Run(db, seed);

        await using (var db = database.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<Sellora.Domain.Abstractions.DomainValidationException>(() =>
                new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(new CreatePurchaseInput(seed.Scenario.SupplierId,
                    StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0,
                    [new PurchaseLineInput(Guid.NewGuid(), seed.Box, 1m, 30m, 0, 0), new PurchaseLineInput(seed.PenId, seed.Box, 1m, 30m, 0, 0)]),
                    seed.Scenario.UserId));
            Assert.Equal("Lines", ex.Field);
        }

        await using var check = database.CreateDbContext();
        var rows = await check.StockItems.AsNoTracking().ToListAsync();
        Assert.Equal(new[] { (seed.PenId, 120m) }, rows.Select(r => (r.ProductId, r.QuantityOnHand)));
        await StockInvariants.AssertHoldAsync(check);
    }
}
