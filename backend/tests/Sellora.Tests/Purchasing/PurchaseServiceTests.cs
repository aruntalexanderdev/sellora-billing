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
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders" };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var bag = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "10 kg bag", ConversionFactorToBase = 10m };
        var looseKg = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "Loose kg", ConversionFactorToBase = 1m };
        product.SellingModes.Add(bag);
        product.SellingModes.Add(looseKg);

        db.AddRange(role, user, unit, tax, supplier, product);
        await db.SaveChangesAsync();

        return new Fixture(db, new PurchaseService(db), supplier, product, bag, looseKg, user.Id);
    }

    private static CreatePurchaseInput BuildInput(Fixture f, decimal qty, decimal rate, decimal otherCharges = 0, decimal discount = 0) =>
        new(f.Supplier.Id, new DateOnly(2026, 9, 15), "SI-001", null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(f.Product.Id, f.Bag.Id, qty, rate, otherCharges, discount)]);

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

        await f.Service.CancelPurchaseAsync(purchase.Id);

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
        await f.Service.CancelPurchaseAsync(purchase.Id);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CancelPurchaseAsync(purchase.Id));
    }

    [Fact]
    public async Task CancelPurchaseAsync_BatchPartiallyConsumed_Throws()
    {
        var f = await SeedAsync();
        var purchase = await f.Service.CreatePurchaseAsync(BuildInput(f, 30, 580), f.UserId);

        var batch = await f.Db.PurchaseBatches.SingleAsync(b => b.ProductId == f.Product.Id);
        batch.RemainingBaseQty -= 50; // simulate a sale having consumed some of this batch
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CancelPurchaseAsync(purchase.Id));
    }
}
