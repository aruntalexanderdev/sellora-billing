using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;
using V = Sellora.Domain.Inventory.ProductUnitConfigurationValidator;

namespace Sellora.Tests.Purchasing;

/// <summary>
/// Phase 4 (analysis §11 scenarios S1-S7): purchases and purchase returns in product units, on EF InMemory. The Pen is
/// Piece 1, Box 10, Half Carton 50, Full Carton 100; products are created through <see cref="ProductService"/> so the
/// rows (and their lock behaviour) are the real ones. Row locks, constraints and concurrency run against PostgreSQL in
/// Sellora.IntegrationTests.
/// </summary>
public class PurchaseUnitScenarioTests
{
    private static readonly DateOnly D = new(2026, 9, 10);

    private sealed record Fixture(
        SelloraDbContext Db, PurchaseService Purchases, ISalesService Sales, ProductService Products,
        Supplier InrSupplier, Supplier UsdSupplier, Customer Customer, Tax Tax, Guid UserId,
        Unit Pc, Unit Box, Unit HCtn, Unit FCtn, Unit Tray, Unit Kg, Unit Kg2, Unit Pack,
        Product Pen, Dictionary<Guid, ProductUnit> PenRows);

    private static async Task<Fixture> SeedAsync(decimal taxRate = 0m)
    {
        var db = TestDb.NewInMemory();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var tax = new Tax { Id = Guid.NewGuid(), Name = $"GST {taxRate}%", RatePercent = taxRate };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var inrSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var usdSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Global Ingredients Inc.", CurrencyId = usd.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var rate = new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = 83.20m, EffectiveDate = new DateOnly(2026, 9, 1), CreatedByUserId = user.Id };
        var (pc, box, hctn, fctn, tray, kg) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton(),
            TestUnits.New("Tray", "tray", MeasurementCategory.Packaging, 0, null), TestUnits.Kilogram());
        var kg2 = TestUnits.New("Kilogram (2 dp)", "kg2", MeasurementCategory.Weight, 2, 1m);
        var pack = TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 3, null);
        db.AddRange(role, user, tax, inr, usd, settings, inrSupplier, usdSupplier, customer, rate, pc, box, hctn, fctn, tray, kg, kg2, pack);
        await db.SaveChangesAsync();
        await TaxFixtures.UseAsCompanyDefaultAsync(db, tax);

        var products = new ProductService(db);
        var f0 = new Fixture(db, new PurchaseService(db, new ExchangeRateService(db)), ListPricedSales.For(db), products,
            inrSupplier, usdSupplier, customer, tax, user.Id, pc, box, hctn, fctn, tray, kg, kg2, pack, null!, null!);
        var pen = await products.CreateProductAsync(Input(f0, "STN-PEN", pc.Id,
            Row(pc.Id, null, null), Row(box.Id, pc.Id, 10m), Row(hctn.Id, box.Id, 5m), Row(fctn.Id, hctn.Id, 2m)));
        return f0 with { Pen = pen, PenRows = await RowsAsync(f0, pen.Id) };
    }

    private static ProductUnitInputItem Row(Guid unitId, Guid? innerUnitId, decimal? qty, Guid? id = null, bool active = true, bool purchasable = true) =>
        new(id, unitId, innerUnitId, qty, null, null, purchasable, true, false, false, active);

    private static UpsertProductInput Input(Fixture f, string sku, Guid baseUnitId, params ProductUnitInputItem[] units) =>
        new(sku, "Ball Pen", null, null, baseUnitId, 0m, units);

    private static async Task<Dictionary<Guid, ProductUnit>> RowsAsync(Fixture f, Guid productId) =>
        (await f.Db.ProductUnits.AsNoTracking().Where(u => u.ProductId == productId).ToListAsync()).ToDictionary(u => u.UnitId);

    /// <summary>A second pen sharing the Piece/Box master rows: Box 10 and a leaf Tray of 20; Piece is not purchasable.</summary>
    private static async Task<(Product Product, Dictionary<Guid, ProductUnit> Rows)> CreateTrayPenAsync(Fixture f, bool pieceIsPurchasable = true)
    {
        var product = await f.Products.CreateProductAsync(Input(f, "STN-PEN2", f.Pc.Id,
            Row(f.Pc.Id, null, null, purchasable: pieceIsPurchasable), Row(f.Box.Id, f.Pc.Id, 10m), Row(f.Tray.Id, f.Pc.Id, 20m)));
        return (product, await RowsAsync(f, product.Id));
    }

    private static PurchaseLineInput Line(ProductUnit unit, decimal qty, decimal rate, decimal otherCharges = 0, decimal discount = 0) =>
        new(unit.ProductId, unit.Id, qty, rate, otherCharges, discount);

    private static Task<PurchaseOrder> BuyAsync(Fixture f, params PurchaseLineInput[] lines) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.InrSupplier.Id, D, null, null, PaymentMethod.Cash, 0, lines), f.UserId);

    private static Task<PurchaseReturn> ReturnAsync(Fixture f, PurchaseOrder purchase, PurchaseOrderLine line, ProductUnit? unit, decimal qty) =>
        f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, "wrong goods",
            [new PurchaseReturnLineInput(line.Id, unit?.Id, qty)]), f.UserId);

    private static async Task<DomainValidationException> RejectedAsync(Func<Task> act, string field, string? message = null)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        Assert.Equal(field, ex.Field);
        if (message is not null) Assert.Equal(message, ex.Message);
        return ex;
    }

    /// <summary>Σ movements = QuantityOnHand = Σ batches per product, and every line's Qty x factor = BaseQty; plus the
    /// snapshot columns are never left at the entity default (empty UnitCode) and match the unit master.</summary>
    private static async Task AssertConsistentAsync(Fixture f)
    {
        f.Db.ChangeTracker.Clear();
        foreach (var stock in await f.Db.StockItems.AsNoTracking().ToListAsync())
        {
            var ledger = (await f.Db.StockMovements.Where(m => m.ProductId == stock.ProductId).Select(m => m.QuantityBaseUnits).ToListAsync()).Sum();
            var batches = (await f.Db.PurchaseBatches.Where(b => b.ProductId == stock.ProductId).Select(b => b.RemainingBaseQty).ToListAsync()).Sum();
            Assert.Equal(stock.QuantityOnHand, ledger);
            Assert.Equal(stock.QuantityOnHand, batches);
        }
        foreach (var l in await f.Db.PurchaseOrderLines.AsNoTracking().Include(x => x.Unit).ToListAsync())
        {
            Assert.Equal(l.BaseQty, l.Qty * l.ConversionFactorToBase);
            Assert.False(string.IsNullOrWhiteSpace(l.UnitCode), $"PO line {l.Id} has an empty UnitCode.");
            Assert.Equal(l.Unit.ShortCode, l.UnitCode);
        }
        foreach (var r in await f.Db.PurchaseReturnLines.AsNoTracking().Include(x => x.Unit).ToListAsync())
        {
            Assert.Equal(r.BaseQty, r.Qty * r.ConversionFactorToBase);
            Assert.False(string.IsNullOrWhiteSpace(r.UnitCode), $"Return line {r.Id} has an empty UnitCode.");
            Assert.Equal(r.Unit.ShortCode, r.UnitCode);
        }
    }

    // ------------------------------------------------------------------ S1-S3: purchase in a pack unit

    [Fact]
    public async Task CreatePurchaseAsync_S1TwoFullCartons_SnapshotsUnitAndPostsTwoHundredPieces()
    {
        var f = await SeedAsync();
        var fctn = f.PenRows[f.FCtn.Id];

        var purchase = await BuyAsync(f, Line(fctn, 2m, 1000m));

        var line = purchase.Lines.Single();
        Assert.Equal((2m, 100m, 200m), (line.Qty, line.ConversionFactorToBase, line.BaseQty));
        Assert.Equal((fctn.Id, f.FCtn.Id, "fctn"), (line.ProductUnitId, line.UnitId, line.UnitCode));
        Assert.Equal(10m, line.UnitCost); // 2000 / 200 pc
        var batch = await f.Db.PurchaseBatches.SingleAsync();
        Assert.Equal((200m, 200m, 10m), (batch.PurchasedBaseQty, batch.RemainingBaseQty, batch.UnitCost));
        Assert.Equal(200m, (await f.Db.StockMovements.SingleAsync()).QuantityBaseUnits);
        var stock = await f.Db.StockItems.SingleAsync();
        Assert.Equal((200m, 10m), (stock.QuantityOnHand, stock.AverageCost));
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreatePurchaseAsync_S2FiveBoxes_SnapshotsFactorTenAndPostsFiftyPieces()
    {
        var f = await SeedAsync();
        var box = f.PenRows[f.Box.Id];

        var purchase = await BuyAsync(f, Line(box, 5m, 100m));

        var line = purchase.Lines.Single();
        Assert.Equal((5m, 10m, 50m, "box"), (line.Qty, line.ConversionFactorToBase, line.BaseQty, line.UnitCode));
        Assert.Equal(50m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreatePurchaseAsync_S3TenPieces_BaseUnitFactorOne()
    {
        var f = await SeedAsync();
        var pc = f.PenRows[f.Pc.Id];

        var purchase = await BuyAsync(f, Line(pc, 10m, 12m));

        var line = purchase.Lines.Single();
        Assert.Equal((10m, 1m, 10m, "pc", f.Pc.Id), (line.Qty, line.ConversionFactorToBase, line.BaseQty, line.UnitCode, line.UnitId));
        Assert.Equal(10m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertConsistentAsync(f);
    }

    // ------------------------------------------------------------------ S4/S5: mixed units

    [Fact]
    public async Task S4_BuyFullCartonsSellBoxes_ConsumesTheCartonBatchAndInvariantsHold()
    {
        var f = await SeedAsync();
        await BuyAsync(f, Line(f.PenRows[f.FCtn.Id], 2m, 1000m));

        var sale = await f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, 0,
            [new SaleLineInput(f.Pen.Id, f.PenRows[f.Box.Id].Id, 3m, 120m, 0)]), f.UserId);

        Assert.Equal(30m, sale.Lines.Single().BaseQty);
        var batch = await f.Db.PurchaseBatches.SingleAsync();
        Assert.Equal((200m, 170m), (batch.PurchasedBaseQty, batch.RemainingBaseQty));
        var consumption = await f.Db.SalesInvoiceLineBatchConsumptions.SingleAsync();
        Assert.Equal((batch.Id, 30m), (consumption.PurchaseBatchId, consumption.QtyBaseUnits));
        Assert.Equal(170m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreatePurchaseAsync_S5CartonAndBoxLinesOfTheSameProductInOneDocument_TwoBatchesAndTwoFiftyOnHand()
    {
        var f = await SeedAsync();

        var purchase = await BuyAsync(f, Line(f.PenRows[f.FCtn.Id], 2m, 1000m), Line(f.PenRows[f.Box.Id], 5m, 100m));

        Assert.Equal(2, purchase.Lines.Count);
        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.BatchSequence).ToListAsync();
        Assert.Equal([50m, 200m], batches.Select(b => b.PurchasedBaseQty).Order()); // BatchSequence is a Postgres sequence: no order in InMemory
        Assert.Equal([10m, 10m], batches.Select(b => b.UnitCost)); // 2000/200 and 500/50
        Assert.Equal(2, await f.Db.StockMovements.CountAsync());
        var stock = await f.Db.StockItems.SingleAsync();
        Assert.Equal((250m, 10m), (stock.QuantityOnHand, stock.AverageCost));
        await AssertConsistentAsync(f);
    }

    // ------------------------------------------------------------------ S6: packaging changes over time

    [Fact]
    public async Task S6_BoxOfTenUsedInAPurchase_EditRejected_ReplacedByBoxOfTwelve_OldLineKeepsFactorTen()
    {
        var f = await SeedAsync();
        var rows = f.PenRows;
        var oldBox = rows[f.Box.Id];
        var first = await BuyAsync(f, Line(oldBox, 1m, 100m));

        // 1. Editing the used Box in place is refused (units[1] = Box).
        ProductUnitInputItem Existing(ProductUnit r, Guid? innerUnit, decimal? qty, bool active = true) =>
            Row(r.UnitId, innerUnit, qty, r.Id, active);
        var edited = new[]
        {
            Existing(rows[f.Pc.Id], null, null), Existing(oldBox, f.Pc.Id, 12m),
            Existing(rows[f.HCtn.Id], f.Box.Id, 5m), Existing(rows[f.FCtn.Id], f.HCtn.Id, 2m),
        };
        var rejected = await Assert.ThrowsAsync<DomainValidationException>(() =>
            f.Products.UpdateProductAsync(f.Pen.Id, Input(f, "STN-PEN", f.Pc.Id, edited)));
        Assert.Contains(rejected.Errors, e => e.Message == V.LockedMessage);
        Assert.Equal(10m, (await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == oldBox.Id)).ConversionFactorToBase);

        // 2. Deactivate it and add a Box of 12; the Half Carton/Full Carton chain over it is deactivated with it.
        var bigBox = await ReplaceBoxAsync(f, rows, oldBox);
        var second = await BuyAsync(f, Line(bigBox, 1m, 120m));

        f.Db.ChangeTracker.Clear();
        var firstLine = await f.Db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == first.Lines.Single().Id);
        var secondLine = await f.Db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == second.Lines.Single().Id);
        Assert.Equal((oldBox.Id, 10m, 10m), (firstLine.ProductUnitId, firstLine.ConversionFactorToBase, firstLine.BaseQty));
        Assert.Equal((bigBox.Id, 12m, 12m), (secondLine.ProductUnitId, secondLine.ConversionFactorToBase, secondLine.BaseQty));
        Assert.False((await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == oldBox.Id)).IsActive);
        await AssertConsistentAsync(f);
    }

    private static async Task<ProductUnit> ReplaceBoxAsync(Fixture f, Dictionary<Guid, ProductUnit> rows, ProductUnit oldBox)
    {
        await f.Products.UpdateProductAsync(f.Pen.Id, Input(f, "STN-PEN", f.Pc.Id,
            Row(f.Pc.Id, null, null, rows[f.Pc.Id].Id),
            Row(f.Box.Id, f.Pc.Id, 10m, oldBox.Id, active: false),
            Row(f.Box.Id, f.Pc.Id, 12m)));
        return await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.ProductId == f.Pen.Id && u.UnitId == f.Box.Id && u.IsActive);
    }

    // ------------------------------------------------------------------ S7: return 37 pc against a carton line

    [Fact]
    public async Task CreateReturnAsync_S7ThirtySevenPiecesAgainstACartonLine_ThenACartonThenTheExactRemainder()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, Line(f.PenRows[f.FCtn.Id], 2m, 1000m)); // 2000 for 200 pc, cost 10
        var line = purchase.Lines.Single();

        var r1 = await ReturnAsync(f, purchase, line, f.PenRows[f.Pc.Id], 37m);
        var l1 = r1.Lines.Single();
        Assert.Equal((37m, 1m, 37m, "pc", f.PenRows[f.Pc.Id].Id), (l1.Qty, l1.ConversionFactorToBase, l1.BaseQty, l1.UnitCode, l1.ProductUnitId));
        Assert.Equal((370m, 370m), (l1.TaxableValue, l1.CostRemoved));
        Assert.Equal(163m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);

        var r2 = await ReturnAsync(f, purchase, line, f.PenRows[f.FCtn.Id], 1m);
        var l2 = r2.Lines.Single();
        Assert.Equal((1m, 100m, 100m, "fctn"), (l2.Qty, l2.ConversionFactorToBase, l2.BaseQty, l2.UnitCode));
        Assert.Equal((1000m, 1000m), (l2.TaxableValue, l2.CostRemoved));

        var r3 = await ReturnAsync(f, purchase, line, f.PenRows[f.Pc.Id], 63m);
        Assert.Equal(630m, r3.Lines.Single().TaxableValue); // exactly what is left of 2000 - 370 - 1000

        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(2000m, (await f.Db.PurchaseOrders.SingleAsync()).ReturnedAmount);
        Assert.Equal(200m, -(await f.Db.StockMovements.Where(m => m.Type == StockMovementType.PurchaseReturn).Select(m => m.QuantityBaseUnits).ToListAsync()).Sum());
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreateReturnAsync_PartialReturnsInMixedUnits_ProrateByBaseAndLastTakesTheExactRemainderWithTax()
    {
        var f = await SeedAsync(taxRate: 5m);
        var purchase = await BuyAsync(f, Line(f.PenRows[f.Box.Id], 1m, 100.01m)); // 10 pc; taxable 100.01, tax 5.0005 -> 5.00 per line? read from the line
        var line = purchase.Lines.Single();

        var a = (await ReturnAsync(f, purchase, line, f.PenRows[f.Pc.Id], 3m)).Lines.Single();
        var b = (await ReturnAsync(f, purchase, line, f.PenRows[f.Pc.Id], 3m)).Lines.Single();
        var c = (await ReturnAsync(f, purchase, line, f.PenRows[f.Pc.Id], 4m)).Lines.Single();

        Assert.Equal(Math.Round(line.TaxableValue * 3 / 10, 2, MidpointRounding.AwayFromZero), a.TaxableValue);
        Assert.Equal(a.TaxableValue, b.TaxableValue);
        Assert.Equal(line.TaxableValue, a.TaxableValue + b.TaxableValue + c.TaxableValue);
        Assert.Equal(line.TaxAmount, a.TaxAmount + b.TaxAmount + c.TaxAmount);
        Assert.Equal(line.LineTotal, a.LineTotal + b.LineTotal + c.LineTotal);
        Assert.Equal(line.LineTotal, (await f.Db.PurchaseOrders.SingleAsync()).ReturnedAmount);
    }

    [Fact]
    public async Task CreateReturnAsync_SameUnitReturn_MatchesTheQtyOverLineQtyProrationOfBefore()
    {
        var f = await SeedAsync(taxRate: 5m);
        var purchase = await BuyAsync(f, Line(f.PenRows[f.Box.Id], 3m, 33.33m));
        var line = purchase.Lines.Single();

        var returned = (await ReturnAsync(f, purchase, line, null, 1m)).Lines.Single(); // null = the line's own unit

        Assert.Equal(line.ProductUnitId, returned.ProductUnitId);
        Assert.Equal(Math.Round(line.TaxableValue * 1m / line.Qty, 2, MidpointRounding.AwayFromZero), returned.TaxableValue);
        Assert.Equal(Math.Round(line.TaxAmount * 1m / line.Qty, 2, MidpointRounding.AwayFromZero), returned.TaxAmount);
        Assert.Equal((1m, 10m, 10m), (returned.Qty, returned.ConversionFactorToBase, returned.BaseQty));
    }

    // ------------------------------------------------------------------ purchase: negative cases

    [Fact]
    public async Task CreatePurchaseAsync_OneAndAHalfBoxes_RejectedAsWholeNumbersAndNothingPosted()
    {
        var f = await SeedAsync();

        await RejectedAsync(() => BuyAsync(f, Line(f.PenRows[f.Box.Id], 1.5m, 100m)), "Lines", "Box is counted in whole numbers.");

        Assert.Empty(await f.Db.PurchaseOrders.ToListAsync());
        Assert.Empty(await f.Db.PurchaseBatches.ToListAsync());
    }

    [Fact]
    public async Task CreatePurchaseAsync_FourDecimalKilograms_RejectedNotRounded()
    {
        var f = await SeedAsync();
        var rice = await f.Products.CreateProductAsync(Input(f, "RICE", f.Kg.Id, Row(f.Kg.Id, null, null)));
        var kg = (await RowsAsync(f, rice.Id))[f.Kg.Id];

        await RejectedAsync(() => BuyAsync(f, Line(kg, 0.0001m, 50m)), "Lines", "Kilogram allows at most 3 decimal place(s).");

        Assert.Empty(await f.Db.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task CreatePurchaseAsync_PackOfHalfAKiloOnATwoDecimalKgBase_RejectedWithTheBaseDecimalsMessage()
    {
        var f = await SeedAsync();
        var rice = await f.Products.CreateProductAsync(Input(f, "RICE2", f.Kg2.Id, Row(f.Kg2.Id, null, null), Row(f.Pack.Id, f.Kg2.Id, 0.5m)));
        var rows = await RowsAsync(f, rice.Id);

        var ex = await RejectedAsync(() => BuyAsync(f, Line(rows[f.Pack.Id], 0.01m, 10m)), "Lines");

        Assert.Equal("0.01 pk = 0.005 kg2; Kilogram (2 dp) is tracked to 2 decimal place(s).", ex.Message);
        await BuyAsync(f, Line(rows[f.Pack.Id], 0.02m, 10m)); // 0.01 kg fits: the rule rejects only what does not fit
        Assert.Equal(0.01m, (await f.Db.PurchaseBatches.SingleAsync()).PurchasedBaseQty);
    }

    // ------------------------------------------------------------------ returns: rejected

    [Fact]
    public async Task CreateReturnAsync_OverReturnInMixedUnits_RejectedAgainstTheBaseRemainder()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, Line(f.PenRows[f.FCtn.Id], 2m, 1000m));
        var line = purchase.Lines.Single();
        await ReturnAsync(f, purchase, line, f.PenRows[f.Pc.Id], 37m);

        await RejectedAsync(() => ReturnAsync(f, purchase, line, f.PenRows[f.FCtn.Id], 2m), "Lines", "Only 163 pc of Ball Pen can still be returned.");

        Assert.Single(await f.Db.PurchaseReturns.ToListAsync());
        Assert.Equal(163m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateReturnAsync_MoreThanIsStillInTheBatchAfterASale_Rejected()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, Line(f.PenRows[f.FCtn.Id], 2m, 1000m));
        await f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, 0,
            [new SaleLineInput(f.Pen.Id, f.PenRows[f.Box.Id].Id, 19m, 120m, 0)]), f.UserId); // 190 pc sold, 10 left

        await RejectedAsync(() => ReturnAsync(f, purchase, purchase.Lines.Single(), f.PenRows[f.FCtn.Id], 1m), "Lines",
            "Only 10 pc of Ball Pen from this purchase is still in stock to return.");
        await ReturnAsync(f, purchase, purchase.Lines.Single(), f.PenRows[f.Box.Id], 1m); // 10 pc is exactly what is left

        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreateReturnAsync_UnitOfAnotherProduct_Rejected()
    {
        var f = await SeedAsync();
        var (_, other) = await CreateTrayPenAsync(f);
        var purchase = await BuyAsync(f, Line(f.PenRows[f.Box.Id], 2m, 100m));

        // The other product is not on this purchase, so its units are not even loaded: the generic message applies.
        await RejectedAsync(() => ReturnAsync(f, purchase, purchase.Lines.Single(), other[f.Box.Id], 1m), "Lines");

        Assert.Empty(await f.Db.PurchaseReturns.ToListAsync());
    }

    [Fact]
    public async Task CreateReturnAsync_UnitOfAnotherProductOnTheSamePurchase_RejectedNamingTheProduct()
    {
        var f = await SeedAsync();
        var (_, other) = await CreateTrayPenAsync(f);
        var purchase = await BuyAsync(f, Line(f.PenRows[f.Box.Id], 2m, 100m), Line(other[f.Box.Id], 2m, 100m));
        var penLine = purchase.Lines.Single(l => l.ProductId == f.Pen.Id);

        await RejectedAsync(() => ReturnAsync(f, purchase, penLine, other[f.Box.Id], 1m), "Lines", "The selected unit does not belong to Ball Pen.");

        Assert.Empty(await f.Db.PurchaseReturns.ToListAsync());
    }

    [Fact]
    public async Task CreateReturnAsync_InactiveUnitThatIsNotTheLineUnit_Rejected()
    {
        var f = await SeedAsync();
        var (pen2, rows) = await CreateTrayPenAsync(f);
        var purchase = await BuyAsync(f, Line(rows[f.Box.Id], 2m, 100m));
        await DeactivateTrayAsync(f, pen2, rows);

        await RejectedAsync(() => ReturnAsync(f, purchase, purchase.Lines.Single(), rows[f.Tray.Id], 1m), "Lines", "Tray is no longer active.");
    }

    [Fact]
    public async Task CreateReturnAsync_OneAndAHalfBoxes_RejectedAsWholeNumbers()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, Line(f.PenRows[f.FCtn.Id], 2m, 1000m));

        await RejectedAsync(() => ReturnAsync(f, purchase, purchase.Lines.Single(), f.PenRows[f.Box.Id], 1.5m), "Lines", "Box is counted in whole numbers.");

        Assert.Empty(await f.Db.PurchaseReturns.ToListAsync());
    }

    [Fact]
    public async Task CreateReturnAsync_QuantityThatDoesNotFitTheUnitDecimals_RejectedNotRoundedToThree()
    {
        // Before Phase 4 the service ran Math.Round(qty, 3) on the way in; 0.0004 kg silently became 0 and then failed
        // as "must be greater than zero". It must now be reported as what it is.
        var f = await SeedAsync();
        var rice = await f.Products.CreateProductAsync(Input(f, "RICE", f.Kg.Id, Row(f.Kg.Id, null, null)));
        var kg = (await RowsAsync(f, rice.Id))[f.Kg.Id];
        var purchase = await BuyAsync(f, Line(kg, 10m, 50m));

        await RejectedAsync(() => ReturnAsync(f, purchase, purchase.Lines.Single(), null, 0.0004m), "Lines", "Kilogram allows at most 3 decimal place(s).");
    }

    // ------------------------------------------------------------------ returns: allowed

    [Fact]
    public async Task CreateReturnAsync_LineUnitDeactivatedSincePurchase_StillAllowedInThatUnit()
    {
        var f = await SeedAsync();
        var (pen2, rows) = await CreateTrayPenAsync(f);
        var purchase = await BuyAsync(f, Line(rows[f.Tray.Id], 2m, 200m)); // 40 pc
        await DeactivateTrayAsync(f, pen2, rows);

        var returned = (await ReturnAsync(f, purchase, purchase.Lines.Single(), null, 1m)).Lines.Single();

        Assert.Equal((rows[f.Tray.Id].Id, "tray", 20m), (returned.ProductUnitId, returned.UnitCode, returned.BaseQty));
    }

    [Fact]
    public async Task CreateReturnAsync_PieceThatIsNotPurchasable_AllowedBecauseReturnsReverseReceivedGoods()
    {
        var f = await SeedAsync();
        var (_, rows) = await CreateTrayPenAsync(f, pieceIsPurchasable: false);
        var purchase = await BuyAsync(f, Line(rows[f.Box.Id], 2m, 100m));
        await RejectedAsync(() => BuyAsync(f, Line(rows[f.Pc.Id], 5m, 10m)), "Lines", "Piece cannot be purchased for Ball Pen.");

        var returned = (await ReturnAsync(f, purchase, purchase.Lines.Single(), rows[f.Pc.Id], 5m)).Lines.Single();

        Assert.Equal((5m, 5m, "pc"), (returned.Qty, returned.BaseQty, returned.UnitCode));
        Assert.Equal(15m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    // ------------------------------------------------------------------ precision

    [Fact]
    public async Task CreatePurchaseAsync_ThousandOverThreePieces_UnitCostKeptToSixDecimals()
    {
        var f = await SeedAsync();

        var purchase = await BuyAsync(f, Line(f.PenRows[f.Pc.Id], 3m, 0m, otherCharges: 1000m));

        Assert.Equal(333.333333m, purchase.Lines.Single().UnitCost);
        Assert.Equal(333.333333m, (await f.Db.PurchaseBatches.SingleAsync()).UnitCost);
        Assert.Equal(333.333333m, (await f.Db.StockItems.SingleAsync()).AverageCost);
    }

    [Fact]
    public async Task CreatePurchaseAsync_SecondPurchase_AverageCostRoundedToSixDecimals()
    {
        var f = await SeedAsync();
        await BuyAsync(f, Line(f.PenRows[f.Pc.Id], 3m, 0m, otherCharges: 1000m)); // 3 pc @ 333.333333
        await BuyAsync(f, Line(f.PenRows[f.Pc.Id], 4m, 0m, otherCharges: 100m));  // 4 pc @ 25

        var stock = await f.Db.StockItems.SingleAsync();

        var expected = Math.Round((3m * 333.333333m + 4m * 25m) / 7m, 6, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, stock.AverageCost);
        Assert.Equal(stock.AverageCost, Math.Round(stock.AverageCost, 6));
    }

    [Fact]
    public async Task CreatePurchaseAsync_ForeignCartonOf144_UnitCostAtSixDecimalsKeepsBatchValueWithinAHundredthOfAPaisa()
    {
        var f = await SeedAsync();
        var carton = new Unit { Id = Guid.NewGuid(), Name = "Carton 144", ShortCode = "c144", MeasurementCategory = MeasurementCategory.Packaging, DecimalPlaces = 0 };
        f.Db.Units.Add(carton);
        await f.Db.SaveChangesAsync();
        var product = await f.Products.CreateProductAsync(Input(f, "PEN-144", f.Pc.Id, Row(f.Pc.Id, null, null), Row(carton.Id, f.Pc.Id, 144m)));
        var rows = await RowsAsync(f, product.Id);

        // USD 12.34 per carton of 144, 3 cartons at 83.20.
        var purchase = await f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.UsdSupplier.Id, D, null, null, PaymentMethod.Cash, 0,
            [Line(rows[carton.Id], 3m, 12.34m)]), f.UserId);

        var line = purchase.Lines.Single();
        Assert.Equal(37.02m, purchase.OriginalGrandTotal);
        Assert.Equal(3080.06m, line.TaxableValue);
        Assert.Equal(1026.688m, line.Rate);
        Assert.Equal((3m, 144m, 432m, "c144"), (line.Qty, line.ConversionFactorToBase, line.BaseQty, line.UnitCode));
        Assert.Equal(7.129769m, line.UnitCost);
        var drift = Math.Abs(432m * line.UnitCost - line.TaxableValue);
        Assert.True(drift < 0.001m, $"Batch value drifts from the invoice by {drift}.");
        Assert.Equal(7.129769m, (await f.Db.PurchaseBatches.SingleAsync()).UnitCost);
    }

    // ------------------------------------------------------------------ ProductUnitUsage: a unit referenced only by a return

    [Fact]
    public async Task GetLockReasonsAsync_UnitReferencedOnlyByAReturnLine_IsLockedAsTransactions()
    {
        var f = await SeedAsync();
        var (pen2, rows) = await CreateTrayPenAsync(f);
        var purchase = await BuyAsync(f, Line(rows[f.Box.Id], 4m, 100m)); // 40 pc; Tray is on no purchase line
        var before = await ProductUnitUsage.GetLockReasonsAsync(f.Db, rows.Values.ToList(), default);
        Assert.DoesNotContain(rows[f.Tray.Id].Id, before.Keys);

        await ReturnAsync(f, purchase, purchase.Lines.Single(), rows[f.Tray.Id], 1m);

        var after = await ProductUnitUsage.GetLockReasonsAsync(f.Db, rows.Values.ToList(), default);
        Assert.Equal(ProductUnitUsage.Transactions, after[rows[f.Tray.Id].Id]);
        // ... and the editor refuses to change it: Tray now contains 25 pieces -> rejected.
        var edit = new[]
        {
            Row(f.Pc.Id, null, null, rows[f.Pc.Id].Id), Row(f.Box.Id, f.Pc.Id, 10m, rows[f.Box.Id].Id), Row(f.Tray.Id, f.Pc.Id, 25m, rows[f.Tray.Id].Id),
        };
        var rejected = await Assert.ThrowsAsync<DomainValidationException>(() => f.Products.UpdateProductAsync(pen2.Id, Input(f, "STN-PEN2", f.Pc.Id, edit)));
        Assert.Contains(rejected.Errors, e => e.Message == V.LockedMessage);
    }

    [Fact]
    public async Task UpdateProductAsync_UnitNotYetUsedByAnyDocument_StillEditable()
    {
        var f = await SeedAsync();
        var (pen2, rows) = await CreateTrayPenAsync(f);
        await BuyAsync(f, Line(rows[f.Box.Id], 4m, 100m));

        await f.Products.UpdateProductAsync(pen2.Id, Input(f, "STN-PEN2", f.Pc.Id,
            Row(f.Pc.Id, null, null, rows[f.Pc.Id].Id), Row(f.Box.Id, f.Pc.Id, 10m, rows[f.Box.Id].Id), Row(f.Tray.Id, f.Pc.Id, 25m, rows[f.Tray.Id].Id)));

        Assert.Equal(25m, (await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == rows[f.Tray.Id].Id)).ConversionFactorToBase);
    }

    private static async Task DeactivateTrayAsync(Fixture f, Product product, Dictionary<Guid, ProductUnit> rows) =>
        await f.Products.UpdateProductAsync(product.Id, Input(f, "STN-PEN2", f.Pc.Id,
            Row(f.Pc.Id, null, null, rows[f.Pc.Id].Id), Row(f.Box.Id, f.Pc.Id, 10m, rows[f.Box.Id].Id),
            Row(f.Tray.Id, f.Pc.Id, 20m, rows[f.Tray.Id].Id, active: false)));
}
