using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Traceability;

/// <summary>
/// The purchase-batch traceability scenario from the implementation plan, end to end: purchase with supplier batch
/// numbers, sell across batches under FIFO, then read the result back the way Stock Details does.
/// </summary>
public class BatchTraceabilityScenarioTests
{
    private sealed record Fixture(SelloraDbContext Db, PurchaseService Purchases, SalesService Sales, StockController Stock,
        Supplier Supplier, Customer Customer, Product Rice, ProductSellingMode LooseKg, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = new SelloraDbContext(new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var kg = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", Type = UnitType.Weight };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 0%", RatePercent = 0m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var rice = new Product { Id = Guid.NewGuid(), Sku = "RICE", Name = "Ponni Rice", BaseUnitId = kg.Id, BaseUnit = kg, TaxId = tax.Id, Tax = tax, ReorderLevel = 5 };
        var looseKg = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = rice.Id, Label = "Loose kg", ConversionFactorToBase = 1m };
        rice.SellingModes.Add(looseKg);

        db.AddRange(role, user, kg, tax, inr, settings, supplier, customer, rice);
        await db.SaveChangesAsync();

        return new Fixture(db, new PurchaseService(db, new ExchangeRateService(db)), new SalesService(db), new StockController(db),
            supplier, customer, rice, looseKg, user.Id);
    }

    private static Task<PurchaseOrder> BuyAsync(Fixture f, decimal kg, decimal ratePerKg, string? supplierBatch) =>
        f.Purchases.CreatePurchaseAsync(
            new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 9, 1), null, null, PaymentMethod.Cash, 0,
                [new PurchaseLineInput(f.Rice.Id, f.LooseKg.Id, kg, ratePerKg, 0, 0, supplierBatch)]),
            f.UserId);

    private static Task<SalesInvoice> SellAsync(Fixture f, decimal kg) =>
        f.Sales.CreateSaleAsync(
            new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 2), PaymentMethod.Cash, 0,
                [new SaleLineInput(f.Rice.Id, f.LooseKg.Id, kg, 80, 0)]),
            f.UserId);

    private static async Task<StockDetailDto> DetailAsync(Fixture f) =>
        (StockDetailDto)((OkObjectResult)(await f.Stock.Detail(f.Rice.Id, CancellationToken.None)).Result!).Value!;

    [Fact]
    public async Task PurchaseSellFifoAndTraceBack_FullScenario()
    {
        var f = await SeedAsync();

        // 1-3. Purchase 10 kg with supplier batch RICE-001: a batch exists, tied to the exact purchase line; stock is 10 kg.
        var purchaseA = await BuyAsync(f, 10, 50, "RICE-001");
        var batchA = await f.Db.PurchaseBatches.SingleAsync();
        Assert.Equal("RICE-001", batchA.SupplierBatchNumber);
        Assert.Equal(purchaseA.Lines.Single().Id, batchA.PurchaseOrderLineId);
        Assert.NotEmpty(batchA.InternalBatchNo);
        Assert.Equal(10m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);

        // 4-6. Sell 2 kg: the batch has 8 kg left and the sale points at it.
        var sale1 = await SellAsync(f, 2);
        Assert.Equal(8m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
        var link = await f.Db.SalesInvoiceLineBatchConsumptions.SingleAsync();
        Assert.Equal(batchA.Id, link.PurchaseBatchId);
        Assert.Equal(sale1.Lines.Single().Id, link.SalesInvoiceLineId);
        Assert.Equal(2m, link.QtyBaseUnits);

        // 7-9. Buy another 10 kg as RICE-002 (dearer), then sell 9 kg: 8 from the first batch, 1 from the second.
        var purchaseB = await BuyAsync(f, 10, 60, "RICE-002");
        var sale2 = await SellAsync(f, 9);
        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(0m, batches[0].RemainingBaseQty);
        Assert.Equal(9m, batches[1].RemainingBaseQty);
        Assert.Equal(8m * 50m + 1m * 60m, sale2.Lines.Single().CostOfGoodsSold);
        Assert.Equal(9m + 0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);

        // 10. Stock Details shows both batches, each traceable to its purchase, with the supplier numbers.
        var detail = await DetailAsync(f);
        Assert.Equal(9m, detail.QuantityOnHand);
        Assert.Equal(2, detail.Batches.Count);
        var a = detail.Batches.Single(b => b.SupplierBatchNumber == "RICE-001");
        var b2 = detail.Batches.Single(b => b.SupplierBatchNumber == "RICE-002");
        Assert.Equal((purchaseA.Id, purchaseA.PurchaseNo, 10m, 0m), (a.PurchaseOrderId, a.SourceRefNo, a.PurchasedBaseQty, a.RemainingBaseQty));
        Assert.Equal((purchaseB.Id, purchaseB.PurchaseNo, 10m, 9m), (b2.PurchaseOrderId, b2.SourceRefNo, b2.PurchasedBaseQty, b2.RemainingBaseQty));
        Assert.All(detail.Batches, b => Assert.False(string.IsNullOrEmpty(b.InternalBatchNo)));

        // ...and batch -> sales -> quantity works from either batch.
        var forA = await ConsumptionsAsync(f, a.Id);
        Assert.Equal([2m, 8m], forA.Select(c => c.QtyBaseUnits).Order().ToArray());
        Assert.Equal([sale1.InvoiceNo, sale2.InvoiceNo], forA.Select(c => c.InvoiceNo).Order().ToArray());
        var forB = await ConsumptionsAsync(f, b2.Id);
        Assert.Equal(1m, Assert.Single(forB).QtyBaseUnits);
        Assert.Equal(sale2.InvoiceNo, forB.Single().InvoiceNo);
        Assert.Equal(60m, forB.Single().UnitCost);
    }

    private static async Task<IReadOnlyList<BatchConsumptionDto>> ConsumptionsAsync(Fixture f, Guid batchId) =>
        (IReadOnlyList<BatchConsumptionDto>)((OkObjectResult)(await f.Stock.BatchConsumptions(batchId, CancellationToken.None)).Result!).Value!;

    [Fact]
    public async Task PurchaseWithoutSupplierBatchNumber_StillCreatesATraceableBatch_AndSellsNormally()
    {
        var f = await SeedAsync();

        // 11.
        await BuyAsync(f, 10, 50, null);
        await SellAsync(f, 3);

        var detail = await DetailAsync(f);
        var batch = Assert.Single(detail.Batches);
        Assert.Null(batch.SupplierBatchNumber);
        Assert.NotEmpty(batch.InternalBatchNo);
        Assert.Equal(7m, batch.RemainingBaseQty);
        Assert.NotNull(batch.PurchaseOrderId);
    }

    [Fact]
    public async Task SameSupplierBatchNumberOnSeparatePurchases_GivesTwoDistinctBatches_ThatFifoTreatsSeparately()
    {
        var f = await SeedAsync();

        // 12.
        var first = await BuyAsync(f, 10, 50, "RICE-001");
        var second = await BuyAsync(f, 10, 60, "RICE-001");
        await SellAsync(f, 12);

        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(2, batches.Count);
        Assert.NotEqual(batches[0].Id, batches[1].Id);
        Assert.All(batches, b => Assert.Equal("RICE-001", b.SupplierBatchNumber));
        Assert.Equal(first.Lines.Single().Id, batches[0].PurchaseOrderLineId);
        Assert.Equal(second.Lines.Single().Id, batches[1].PurchaseOrderLineId);
        Assert.Equal([0m, 8m], batches.Select(b => b.RemainingBaseQty).ToArray());
    }

    [Fact]
    public async Task CancellingASale_RestoresTheSameBatches_AndTheTraceStaysReadable()
    {
        var f = await SeedAsync();
        await BuyAsync(f, 10, 50, "RICE-001");
        await BuyAsync(f, 10, 60, "RICE-002");
        var sale = await SellAsync(f, 15); // 10 from RICE-001, 5 from RICE-002

        await f.Sales.CancelSaleAsync(sale.Id, f.UserId);

        var detail = await DetailAsync(f);
        Assert.Equal(20m, detail.QuantityOnHand);
        Assert.All(detail.Batches, b => Assert.Equal(b.PurchasedBaseQty, b.RemainingBaseQty));
        var rows = await ConsumptionsAsync(f, detail.Batches.Single(b => b.SupplierBatchNumber == "RICE-001").Id);
        Assert.Equal(SalesStatus.Cancelled, Assert.Single(rows).SaleStatus);
    }
}
