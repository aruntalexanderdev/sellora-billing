using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 8b multi-location scenario (design section 7, InMemory twin of the Postgres test): purchases into BLR's store A and BLR's
/// warehouse W (and the company warehouse), a sale at A, FIFO per location, returns and cancellations back to the original location,
/// weighted average cost per location. After EVERY step: QOH = Σ movements = Σ balances per (location, product), batch remaining = Σ its
/// balances (dual-write), no negative stock.
/// </summary>
public class MultiLocationScenarioTests
{
    private static async Task<(decimal Remaining, decimal Balance)> BatchAsync(LocationWorld lw, Guid purchaseId, Guid location)
    {
        await using var db = lw.W.Db();
        var line = await db.PurchaseOrderLines.Where(l => l.PurchaseOrderId == purchaseId).Select(l => l.Id).SingleAsync();
        var batch = await db.PurchaseBatches.SingleAsync(b => b.PurchaseOrderLineId == line);
        var balance = await db.BatchLocationBalances.Where(b => b.PurchaseBatchId == batch.Id && b.StockLocationId == location).Select(b => b.RemainingBaseQty).SingleOrDefaultAsync();
        return (batch.RemainingBaseQty, balance);
    }

    [Fact]
    public async Task PurchaseSaleReturnCancel_AcrossStoreAndWarehouse_KeepsEveryLocationReconciledAndFifoPerLocation()
    {
        var lw = await LocationWorld.CreateAsync();
        var (a, wh) = (lw.BlrStore, lw.BlrWarehouse);

        var p1 = await lw.BuyAsync(lw.W.Blr, a, 10m, 10m);
        await lw.AssertInvariantsAsync("buy A 10");
        var p2 = await lw.BuyAsync(lw.W.Blr, a, 20m, 12m);
        await lw.AssertInvariantsAsync("buy A 20");
        var p3 = await lw.BuyAsync(lw.W.Blr, wh, 30m, 15m);
        await lw.AssertInvariantsAsync("buy W 30");
        Assert.Equal((30m, 30m, 100m), (await lw.QohAsync(a), await lw.QohAsync(wh), await lw.QohAsync(lw.MainStore)));

        // Sale at A: FIFO inside A only (p1 then p2); the warehouse batch, although it is stock of the same product, is untouched.
        var sale = await lw.SellAsync(lw.W.Blr, a, 15m);
        await lw.AssertInvariantsAsync("sell 15 at A");
        Assert.Equal(a, sale.StockLocationId);
        Assert.Equal((0m, 0m), await BatchAsync(lw, p1.Id, a));
        Assert.Equal((15m, 15m), await BatchAsync(lw, p2.Id, a));
        Assert.Equal((30m, 30m), await BatchAsync(lw, p3.Id, wh));
        Assert.Equal((15m, 30m, 100m), (await lw.QohAsync(a), await lw.QohAsync(wh), await lw.QohAsync(lw.MainStore)));

        // Availability is per location: A has 15, W has 30 - selling 16 at A is refused although the product has 45 at BLR.
        var before = await CountMovementsAsync(lw);
        await Assert.ThrowsAsync<DomainValidationException>(() => lw.SellAsync(lw.W.Blr, a, 16m));
        Assert.Equal(before, await CountMovementsAsync(lw));

        // Return of 4 restores into the consumed batch(es) at A, stamped with A even though the caller names nothing.
        SalesReturn ret;
        await using (var db = lw.W.Db(lw.W.Blr))
        {
            ret = await ListPricedSales.For(db).CreateReturnAsync(new CreateSaleReturnInput(sale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 4m)]), lw.W.UserId);
        }
        await lw.AssertInvariantsAsync("return 4");
        Assert.Equal(a, ret.StockLocationId);
        Assert.Equal((19m, 30m), (await lw.QohAsync(a), await lw.QohAsync(wh)));

        // A second sale at A, cancelled: back at A, as SaleCancellation movements at A.
        var second = await lw.SellAsync(lw.W.Blr, a, 5m);
        await lw.AssertInvariantsAsync("sell 5 at A");
        await using (var db = lw.W.Db(lw.W.Blr)) await ListPricedSales.For(db).CancelSaleAsync(second.Id, lw.W.UserId);
        await lw.AssertInvariantsAsync("cancel sale at A");
        Assert.Equal((19m, 30m), (await lw.QohAsync(a), await lw.QohAsync(wh)));
        await using (var db = lw.W.Db())
        {
            var cancelled = await db.StockMovements.Where(m => m.Type == StockMovementType.SaleCancellation).ToListAsync();
            Assert.NotEmpty(cancelled);
            Assert.All(cancelled, m => Assert.Equal(a, m.StockLocationId));
        }

        // Purchase return against the warehouse receipt posts at W; A is untouched.
        PurchaseReturn pret;
        await using (var db = lw.W.Db(lw.W.Blr))
        {
            pret = await new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
                new CreatePurchaseReturnInput(p3.Id, BranchWorld.Day, null, [new PurchaseReturnLineInput(p3.Lines.Single().Id, null, 5m)]), lw.W.UserId);
        }
        await lw.AssertInvariantsAsync("purchase return at W");
        Assert.Equal(wh, pret.StockLocationId);
        Assert.Equal((19m, 25m), (await lw.QohAsync(a), await lw.QohAsync(wh)));

        // Cancelling an untouched receipt at W removes exactly it from W.
        var p4 = await lw.BuyAsync(lw.W.Blr, wh, 7m, 20m);
        await lw.AssertInvariantsAsync("buy W 7");
        await using (var db = lw.W.Db(lw.W.Blr)) await new PurchaseService(db, new ExchangeRateService(db)).CancelPurchaseAsync(p4.Id, lw.W.UserId);
        await lw.AssertInvariantsAsync("cancel purchase at W");
        Assert.Equal((19m, 25m), (await lw.QohAsync(a), await lw.QohAsync(wh)));

        // p1 was consumed at A: its receipt can no longer be cancelled, and the refusal writes nothing.
        before = await CountMovementsAsync(lw);
        await using (var db = lw.W.Db(lw.W.Blr)) await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(db, new ExchangeRateService(db)).CancelPurchaseAsync(p1.Id, lw.W.UserId));
        Assert.Equal(before, await CountMovementsAsync(lw));
        await lw.AssertInvariantsAsync("refused cancel");
    }

    [Fact]
    public async Task AverageCost_IsMovingAveragePerLocation_NotMixedAcrossLocations()
    {
        var lw = await LocationWorld.CreateAsync();

        await lw.BuyAsync(lw.W.Blr, lw.BlrStore, 10m, 10m);
        await lw.BuyAsync(lw.W.Blr, lw.BlrStore, 30m, 20m);
        await lw.BuyAsync(lw.W.Blr, lw.BlrWarehouse, 10m, 50m);

        await using var db = lw.W.Db();
        var avg = await db.StockItems.Where(i => i.ProductId == lw.W.ProductId).ToDictionaryAsync(i => i.StockLocationId, i => i.AverageCost);
        Assert.Equal(17.5m, avg[lw.BlrStore]);     // (10x10 + 30x20) / 40
        Assert.Equal(50m, avg[lw.BlrWarehouse]);    // untouched by the store's purchases
        Assert.Equal(10m, avg[lw.MainStore]);       // the seeding purchase
    }

    [Fact]
    public async Task SaleAtOneLocation_DoesNotConsumeAnOlderBatchOfAnotherLocation()
    {
        var lw = await LocationWorld.CreateAsync(); // Main holds the OLDEST batch (the seeding purchase, 100 kg)
        await lw.BuyAsync(lw.W.Blr, lw.BlrStore, 10m, 10m);

        await lw.SellAsync(lw.W.Blr, lw.BlrStore, 6m);

        await lw.AssertInvariantsAsync("fifo per location");
        await using var db = lw.W.Db();
        Assert.Equal(100m, await db.BatchLocationBalances.Where(b => b.StockLocationId == lw.MainStore).SumAsync(b => b.RemainingBaseQty));
        Assert.Equal(4m, await db.BatchLocationBalances.Where(b => b.StockLocationId == lw.BlrStore).SumAsync(b => b.RemainingBaseQty));
    }

    [Fact]
    public async Task ReturnableQuantity_IsTheBalanceAtTheOriginalLocation_WhenAnotherLocationHoldsTheSameProduct()
    {
        var lw = await LocationWorld.CreateAsync();
        var p = await lw.BuyAsync(lw.W.Blr, lw.BlrStore, 10m, 10m);
        await lw.SellAsync(lw.W.Blr, lw.BlrStore, 6m);
        await lw.BuyAsync(lw.W.Blr, lw.BlrWarehouse, 50m, 10m);

        // 6 sold from the batch received at A; returning 5 against it must fail on A's remaining (4), not pass on the 54 the product holds at BLR.
        await using var db = lw.W.Db(lw.W.Blr);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
            new CreatePurchaseReturnInput(p.Id, BranchWorld.Day, null, [new PurchaseReturnLineInput(p.Lines.Single().Id, null, 5m)]), lw.W.UserId));
        Assert.NotNull(ex.Field);
        await lw.AssertInvariantsAsync("refused return");
    }

    private static async Task<int> CountMovementsAsync(LocationWorld lw)
    {
        await using var db = lw.W.Db();
        return await db.StockMovements.CountAsync();
    }
}
