using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Stock;

/// <summary>
/// Phase 8b multi-location scenario on PostgreSQL (design section 7): purchases into BLR's store A, BLR's warehouse W and the company
/// warehouse CW (O2), a sale at A and one from BLR's van (O4), FIFO per location, returns and cancellations back to their original location,
/// availability per location, adjustments. After EVERY step the full invariant set holds: QOH = Σ movements = Σ balances per (location,
/// product), batch remaining = Σ its balances (dual-write), balances belong to their batch's product, line arithmetic, no negative stock.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MultiLocationPostgresTests(PostgresFixture postgres)
{
    private static async Task<int> MovementCountAsync(LocationScenario l)
    {
        await using var db = l.Db();
        return await db.StockMovements.CountAsync();
    }

    [Fact]
    public async Task PurchaseSaleReturnCancel_AcrossStoreWarehousesAndVan_KeepEveryInvariantAfterEveryStep()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        var (a, w, cw, van) = (l.BlrStore, l.BlrWarehouse, l.CompanyWarehouse, l.BlrVan);

        var p1 = await l.BuyAsync(l.Blr, a, 10m, 10m); await l.AssertInvariantsAsync("buy A 10");
        var p2 = await l.BuyAsync(l.Blr, a, 20m, 12m); await l.AssertInvariantsAsync("buy A 20");
        var p3 = await l.BuyAsync(l.Blr, w, 30m, 15m); await l.AssertInvariantsAsync("buy W 30");
        var p5 = await l.BuyAsync(l.Blr, cw, 25m, 18m); await l.AssertInvariantsAsync("buy CW 25 (company-level)");
        await l.AdjustAsync(l.Blr, van, AdjustmentDirection.Increase, 8m); await l.AssertInvariantsAsync("adjust van +8");
        Assert.Equal((30m, 30m, 25m, 8m), (await l.QohAsync(a), await l.QohAsync(w), await l.QohAsync(cw), await l.QohAsync(van)));

        // Sale at A: FIFO inside A only (p1 then p2).
        var sale = await l.SellAsync(l.Blr, a, 15m); await l.AssertInvariantsAsync("sell 15 at A");
        Assert.Equal(a, sale.StockLocationId);
        await using (var db = l.Db())
        {
            var batches = await db.PurchaseBatches.Where(b => b.PurchaseOrderLine!.PurchaseOrderId == p1.Id || b.PurchaseOrderLine!.PurchaseOrderId == p2.Id || b.PurchaseOrderLine!.PurchaseOrderId == p3.Id)
                .Select(b => new { b.PurchaseOrderLine!.PurchaseOrderId, b.RemainingBaseQty }).ToListAsync();
            Assert.Equal((0m, 15m, 30m), (batches.Single(b => b.PurchaseOrderId == p1.Id).RemainingBaseQty, batches.Single(b => b.PurchaseOrderId == p2.Id).RemainingBaseQty, batches.Single(b => b.PurchaseOrderId == p3.Id).RemainingBaseQty));
        }

        // Availability is per location: A holds 15, so 16 is refused although BLR holds 15 + 30 + 8 in other locations; nothing is written.
        var before = await MovementCountAsync(l);
        await Assert.ThrowsAsync<DomainValidationException>(() => l.SellAsync(l.Blr, a, 16m));
        Assert.Equal(before, await MovementCountAsync(l));
        await l.AssertInvariantsAsync("refused oversell");

        // Sale from the van (CanSell, assigned to BLR: no company-level grant needed).
        var vanSale = await l.SellAsync(l.Blr, van, 3m, new FakePermissions()); await l.AssertInvariantsAsync("sell 3 from van");
        Assert.Equal((van, 5m), (vanSale.StockLocationId, await l.QohAsync(van)));

        // Return 4 of the A sale: back into A, stamped with A.
        await using (var db = l.Db(l.Blr))
        {
            var ret = await new SalesService(db).CreateReturnAsync(new CreateSaleReturnInput(sale.Id, StockScenario.ReturnDate, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 4m)]), l.S.UserId);
            Assert.Equal(a, ret.StockLocationId);
        }
        await l.AssertInvariantsAsync("return 4");
        Assert.Equal((19m, 30m), (await l.QohAsync(a), await l.QohAsync(w)));

        // A second sale at A, cancelled: SaleCancellation movements at A.
        var second = await l.SellAsync(l.Blr, a, 5m); await l.AssertInvariantsAsync("sell 5 at A");
        await using (var db = l.Db(l.Blr)) await new SalesService(db).CancelSaleAsync(second.Id, l.S.UserId);
        await l.AssertInvariantsAsync("cancel sale");
        Assert.Equal(19m, await l.QohAsync(a));
        await using (var db = l.Db())
        {
            Assert.All(await db.StockMovements.Where(m => m.Type == StockMovementType.SaleCancellation).ToListAsync(), m => Assert.Equal(a, m.StockLocationId));
        }

        // Purchase return against the W receipt posts at W; cancelling an untouched CW receipt posts at CW.
        await using (var db = l.Db(l.Blr))
        {
            var pret = await new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
                new CreatePurchaseReturnInput(p3.Id, StockScenario.ReturnDate, null, [new PurchaseReturnLineInput(p3.Lines.Single().Id, null, 5m)]), l.S.UserId);
            Assert.Equal(w, pret.StockLocationId);
        }
        await l.AssertInvariantsAsync("purchase return at W");
        await using (var db = l.Db(l.Blr)) await new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CancelPurchaseAsync(p5.Id, l.S.UserId);
        await l.AssertInvariantsAsync("cancel purchase at CW");
        Assert.Equal((19m, 25m, 0m, 5m), (await l.QohAsync(a), await l.QohAsync(w), await l.QohAsync(cw), await l.QohAsync(van)));

        // p1 was consumed at A: its receipt can no longer be cancelled; the refusal writes nothing.
        before = await MovementCountAsync(l);
        await using (var db = l.Db(l.Blr)) await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(db, new ExchangeRateService(db)).CancelPurchaseAsync(p1.Id, l.S.UserId));
        Assert.Equal(before, await MovementCountAsync(l));
        await l.AssertInvariantsAsync("refused cancel");
    }

    [Fact]
    public async Task Sale_DoesNotConsumeAnOlderBatchOfAnotherLocation()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        await l.BuyAsync(l.Blr, l.CompanyWarehouse, 100m, 5m);   // OLDEST batch, at CW
        await l.BuyAsync(l.Blr, l.BlrStore, 10m, 9m);

        await l.SellAsync(l.Blr, l.BlrStore, 6m);

        await l.AssertInvariantsAsync("fifo per location");
        Assert.Equal((100m, 4m), (await l.QohAsync(l.CompanyWarehouse), await l.QohAsync(l.BlrStore)));
        await using var db = l.Db();
        var unitCosts = await db.SalesInvoiceLineBatchConsumptions.Select(c => c.UnitCost).ToListAsync();
        Assert.All(unitCosts, c => Assert.Equal(9m, c)); // the batch bought at A, not the cheaper older one at CW
    }

    [Fact]
    public async Task PurchaseReturn_IsLimitedByTheBalanceAtThePurchasesLocation()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        var p = await l.BuyAsync(l.Blr, l.BlrStore, 10m);
        await l.SellAsync(l.Blr, l.BlrStore, 6m);
        await l.BuyAsync(l.Blr, l.BlrWarehouse, 50m); // the same product is plentiful elsewhere in BLR

        await using var db = l.Db(l.Blr);
        await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
            new CreatePurchaseReturnInput(p.Id, StockScenario.ReturnDate, null, [new PurchaseReturnLineInput(p.Lines.Single().Id, null, 5m)]), l.S.UserId));
        await l.AssertInvariantsAsync("refused return");
    }

    [Fact]
    public async Task AverageCost_IsMovingAveragePerLocation()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        await l.BuyAsync(l.Blr, l.BlrStore, 10m, 10m);
        await l.BuyAsync(l.Blr, l.BlrStore, 30m, 20m);
        await l.BuyAsync(l.Blr, l.CompanyWarehouse, 10m, 50m);

        await using var db = l.Db();
        var avg = await db.StockItems.ToDictionaryAsync(i => i.StockLocationId, i => i.AverageCost);
        Assert.Equal((17.5m, 50m), (avg[l.BlrStore], avg[l.CompanyWarehouse]));
    }

    [Fact]
    public async Task CompanyLevelLocations_WithoutTheCompanyLevelGrant_AreRefusedOnPostgresToo()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var l = await LocationScenario.CreateAsync(database);
        var none = new FakePermissions();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => l.BuyAsync(l.Blr, l.CompanyWarehouse, 5m, 10m, none));
        Assert.Equal("stockLocationId", ex.Field);
        Assert.Equal("stockLocationId", (await Assert.ThrowsAsync<DomainValidationException>(() => l.AdjustAsync(l.Blr, l.CompanyWarehouse, AdjustmentDirection.Increase, 5m, none))).Field);
        Assert.Equal("stockLocationId", (await Assert.ThrowsAsync<DomainValidationException>(() => l.SellAsync(l.Blr, l.BlrWarehouse, 1m))).Field); // warehouses cannot sell
        await l.AssertInvariantsAsync("refusals");
        await using var db = l.Db();
        Assert.Equal(0, await db.StockMovements.CountAsync());
    }
}
