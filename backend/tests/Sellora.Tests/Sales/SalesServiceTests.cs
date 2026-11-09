using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Sales;

public class SalesServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SelloraDbContext(options, Sellora.Tests.TestData.TestCompany.Context);
    }

    private sealed record Fixture(
        SelloraDbContext Db, ISalesService Service, PurchaseService PurchaseService,
        Supplier Supplier, Customer Customer, Product Product, ProductUnit Bag, ProductUnit LooseKg, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = NewDb();

        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var looseKg = TestProductUnits.Base(product, "Loose kg");
        var bag = TestProductUnits.Pack(product, TestUnits.Bag(), looseKg, 10m, "10 kg bag");

        db.AddRange(role, user, unit, tax, inr, supplier, customer, settings, product);
        await db.SaveChangesAsync();

        return new Fixture(db, ListPricedSales.For(db), new PurchaseService(db, new ExchangeRateService(db)), supplier, customer, product, bag, looseKg, user.Id);
    }

    private static Task<PurchaseOrder> PurchaseAsync(Fixture f, decimal bags, decimal ratePerBag) =>
        f.PurchaseService.CreatePurchaseAsync(
            new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 9, 1), null, null, PaymentMethod.Cash, 0,
                [new PurchaseLineInput(f.Product.Id, f.Bag.Id, bags, ratePerBag, 0, 0)]),
            f.UserId);

    private static CreateSaleInput BuildInput(Fixture f, decimal qty, decimal unitPrice, decimal discount = 0) =>
        new(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
            [new SaleLineInput(f.Product.Id, f.LooseKg.Id, qty, unitPrice, discount)]);

    [Fact]
    public async Task CreateSaleAsync_ComputesLineAndHeaderTotalsCorrectly()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 30, 580); // 300 kg @ 58/kg landed cost

        // 20 kg loose @ 65/kg = 1300 subtotal, 5% GST = 65, grand total 1365
        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 20, 65), f.UserId);

        Assert.Equal("INV-0001", sale.InvoiceNo);
        Assert.Equal(1300m, sale.Subtotal);
        Assert.Equal(65m, sale.TaxTotal);
        Assert.Equal(1365m, sale.GrandTotal);

        var line = sale.Lines.Single();
        Assert.Equal(20m, line.BaseQty);
        Assert.Equal(1160m, line.CostOfGoodsSold); // 20 kg * 58/kg landed cost
    }

    [Fact]
    public async Task CreateSaleAsync_DecreasesStockAndWritesSaleMovement()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 30, 580); // 300 kg

        await f.Service.CreateSaleAsync(BuildInput(f, 20, 65), f.UserId);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(280m, stockItem.QuantityOnHand);
        Assert.Equal(58m, stockItem.AverageCost); // unchanged by a sale

        var saleMovement = await f.Db.StockMovements.SingleAsync(m => m.Type == StockMovementType.Sale);
        Assert.Equal(-20m, saleMovement.QuantityBaseUnits);
    }

    [Fact]
    public async Task CreateSaleAsync_ConsumesOldestBatchFirstAcrossTwoPurchases()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580); // 100 kg @ 58/kg (oldest)
        await PurchaseAsync(f, 10, 620); // 100 kg @ 62/kg (newer)

        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 150, 70), f.UserId); // draws 100 from first batch, 50 from second

        var line = sale.Lines.Single();
        Assert.Equal(100m * 58m + 50m * 62m, line.CostOfGoodsSold); // 8900

        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(0m, batches[0].RemainingBaseQty);
        Assert.Equal(50m, batches[1].RemainingBaseQty);
    }

    [Fact]
    public async Task CreateSaleAsync_PartialAcrossTwoBatches_RecordsEachBatchAndKeepsStockConsistent()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580); // Batch A: 10 kg @ 58/kg
        await PurchaseAsync(f, 2, 620); // Batch B: 20 kg @ 62/kg

        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 12, 70), f.UserId); // 10 from A, 2 from B

        var line = sale.Lines.Single();
        Assert.Equal(10m * 58m + 2m * 62m, line.CostOfGoodsSold);

        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(0m, batches[0].RemainingBaseQty);
        Assert.Equal(18m, batches[1].RemainingBaseQty);

        var consumptions = await f.Db.SalesInvoiceLineBatchConsumptions.OrderBy(c => c.PurchaseBatch.CreatedAt).ToListAsync();
        Assert.Equal(2, consumptions.Count);
        Assert.Equal((batches[0].Id, 10m, 58m), (consumptions[0].PurchaseBatchId, consumptions[0].QtyBaseUnits, consumptions[0].UnitCost));
        Assert.Equal((batches[1].Id, 2m, 62m), (consumptions[1].PurchaseBatchId, consumptions[1].QtyBaseUnits, consumptions[1].UnitCost));
        Assert.All(consumptions, c => Assert.Equal(line.Id, c.SalesInvoiceLineId));

        // Each batch traces back to its own purchase line.
        Assert.All(batches, b => Assert.NotNull(b.PurchaseOrderLineId));

        Assert.Equal(await f.Db.PurchaseBatches.SumAsync(b => b.RemainingBaseQty), (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateSaleAsync_QuarterKgPackUnit_ConsumesBaseQuantityFromBatch()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580); // 10 kg @ 58/kg
        var pack250 = TestUnits.New("250 g pack", "p250", MeasurementCategory.Packaging, 0, null);
        var gram250 = TestProductUnits.Pack(f.Product, pack250, f.LooseKg, 0.25m, "250 g");
        f.Db.Units.Add(pack250);
        f.Db.ProductUnits.Add(gram250);
        await f.Db.SaveChangesAsync();

        var sale = await f.Service.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
                [new SaleLineInput(f.Product.Id, gram250.Id, 3, 20, 0)]), // 3 x 250 g = 0.75 kg
            f.UserId);

        Assert.Equal(0.75m, sale.Lines.Single().BaseQty);
        Assert.Equal(0.75m * 58m, sale.Lines.Single().CostOfGoodsSold);
        Assert.Equal(9.25m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
        Assert.Equal(0.75m, (await f.Db.SalesInvoiceLineBatchConsumptions.SingleAsync()).QtyBaseUnits);
    }

    [Fact]
    public async Task CreateSaleAsync_SixDecimalBatchCost_CostOfGoodsSoldRoundedToFourDecimals()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580); // 10 kg
        var batch = await f.Db.PurchaseBatches.SingleAsync();
        batch.UnitCost = 7.129769m; // per-base cost at the widened 6-dp precision
        await f.Db.SaveChangesAsync();

        var sale = await f.Service.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
                [new SaleLineInput(f.Product.Id, f.LooseKg.Id, 3, 20, 0)]), // 3 kg x 7.129769 = 21.389307
            f.UserId);

        Assert.Equal(21.3893m, sale.Lines.Single().CostOfGoodsSold);
        Assert.Equal(7.129769m, (await f.Db.SalesInvoiceLineBatchConsumptions.SingleAsync()).UnitCost);
    }

    [Fact]
    public async Task CreateSaleAsync_InactiveProductUnit_RejectedOnLinesAndStockUntouched()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);
        f.LooseKg.IsActive = false; // legacy data: the service must not rely on the base-row CHECK
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(BuildInput(f, 2, 65), f.UserId));

        Assert.Equal(("Lines", "Loose kg is no longer active."), (ex.Field, ex.Message));
        Assert.False(await f.Db.SalesInvoices.AnyAsync());
        Assert.Equal(10m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateSaleAsync_ProductUnitNotSellable_RejectedOnLines()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);
        f.LooseKg.IsSellable = false;
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(BuildInput(f, 2, 65), f.UserId));

        Assert.Equal(("Lines", "Loose kg cannot be sold for Ponni Rice."), (ex.Field, ex.Message));
    }

    [Fact]
    public async Task CreateSaleAsync_NotPurchasableButSellableUnit_Accepted()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);
        f.LooseKg.IsPurchasable = false;
        await f.Db.SaveChangesAsync();

        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 2, 65), f.UserId);

        Assert.Equal(2m, sale.Lines.Single().BaseQty);
    }

    [Fact]
    public async Task CreateSaleAsync_ProductUnitFromDifferentProduct_RejectedOnLines()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);
        var other = new ProductBuilder().WithSku("OTHER").WithBaseUnit(f.Product.BaseUnit).WithTax(f.Product.Tax).Build();
        var otherUnit = TestProductUnits.Base(other);
        f.Db.Add(other);
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
                [new SaleLineInput(f.Product.Id, otherUnit.Id, 1, 65, 0)]), f.UserId));

        Assert.Equal(("Lines", "The selected unit does not belong to this product."), (ex.Field, ex.Message));
    }

    [Fact]
    public async Task CreateSaleAsync_SecondLineInactive_RejectsWholeSale()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 2, 580);
        f.Bag.IsActive = false;
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
                [new SaleLineInput(f.Product.Id, f.LooseKg.Id, 1, 65, 0), new SaleLineInput(f.Product.Id, f.Bag.Id, 1, 600, 0)]), f.UserId));

        Assert.Equal("10 kg bag is no longer active.", ex.Message);
        Assert.False(await f.Db.SalesInvoices.AnyAsync());
        Assert.Equal(20m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateSaleAsync_BatchesShortOfStockOnHand_RefusesInsteadOfUnderstatingCost()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580); // 10 kg
        // Simulate drift: stock on hand claims more than the batches hold.
        var stock = await f.Db.StockItems.SingleAsync();
        stock.QuantityOnHand = 20;
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(BuildInput(f, 15, 70), f.UserId));

        Assert.Empty(await f.Db.SalesInvoices.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateSaleAsync_ZeroOrNegativeQuantity_IsRejected(double qty)
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(BuildInput(f, (decimal)qty, 70), f.UserId));
        Assert.Equal(10m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
    }

    [Fact]
    public async Task CreateSaleAsync_BaseQuantityBeyondThreeDecimals_IsRejected()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);

        // 1.2345 kg would be rounded when stored, letting batch and stock totals drift apart.
        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(BuildInput(f, 1.2345m, 70), f.UserId));
    }

    [Fact]
    public async Task CreateSaleAsync_SellingEverythingDepletesBatchesExactly_AndNextSaleIsRefused()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);
        await PurchaseAsync(f, 1, 620);

        await f.Service.CreateSaleAsync(BuildInput(f, 20, 70), f.UserId);

        Assert.All(await f.Db.PurchaseBatches.ToListAsync(), b => Assert.Equal(0m, b.RemainingBaseQty));
        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(BuildInput(f, 0.001m, 70), f.UserId));
    }

    [Fact]
    public async Task CreateSaleAsync_ReplayedWithSameIdempotencyKey_SellsOnlyOnce()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580); // 10 kg
        var input = BuildInput(f, 4, 70) with { IdempotencyKey = "pos-tap-1" };

        var first = await f.Service.CreateSaleAsync(input, f.UserId);
        var replay = await f.Service.CreateSaleAsync(input, f.UserId);

        Assert.Equal(first.Id, replay.Id);
        Assert.Single(await f.Db.SalesInvoices.ToListAsync());
        Assert.Equal(6m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
        Assert.Equal(6m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CancelSaleAsync_RecordsWhoCancelled()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580);
        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 4, 70), f.UserId);
        var canceller = new User
        {
            Id = Guid.NewGuid(), FirstName = "Meena", LastName = "Iyer", Username = "meena", Email = "meena@selloramart.in",
            PasswordHash = "x", RoleId = (await f.Db.Roles.FirstAsync()).Id,
        };
        f.Db.Users.Add(canceller);
        await f.Db.SaveChangesAsync();

        await f.Service.CancelSaleAsync(sale.Id, canceller.Id);

        Assert.Equal(canceller.Id, (await f.Db.SalesInvoices.SingleAsync()).CancelledByUserId);
        Assert.Equal(canceller.Id, (await f.Db.StockMovements.SingleAsync(m => m.Notes == "Sale cancelled")).UserId);
    }

    [Fact]
    public async Task CreateSaleAsync_QuantityExceedingStock_Throws()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580); // 100 kg

        var input = BuildInput(f, 101, 65);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateSaleAsync_SameProductAcrossTwoLines_SumsAgainstAvailableStock()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580); // 100 kg

        // 5 bags (50kg) + 60kg loose = 110kg requested against 100kg available.
        var input = new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
            [
                new SaleLineInput(f.Product.Id, f.Bag.Id, 5, 600, 0),
                new SaleLineInput(f.Product.Id, f.LooseKg.Id, 60, 65, 0),
            ]);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateSaleAsync(input, f.UserId));
    }

    /// <summary>Phase 3 §4 behaviour delta: line 1 (a bag) exactly drains the oldest batch; line 2 (loose kg) must draw
    /// only from the next batch. Before the shared allocator, line 2 also wrote a zero-quantity consumption on the drained
    /// batch (the DB filter sees the saved remainder, EF hands back the tracked, already drained instance).</summary>
    [Fact]
    public async Task CreateSaleAsync_LineOneDrainsOldestBatch_LineTwoWritesNoZeroQuantityConsumption()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 1, 580); // B1: 10 kg @ 58
        await PurchaseAsync(f, 2, 620); // B2: 20 kg @ 62

        var sale = await f.Service.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
            [
                new SaleLineInput(f.Product.Id, f.Bag.Id, 1, 600, 0),      // 10 kg: all of B1
                new SaleLineInput(f.Product.Id, f.LooseKg.Id, 5, 65, 0),   // 5 kg: from B2 only
            ]), f.UserId);

        var batches = await f.Db.PurchaseBatches.AsNoTracking().OrderBy(b => b.CreatedAt).ToListAsync();
        var consumptions = await f.Db.SalesInvoiceLineBatchConsumptions.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(consumptions, c => c.QtyBaseUnits <= 0);
        Assert.Equal(2, consumptions.Count);
        var (bagLine, looseLine) = (sale.Lines.Single(l => l.ProductUnitId == f.Bag.Id), sale.Lines.Single(l => l.ProductUnitId == f.LooseKg.Id));
        Assert.Equal((batches[0].Id, 10m, 58m), consumptions.Where(c => c.SalesInvoiceLineId == bagLine.Id).Select(c => (c.PurchaseBatchId, c.QtyBaseUnits, c.UnitCost)).Single());
        Assert.Equal((batches[1].Id, 5m, 62m), consumptions.Where(c => c.SalesInvoiceLineId == looseLine.Id).Select(c => (c.PurchaseBatchId, c.QtyBaseUnits, c.UnitCost)).Single());
        Assert.Equal((580m, 310m), (bagLine.CostOfGoodsSold, looseLine.CostOfGoodsSold));
        Assert.Equal(new[] { 0m, 15m }, batches.Select(b => b.RemainingBaseQty));
        Assert.Equal(15m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CancelSaleAsync_RestoresBatchesAndStock()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580); // 100 kg @ 58/kg
        await PurchaseAsync(f, 10, 620); // 100 kg @ 62/kg

        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 150, 70), f.UserId); // draws 100 + 50

        await f.Service.CancelSaleAsync(sale.Id, f.UserId);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(200m, stockItem.QuantityOnHand);

        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(100m, batches[0].RemainingBaseQty);
        Assert.Equal(100m, batches[1].RemainingBaseQty);

        var cancelled = await f.Db.SalesInvoices.SingleAsync(i => i.Id == sale.Id);
        Assert.Equal(SalesStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
    }

    [Fact]
    public async Task CancelSaleAsync_AlreadyCancelled_Throws()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580);
        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 20, 65), f.UserId);
        await f.Service.CancelSaleAsync(sale.Id, f.UserId);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CancelSaleAsync(sale.Id, f.UserId));
    }

    [Fact]
    public async Task CancelSaleAsync_LaterSaleConsumedRestOfSameBatch_StillRestoresExactAmount()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580); // 100 kg @ 58/kg — single batch

        var firstSale = await f.Service.CreateSaleAsync(BuildInput(f, 40, 65), f.UserId); // batch remaining -> 60
        await f.Service.CreateSaleAsync(BuildInput(f, 60, 65), f.UserId); // batch remaining -> 0

        await f.Service.CancelSaleAsync(firstSale.Id, f.UserId); // should give back exactly 40, regardless of the second sale

        var batch = await f.Db.PurchaseBatches.SingleAsync(b => b.ProductId == f.Product.Id);
        Assert.Equal(40m, batch.RemainingBaseQty);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(40m, stockItem.QuantityOnHand); // 100 - 40 - 60 + 40 restored
    }

    private static async Task<SalesInvoice> SaleAsync(Fixture f, decimal paid, PaymentMethod method = PaymentMethod.Cash)
    {
        await PurchaseAsync(f, 30, 580);
        // 20 kg @ 65 = 1300 + 5% GST = 1365
        return await f.Service.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), method, paid,
                [new SaleLineInput(f.Product.Id, f.LooseKg.Id, 20, 65, 0)]), f.UserId);
    }

    [Fact]
    public async Task AddPaymentAsync_PartialPayments_AppendRowsAndTrackDueAndStatus()
    {
        var f = await SeedAsync();
        var sale = await SaleAsync(f, 300m);
        Assert.Equal(PaymentStatus.PartiallyPaid, sale.PaymentStatus);

        var second = await f.Service.AddPaymentAsync(sale.Id, new AddSalePaymentInput(200m, PaymentMethod.Upi, 1065m), f.UserId);
        Assert.Equal(500m, second!.PaidAmount);
        Assert.Equal(865m, second.DueAmount);
        Assert.Equal(PaymentMethod.Upi, second.PaymentMethod);

        var last = await f.Service.AddPaymentAsync(sale.Id, new AddSalePaymentInput(865m, PaymentMethod.Card, 865m), f.UserId);
        Assert.Equal(PaymentStatus.Paid, last!.PaymentStatus);
        Assert.Equal(0m, last.DueAmount);

        var rows = await f.Db.SalesPayments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
        Assert.Equal([300m, 200m, 865m], rows.Select(r => r.Amount));
        Assert.Equal([PaymentMethod.Cash, PaymentMethod.Upi, PaymentMethod.Card], rows.Select(r => r.PaymentMethod));
        Assert.All(rows, r => Assert.Equal(1m, r.ExchangeRateToBase));
        Assert.Equal(1365m, rows.Sum(r => r.BaseAmount));
    }

    [Fact]
    public async Task CreateSaleAsync_WithoutPayment_WritesNoRow_AndOverTenderIsCappedAsChange()
    {
        var f = await SeedAsync();
        var unpaid = await SaleAsync(f, 0m);
        Assert.Empty(await f.Db.SalesPayments.ToListAsync());
        Assert.Equal(PaymentStatus.Due, unpaid.PaymentStatus);

        var overTendered = await f.Service.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 2000m,
                [new SaleLineInput(f.Product.Id, f.LooseKg.Id, 20, 65, 0)]), f.UserId);

        Assert.Equal(1365m, overTendered.PaidAmount);
        Assert.Equal(1365m, (await f.Db.SalesPayments.SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1065.01)]
    public async Task AddPaymentAsync_ZeroNegativeOrOverpayment_IsRejected(double amount)
    {
        var f = await SeedAsync();
        var sale = await SaleAsync(f, 300m);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.AddPaymentAsync(sale.Id, new AddSalePaymentInput((decimal)amount, PaymentMethod.Cash, 1065m), f.UserId));
        Assert.Equal("Amount", ex.Field);
        Assert.Single(await f.Db.SalesPayments.ToListAsync());
    }

    [Fact]
    public async Task AddPaymentAsync_DuplicateSubmission_FullyPaidAndCancelled_AreRejected()
    {
        var f = await SeedAsync();
        var sale = await SaleAsync(f, 0m);
        var request = new AddSalePaymentInput(1365m, PaymentMethod.Cash, 1365m);

        await f.Service.AddPaymentAsync(sale.Id, request, f.UserId);
        var paid = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.AddPaymentAsync(sale.Id, request, f.UserId));
        Assert.Equal("Amount", paid.Field);

        var other = await SaleAsync(f, 0m);
        await f.Service.AddPaymentAsync(other.Id, new AddSalePaymentInput(100m, PaymentMethod.Cash, 1365m), f.UserId);
        var stale = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.AddPaymentAsync(other.Id, new AddSalePaymentInput(100m, PaymentMethod.Cash, 1365m), f.UserId));
        Assert.Equal("ExpectedDue", stale.Field);

        await f.Service.CancelSaleAsync(other.Id, f.UserId);
        var cancelled = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.AddPaymentAsync(other.Id, new AddSalePaymentInput(10m, PaymentMethod.Cash, 1265m), f.UserId));
        Assert.Equal("Status", cancelled.Field);
        Assert.Null(await f.Service.AddPaymentAsync(Guid.NewGuid(), request, f.UserId));
    }

    [Fact]
    public async Task Payments_EachWriteACustomerPaymentTransaction_InTheBaseCurrency()
    {
        var f = await SeedAsync();
        var sale = await SaleAsync(f, 300m);
        await f.Service.AddPaymentAsync(sale.Id, new AddSalePaymentInput(200m, PaymentMethod.Upi, 1065m), f.UserId);

        var payments = await f.Db.SalesPayments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
        var txns = await f.Db.FinancialTransactions.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
        var baseCurrencyId = (await f.Db.BusinessSettings.SingleAsync()).BaseCurrencyId;

        Assert.Equal(2, txns.Count);
        Assert.All(txns, t =>
        {
            Assert.Equal(FinancialTransactionType.CustomerPayment, t.Type);
            Assert.Equal(MoneyDirection.In, t.Direction);
            Assert.Equal(baseCurrencyId, t.CurrencyId);
        });
        Assert.Equal(payments.Select(p => p.Id), txns.Select(t => t.SalesPaymentId!.Value));
        Assert.Equal([300m, 200m], txns.Select(t => t.BaseAmount));
    }

    [Fact]
    public async Task UnpaidSale_AndCappedOverTender_WriteTheRightTransactions()
    {
        var f = await SeedAsync();
        await SaleAsync(f, 0m);
        Assert.Empty(await f.Db.FinancialTransactions.ToListAsync());

        await f.Service.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 2000m,
                [new SaleLineInput(f.Product.Id, f.LooseKg.Id, 20, 65, 0)]), f.UserId);

        Assert.Equal(1365m, (await f.Db.FinancialTransactions.SingleAsync()).BaseAmount); // change is not money received
    }
}
