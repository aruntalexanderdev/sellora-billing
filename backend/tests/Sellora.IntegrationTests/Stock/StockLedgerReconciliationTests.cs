using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Stock;

[Collection(PostgresCollection.Name)]
public class StockLedgerReconciliationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task StockInvariants_AfterPurchaseSaleReturnAndAdjustment_ReturnNoRows()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        StockScenario scenario;
        await using (var db = database.CreateDbContext())
        {
            scenario = await StockScenario.SeedAsync(db);
        }

        // Each step on its own context, like separate API requests.
        await using (var db = database.CreateDbContext()) await scenario.PurchaseBagsAsync(db, 3, 580);   // +30 kg @ 58
        await using (var db = database.CreateDbContext()) await scenario.PurchaseBagsAsync(db, 2, 620);   // +20 kg @ 62
        SalesInvoice sale;
        await using (var db = database.CreateDbContext()) sale = await scenario.SellLooseKgAsync(db, 35); // -35 kg across both batches
        await using (var db = database.CreateDbContext()) await scenario.ReturnAsync(db, sale.Id, sale.Lines.Single().Id, 7.5m); // +7.5 kg
        await using (var db = database.CreateDbContext()) await scenario.AdjustAsync(db, AdjustmentType.Damage, AdjustmentDirection.Decrease, 2.25m);
        await using (var db = database.CreateDbContext()) await scenario.AdjustAsync(db, AdjustmentType.Correction, AdjustmentDirection.Increase, 1.5m, 60m);

        await using var check = database.CreateDbContext();
        Assert.Equal(50m - 35m + 7.5m - 2.25m + 1.5m, (await check.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(6, await check.StockMovements.CountAsync());
        await StockInvariants.AssertHoldAsync(check);
    }
}
