using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Guard for an EF Core interaction with the company query filter: a required navigation reached through an optional
/// one (<c>PurchaseBatch.PurchaseOrderLine?.PurchaseOrder</c>) must not become an INNER JOIN that drops adjustment
/// batches. A sale consumes both an adjustment batch and a purchase batch; the sale detail and the stock detail
/// (through the full HTTP pipeline on Postgres) must list every batch with the right source.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MixedBatchDetailApiTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SaleAndStockDetail_ListBothAdjustmentAndPurchaseBatches()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var login = await host.LoginAsync();
        Guid productId, saleId;
        int batchesWithStock;
        await using (var db = host.Database.CreateDbContext())
        {
            var userId = login.User.Id;
            var product = await db.Products.Include(p => p.Units).SingleAsync(p => p.Sku == "STN-PEN");
            var unit = product.Units.Single(u => u.IsBaseUnit);
            var supplier = await db.Suppliers.Where(s => s.Currency.Code == "INR").OrderBy(s => s.Name).FirstAsync();
            var customer = await db.Customers.OrderBy(c => c.Name).FirstAsync();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            await new PricingService(db).SetPriceAsync(unit.Id, 10m, userId);
            await new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(product.Id, unit.Id, AdjustmentType.Correction, AdjustmentDirection.Increase, 4m, 3m, "opening count"), userId);
            await new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(new CreatePurchaseInput(supplier.Id, today, null, null,
                PaymentMethod.Cash, 0m, [new PurchaseLineInput(product.Id, unit.Id, 6m, 5m, 0, 0)], null), userId);

            batchesWithStock = await db.PurchaseBatches.CountAsync(b => b.ProductId == product.Id && b.RemainingBaseQty > 0);
            var onHand = await db.StockItems.Where(s => s.ProductId == product.Id).Select(s => s.QuantityOnHand).SingleAsync();
            var sale = await new SalesService(db).CreateSaleAsync(new CreateSaleInput(customer.Id, today, PaymentMethod.Cash, 0m,
                [new SaleLineInput(product.Id, unit.Id, onHand, 10m, 0)]), userId);
            (productId, saleId) = (product.Id, sale.Id);
        }

        using var client = host.NewClient(login.AccessToken);

        var saleResponse = await client.GetAsync($"/api/v1/sales/{saleId}");
        Assert.Equal(HttpStatusCode.OK, saleResponse.StatusCode);
        using var saleJson = JsonDocument.Parse(await saleResponse.Content.ReadAsStringAsync());
        var consumptions = saleJson.RootElement.GetProperty("lines")[0].GetProperty("batchConsumptions").EnumerateArray()
            .Select(c => c.GetProperty("sourceType").GetString()).ToList();
        Assert.Equal(batchesWithStock, consumptions.Count);
        Assert.Contains("Adjustment", consumptions);
        Assert.Contains("Purchase", consumptions);

        var stockResponse = await client.GetAsync($"/api/v1/stock/{productId}");
        Assert.Equal(HttpStatusCode.OK, stockResponse.StatusCode);
        using var stockJson = JsonDocument.Parse(await stockResponse.Content.ReadAsStringAsync());
        await using var check = host.Database.CreateDbContext();
        var expectedBatches = await check.PurchaseBatches.CountAsync(b => b.ProductId == productId);
        var sources = stockJson.RootElement.GetProperty("batches").EnumerateArray().Select(b => b.GetProperty("sourceType").GetString()).ToList();
        Assert.Equal(expectedBatches, sources.Count);
        Assert.Contains("Adjustment", sources);
        Assert.Contains("Purchase", sources);
    }
}
