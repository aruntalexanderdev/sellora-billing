using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Sales;

public class SalesServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SelloraDbContext(options);
    }

    private sealed record Fixture(
        SelloraDbContext Db, SalesService Service, PurchaseService PurchaseService,
        Supplier Supplier, Customer Customer, Product Product, ProductSellingMode Bag, ProductSellingMode LooseKg, Guid UserId);

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
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV" };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var bag = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "10 kg bag", ConversionFactorToBase = 10m };
        var looseKg = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "Loose kg", ConversionFactorToBase = 1m };
        product.SellingModes.Add(bag);
        product.SellingModes.Add(looseKg);

        db.AddRange(role, user, unit, tax, supplier, customer, settings, product);
        await db.SaveChangesAsync();

        return new Fixture(db, new SalesService(db), new PurchaseService(db), supplier, customer, product, bag, looseKg, user.Id);
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

    [Fact]
    public async Task CancelSaleAsync_RestoresBatchesAndStock()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580); // 100 kg @ 58/kg
        await PurchaseAsync(f, 10, 620); // 100 kg @ 62/kg

        var sale = await f.Service.CreateSaleAsync(BuildInput(f, 150, 70), f.UserId); // draws 100 + 50

        await f.Service.CancelSaleAsync(sale.Id);

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
        await f.Service.CancelSaleAsync(sale.Id);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CancelSaleAsync(sale.Id));
    }

    [Fact]
    public async Task CancelSaleAsync_LaterSaleConsumedRestOfSameBatch_StillRestoresExactAmount()
    {
        var f = await SeedAsync();
        await PurchaseAsync(f, 10, 580); // 100 kg @ 58/kg — single batch

        var firstSale = await f.Service.CreateSaleAsync(BuildInput(f, 40, 65), f.UserId); // batch remaining -> 60
        await f.Service.CreateSaleAsync(BuildInput(f, 60, 65), f.UserId); // batch remaining -> 0

        await f.Service.CancelSaleAsync(firstSale.Id); // should give back exactly 40, regardless of the second sale

        var batch = await f.Db.PurchaseBatches.SingleAsync(b => b.ProductId == f.Product.Id);
        Assert.Equal(40m, batch.RemainingBaseQty);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(40m, stockItem.QuantityOnHand); // 100 - 40 - 60 + 40 restored
    }
}
