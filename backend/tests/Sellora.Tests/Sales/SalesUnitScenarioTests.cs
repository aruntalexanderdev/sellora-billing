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

namespace Sellora.Tests.Sales;

/// <summary>
/// Phase 5 (analysis §12 scenarios S1-S7): sales and sales returns in product units against the REAL
/// <see cref="SalesService"/> (not the list-pricing decorator the other fixtures use), on EF InMemory. The Pen is Piece 1,
/// Box 10, Half Carton 50, Full Carton 100, listed at 12 / 100 / 480 / 900 so every unit has its own price. Also the
/// price rules (D2/D3 + the discount decision), decimals, returns in any unit, cancellation and the unit lock. Row locks,
/// constraints and concurrency run against PostgreSQL in Sellora.IntegrationTests.
/// </summary>
public class SalesUnitScenarioTests
{
    private static readonly DateOnly D = new(2026, 9, 10);

    private sealed record Fixture(
        SelloraDbContext Db, SalesService Sales, PurchaseService Purchases, ProductService Products, PricingService Pricing,
        Customer Customer, Tax Tax, Guid UserId,
        Unit Pc, Unit Box, Unit HCtn, Unit FCtn, Unit Tray, Unit Kg, Unit Kg2, Unit Pack,
        Product Pen, Dictionary<Guid, ProductUnit> PenRows);

    private static async Task<Fixture> SeedAsync(decimal taxRate = 0m)
    {
        var db = TestDb.NewInMemory();
        var user = TestUsers.Admin();
        var tax = new Tax { Id = Guid.NewGuid(), Name = $"GST {taxRate}%", RatePercent = taxRate };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var (pc, box, hctn, fctn, tray, kg) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton(),
            TestUnits.New("Tray", "tray", MeasurementCategory.Packaging, 0, null), TestUnits.Kilogram());
        var kg2 = TestUnits.New("Kilogram (2 dp)", "kg2", MeasurementCategory.Weight, 2, 1m);
        var pack = TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 3, null);
        db.AddRange(user.Role, user, tax, inr, settings, supplier, customer, pc, box, hctn, fctn, tray, kg, kg2, pack);
        await db.SaveChangesAsync();

        var products = new ProductService(db);
        var pricing = new PricingService(db);
        var f0 = new Fixture(db, new SalesService(db), new PurchaseService(db, new ExchangeRateService(db)), products, pricing,
            customer, tax, user.Id, pc, box, hctn, fctn, tray, kg, kg2, pack, null!, null!);
        var pen = await products.CreateProductAsync(Input(f0, "STN-PEN", pc.Id,
            Row(pc.Id, null, null), Row(box.Id, pc.Id, 10m), Row(hctn.Id, box.Id, 5m), Row(fctn.Id, hctn.Id, 2m)));
        var rows = await RowsAsync(f0, pen.Id);
        var f = f0 with { Pen = pen, PenRows = rows };
        await PriceAsync(f, rows[pc.Id], 12m);
        await PriceAsync(f, rows[box.Id], 100m);
        await PriceAsync(f, rows[hctn.Id], 480m);
        await PriceAsync(f, rows[fctn.Id], 900m);
        return f;
    }

    private static Task PriceAsync(Fixture f, ProductUnit unit, decimal price) => f.Pricing.SetPriceAsync(unit.Id, price, f.UserId);

    private static ProductUnitInputItem Row(Guid unitId, Guid? innerUnitId, decimal? qty, Guid? id = null, bool active = true, bool sellable = true, string? barcode = null) =>
        new(id, unitId, innerUnitId, qty, null, barcode, true, sellable, false, false, active);

    private static UpsertProductInput Input(Fixture f, string sku, Guid baseUnitId, params ProductUnitInputItem[] units) =>
        new(sku, "Ball Pen", null, null, baseUnitId, f.Tax.Id, 0m, units);

    private static async Task<Dictionary<Guid, ProductUnit>> RowsAsync(Fixture f, Guid productId) =>
        (await f.Db.ProductUnits.AsNoTracking().Where(u => u.ProductId == productId).ToListAsync()).ToDictionary(u => u.UnitId);

    /// <summary>A second pen sharing Piece/Box: Box 10 and a leaf Tray of 20, all priced (Piece 12, Box 100, Tray 220).</summary>
    private static async Task<(Product Product, Dictionary<Guid, ProductUnit> Rows)> CreateTrayPenAsync(Fixture f)
    {
        var product = await f.Products.CreateProductAsync(Input(f, "STN-PEN2", f.Pc.Id,
            Row(f.Pc.Id, null, null), Row(f.Box.Id, f.Pc.Id, 10m), Row(f.Tray.Id, f.Pc.Id, 20m)));
        var rows = await RowsAsync(f, product.Id);
        await PriceAsync(f, rows[f.Pc.Id], 12m);
        await PriceAsync(f, rows[f.Box.Id], 100m);
        await PriceAsync(f, rows[f.Tray.Id], 220m);
        return (product, rows);
    }

    private static Task<PurchaseOrder> BuyAsync(Fixture f, ProductUnit unit, decimal qty, decimal rate) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Db.Suppliers.Single().Id, D, null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(unit.ProductId, unit.Id, qty, rate, 0, 0)]), f.UserId);

    private static SaleLineInput SLine(ProductUnit unit, decimal qty, decimal price, decimal discount = 0m, bool overridePrice = false) =>
        new(unit.ProductId, unit.Id, qty, price, discount, overridePrice);

    private static Task<SalesInvoice> SellAsync(Fixture f, bool canOverride, params SaleLineInput[] lines) =>
        f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, 0, lines, CanOverridePrice: canOverride), f.UserId);

    private static Task<SalesInvoice> SellAsync(Fixture f, params SaleLineInput[] lines) => SellAsync(f, false, lines);

    private static Task<SalesReturn> ReturnAsync(Fixture f, SalesInvoice sale, SalesInvoiceLine line, ProductUnit? unit, decimal qty) =>
        f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, "changed mind", [new SaleReturnLineInput(line.Id, unit?.Id, qty)]), f.UserId);

    private static async Task<DomainValidationException> RejectedAsync(Func<Task> act, string field, string? message = null)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        Assert.Equal(field, ex.Field);
        if (message is not null) Assert.Equal(message, ex.Message);
        return ex;
    }

    /// <summary>Σ movements = QuantityOnHand = Σ batches per product; Qty x factor = BaseQty on sales and return lines; no empty
    /// UnitCode and the snapshot matches the unit master; consumption returned = Σ return base qty per line; no positive 'Sale'.</summary>
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
        Assert.DoesNotContain(await f.Db.StockMovements.ToListAsync(), m => m.Type == StockMovementType.Sale && m.QuantityBaseUnits > 0);
        var returns = await f.Db.SalesReturnLines.AsNoTracking().Include(x => x.Unit).ToListAsync();
        foreach (var l in await f.Db.SalesInvoiceLines.AsNoTracking().Include(x => x.Unit).ToListAsync())
        {
            Assert.Equal(l.BaseQty, l.Qty * l.ConversionFactorToBase);
            Assert.False(string.IsNullOrWhiteSpace(l.UnitCode), $"Sales line {l.Id} has an empty UnitCode.");
            Assert.Equal(l.Unit.ShortCode, l.UnitCode);
            var returnedBase = returns.Where(r => r.SalesInvoiceLineId == l.Id).Sum(r => r.BaseQty);
            Assert.True(returnedBase <= l.BaseQty);
            Assert.Equal(returnedBase, await f.Db.SalesInvoiceLineBatchConsumptions.Where(c => c.SalesInvoiceLineId == l.Id).SumAsync(c => c.ReturnedQtyBaseUnits));
        }
        foreach (var r in returns)
        {
            Assert.Equal(r.BaseQty, r.Qty * r.ConversionFactorToBase);
            Assert.False(string.IsNullOrWhiteSpace(r.UnitCode), $"Return line {r.Id} has an empty UnitCode.");
            Assert.Equal(r.Unit.ShortCode, r.UnitCode);
        }
    }

    // ------------------------------------------------------------------ S1-S5

    [Fact]
    public async Task CreateSaleAsync_S1BuyOneFullCartonSellTwoBoxes_BaseQtyTwentyFromTheCartonBatchEightyLeft()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 2m, 100m));

        var line = sale.Lines.Single();
        Assert.Equal((2m, 10m, 20m), (line.Qty, line.ConversionFactorToBase, line.BaseQty));
        Assert.Equal((f.PenRows[f.Box.Id].Id, f.Box.Id, "box"), (line.ProductUnitId, line.UnitId, line.UnitCode));
        Assert.Equal((100m, false), (line.ListUnitPrice, line.PriceOverridden));
        var batch = await f.Db.PurchaseBatches.SingleAsync();
        Assert.Equal((100m, 80m), (batch.PurchasedBaseQty, batch.RemainingBaseQty));
        var consumption = await f.Db.SalesInvoiceLineBatchConsumptions.SingleAsync();
        Assert.Equal((batch.Id, 20m), (consumption.PurchaseBatchId, consumption.QtyBaseUnits));
        Assert.Equal(80m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(-20m, (await f.Db.StockMovements.SingleAsync(m => m.Type == StockMovementType.Sale)).QuantityBaseUnits);
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreateSaleAsync_S2FivePiecesOkTwoAndAHalfPiecesRejectedAsWholeNumbers()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        var sale = await SellAsync(f, SLine(f.PenRows[f.Pc.Id], 5m, 12m));
        await RejectedAsync(() => SellAsync(f, SLine(f.PenRows[f.Pc.Id], 2.5m, 12m)), "Lines", "Piece is counted in whole numbers.");

        Assert.Equal((5m, 1m, 5m, "pc"), (sale.Lines.Single().Qty, sale.Lines.Single().ConversionFactorToBase, sale.Lines.Single().BaseQty, sale.Lines.Single().UnitCode));
        Assert.Single(await f.Db.SalesInvoices.ToListAsync());
        Assert.Equal(95m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreateSaleAsync_S3OneBox_BaseQtyTen()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 100m));

        Assert.Equal((10m, 100m), (sale.Lines.Single().BaseQty, sale.GrandTotal));
        Assert.Equal(90m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateSaleAsync_S4BasePieceIsSellableByDefaultAndRejectedWhenDisabled()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        await SellAsync(f, SLine(f.PenRows[f.Pc.Id], 1m, 12m)); // default row is sellable

        var pen2 = await f.Products.CreateProductAsync(Input(f, "STN-PEN2", f.Pc.Id, Row(f.Pc.Id, null, null, sellable: false), Row(f.Box.Id, f.Pc.Id, 10m)));
        var rows = await RowsAsync(f, pen2.Id);
        await PriceAsync(f, rows[f.Pc.Id], 12m);
        await BuyAsync(f, rows[f.Box.Id], 2m, 100m);

        await RejectedAsync(() => SellAsync(f, SLine(rows[f.Pc.Id], 1m, 12m)), "Lines", "Piece cannot be sold for Ball Pen.");
    }

    [Fact]
    public async Task CreateSaleAsync_S5BoxAndPieceLinesOfOneProduct_AggregateAndOversellAcrossTheTwoIsRejectedWithoutTrailingZeros()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m); // 100 pc

        await RejectedAsync(() => SellAsync(f, SLine(f.PenRows[f.Box.Id], 9m, 100m), SLine(f.PenRows[f.Pc.Id], 11m, 12m)), "Lines",
            "Only 100 pc of Ball Pen in stock — cannot sell 101 pc.");
        Assert.Empty(await f.Db.SalesInvoices.ToListAsync());
        Assert.Equal(100m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);

        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 9m, 100m), SLine(f.PenRows[f.Pc.Id], 10m, 12m));

        Assert.Equal(2, sale.Lines.Count);
        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertConsistentAsync(f);
    }

    // ------------------------------------------------------------------ S6: returns in any unit

    [Fact]
    public async Task CreateReturnAsync_S6BoxThenPiecesThenTheExactRemainder_ProratedByBaseAndCreditsSumToTheLineTotal()
    {
        var f = await SeedAsync(taxRate: 5m);
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 3m, 100m)); // 30 pc, taxable 300, tax 15
        var line = sale.Lines.Single();

        var r1 = (await ReturnAsync(f, sale, line, f.PenRows[f.Box.Id], 1m)).Lines.Single();
        Assert.Equal((1m, 10m, 10m, "box"), (r1.Qty, r1.ConversionFactorToBase, r1.BaseQty, r1.UnitCode));
        Assert.Equal((100m, 5m), (r1.TaxableValue, r1.TaxAmount));

        var r2 = (await ReturnAsync(f, sale, line, f.PenRows[f.Pc.Id], 3m)).Lines.Single(); // other unit than the line
        Assert.Equal((3m, 1m, 3m, "pc", f.PenRows[f.Pc.Id].Id), (r2.Qty, r2.ConversionFactorToBase, r2.BaseQty, r2.UnitCode, r2.ProductUnitId));
        Assert.Equal((30m, 1.5m), (r2.TaxableValue, r2.TaxAmount));

        var r3 = (await ReturnAsync(f, sale, line, f.PenRows[f.Pc.Id], 17m)).Lines.Single(); // exactly what is left
        Assert.Equal((170m, 8.5m), (r3.TaxableValue, r3.TaxAmount));

        Assert.Equal(line.LineTotal, r1.LineTotal + r2.LineTotal + r3.LineTotal);
        Assert.Equal(line.LineTotal, (await f.Db.SalesInvoices.SingleAsync()).ReturnedAmount);
        Assert.Equal(100m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(30m, (await f.Db.StockMovements.Where(m => m.Type == StockMovementType.SaleReturn).Select(m => m.QuantityBaseUnits).ToListAsync()).Sum());
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreateReturnAsync_AwkwardPriceReturnedInThreeMixedPartials_LastTakesTheExactRemainderWithTax()
    {
        var f = await SeedAsync(taxRate: 5m);
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        await f.Pricing.SetPriceAsync(f.PenRows[f.Box.Id].Id, 100.01m, f.UserId);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 100.01m));
        var line = sale.Lines.Single();

        var a = (await ReturnAsync(f, sale, line, f.PenRows[f.Pc.Id], 3m)).Lines.Single();
        var b = (await ReturnAsync(f, sale, line, f.PenRows[f.Pc.Id], 3m)).Lines.Single();
        var c = (await ReturnAsync(f, sale, line, f.PenRows[f.Pc.Id], 4m)).Lines.Single();

        Assert.Equal(Math.Round(line.TaxableValue * 3 / 10, 2, MidpointRounding.AwayFromZero), a.TaxableValue);
        Assert.Equal(a.TaxableValue, b.TaxableValue);
        Assert.Equal(line.TaxableValue, a.TaxableValue + b.TaxableValue + c.TaxableValue);
        Assert.Equal(line.TaxAmount, a.TaxAmount + b.TaxAmount + c.TaxAmount);
        Assert.Equal(line.LineTotal, a.LineTotal + b.LineTotal + c.LineTotal);
    }

    [Fact]
    public async Task CreateReturnAsync_SameUnitReturnWithoutAUnit_UsesTheLineUnit()
    {
        var f = await SeedAsync(taxRate: 5m);
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 3m, 100m));

        var returned = (await ReturnAsync(f, sale, sale.Lines.Single(), null, 1m)).Lines.Single();

        Assert.Equal((sale.Lines.Single().ProductUnitId, "box", 10m), (returned.ProductUnitId, returned.UnitCode, returned.BaseQty));
    }

    [Fact]
    public async Task CreateReturnAsync_OverReturnInMixedUnits_RejectedAgainstTheBaseRemainder()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 3m, 100m));
        var line = sale.Lines.Single();
        await ReturnAsync(f, sale, line, f.PenRows[f.Pc.Id], 13m);

        await RejectedAsync(() => ReturnAsync(f, sale, line, f.PenRows[f.Box.Id], 2m), "Lines", "Only 17 pc of Ball Pen can still be returned.");

        Assert.Single(await f.Db.SalesReturns.ToListAsync());
        Assert.Equal(83m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await ReturnAsync(f, sale, line, f.PenRows[f.Box.Id], 1m); // 10 of the 17 left: fine
    }

    [Fact]
    public async Task CreateReturnAsync_UnitOfAnotherProductOnTheSameSale_RejectedNamingTheProduct()
    {
        var f = await SeedAsync();
        var (_, other) = await CreateTrayPenAsync(f);
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        await BuyAsync(f, other[f.Box.Id], 2m, 100m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 100m), SLine(other[f.Box.Id], 1m, 100m));
        var penLine = sale.Lines.Single(l => l.ProductId == f.Pen.Id);

        await RejectedAsync(() => ReturnAsync(f, sale, penLine, other[f.Box.Id], 1m), "Lines", "The selected unit does not belong to Ball Pen.");

        Assert.Empty(await f.Db.SalesReturns.ToListAsync());
    }

    [Fact]
    public async Task CreateReturnAsync_InactiveUnitThatIsNotTheLineUnit_RejectedButTheLinesOwnDeactivatedUnitIsAccepted()
    {
        var f = await SeedAsync();
        var (pen2, rows) = await CreateTrayPenAsync(f);
        await BuyAsync(f, rows[f.Box.Id], 4m, 100m); // 40 pc
        var boxSale = await SellAsync(f, SLine(rows[f.Box.Id], 1m, 100m));
        var traySale = await SellAsync(f, SLine(rows[f.Tray.Id], 1m, 220m));
        await f.Products.UpdateProductAsync(pen2.Id, Input(f, "STN-PEN2", f.Pc.Id,
            Row(f.Pc.Id, null, null, rows[f.Pc.Id].Id), Row(f.Box.Id, f.Pc.Id, 10m, rows[f.Box.Id].Id), Row(f.Tray.Id, f.Pc.Id, 20m, rows[f.Tray.Id].Id, active: false)));

        await RejectedAsync(() => ReturnAsync(f, boxSale, boxSale.Lines.Single(), rows[f.Tray.Id], 1m), "Lines", "Tray is no longer active.");
        var returned = (await ReturnAsync(f, traySale, traySale.Lines.Single(), null, 1m)).Lines.Single();

        Assert.Equal((rows[f.Tray.Id].Id, "tray", 20m), (returned.ProductUnitId, returned.UnitCode, returned.BaseQty));
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreateReturnAsync_OneAndAHalfBoxes_RejectedAsWholeNumbersNotRounded()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 3m, 100m));

        await RejectedAsync(() => ReturnAsync(f, sale, sale.Lines.Single(), f.PenRows[f.Box.Id], 1.5m), "Lines", "Box is counted in whole numbers.");

        Assert.Empty(await f.Db.SalesReturns.ToListAsync());
    }

    [Fact]
    public async Task CreateReturnAsync_FourDecimalKilograms_RejectedNotRoundedToThree()
    {
        var f = await SeedAsync();
        var rice = await f.Products.CreateProductAsync(Input(f, "RICE", f.Kg.Id, Row(f.Kg.Id, null, null)));
        var kg = (await RowsAsync(f, rice.Id))[f.Kg.Id];
        await PriceAsync(f, kg, 50m);
        await BuyAsync(f, kg, 10m, 40m);
        var sale = await SellAsync(f, SLine(kg, 5m, 50m));

        await RejectedAsync(() => ReturnAsync(f, sale, sale.Lines.Single(), null, 0.0004m), "Lines", "Kilogram allows at most 3 decimal place(s).");
    }

    // ------------------------------------------------------------------ price rules (D2, D3, discount decision)

    private static async Task AssertNothingSoldAsync(Fixture f, decimal stockOnHand)
    {
        Assert.Empty(await f.Db.SalesInvoices.ToListAsync());
        Assert.Empty(await f.Db.SalesInvoiceLines.ToListAsync());
        Assert.Equal(stockOnHand, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateSaleAsync_ChangedPriceWithoutOverrideFlag_RejectedWithTheCurrentPriceAndNothingPersisted()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        await RejectedAsync(() => SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 90m)), "Lines", "The price of Ball Pen (Box) is now 100.00. Refresh the item and try again.");
        // even a user who may override gets the refresh message unless the override is explicit (a stale cart is not an override)
        await RejectedAsync(() => SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 90m)), "Lines", "The price of Ball Pen (Box) is now 100.00. Refresh the item and try again.");

        await AssertNothingSoldAsync(f, 100m);
    }

    [Fact]
    public async Task CreateSaleAsync_OverridePriceFlagWithoutPermission_Rejected()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        await RejectedAsync(() => SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 90m, overridePrice: true)), "Lines", "You are not allowed to change the price of Ball Pen.");
        await RejectedAsync(() => SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 100m, overridePrice: true)), "Lines", "You are not allowed to change the price of Ball Pen.");

        await AssertNothingSoldAsync(f, 100m);
    }

    [Fact]
    public async Task CreateSaleAsync_DiscountWithoutPermission_RejectedEvenAtTheListPrice()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        await RejectedAsync(() => SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 100m, discount: 0.01m)), "Lines", "You are not allowed to give a discount on Ball Pen.");

        await AssertNothingSoldAsync(f, 100m);
    }

    [Fact]
    public async Task CreateSaleAsync_OverriddenPriceWithPermission_SavedAndFlaggedWithTheListPriceSnapshot()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        var sale = await SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 2m, 90m, overridePrice: true));

        var line = sale.Lines.Single();
        Assert.Equal((90m, 100m, true, 180m), (line.UnitPrice, line.ListUnitPrice, line.PriceOverridden, line.LineTotal));
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.SalesInvoiceLines.AsNoTracking().SingleAsync();
        Assert.Equal((90m, 100m, true), (saved.UnitPrice, saved.ListUnitPrice, saved.PriceOverridden));
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CreateSaleAsync_DiscountAtTheListPriceWithPermission_SavedNotFlaggedAsOverriddenPrice()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        var sale = await SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 2m, 100m, discount: 10m));

        var line = sale.Lines.Single();
        Assert.Equal((100m, 100m, false, 10m, 190m), (line.UnitPrice, line.ListUnitPrice, line.PriceOverridden, line.Discount, line.TaxableValue));
    }

    [Fact]
    public async Task CreateSaleAsync_ExplicitOverrideAtTheListPriceWithPermission_AcceptedAndNotFlagged()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        var sale = await SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 100m, overridePrice: true));

        Assert.False(sale.Lines.Single().PriceOverridden); // flagged only when the charged price really differs
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateSaleAsync_UnpricedUnit_RefusedWithAndWithoutTheOverridePermission(bool canOverride)
    {
        var f = await SeedAsync();
        var (_, rows) = await CreateTrayPenAsync(f);
        await f.Products.UpdateProductAsync(rows[f.Tray.Id].ProductId, Input(f, "STN-PEN2", f.Pc.Id,
            Row(f.Pc.Id, null, null, rows[f.Pc.Id].Id), Row(f.Box.Id, f.Pc.Id, 10m, rows[f.Box.Id].Id), Row(f.Tray.Id, f.Pc.Id, 20m, rows[f.Tray.Id].Id)));
        var unpriced = new ProductUnit { Id = Guid.NewGuid(), ProductId = rows[f.Pc.Id].ProductId, UnitId = f.HCtn.Id, InnerProductUnitId = rows[f.Box.Id].Id, InnerQuantity = 5m, ConversionFactorToBase = 50m, IsSellable = true, IsPurchasable = true, SortOrder = 9 };
        f.Db.ProductUnits.Add(unpriced);
        await f.Db.SaveChangesAsync();
        await BuyAsync(f, rows[f.Box.Id], 10m, 100m);

        await RejectedAsync(() => SellAsync(f, canOverride, SLine(unpriced, 1m, 480m, overridePrice: canOverride)), "Lines",
            "Half Carton of Ball Pen has no price. Set a price before selling it.");

        Assert.Empty(await f.Db.SalesInvoices.ToListAsync());
    }

    [Fact]
    public async Task CreateSaleAsync_UnitWhoseOnlyPriceRowIsClosed_CountsAsUnpriced()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        var open = await f.Db.SellingPrices.SingleAsync(p => p.ProductUnitId == f.PenRows[f.Box.Id].Id && p.EffectiveTo == null);
        open.EffectiveTo = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();

        await RejectedAsync(() => SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 100m)), "Lines", "Box of Ball Pen has no price. Set a price before selling it.");
    }

    [Fact]
    public async Task CreateSaleAsync_PriceChangedAfterTheCatalogWasLoaded_RejectedWithTheNewPriceThenAcceptedAtIt()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        await PriceAsync(f, f.PenRows[f.Box.Id], 110m); // a manager repriced while the cashier's cart held 100

        await RejectedAsync(() => SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 100m)), "Lines", "The price of Ball Pen (Box) is now 110.00. Refresh the item and try again.");
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 110m));

        Assert.Equal((110m, false), (sale.Lines.Single().ListUnitPrice, sale.Lines.Single().PriceOverridden));
        // the old sale keeps its price when the list moves on
        await PriceAsync(f, f.PenRows[f.Box.Id], 120m);
        f.Db.ChangeTracker.Clear();
        var kept = await f.Db.SalesInvoiceLines.AsNoTracking().SingleAsync();
        Assert.Equal((110m, 110m), (kept.UnitPrice, kept.ListUnitPrice));
    }

    [Fact]
    public async Task CreateSaleAsync_UnitPriceWithFiveDecimals_RejectedNotTruncated()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        await RejectedAsync(() => SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 99.99999m, overridePrice: true)), "Lines", "Unit price has too many decimals (at most 4).");
    }

    [Fact]
    public async Task CreateSaleAsync_DiscountAboveTheLineAmount_RejectedEvenWithPermissionAndEqualIsAllowed()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        await RejectedAsync(() => SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 100m, discount: 100.01m)), "Lines", "Discount on Ball Pen cannot exceed the line amount of 100.00.");
        await RejectedAsync(() => SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 100m, discount: -1m)), "Lines", "Discount cannot be negative.");
        // FI-D24: a discount is a money amount - at most the base currency's decimals (2 here), no longer 4.
        await RejectedAsync(() => SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 100m, discount: 12.34567m)), "Lines", "Discount can have at most 2 decimal places.");
        await RejectedAsync(() => SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 100m, discount: 1.005m)), "Lines", "Discount can have at most 2 decimal places.");
        Assert.Empty(await f.Db.SalesInvoices.ToListAsync());

        var free = await SellAsync(f, canOverride: true, SLine(f.PenRows[f.Box.Id], 1m, 100m, discount: 100m));
        Assert.Equal(0m, free.Lines.Single().LineTotal);
    }

    // ------------------------------------------------------------------ decimals

    [Fact]
    public async Task CreateSaleAsync_FourDecimalKilograms_RejectedNotRounded()
    {
        var f = await SeedAsync();
        var rice = await f.Products.CreateProductAsync(Input(f, "RICE", f.Kg.Id, Row(f.Kg.Id, null, null)));
        var kg = (await RowsAsync(f, rice.Id))[f.Kg.Id];
        await PriceAsync(f, kg, 50m);
        await BuyAsync(f, kg, 10m, 40m);

        await RejectedAsync(() => SellAsync(f, SLine(kg, 0.0001m, 50m)), "Lines", "Kilogram allows at most 3 decimal place(s).");
        var sale = await SellAsync(f, SLine(kg, 1.25m, 50m));

        Assert.Equal((1.25m, 1.25m), (sale.Lines.Single().Qty, sale.Lines.Single().BaseQty));
    }

    [Fact]
    public async Task CreateSaleAsync_PackOfHalfAKiloOnATwoDecimalKgBase_RejectedWithTheBaseDecimalsMessageUntilItFits()
    {
        var f = await SeedAsync();
        var rice = await f.Products.CreateProductAsync(Input(f, "RICE2", f.Kg2.Id, Row(f.Kg2.Id, null, null), Row(f.Pack.Id, f.Kg2.Id, 0.5m)));
        var rows = await RowsAsync(f, rice.Id);
        await PriceAsync(f, rows[f.Pack.Id], 30m);
        await BuyAsync(f, rows[f.Pack.Id], 10m, 20m);

        var ex = await RejectedAsync(() => SellAsync(f, SLine(rows[f.Pack.Id], 0.01m, 30m)), "Lines");

        Assert.Equal("0.01 pk = 0.005 kg2; Kilogram (2 dp) is tracked to 2 decimal place(s).", ex.Message);
        await SellAsync(f, SLine(rows[f.Pack.Id], 0.02m, 30m));
        Assert.Equal(4.99m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public async Task CancelSaleAsync_AfterABoxSale_WritesSaleCancellationRestoresStockAndLedgerReconciles()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 2m, 1000m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 3m, 100m), SLine(f.PenRows[f.Pc.Id], 4m, 12m));

        await f.Sales.CancelSaleAsync(sale.Id, f.UserId);

        f.Db.ChangeTracker.Clear();
        var movements = await f.Db.StockMovements.AsNoTracking().Where(m => m.ReferenceId == sale.Id).ToListAsync();
        // Phase 6a-D3: one SaleCancellation per sales line (30 + 4 base pieces), same product total as before.
        var cancellations = movements.Where(m => m.Type == StockMovementType.SaleCancellation).ToList();
        Assert.Equal(2, cancellations.Count);
        Assert.Equal(34m, cancellations.Sum(m => m.QuantityBaseUnits));
        Assert.Equal(-34m, movements.Where(m => m.Type == StockMovementType.Sale).Sum(m => m.QuantityBaseUnits)); // one Sale movement per line
        Assert.Equal(200m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(200m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
        await AssertConsistentAsync(f);
    }

    [Fact]
    public async Task CancelSaleAsync_AfterAPartialReturn_Refused()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 3m, 100m));
        await ReturnAsync(f, sale, sale.Lines.Single(), f.PenRows[f.Pc.Id], 1m);

        await RejectedAsync(() => f.Sales.CancelSaleAsync(sale.Id, f.UserId), "Status", "Cannot cancel — items from this sale have been returned.");
    }

    // ------------------------------------------------------------------ S7 prices per unit; unit lock

    [Fact]
    public async Task CreateSaleAsync_S7BoxAndPieceAtDifferentListPrices_EachLineSnapshotsItsOwnListPrice()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 1m, 1000m);

        var sale = await SellAsync(f, SLine(f.PenRows[f.Box.Id], 1m, 100m), SLine(f.PenRows[f.Pc.Id], 1m, 12m));

        var byCode = sale.Lines.ToDictionary(l => l.UnitCode);
        Assert.Equal((100m, 100m), (byCode["box"].UnitPrice, byCode["box"].ListUnitPrice));
        Assert.Equal((12m, 12m), (byCode["pc"].UnitPrice, byCode["pc"].ListUnitPrice));
        Assert.NotEqual(10 * byCode["pc"].ListUnitPrice, byCode["box"].ListUnitPrice); // a Box is not priced as 10 x Piece
        Assert.Equal(112m, sale.GrandTotal);
    }

    [Fact]
    public async Task GetLockReasonsAsync_UnitReferencedOnlyBySalesReturnLine_IsLockedAsTransactions()
    {
        var f = await SeedAsync();
        var (pen2, rows) = await CreateTrayPenAsync(f);
        await BuyAsync(f, rows[f.Box.Id], 4m, 100m);
        var sale = await SellAsync(f, SLine(rows[f.Box.Id], 2m, 100m));
        var before = await ProductUnitUsage.GetLockReasonsAsync(f.Db, rows.Values.ToList(), default);
        Assert.Equal(ProductUnitUsage.Prices, before[rows[f.Tray.Id].Id]); // priced, but on no document line: not yet a transaction lock

        await ReturnAsync(f, sale, sale.Lines.Single(), rows[f.Tray.Id], 1m);

        var after = await ProductUnitUsage.GetLockReasonsAsync(f.Db, rows.Values.ToList(), default);
        Assert.Equal(ProductUnitUsage.Transactions, after[rows[f.Tray.Id].Id]);
        var edit = new[]
        {
            Row(f.Pc.Id, null, null, rows[f.Pc.Id].Id), Row(f.Box.Id, f.Pc.Id, 10m, rows[f.Box.Id].Id), Row(f.Tray.Id, f.Pc.Id, 25m, rows[f.Tray.Id].Id),
        };
        var rejected = await Assert.ThrowsAsync<DomainValidationException>(() => f.Products.UpdateProductAsync(pen2.Id, Input(f, "STN-PEN2", f.Pc.Id, edit)));
        Assert.Contains(rejected.Errors, e => e.Message == V.LockedMessage);
    }

    [Fact]
    public async Task SaleAndReturn_AcrossUnits_NeverLeaveAnEmptyUnitCodeAndSnapshotsMatchTheMaster()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.PenRows[f.FCtn.Id], 2m, 1000m);
        var sale = await SellAsync(f, SLine(f.PenRows[f.FCtn.Id], 1m, 900m), SLine(f.PenRows[f.Box.Id], 2m, 100m), SLine(f.PenRows[f.Pc.Id], 7m, 12m));
        await ReturnAsync(f, sale, sale.Lines.Single(l => l.UnitCode == "fctn"), f.PenRows[f.Box.Id], 3m);
        await ReturnAsync(f, sale, sale.Lines.Single(l => l.UnitCode == "pc"), null, 7m);

        await AssertConsistentAsync(f);
        Assert.Equal(0, await f.Db.SalesInvoiceLines.CountAsync(l => l.UnitCode == ""));
        Assert.Equal(0, await f.Db.SalesReturnLines.CountAsync(l => l.UnitCode == ""));
    }
}
