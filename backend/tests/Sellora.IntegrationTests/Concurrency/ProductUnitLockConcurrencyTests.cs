using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.PenScenario;
using V = Sellora.Domain.Inventory.ProductUnitConfigurationValidator;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 2 lock order (design §5.4) under real PostgreSQL row locks, made deterministic with <see cref="LockRace"/>:
/// <list type="bullet">
/// <item>Product edits take <c>Products FOR UPDATE</c>, then <c>Units FOR SHARE</c>.</item>
/// <item>Purchase / sale creates take <c>Products FOR KEY SHARE</c> before reading factors, so an edit of a unit they
/// are using waits for them (and then sees the line, i.e. the row is locked) — the line's factor snapshot can never
/// disagree with the row it references.</item>
/// <item><c>PricingService.SetPrice</c> takes <c>Products FOR UPDATE</c>: serialised with edits and other prices.</item>
/// <item><c>UnitService</c> takes <c>Units FOR UPDATE</c>: serialised with a product edit that starts using the unit.</item>
/// </list>
/// Where possible the gate sits <em>after</em> the operations' validation reads, so a missing lock shows up as
/// corrupted data (a cycle, a factor mismatch, two open prices), not only as "did not block".
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProductUnitLockConcurrencyTests(PostgresFixture postgres)
{
    private const string ProductsForUpdate = """%"Products"%FOR UPDATE%""";
    private const string ProductsKeyShare = """%"Products"%FOR KEY SHARE%""";
    private const string UnitsForShare = """%"Units"%FOR SHARE%""";
    private const string UnitsForUpdate = """%"Units"%FOR UPDATE%""";

    private static void AssertSucceeded(Exception? outcome, string what) =>
        Assert.True(outcome is null, $"{what} failed: {outcome}");

    private static DomainValidationException AssertRejected(Exception? outcome, string field, string? message = null)
    {
        var ex = Assert.IsType<DomainValidationException>(outcome);
        Assert.True(ex.Errors.Any(e => e.Field == field && (message is null || e.Message == message)),
            $"Expected {field}{(message is null ? "" : $" '{message}'")}. Errors: " + string.Join(" | ", ex.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return ex;
    }

    /// <summary>Every inner chain of every product terminates at its base row (no cycle was stored).</summary>
    private static async Task AssertNoCycleAsync(SelloraDbContext db)
    {
        var rows = await db.ProductUnits.AsNoTracking().ToDictionaryAsync(u => u.Id);
        foreach (var row in rows.Values)
        {
            var seen = new HashSet<Guid> { row.Id };
            var current = row;
            while (current.InnerProductUnitId is { } inner)
            {
                Assert.True(seen.Add(inner), $"Stored cycle through product unit {row.Id}.");
                current = rows[inner];
            }
            Assert.True(current.IsBaseUnit, $"Chain of {row.Id} does not end at a base row.");
        }
    }

    /// <summary>Every purchase/sale line's factor snapshot equals the factor of the row it references.</summary>
    private static async Task AssertLineFactorsMatchRowsAsync(SelloraDbContext db)
    {
        var mismatches = (await db.SalesInvoiceLines.AsNoTracking()
                .Where(l => l.ConversionFactorToBase != l.ProductUnit.ConversionFactorToBase)
                .Select(l => new { l.Id, Line = l.ConversionFactorToBase, Row = l.ProductUnit.ConversionFactorToBase }).ToListAsync())
            .Select(x => $"SI line {x.Id}: line {x.Line} vs row {x.Row}")
            .Concat((await db.PurchaseOrderLines.AsNoTracking()
                .Where(l => l.ConversionFactorToBase != l.ProductUnit.ConversionFactorToBase)
                .Select(l => new { l.Id, Line = l.ConversionFactorToBase, Row = l.ProductUnit.ConversionFactorToBase }).ToListAsync())
                .Select(x => $"PO line {x.Id}: line {x.Line} vs row {x.Row}"))
            .ToList();
        Assert.True(mismatches.Count == 0, "Line factor snapshots disagree with their product unit: " + string.Join("; ", mismatches));
    }

    // ------------------------------------------------------------------ edit vs edit

    /// <summary>
    /// Pen with Box and Half Carton both directly over Piece. Edit 1: "Box contains 2 Half Carton"; edit 2: "Half Carton
    /// contains 5 Box". Each submission is a complete, valid hierarchy, but EF writes only the columns that changed, so
    /// two unserialised edits would store Box → Half Carton and Half Carton → Box: a cycle. The gate holds the Half
    /// Carton row (both edits write it, after validating); the product lock makes edit 2 wait for edit 1 and validate
    /// against its committed state, so the result is exactly edit 2's (last writer's) hierarchy and never a cycle.
    /// Note: both edits succeed — the upsert is last-writer-wins on a full submission; there is no stale-form check.
    /// </summary>
    [Fact]
    public async Task UpdateProductAsync_TwoEditsThatTogetherFormACycle_SerialisedNoCycleStored()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Dictionary<Guid, ProductUnitInputItem> c;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            c = await CurrentAsync(db, s.PenId);
            // Flatten: Half Carton contains 50 Piece directly; drop Full Carton.
            await s.UpdatePenAsync(db, c[s.Pc], c[s.Box], c[s.HCtn] with { InnerUnitId = s.Pc, InnerQuantity = 50m });
            c = await CurrentAsync(db, s.PenId);
        }
        var hctnId = c[s.HCtn].Id!.Value;
        ProductUnitInputItem[] edit1 = [c[s.Pc], c[s.Box] with { InnerUnitId = s.HCtn, InnerQuantity = 2m }, c[s.HCtn]];
        ProductUnitInputItem[] edit2 = [c[s.Pc], c[s.Box], c[s.HCtn] with { InnerUnitId = s.Box, InnerQuantity = 5m }];

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("ProductUnits", "Id", hctnId));
        var first = await race.StartAsync("edit 1", db => s.UpdatePenAsync(db, edit1));
        await race.WaitBlockedAsync(first);
        var second = await race.StartAsync("edit 2", db => s.UpdatePenAsync(db, edit2));
        await race.WaitBlockedAsync(second);
        var outcomes = await race.ReleaseAsync();

        await using var check = database.CreateDbContext();
        await AssertNoCycleAsync(check);
        await ProductUnitValidation.AssertHoldAsync(check);
        AssertSucceeded(outcomes[0], "edit 1");
        AssertSucceeded(outcomes[1], "edit 2");
        var rows = await check.ProductUnits.AsNoTracking().Where(u => u.ProductId == s.PenId).ToDictionaryAsync(u => u.UnitId);
        Assert.Equal((rows[s.Pc].Id, 10m), (rows[s.Box].InnerProductUnitId, rows[s.Box].ConversionFactorToBase));
        Assert.Equal((rows[s.Box].Id, 50m), (rows[s.HCtn].InnerProductUnitId, rows[s.HCtn].ConversionFactorToBase));
    }

    // ------------------------------------------------------------------ edit vs Unit Master

    /// <summary>A kg product starts using the (unused) Crate unit while the Unit Master changes Crate from Packaging to
    /// Count (not allowed over a kg base). The gate holds Crate FOR UPDATE; the edit waits in its Units FOR SHARE, the
    /// unit update in its FOR UPDATE. Whichever goes first, the other sees its committed result.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductEditAndUnitCategoryChange_Concurrent_Serialised(bool editFirst)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Guid riceId;
        var crateId = Guid.NewGuid();
        Dictionary<Guid, ProductUnitInputItem> c;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            riceId = (await new ProductService(db).CreateProductAsync(s.Input("RICE", s.Kg, Row(s.Kg, null, 1m)))).Id;
            c = await CurrentAsync(db, riceId);
            db.Units.Add(new Unit { Id = crateId, Name = "Crate", ShortCode = "crate", MeasurementCategory = MeasurementCategory.Packaging });
            await db.SaveChangesAsync();
        }

        Task Edit(SelloraDbContext db) => new ProductService(db).UpdateProductAsync(riceId, s.Input("RICE", s.Kg, c[s.Kg], Row(crateId, s.Kg, 10m)));
        Task ChangeCategory(SelloraDbContext db) => new UnitService(db).UpdateUnitAsync(crateId, new UpsertUnitInput("Crate", "crate", MeasurementCategory.Count, 0, null));

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("Units", "Id", crateId));
        LockRace.Writer edit, unit;
        if (editFirst)
        {
            edit = await race.StartAsync("product edit", Edit);
            await race.WaitBlockedAsync(edit, UnitsForShare);
            unit = await race.StartAsync("unit update", ChangeCategory);
            await race.WaitBlockedAsync(unit, UnitsForUpdate);
        }
        else
        {
            unit = await race.StartAsync("unit update", ChangeCategory);
            await race.WaitBlockedAsync(unit, UnitsForUpdate);
            edit = await race.StartAsync("product edit", Edit);
            await race.WaitBlockedAsync(edit, UnitsForShare);
        }
        await race.ReleaseAsync();
        var (editOutcome, unitOutcome) = (await edit.Outcome, await unit.Outcome);

        await using var check = database.CreateDbContext();
        var box = await check.Units.AsNoTracking().SingleAsync(u => u.Id == crateId);
        var riceUsesBox = await check.ProductUnits.AnyAsync(u => u.ProductId == riceId && u.UnitId == crateId);
        Assert.False(riceUsesBox && box.MeasurementCategory == MeasurementCategory.Count, "A kg product now has a Count unit over its base.");
        if (editFirst)
        {
            AssertSucceeded(editOutcome, "product edit");
            Assert.Equal(nameof(UpsertUnitInput.MeasurementCategory), Assert.IsType<DomainValidationException>(unitOutcome).Field);
            Assert.True(riceUsesBox);
            Assert.Equal(MeasurementCategory.Packaging, box.MeasurementCategory);
        }
        else
        {
            AssertSucceeded(unitOutcome, "unit update");
            AssertRejected(editOutcome, "Units[1].UnitId", "Crate is a count unit; it can only be used over a count base unit.");
            Assert.False(riceUsesBox);
        }
    }

    // ------------------------------------------------------------------ document create vs edit

    private sealed record StockSeed(StockScenario Scenario, Guid KgUnitId, Guid BagUnitId, Guid TaxId);

    /// <summary>Rice (kg base, 10 kg Bag unlocked) with 30 kg in stock bought as loose kg (locks only the base row).</summary>
    private static async Task<StockSeed> SeedRiceWithStockAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var scenario = await StockScenario.SeedAsync(db);
        await new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
            new CreatePurchaseInput(scenario.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0,
                [new PurchaseLineInput(scenario.ProductId, scenario.LooseKgModeId, 30m, 58m, 0, 0)]), scenario.UserId);
        var bag = await db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == scenario.BagModeId);
        var kg = await db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == scenario.LooseKgModeId);
        var taxId = await db.Products.AsNoTracking().Where(p => p.Id == scenario.ProductId).Select(p => p.TaxId).SingleAsync();
        return new StockSeed(scenario, kg.UnitId, bag.UnitId, taxId);
    }

    private static Task<Product?> EditBagQuantityAsync(SelloraDbContext db, StockSeed seed, decimal kgPerBag) =>
        new ProductService(db).UpdateProductAsync(seed.Scenario.ProductId, new UpsertProductInput(
            "RICE-BP10", "Ponni Rice", null, seed.KgUnitId, seed.TaxId, 100m,
            [
                new ProductUnitInputItem(seed.Scenario.LooseKgModeId, seed.KgUnitId, null, 1m, "Loose kg", null, true, true, false, false, true),
                new ProductUnitInputItem(seed.Scenario.BagModeId, seed.BagUnitId, seed.KgUnitId, kgPerBag, $"{kgPerBag} kg bag", null, true, true, false, false, true),
            ]));

    private static Task SellOneBagAsync(SelloraDbContext db, StockScenario scenario) =>
        new SalesService(db).CreateSaleAsync(new CreateSaleInput(scenario.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0,
            [new SaleLineInput(scenario.ProductId, scenario.BagModeId, 1m, 600m, 0)]), scenario.UserId);

    private static Task BuyOneBagAsync(SelloraDbContext db, StockScenario scenario) =>
        new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(new CreatePurchaseInput(scenario.SupplierId, StockScenario.PurchaseDate,
            null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(scenario.ProductId, scenario.BagModeId, 1m, 580m, 0, 0)]), scenario.UserId);

    /// <summary>
    /// The sale has read the Bag factor (10) and built its line, then waits on the gate (StockItems FOR UPDATE).
    /// The edit changing the Bag to 12 kg must wait for the sale (its Products FOR UPDATE conflicts with the sale's
    /// KEY SHARE) and, once the sale commits, sees the Bag is used and rejects the change. Without the KEY SHARE the
    /// edit commits factor 12 underneath the sale, which then stores a 10 kg line against a 12 kg unit.
    /// </summary>
    [Fact]
    public async Task SaleHoldingProductKeyShare_EditOfItsUnit_WaitsAndIsRejectedNeverAFactorMismatch()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedRiceWithStockAsync(database);

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockItems", "ProductId", seed.Scenario.ProductId));
        var sale = await race.StartAsync("sale", db => SellOneBagAsync(db, seed.Scenario));
        await race.WaitBlockedAsync(sale, """%"StockItems"%FOR UPDATE%""");
        var edit = await race.StartAsync("edit", db => EditBagQuantityAsync(db, seed, 12m));
        var editBlocked = await race.WaitBlockedOrFinishedAsync(edit, ProductsForUpdate);
        var outcomes = await race.ReleaseAsync();

        await using var check = database.CreateDbContext();
        await AssertLineFactorsMatchRowsAsync(check);
        await ProductUnitValidation.AssertHoldAsync(check);
        Assert.True(editBlocked, "The product edit did not wait for the sale holding the product's KEY SHARE lock.");
        AssertSucceeded(outcomes[0], "sale");
        AssertRejected(outcomes[1], "Units[1].InnerQuantity", V.LockedMessage);
        Assert.Equal(10m, (await check.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == seed.Scenario.BagModeId)).ConversionFactorToBase);
    }

    /// <summary>
    /// The purchase takes KEY SHARE, builds its line from the Bag (factor 10) and then waits in its INSERT behind an
    /// uncommitted purchase with the same document number (the gate). The edit to 12 kg must wait and is then rejected.
    /// </summary>
    [Fact]
    public async Task PurchaseHoldingProductKeyShare_EditOfItsUnit_WaitsAndIsRejectedNeverAFactorMismatch()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedRiceWithStockAsync(database);
        var nextNo = "PUR-0002"; // one purchase exists

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            var supplier = await gate.Suppliers.AsNoTracking().SingleAsync(x => x.Id == seed.Scenario.SupplierId);
            gate.PurchaseOrders.Add(new PurchaseOrder
            {
                Id = Guid.NewGuid(), PurchaseNo = nextNo, SupplierId = supplier.Id, CurrencyId = supplier.CurrencyId,
                PurchaseDate = StockScenario.PurchaseDate, PaymentMethod = PaymentMethod.Cash, CreatedByUserId = seed.Scenario.UserId,
            });
            await gate.SaveChangesAsync();
        });
        var purchase = await race.StartAsync("purchase", db => BuyOneBagAsync(db, seed.Scenario));
        await race.WaitBlockedAsync(purchase, """%INSERT INTO "PurchaseOrders"%""");
        var edit = await race.StartAsync("edit", db => EditBagQuantityAsync(db, seed, 12m));
        var editBlocked = await race.WaitBlockedOrFinishedAsync(edit, ProductsForUpdate);
        var outcomes = await race.ReleaseAsync();

        await using var check = database.CreateDbContext();
        await AssertLineFactorsMatchRowsAsync(check);
        await ProductUnitValidation.AssertHoldAsync(check);
        Assert.True(editBlocked, "The product edit did not wait for the purchase holding the product's KEY SHARE lock.");
        AssertSucceeded(outcomes[0], "purchase");
        AssertRejected(outcomes[1], "Units[1].InnerQuantity", V.LockedMessage);
    }

    /// <summary>Edit first: both wait behind a gate holding Products FOR UPDATE (edit in FOR UPDATE, sale in KEY
    /// SHARE). The edit commits 12 kg; the sale then reads the new factor, so its line snapshots 12.</summary>
    [Fact]
    public async Task EditBeforeSale_SaleWaitsAndSnapshotsTheNewFactor()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedRiceWithStockAsync(database);

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("Products", "Id", seed.Scenario.ProductId));
        var edit = await race.StartAsync("edit", db => EditBagQuantityAsync(db, seed, 12m));
        await race.WaitBlockedAsync(edit, ProductsForUpdate);
        var sale = await race.StartAsync("sale", db => SellOneBagAsync(db, seed.Scenario));
        await race.WaitBlockedAsync(sale, ProductsKeyShare);
        var outcomes = await race.ReleaseAsync();

        AssertSucceeded(outcomes[0], "edit");
        AssertSucceeded(outcomes[1], "sale");
        await using var check = database.CreateDbContext();
        await AssertLineFactorsMatchRowsAsync(check);
        await ProductUnitValidation.AssertHoldAsync(check);
        var line = await check.SalesInvoiceLines.AsNoTracking().SingleAsync();
        Assert.Equal((12m, 12m), (line.ConversionFactorToBase, line.BaseQty));
    }

    // ------------------------------------------------------------------ pricing

    /// <summary>SetPrice and an edit of the same Box both wait behind a gate on the product row. Price first: the edit
    /// then sees the price and is rejected (a 10-pack price never silently becomes a 12-pack price). Edit first: the
    /// Box is a 12-pack when it is priced.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetPriceAndEditOfTheSameUnit_Concurrent_Serialised(bool priceFirst)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Dictionary<Guid, ProductUnitInputItem> c;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            c = await CurrentAsync(db, s.PenId);
        }
        var boxId = c[s.Box].Id!.Value;
        Task Price(SelloraDbContext db) => new PricingService(db).SetPriceAsync(boxId, 95m, s.UserId);
        Task Edit(SelloraDbContext db) => s.UpdatePenAsync(db, c[s.Pc], c[s.Box] with { InnerQuantity = 12m }, c[s.HCtn], c[s.FCtn]);

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("Products", "Id", s.PenId));
        var firstWriter = await race.StartAsync(priceFirst ? "price" : "edit", priceFirst ? Price : Edit);
        await race.WaitBlockedAsync(firstWriter, ProductsForUpdate);
        var secondWriter = await race.StartAsync(priceFirst ? "edit" : "price", priceFirst ? Edit : Price);
        await race.WaitBlockedAsync(secondWriter, ProductsForUpdate);
        await race.ReleaseAsync();
        var (price, edit) = priceFirst ? (await firstWriter.Outcome, await secondWriter.Outcome) : (await secondWriter.Outcome, await firstWriter.Outcome);

        AssertSucceeded(price, "set price");
        await using var check = database.CreateDbContext();
        var box = await check.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == boxId);
        Assert.Equal(1, await check.SellingPrices.CountAsync(p => p.ProductUnitId == boxId && p.EffectiveTo == null));
        if (priceFirst)
        {
            AssertRejected(edit, "Units[1].InnerQuantity", V.LockedMessage);
            Assert.Equal(10m, box.ConversionFactorToBase);
        }
        else
        {
            AssertSucceeded(edit, "edit");
            Assert.Equal(12m, box.ConversionFactorToBase);
        }
    }

    /// <summary>
    /// Two price changes for a Box that already has an open price. The gate holds the open price row, so without the
    /// product lock both would read it as current, both close it and both insert an open price. With the lock the
    /// second waits on the product row and then closes the first one's new price: exactly one open price remains.
    /// </summary>
    [Fact]
    public async Task SetPriceAsync_TwoConcurrentChanges_LeaveExactlyOneOpenPrice()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Guid boxId, openPriceId;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            boxId = (await ActiveRowAsync(db, s.PenId, s.Box)).Id;
            openPriceId = (await new PricingService(db).SetPriceAsync(boxId, 90m, s.UserId)).Id;
        }

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("SellingPrices", "Id", openPriceId));
        var first = await race.StartAsync("price 95", db => new PricingService(db).SetPriceAsync(boxId, 95m, s.UserId));
        await race.WaitBlockedAsync(first);
        var second = await race.StartAsync("price 99", db => new PricingService(db).SetPriceAsync(boxId, 99m, s.UserId));
        await race.WaitBlockedAsync(second);
        var outcomes = await race.ReleaseAsync();

        await using var check = database.CreateDbContext();
        var open = await check.SellingPrices.AsNoTracking().Where(p => p.ProductUnitId == boxId && p.EffectiveTo == null).ToListAsync();
        Assert.True(open.Count == 1, $"Expected one open price, found {open.Count}: {string.Join(", ", open.Select(p => p.Price))}.");
        AssertSucceeded(outcomes[0], "price 95");
        AssertSucceeded(outcomes[1], "price 99");
        Assert.Equal(99m, open[0].Price);
        Assert.Equal(3, await check.SellingPrices.CountAsync(p => p.ProductUnitId == boxId));
    }

    // ------------------------------------------------------------------ barcode uniqueness

    /// <summary>The gate inserts (uncommitted) another product whose base row carries the barcode, standing in for a
    /// concurrent save that passed its pre-check. The Pen edit cannot see it, passes its own pre-check and waits in its
    /// UPDATE on IX_ProductUnits_Barcode; when the gate commits it gets 23505, which must surface as the same
    /// Units[i].Barcode field error as the pre-check, not as a DbUpdateException.</summary>
    [Fact]
    public async Task UpdateProductAsync_ConcurrentDuplicateBarcode_MappedToUnitsBarcodeError()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Dictionary<Guid, ProductUnitInputItem> c;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            c = await CurrentAsync(db, s.PenId);
        }
        const string barcode = "8901000000777";

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            var other = new Product { Id = Guid.NewGuid(), Sku = "OTHER", Name = "Other", BaseUnitId = s.Pc, TaxId = s.TaxId };
            other.Units.Add(new ProductUnit
            {
                Id = Guid.NewGuid(), ProductId = other.Id, UnitId = s.Pc, IsBaseUnit = true, InnerQuantity = 1m, ConversionFactorToBase = 1m, Barcode = barcode,
            });
            gate.Products.Add(other);
            await gate.SaveChangesAsync();
        });
        var edit = await race.StartAsync("edit", db => s.UpdatePenAsync(db, c[s.Pc], c[s.Box], c[s.HCtn] with { Barcode = barcode }, c[s.FCtn]));
        await race.WaitBlockedAsync(edit, """%"ProductUnits"%""",
            "The barcode UPDATE did not block on the gate's uncommitted row: IX_ProductUnits_Barcode is missing or not unique.");
        var outcomes = await race.ReleaseAsync(commit: true);

        var ex = AssertRejected(outcomes[0], "Units[2].Barcode", "This barcode is already used by another product.");
        Assert.Single(ex.Errors);
        await using var check = database.CreateDbContext();
        Assert.Equal(1, await check.ProductUnits.CountAsync(u => u.Barcode == barcode));
        Assert.Null((await check.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == c[s.HCtn].Id)).Barcode);
    }
}
