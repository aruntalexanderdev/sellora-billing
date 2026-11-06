using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
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

    /// <summary>Phase 3: stock put in and taken out in packs (10 kg bags) by adjustments, mixed with purchases, sales in bags
    /// and loose kg, and a return. Every adjustment's entered quantity × factor snapshot equals its base quantity, and the
    /// ledger, batches and QOH reconcile.</summary>
    [Fact]
    public async Task StockInvariants_AfterAdjustmentsInPacksMixedWithPurchasesSalesAndReturns_ReturnNoRows()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        StockScenario scenario;
        await using (var db = database.CreateDbContext())
        {
            scenario = await StockScenario.SeedAsync(db);
        }
        Task AdjustBagsAsync(Sellora.Infrastructure.Data.SelloraDbContext db, AdjustmentType type, AdjustmentDirection direction, decimal bags, decimal? costPerBag = null) =>
            new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(scenario.ProductId, scenario.BagModeId, type, direction, bags, costPerBag, "Packs"), scenario.UserId);

        await using (var db = database.CreateDbContext()) await AdjustBagsAsync(db, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 3, 575m); // +30 kg @ 57.5
        await using (var db = database.CreateDbContext()) await scenario.PurchaseBagsAsync(db, 2, 620);                                                // +20 kg @ 62
        SalesInvoice sale;
        await using (var db = database.CreateDbContext()) sale = await scenario.SellLooseKgAsync(db, 32.5m);                                           // -32.5 kg
        await using (var db = database.CreateDbContext()) await AdjustBagsAsync(db, AdjustmentType.Damage, AdjustmentDirection.Decrease, 1);           // -10 kg
        await using (var db = database.CreateDbContext()) await scenario.ReturnAsync(db, sale.Id, sale.Lines.Single().Id, 2.5m);                       // +2.5 kg
        await using (var db = database.CreateDbContext()) await AdjustBagsAsync(db, AdjustmentType.Correction, AdjustmentDirection.Increase, 1, 333.33m); // +10 kg @ 33.333
        await using (var db = database.CreateDbContext()) await scenario.AdjustAsync(db, AdjustmentType.Loss, AdjustmentDirection.Decrease, 0.125m);  // -0.125 kg

        await using var check = database.CreateDbContext();
        Assert.Equal(30m + 20m - 32.5m - 10m + 2.5m + 10m - 0.125m, (await check.StockItems.SingleAsync()).QuantityOnHand);
        await StockInvariants.AssertHoldAsync(check);
        var adjustments = await check.StockAdjustments.AsNoTracking().OrderBy(a => a.AdjustmentNo).ToListAsync();
        Assert.Equal(new[] { "bag", "bag", "bag", "kg" }, adjustments.Select(a => a.UnitCode));
        Assert.All(adjustments, a => Assert.Equal(a.QuantityBaseUnits, a.EnteredQuantity * a.ConversionFactorToBase));
        Assert.Equal(33.3330m, adjustments[2].UnitCost);
        var correctionBatch = await check.PurchaseBatches.AsNoTracking().SingleAsync(b => b.StockAdjustmentId == adjustments[2].Id);
        Assert.Equal((10m, 33.3330m), (correctionBatch.PurchasedBaseQty, correctionBatch.UnitCost));
        Assert.Equal(-10m, await check.StockMovements.Where(m => m.ReferenceId == adjustments[1].Id).SumAsync(m => m.QuantityBaseUnits));
    }

    /// <summary>Phase 5: sell in 10 kg bags, return in loose kg then in bags then the exact remainder in kg, and sell and cancel
    /// another bag. Every sales and return line keeps Qty x factor = BaseQty with its own unit snapshot, the credits add up to the
    /// line total, the cancellation is a SaleCancellation movement, and ledger, batches and QOH reconcile.</summary>
    [Fact]
    public async Task StockInvariants_AfterBagSaleMixedUnitReturnsAndACancellation_ReturnNoRows()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        StockScenario scenario;
        await using (var db = database.CreateDbContext())
        {
            scenario = await StockScenario.SeedAsync(db);
            await StockScenario.EnsureListPriceAsync(db, scenario.UserId, scenario.BagModeId, 600m);
        }
        await using (var db = database.CreateDbContext()) await scenario.PurchaseBagsAsync(db, 5, 580);   // +50 kg
        SalesInvoice sale;
        await using (var db = database.CreateDbContext())
            sale = await new SalesService(db).CreateSaleAsync(new CreateSaleInput(scenario.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0,
                [new SaleLineInput(scenario.ProductId, scenario.BagModeId, 2m, 600m, 0)]), scenario.UserId);                                    // -20 kg
        var line = sale.Lines.Single();
        Task<SalesReturn> ReturnInAsync(Sellora.Infrastructure.Data.SelloraDbContext db, Guid? unitId, decimal qty) =>
            new SalesService(db).CreateReturnAsync(new CreateSaleReturnInput(sale.Id, StockScenario.ReturnDate, null, [new SaleReturnLineInput(line.Id, unitId, qty)]), scenario.UserId);
        await using (var db = database.CreateDbContext()) await ReturnInAsync(db, scenario.LooseKgModeId, 5m);   // +5 kg, other unit than the line
        await using (var db = database.CreateDbContext()) await ReturnInAsync(db, null, 1m);                      // +10 kg, the line's own unit (bag)
        await using (var db = database.CreateDbContext()) await ReturnInAsync(db, scenario.LooseKgModeId, 5m);   // exactly the remainder
        SalesInvoice second;
        await using (var db = database.CreateDbContext())
            second = await new SalesService(db).CreateSaleAsync(new CreateSaleInput(scenario.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0,
                [new SaleLineInput(scenario.ProductId, scenario.BagModeId, 1m, 600m, 0)]), scenario.UserId);                                    // -10 kg
        await using (var db = database.CreateDbContext()) await new SalesService(db).CancelSaleAsync(second.Id, scenario.UserId);                 // +10 kg

        await using var check = database.CreateDbContext();
        Assert.Equal(50m, (await check.StockItems.SingleAsync()).QuantityOnHand); // everything is back
        await StockInvariants.AssertHoldAsync(check);
        var returns = await check.SalesReturnLines.AsNoTracking().OrderBy(r => r.SalesReturn.CreatedAt).ToListAsync();
        Assert.Equal(new[] { "kg", "bag", "kg" }, returns.Select(r => r.UnitCode));
        Assert.All(returns, r => Assert.Equal(r.BaseQty, r.Qty * r.ConversionFactorToBase));
        Assert.Equal(20m, returns.Sum(r => r.BaseQty));
        var saved = await check.SalesInvoiceLines.AsNoTracking().SingleAsync(l => l.Id == line.Id);
        Assert.Equal(saved.LineTotal, returns.Sum(r => r.LineTotal));
        Assert.Equal(("bag", 600m, false), (saved.UnitCode, saved.ListUnitPrice, saved.PriceOverridden));
        Assert.Equal(20m, await check.SalesInvoiceLineBatchConsumptions.Where(c => c.SalesInvoiceLineId == line.Id).SumAsync(c => c.ReturnedQtyBaseUnits));
        Assert.Equal(10m, await check.StockMovements.Where(m => m.Type == StockMovementType.SaleCancellation).SumAsync(m => m.QuantityBaseUnits));
        Assert.Empty(await check.StockMovements.Where(m => m.Type == StockMovementType.Sale && m.QuantityBaseUnits > 0).ToListAsync());
    }

    /// <summary>The refusal message is built from decimals loaded from numeric(18,3)/numeric(28,10) columns, which carry
    /// the column scale in .NET ("10.0000000000"). The message must read like the design's "Cannot remove 2 bag (20 kg) —
    /// only 5 kg is in stock.", not expose that scale.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_DecreaseInPacksBeyondStockOnPostgres_MessageShowsPlainQuantities()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        StockScenario scenario;
        await using (var db = database.CreateDbContext())
        {
            scenario = await StockScenario.SeedAsync(db);
            await scenario.AdjustAsync(db, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 60m);
        }

        await using var write = database.CreateDbContext();
        var ex = await Assert.ThrowsAsync<Sellora.Domain.Abstractions.DomainValidationException>(() => new StockAdjustmentService(write).CreateAdjustmentAsync(
            new CreateAdjustmentInput(scenario.ProductId, scenario.BagModeId, AdjustmentType.Damage, AdjustmentDirection.Decrease, 2m, null, null), scenario.UserId));

        Assert.Equal("Quantity", ex.Field);
        const string expected = "Cannot remove 2 bag (20 kg) — only 5 kg is in stock.";
        Assert.True(ex.Message == expected, $"Expected: {expected}\nActual:   {ex.Message}");
    }
}
