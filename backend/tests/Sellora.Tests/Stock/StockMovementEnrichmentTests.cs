using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 6a (design §7): every ledger writer — purchase, purchase return, purchase cancellation, sale, sale return, sale
/// cancellation (one row per line, 6a-D3) and stock adjustment incl. OpeningStock — writes the entered-unit snapshot on
/// its StockMovement rows (unit id, code, factor on every row; EnteredQuantity only when one row carries the whole line,
/// 6a-D2), the new movement types / reason codes, and the ledger still reconciles. Real services on EF InMemory with the
/// Pen (Piece base, Box of 10).
/// </summary>
public class StockMovementEnrichmentTests
{
    private static readonly DateOnly D = new(2026, 9, 10);

    private sealed record Fixture(
        SelloraDbContext Db, SalesService Sales, PurchaseService Purchases, StockAdjustmentService Adjustments, Guid UserId,
        Customer Customer, Supplier Supplier, Product Pen, ProductUnit Pc, ProductUnit Box);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var user = TestUsers.Admin();
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 0%", RatePercent = 0m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var (pcUnit, boxUnit) = (TestUnits.Piece(), TestUnits.Box());
        db.AddRange(user.Role, user, tax, inr, settings, supplier, customer, pcUnit, boxUnit);
        await db.SaveChangesAsync();
        await TaxFixtures.UseAsCompanyDefaultAsync(db, tax);

        var products = new ProductService(db);
        var pen = await products.CreateProductAsync(new UpsertProductInput("STN-PEN", "Ball Pen", null, null, pcUnit.Id, 0m,
        [
            new ProductUnitInputItem(null, pcUnit.Id, null, null, null, null, true, true, false, false, true),
            new ProductUnitInputItem(null, boxUnit.Id, pcUnit.Id, 10m, null, null, true, true, false, false, true),
        ]));
        var rows = await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == pen.Id).ToListAsync();
        var (pc, box) = (rows.Single(r => r.IsBaseUnit), rows.Single(r => !r.IsBaseUnit));
        var pricing = new PricingService(db);
        await pricing.SetPriceAsync(pc.Id, 12m, user.Id);
        await pricing.SetPriceAsync(box.Id, 100m, user.Id);
        await TaxFixtures.EnablePurchasesAsync(db); // TP-3: purchases need a country, an own state and a purchase profile per product
        return new Fixture(db, new SalesService(db), new PurchaseService(db, new ExchangeRateService(db)), new StockAdjustmentService(db),
            user.Id, customer, supplier, pen, pc, box);
    }

    private static Task<PurchaseOrder> BuyAsync(Fixture f, ProductUnit unit, decimal qty, decimal rate) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, D, null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(f.Pen.Id, unit.Id, qty, rate, 0, 0)]), f.UserId);

    private static Task<SalesInvoice> SellAsync(Fixture f, params (ProductUnit Unit, decimal Qty, decimal Price)[] lines) =>
        f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, 0,
            lines.Select(l => new SaleLineInput(f.Pen.Id, l.Unit.Id, l.Qty, l.Price, 0m, false)).ToList()), f.UserId);

    private static Task<StockAdjustment> AdjustAsync(Fixture f, ProductUnit unit, AdjustmentType type, AdjustmentDirection dir, decimal qty, decimal? cost = null) =>
        f.Adjustments.CreateAdjustmentAsync(new CreateAdjustmentInput(f.Pen.Id, unit.Id, type, dir, qty, cost, "test"), f.UserId);

    private static async Task<List<StockMovement>> MovementsAsync(Fixture f, Guid referenceId)
    {
        f.Db.ChangeTracker.Clear();
        return await f.Db.StockMovements.AsNoTracking().Where(m => m.ReferenceId == referenceId).OrderBy(m => m.OccurredAt).ToListAsync();
    }

    private static void AssertSnapshot(StockMovement m, ProductUnit unit, string code, decimal factor, decimal? entered)
    {
        Assert.Equal((unit.Id, code, factor, entered), (m.EnteredProductUnitId, m.EnteredUnitCode, m.ConversionFactorSnapshot, m.EnteredQuantity));
        if (m.EnteredQuantity is { } e) Assert.Equal(Math.Abs(m.QuantityBaseUnits), e * m.ConversionFactorSnapshot!.Value);
    }

    /// <summary>Σ movements = QuantityOnHand = Σ batches, and every snapshot row satisfies the DB arithmetic CHECK and
    /// type/sign rules (InMemory does not run CHECKs, so they are asserted here).</summary>
    private static async Task AssertLedgerAsync(Fixture f)
    {
        f.Db.ChangeTracker.Clear();
        var stock = await f.Db.StockItems.AsNoTracking().SingleAsync();
        var movements = await f.Db.StockMovements.AsNoTracking().ToListAsync();
        Assert.Equal(stock.QuantityOnHand, movements.Sum(m => m.QuantityBaseUnits));
        Assert.Equal(stock.QuantityOnHand, await f.Db.PurchaseBatches.SumAsync(b => b.RemainingBaseQty));
        foreach (var m in movements)
        {
            Assert.Equal(m.EnteredProductUnitId is null, m.EnteredUnitCode is null);
            Assert.Equal(m.EnteredProductUnitId is null, m.ConversionFactorSnapshot is null);
            Assert.NotNull(m.EnteredProductUnitId); // every NEW row carries the unit context (V9)
            if (m.EnteredQuantity is { } e) Assert.Equal(Math.Abs(m.QuantityBaseUnits), e * m.ConversionFactorSnapshot!.Value);
            switch (m.Type)
            {
                case StockMovementType.Purchase or StockMovementType.SaleReturn or StockMovementType.SaleCancellation or StockMovementType.OpeningStock:
                    Assert.True(m.QuantityBaseUnits > 0, $"{m.Type} must be positive"); break;
                case StockMovementType.Sale or StockMovementType.PurchaseReturn or StockMovementType.PurchaseCancellation:
                    Assert.True(m.QuantityBaseUnits < 0, $"{m.Type} must be negative"); break;
            }
            Assert.Equal(m.Type is StockMovementType.Adjustment or StockMovementType.OpeningStock, m.ReasonCode is not null);
            if (m.Type == StockMovementType.OpeningStock) Assert.Equal(AdjustmentType.OpeningStock, m.ReasonCode);
            if (m.Type == StockMovementType.Adjustment) Assert.NotEqual(AdjustmentType.OpeningStock, m.ReasonCode);
        }
    }

    [Fact]
    public async Task CreatePurchaseAsync_TwoBoxes_MovementCarriesBoxSnapshotAndEnteredQuantity()
    {
        var f = await SeedAsync();

        var purchase = await BuyAsync(f, f.Box, 2m, 100m);

        var m = Assert.Single(await MovementsAsync(f, purchase.Id));
        Assert.Equal((StockMovementType.Purchase, 20m, (AdjustmentType?)null), (m.Type, m.QuantityBaseUnits, m.ReasonCode));
        AssertSnapshot(m, f.Box, "box", 10m, 2m);
        Assert.NotNull(m.PurchaseBatchId);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CreateReturnAsync_PurchaseReturnedInPieces_MovementCarriesTheReturnsOwnUnit()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Box, 2m, 100m);

        var ret = await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, "wrong goods",
            [new PurchaseReturnLineInput(purchase.Lines.Single().Id, f.Pc.Id, 5m)]), f.UserId);

        var m = Assert.Single(await MovementsAsync(f, ret.Id));
        Assert.Equal((StockMovementType.PurchaseReturn, -5m), (m.Type, m.QuantityBaseUnits));
        AssertSnapshot(m, f.Pc, "pc", 1m, 5m); // the return's unit, not the Box of the purchase line
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CancelPurchaseAsync_ThreeBoxes_WritesPurchaseCancellationWithTheLineSnapshot()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Box, 3m, 100m);

        await f.Purchases.CancelPurchaseAsync(purchase.Id, f.UserId);

        var movements = await MovementsAsync(f, purchase.Id);
        Assert.Equal(2, movements.Count);
        Assert.Equal(StockMovementType.Purchase, movements[0].Type);
        var cancel = movements.Single(m => m.Type == StockMovementType.PurchaseCancellation);
        Assert.Equal(-30m, cancel.QuantityBaseUnits);
        AssertSnapshot(cancel, f.Box, "box", 10m, 3m);
        Assert.DoesNotContain(movements, m => m.Type == StockMovementType.Purchase && m.QuantityBaseUnits < 0);
        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CreateSaleAsync_BoxAndPieceLines_OneSaleMovementPerLineEachWithItsOwnSnapshot()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.Box, 10m, 100m);

        var sale = await SellAsync(f, (f.Box, 1m, 100m), (f.Pc, 3m, 12m));

        var movements = await MovementsAsync(f, sale.Id);
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Equal(StockMovementType.Sale, m.Type));
        AssertSnapshot(movements.Single(m => m.QuantityBaseUnits == -10m), f.Box, "box", 10m, 1m);
        AssertSnapshot(movements.Single(m => m.QuantityBaseUnits == -3m), f.Pc, "pc", 1m, 3m);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CreateReturnAsync_SaleReturnedInAnotherUnit_MovementCarriesTheReturnsOwnUnit()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.Box, 10m, 100m);
        var sale = await SellAsync(f, (f.Box, 1m, 100m));

        var ret = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, "changed mind",
            [new SaleReturnLineInput(sale.Lines.Single().Id, f.Pc.Id, 3m)]), f.UserId);

        var m = Assert.Single(await MovementsAsync(f, ret.Id));
        Assert.Equal((StockMovementType.SaleReturn, 3m), (m.Type, m.QuantityBaseUnits));
        AssertSnapshot(m, f.Pc, "pc", 1m, 3m);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CancelSaleAsync_OneBoxAndThreePiecesOfTheSameProduct_TwoSaleCancellationRowsEachWithItsLineSnapshotSummingToThirteen()
    {
        var f = await SeedAsync();
        await BuyAsync(f, f.Box, 10m, 100m);
        var sale = await SellAsync(f, (f.Box, 1m, 100m), (f.Pc, 3m, 12m));

        await f.Sales.CancelSaleAsync(sale.Id, f.UserId);

        var cancellations = (await MovementsAsync(f, sale.Id)).Where(m => m.Type == StockMovementType.SaleCancellation).ToList();
        Assert.Equal(2, cancellations.Count);
        Assert.Equal(13m, cancellations.Sum(m => m.QuantityBaseUnits));
        AssertSnapshot(cancellations.Single(m => m.QuantityBaseUnits == 10m), f.Box, "box", 10m, 1m);
        AssertSnapshot(cancellations.Single(m => m.QuantityBaseUnits == 3m), f.Pc, "pc", 1m, 3m);
        Assert.Equal(100m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_OpeningStockInBoxes_WritesOpeningStockTypeWithOpeningStockReasonAndSnapshot()
    {
        var f = await SeedAsync();

        var adj = await AdjustAsync(f, f.Box, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 4m, 30m);

        var m = Assert.Single(await MovementsAsync(f, adj.Id));
        Assert.Equal((StockMovementType.OpeningStock, 40m, (AdjustmentType?)AdjustmentType.OpeningStock), (m.Type, m.QuantityBaseUnits, m.ReasonCode));
        AssertSnapshot(m, f.Box, "box", 10m, 4m);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_CorrectionIncrease_WritesAdjustmentTypeWithCorrectionReason()
    {
        var f = await SeedAsync();

        var adj = await AdjustAsync(f, f.Pc, AdjustmentType.Correction, AdjustmentDirection.Increase, 7m, 2m);

        var m = Assert.Single(await MovementsAsync(f, adj.Id));
        Assert.Equal((StockMovementType.Adjustment, 7m, (AdjustmentType?)AdjustmentType.Correction), (m.Type, m.QuantityBaseUnits, m.ReasonCode));
        AssertSnapshot(m, f.Pc, "pc", 1m, 7m);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DamageDecreaseFromOneBatch_SingleRowKeepsEnteredQuantity()
    {
        var f = await SeedAsync();
        await AdjustAsync(f, f.Pc, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 100m, 2m);

        var adj = await AdjustAsync(f, f.Box, AdjustmentType.Damage, AdjustmentDirection.Decrease, 2m);

        var m = Assert.Single(await MovementsAsync(f, adj.Id));
        Assert.Equal((StockMovementType.Adjustment, -20m, (AdjustmentType?)AdjustmentType.Damage), (m.Type, m.QuantityBaseUnits, m.ReasonCode));
        AssertSnapshot(m, f.Box, "box", 10m, 2m);
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DecreaseSpanningTwoBatches_UnitContextOnBothRowsButEnteredQuantityNull()
    {
        var f = await SeedAsync();
        await AdjustAsync(f, f.Pc, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 30m, 2m);
        await BuyAsync(f, f.Pc, 30m, 3m);

        var adj = await AdjustAsync(f, f.Box, AdjustmentType.Loss, AdjustmentDirection.Decrease, 4m); // 40 pc: 30 from batch 1, 10 from batch 2

        var rows = await MovementsAsync(f, adj.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal(-40m, rows.Sum(m => m.QuantityBaseUnits));
        Assert.All(rows, m =>
        {
            Assert.Equal((StockMovementType.Adjustment, (AdjustmentType?)AdjustmentType.Loss), (m.Type, m.ReasonCode));
            AssertSnapshot(m, f.Box, "box", 10m, null);
        });
        Assert.Equal(4m, (await f.Db.StockAdjustments.AsNoTracking().SingleAsync(a => a.Id == adj.Id)).EnteredQuantity); // entered total lives on the document
        await AssertLedgerAsync(f);
    }

    [Fact]
    public async Task MixedScenario_AllWriters_LedgerEqualsQohEqualsBatchesAndEveryNewRowHasUnitContext()
    {
        var f = await SeedAsync();
        await AdjustAsync(f, f.Box, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 30m);       // +50
        var purchase = await BuyAsync(f, f.Box, 3m, 100m);                                                        // +30
        var sale = await SellAsync(f, (f.Box, 2m, 100m), (f.Pc, 4m, 12m));                                        // -24
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, "x",
            [new SaleReturnLineInput(sale.Lines.First(l => l.UnitCode == "box").Id, f.Pc.Id, 5m)]), f.UserId);   // +5
        await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, "x",
            [new PurchaseReturnLineInput(purchase.Lines.Single().Id, f.Pc.Id, 6m)]), f.UserId);                   // -6
        await AdjustAsync(f, f.Pc, AdjustmentType.Damage, AdjustmentDirection.Decrease, 45m);                    // -45 across batches
        var sale2 = await SellAsync(f, (f.Pc, 2m, 12m));
        await f.Sales.CancelSaleAsync(sale2.Id, f.UserId);                                                        // -2 / +2

        await AssertLedgerAsync(f);
        Assert.Equal(50m + 30m - 24m + 5m - 6m - 45m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task GetLockReasonsAsync_UnitReferencedOnlyByAMovement_IsLockedAsTransactions()
    {
        var f = await SeedAsync();
        f.Db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), ProductId = f.Pen.Id, Type = StockMovementType.Purchase, QuantityBaseUnits = 10m, ReferenceType = "PurchaseOrder",
            ReferenceId = Guid.NewGuid(), ReferenceNo = "PO-X", UserId = f.UserId, EnteredProductUnitId = f.Box.Id, EnteredUnitCode = "box", ConversionFactorSnapshot = 10m,
        });
        await f.Db.SaveChangesAsync();
        var units = await f.Db.ProductUnits.AsNoTracking().Where(u => u.ProductId == f.Pen.Id).ToListAsync();

        var reasons = await ProductUnitUsage.GetLockReasonsAsync(f.Db, units, CancellationToken.None);

        Assert.Equal(ProductUnitUsage.Transactions, reasons[f.Box.Id]);
        Assert.Equal(ProductUnitUsage.Prices, reasons[f.Pc.Id]); // untouched by movements: only its price locks it
    }
}
