using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Purchasing;

public class PurchaseServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SelloraDbContext(options);
    }

    private sealed record Fixture(SelloraDbContext Db, PurchaseService Service, Supplier Supplier, Product Product, ProductSellingMode Bag, ProductSellingMode LooseKg, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = NewDb();

        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", Type = UnitType.Weight };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var bag = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "10 kg bag", ConversionFactorToBase = 10m };
        var looseKg = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "Loose kg", ConversionFactorToBase = 1m };
        product.SellingModes.Add(bag);
        product.SellingModes.Add(looseKg);

        db.AddRange(role, user, unit, tax, inr, settings, supplier, product);
        await db.SaveChangesAsync();

        return new Fixture(db, new PurchaseService(db, new ExchangeRateService(db)), supplier, product, bag, looseKg, user.Id);
    }

    private static CreatePurchaseInput BuildInput(Fixture f, decimal qty, decimal rate, decimal otherCharges = 0, decimal discount = 0) =>
        new(f.Supplier.Id, new DateOnly(2026, 9, 15), "SI-001", null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(f.Product.Id, f.Bag.Id, qty, rate, otherCharges, discount)]);

    [Fact]
    public async Task CreatePurchaseAsync_StampsBatchWithReceivedDateAndLeavesSupplierBatchNumberEmpty()
    {
        var f = await SeedAsync();

        await f.Service.CreatePurchaseAsync(BuildInput(f, 10, 580), f.UserId);

        var batch = await f.Db.PurchaseBatches.SingleAsync();
        Assert.Equal(new DateOnly(2026, 9, 15), batch.ReceivedDate);
        Assert.Null(batch.SupplierBatchNumber);
        Assert.Equal(BatchNo(batch.BatchSequence), batch.InternalBatchNo);
    }

    private static CreatePurchaseInput WithBatch(Fixture f, string? supplierBatchNumber, decimal qty = 10) =>
        new(f.Supplier.Id, new DateOnly(2026, 9, 15), null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(f.Product.Id, f.Bag.Id, qty, 580, 0, 0, supplierBatchNumber)]);

    [Fact]
    public async Task CreatePurchaseAsync_StoresSupplierBatchNumberExactlyAsEntered()
    {
        var f = await SeedAsync();

        await f.Service.CreatePurchaseAsync(WithBatch(f, " rice-001/A "), f.UserId);

        Assert.Equal(" rice-001/A ", (await f.Db.PurchaseBatches.SingleAsync()).SupplierBatchNumber);
    }

    [Fact]
    public async Task CreatePurchaseAsync_TreatsBlankSupplierBatchNumberAsNone()
    {
        var f = await SeedAsync();

        await f.Service.CreatePurchaseAsync(WithBatch(f, "   "), f.UserId);

        Assert.Null((await f.Db.PurchaseBatches.SingleAsync()).SupplierBatchNumber);
    }

    [Fact]
    public async Task CreatePurchaseAsync_AllowsSameSupplierBatchNumberOnSeparatePurchases()
    {
        var f = await SeedAsync();

        await f.Service.CreatePurchaseAsync(WithBatch(f, "RICE-001"), f.UserId);
        await f.Service.CreatePurchaseAsync(WithBatch(f, "RICE-001"), f.UserId);

        var batches = await f.Db.PurchaseBatches.ToListAsync();
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal("RICE-001", b.SupplierBatchNumber));
        Assert.Equal(2, batches.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public async Task CreatePurchaseAsync_RejectsOverlongSupplierBatchNumber()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            f.Service.CreatePurchaseAsync(WithBatch(f, new string('x', 61)), f.UserId));
        Assert.Empty(await f.Db.PurchaseBatches.ToListAsync());
    }

    [Fact]
    public async Task CreatePurchaseAsync_LinksStockMovementToBatchAndKeepsStockConsistent()
    {
        var f = await SeedAsync();

        var purchase = await f.Service.CreatePurchaseAsync(WithBatch(f, "RICE-001"), f.UserId); // 100 kg

        var batch = await f.Db.PurchaseBatches.SingleAsync();
        var movement = await f.Db.StockMovements.SingleAsync();
        Assert.Equal(batch.Id, movement.PurchaseBatchId);
        Assert.Equal(purchase.Id, movement.ReferenceId);
        Assert.Equal(purchase.Lines.Single().Id, batch.PurchaseOrderLineId);

        var stock = await f.Db.StockItems.SingleAsync();
        Assert.Equal(100m, stock.QuantityOnHand);
        Assert.Equal(stock.QuantityOnHand, await f.Db.PurchaseBatches.SumAsync(b => b.RemainingBaseQty));
    }

    [Fact]
    public async Task CancelPurchaseAsync_LinksReversalMovementToSameBatch()
    {
        var f = await SeedAsync();
        var purchase = await f.Service.CreatePurchaseAsync(WithBatch(f, "RICE-001"), f.UserId);
        var batch = await f.Db.PurchaseBatches.SingleAsync();

        await f.Service.CancelPurchaseAsync(purchase.Id, f.UserId);

        var movements = await f.Db.StockMovements.ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Equal(batch.Id, m.PurchaseBatchId));
        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(0m, await f.Db.PurchaseBatches.SumAsync(b => b.RemainingBaseQty));
    }

    [Fact]
    public async Task CreatePurchaseAsync_FailureOnLaterLine_LeavesNoPurchaseBatchOrStockBehind()
    {
        var f = await SeedAsync();
        var input = new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 9, 15), null, null, PaymentMethod.Cash, 0,
        [
            new PurchaseLineInput(f.Product.Id, f.Bag.Id, 10, 580, 0, 0, "RICE-001"),
            new PurchaseLineInput(f.Product.Id, Guid.NewGuid(), 10, 580, 0, 0, "RICE-002"), // unknown selling mode
        ]);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreatePurchaseAsync(input, f.UserId));

        Assert.Empty(await f.Db.PurchaseOrders.ToListAsync());
        Assert.Empty(await f.Db.PurchaseBatches.ToListAsync());
        Assert.Empty(await f.Db.StockMovements.ToListAsync());
        Assert.Empty(await f.Db.StockItems.ToListAsync());
    }

    private static string BatchNo(long sequence) => PurchaseBatch.FormatInternalBatchNo(sequence);

    [Fact]
    public async Task CreatePurchaseAsync_ReplayedWithSameIdempotencyKey_CreatesOnePurchaseAndMovesStockOnce()
    {
        var f = await SeedAsync();
        var input = WithBatch(f, "RICE-001") with { IdempotencyKey = "form-abc" };

        var first = await f.Service.CreatePurchaseAsync(input, f.UserId);
        var replay = await f.Service.CreatePurchaseAsync(input, f.UserId);

        Assert.Equal(first.Id, replay.Id);
        Assert.Single(await f.Db.PurchaseOrders.ToListAsync());
        Assert.Single(await f.Db.PurchaseBatches.ToListAsync());
        Assert.Equal(100m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreatePurchaseAsync_DifferentIdempotencyKeys_AreSeparatePurchases()
    {
        var f = await SeedAsync();

        await f.Service.CreatePurchaseAsync(WithBatch(f, null) with { IdempotencyKey = "a" }, f.UserId);
        await f.Service.CreatePurchaseAsync(WithBatch(f, null) with { IdempotencyKey = "b" }, f.UserId);

        Assert.Equal(2, await f.Db.PurchaseOrders.CountAsync());
        Assert.Equal(200m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CancelPurchaseAsync_RecordsWhoCancelled_NotJustWhoCreated()
    {
        var f = await SeedAsync();
        var purchase = await f.Service.CreatePurchaseAsync(WithBatch(f, "RICE-001"), f.UserId);
        var canceller = new User
        {
            Id = Guid.NewGuid(), FirstName = "Meena", LastName = "Iyer", Username = "meena", Email = "meena@selloramart.in",
            PasswordHash = "x", RoleId = (await f.Db.Roles.FirstAsync()).Id,
        };
        f.Db.Users.Add(canceller);
        await f.Db.SaveChangesAsync();

        await f.Service.CancelPurchaseAsync(purchase.Id, canceller.Id);

        var saved = await f.Db.PurchaseOrders.SingleAsync();
        Assert.Equal(canceller.Id, saved.CancelledByUserId);
        Assert.Equal(f.UserId, saved.CreatedByUserId);
        var reversal = await f.Db.StockMovements.SingleAsync(m => m.QuantityBaseUnits < 0);
        Assert.Equal(canceller.Id, reversal.UserId);
    }

    [Fact]
    public async Task CreatePurchaseAsync_ComputesLineAndHeaderTotalsCorrectly()
    {
        var f = await SeedAsync();

        // 30 bags @ 580/bag = 17400 subtotal, 5% GST = 870, grand total 18270
        var purchase = await f.Service.CreatePurchaseAsync(BuildInput(f, 30, 580), f.UserId);

        Assert.Equal("PUR-0001", purchase.PurchaseNo);
        Assert.Equal(17400m, purchase.Subtotal);
        Assert.Equal(870m, purchase.TaxTotal);
        Assert.Equal(18270m, purchase.GrandTotal);

        var line = purchase.Lines.Single();
        Assert.Equal(300m, line.BaseQty); // 30 bags * 10 kg
        Assert.Equal(58m, line.UnitCost); // 17400 / 300
    }

    [Fact]
    public async Task CreatePurchaseAsync_IncreasesStockAndCreatesBatchAndMovement()
    {
        var f = await SeedAsync();

        var purchase = await f.Service.CreatePurchaseAsync(BuildInput(f, 30, 580), f.UserId);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(300m, stockItem.QuantityOnHand);
        Assert.Equal(58m, stockItem.AverageCost);

        var batch = await f.Db.PurchaseBatches.SingleAsync(b => b.ProductId == f.Product.Id);
        Assert.Equal(300m, batch.PurchasedBaseQty);
        Assert.Equal(300m, batch.RemainingBaseQty);

        var movement = await f.Db.StockMovements.SingleAsync(m => m.ProductId == f.Product.Id);
        Assert.Equal(300m, movement.QuantityBaseUnits);
        Assert.Equal(purchase.PurchaseNo, movement.ReferenceNo);
    }

    [Fact]
    public async Task CreatePurchaseAsync_SecondPurchase_WeightsAverageCostAcrossBatches()
    {
        var f = await SeedAsync();

        await f.Service.CreatePurchaseAsync(BuildInput(f, 30, 580), f.UserId); // 300 kg @ 58/kg
        await f.Service.CreatePurchaseAsync(BuildInput(f, 10, 620), f.UserId); // 100 kg @ 62/kg

        // (300*58 + 100*62) / 400 = 59
        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(400m, stockItem.QuantityOnHand);
        Assert.Equal(59m, stockItem.AverageCost);
    }

    [Fact]
    public async Task CreatePurchaseAsync_TwoLinesForSameNewProduct_CreatesOneStockItemWithCombinedQtyAndWeightedCost()
    {
        var f = await SeedAsync();

        // First-ever purchase of this product, split across two lines/selling modes in one purchase —
        // regression test for the "StockItem already tracked" bug when no StockItem row exists yet.
        var input = new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 9, 18), null, null, PaymentMethod.Cash, 0,
            [
                new PurchaseLineInput(f.Product.Id, f.Bag.Id, 10, 580, 0, 0),      // 100 kg @ 58/kg
                new PurchaseLineInput(f.Product.Id, f.LooseKg.Id, 100, 70, 0, 0),  // 100 kg @ 70/kg
            ]);

        var purchase = await f.Service.CreatePurchaseAsync(input, f.UserId);

        Assert.Equal(2, purchase.Lines.Count);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(200m, stockItem.QuantityOnHand);
        Assert.Equal(64m, stockItem.AverageCost); // (100*58 + 100*70) / 200 = 64

        var batches = await f.Db.PurchaseBatches.Where(b => b.ProductId == f.Product.Id).ToListAsync();
        Assert.Equal(2, batches.Count); // one batch per line, never merged
    }

    [Fact]
    public async Task CreatePurchaseAsync_SellingModeFromDifferentProduct_Throws()
    {
        var f = await SeedAsync();
        var otherProduct = new Product
        {
            Id = Guid.NewGuid(), Sku = "OTHER", Name = "Other", BaseUnitId = f.Product.BaseUnitId,
            TaxId = f.Product.TaxId, ReorderLevel = 0,
        };
        var otherMode = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = otherProduct.Id, Label = "1 kg", ConversionFactorToBase = 1m };
        f.Db.AddRange(otherProduct, otherMode);
        await f.Db.SaveChangesAsync();

        var input = new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 9, 15), null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(f.Product.Id, otherMode.Id, 1, 100, 0, 0)]);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreatePurchaseAsync(input, f.UserId));
    }

    [Fact]
    public async Task CancelPurchaseAsync_ReversesStockAndZeroesBatch()
    {
        var f = await SeedAsync();
        await f.Service.CreatePurchaseAsync(BuildInput(f, 30, 580), f.UserId); // 300 kg @ 58
        var purchase = await f.Service.CreatePurchaseAsync(BuildInput(f, 10, 620), f.UserId); // +100 kg @ 62 -> avg 59

        await f.Service.CancelPurchaseAsync(purchase.Id, f.UserId);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(300m, stockItem.QuantityOnHand);
        Assert.Equal(58m, stockItem.AverageCost); // back to the first batch's cost

        var cancelledPurchase = await f.Db.PurchaseOrders.SingleAsync(p => p.Id == purchase.Id);
        Assert.Equal(PurchaseStatus.Cancelled, cancelledPurchase.Status);
        Assert.NotNull(cancelledPurchase.CancelledAt);

        var batches = await f.Db.PurchaseBatches.Where(b => b.ProductId == f.Product.Id).ToListAsync();
        Assert.Contains(batches, b => b.RemainingBaseQty == 0 && b.PurchasedBaseQty == 100m);
    }

    [Fact]
    public async Task CancelPurchaseAsync_AlreadyCancelled_Throws()
    {
        var f = await SeedAsync();
        var purchase = await f.Service.CreatePurchaseAsync(BuildInput(f, 30, 580), f.UserId);
        await f.Service.CancelPurchaseAsync(purchase.Id, f.UserId);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CancelPurchaseAsync(purchase.Id, f.UserId));
    }

    [Fact]
    public async Task CancelPurchaseAsync_BatchPartiallyConsumed_Throws()
    {
        var f = await SeedAsync();
        var purchase = await f.Service.CreatePurchaseAsync(BuildInput(f, 30, 580), f.UserId);

        var batch = await f.Db.PurchaseBatches.SingleAsync(b => b.ProductId == f.Product.Id);
        batch.RemainingBaseQty -= 50; // simulate a sale having consumed some of this batch
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CancelPurchaseAsync(purchase.Id, f.UserId));
    }
}
