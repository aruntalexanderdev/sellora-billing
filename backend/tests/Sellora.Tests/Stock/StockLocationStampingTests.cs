using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 8a behaviour identity (owner O1 = A, design D2/D5): every stock-changing document posts at the company's default store - also a
/// document of a non-default branch - and stamps its header, movements, batches and StockItem with that store's id. After every
/// writer: Σ balances per batch = batch remaining, StockItem QOH = Σ movements = Σ balances per (location, product), one location only.
/// </summary>
public class StockLocationStampingTests
{
    private static readonly Guid Store = DefaultTenant.MainStoreLocationId;

    private static async Task AssertEngineStateAsync(BranchWorld w)
    {
        await using var db = w.Db();
        var batches = await db.PurchaseBatches.AsNoTracking().ToListAsync();
        var balances = await db.BatchLocationBalances.AsNoTracking().ToListAsync();
        var items = await db.StockItems.AsNoTracking().ToListAsync();
        var moves = await db.StockMovements.AsNoTracking().ToListAsync();

        Assert.All(batches, b => Assert.Equal(Store, b.OriginStockLocationId));
        Assert.All(balances, b => Assert.Equal(Store, b.StockLocationId));
        Assert.All(items, i => Assert.Equal(Store, i.StockLocationId));
        Assert.All(moves, m => Assert.Equal(Store, m.StockLocationId));
        foreach (var batch in batches)
        {
            Assert.Equal(batch.RemainingBaseQty, balances.Where(b => b.PurchaseBatchId == batch.Id).Sum(b => b.RemainingBaseQty));
        }
        foreach (var item in items)
        {
            Assert.Equal(item.QuantityOnHand, moves.Where(m => m.ProductId == item.ProductId).Sum(m => m.QuantityBaseUnits));
            Assert.Equal(item.QuantityOnHand, balances.Where(b => b.ProductId == item.ProductId).Sum(b => b.RemainingBaseQty));
        }
        Assert.All(await db.PurchaseOrders.AsNoTracking().ToListAsync(), x => Assert.Equal(Store, x.StockLocationId));
        Assert.All(await db.PurchaseReturns.AsNoTracking().ToListAsync(), x => Assert.Equal(Store, x.StockLocationId));
        Assert.All(await db.SalesInvoices.AsNoTracking().ToListAsync(), x => Assert.Equal(Store, x.StockLocationId));
        Assert.All(await db.SalesReturns.AsNoTracking().ToListAsync(), x => Assert.Equal(Store, x.StockLocationId));
        Assert.All(await db.StockAdjustments.AsNoTracking().ToListAsync(), x => Assert.Equal(Store, x.StockLocationId));
    }

    [Fact]
    public async Task CreatePurchase_AtMain_PostsAtDefaultStoreAndStampsEverything()
    {
        var w = await BranchWorld.CreateAsync();

        var purchase = await w.PurchaseAsync(w.Main);

        Assert.Equal(Store, purchase.StockLocationId);
        await AssertEngineStateAsync(w);
        await using var db = w.Db();
        Assert.Equal(110m, (await db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(2, await db.BatchLocationBalances.CountAsync());
    }

    [Fact]
    public async Task CreatePurchase_AtNonDefaultBranch_StillPostsAtTheDefaultStoreAndKeepsItsBranch()
    {
        var w = await BranchWorld.CreateAsync();

        var purchase = await w.PurchaseAsync(w.Blr);

        Assert.Equal((w.Blr, Store), (purchase.BranchId, purchase.StockLocationId));
        await AssertEngineStateAsync(w);
        await using var db = w.Db();
        Assert.Single(await db.StockItems.ToListAsync()); // no StockItem at BLR's own store
    }

    [Fact]
    public async Task CreateSale_AtNonDefaultBranch_DrawsTheDefaultStoreAndStampsInvoiceAndMovements()
    {
        var w = await BranchWorld.CreateAsync();

        var sale = await w.SaleAsync(w.Blr);

        Assert.Equal((w.Blr, Store), (sale.BranchId, sale.StockLocationId));
        await AssertEngineStateAsync(w);
        await using var db = w.Db();
        Assert.Equal(98m, (await db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(98m, (await db.BatchLocationBalances.SingleAsync()).RemainingBaseQty);
    }

    [Fact]
    public async Task CreateSaleReturn_OfANonDefaultBranchSale_RestoresIntoTheConsumedBatchAndStampsTheReturn()
    {
        var w = await BranchWorld.CreateAsync();
        var sale = await w.SaleAsync(w.Blr);
        SalesReturn ret;
        await using (var db = w.Db())
        {
            ret = await ListPricedSales(db).CreateReturnAsync(new CreateSaleReturnInput(sale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 1m)]), w.UserId);
        }

        Assert.Equal(Store, ret.StockLocationId);
        await AssertEngineStateAsync(w);
        await using var check = w.Db();
        Assert.Equal(99m, (await check.BatchLocationBalances.SingleAsync()).RemainingBaseQty);
    }

    [Fact]
    public async Task CancelSale_RestoresBalancesAndPostsSaleCancellationAtTheDefaultStore()
    {
        var w = await BranchWorld.CreateAsync();
        var sale = await w.SaleAsync(w.Blr);
        await using (var db = w.Db()) await ListPricedSales(db).CancelSaleAsync(sale.Id, w.UserId);

        await AssertEngineStateAsync(w);
        await using var check = w.Db();
        Assert.Equal(100m, (await check.BatchLocationBalances.SingleAsync()).RemainingBaseQty);
        Assert.Contains(await check.StockMovements.ToListAsync(), m => m.Type == StockMovementType.SaleCancellation && m.StockLocationId == Store);
    }

    [Fact]
    public async Task CreatePurchaseReturn_AndCancelPurchase_PostAtTheDefaultStoreAndKeepBalancesInStep()
    {
        var w = await BranchWorld.CreateAsync();
        var purchase = await w.PurchaseAsync(w.Blr);
        PurchaseReturn ret;
        await using (var db = w.Db())
        {
            ret = await new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
                new CreatePurchaseReturnInput(purchase.Id, BranchWorld.Day, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 4m)]), w.UserId);
        }
        Assert.Equal(Store, ret.StockLocationId);
        await AssertEngineStateAsync(w);

        var untouched = await w.PurchaseAsync(w.Blr);
        await using (var db = w.Db()) await new PurchaseService(db, new ExchangeRateService(db)).CancelPurchaseAsync(untouched.Id, w.UserId);

        await AssertEngineStateAsync(w);
        await using var check = w.Db();
        Assert.Equal(106m, (await check.StockItems.SingleAsync()).QuantityOnHand); // 100 + 10 - 4 + 10 - 10
        Assert.Contains(await check.BatchLocationBalances.ToListAsync(), b => b.RemainingBaseQty == 0m); // the cancelled batch keeps its zero row
    }

    [Fact]
    public async Task CreateAdjustment_AtNonDefaultBranch_IncreaseAndDecreasePostAtTheDefaultStore()
    {
        var w = await BranchWorld.CreateAsync();
        StockAdjustment up, down;
        await using (var db = w.Db(w.Blr))
        {
            up = await new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(w.ProductId, w.ProductUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 5m, 12m, "x"), w.UserId);
        }
        await using (var db = w.Db(w.Blr))
        {
            down = await new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(w.ProductId, w.ProductUnitId, AdjustmentType.Damage, AdjustmentDirection.Decrease, 103m, null, "x"), w.UserId);
        }

        Assert.Equal((w.Blr, Store), (up.BranchId, up.StockLocationId));
        Assert.Equal((Store, 105m, 2m), (down.StockLocationId, down.BeforeQty, down.AfterQty));
        await AssertEngineStateAsync(w);
        await using var check = w.Db();
        Assert.Equal(2m, (await check.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateAdjustment_OpeningStockForAFirstProduct_CreatesTheStockItemAndBalanceAtTheDefaultStore()
    {
        var w = await BranchWorld.CreateAsync();
        Guid productId, unitId;
        await using (var db = w.Db())
        {
            var kg = await db.Units.SingleAsync();
            var tax = await db.Taxes.SingleAsync();
            var product = new Product { Id = Guid.NewGuid(), Sku = "NEW", Name = "New", BaseUnitId = kg.Id, BaseUnit = kg, TaxId = tax.Id };
            var unit = Sellora.Tests.TestData.TestProductUnits.Base(product, "Loose kg");
            db.Add(product);
            await db.SaveChangesAsync();
            (productId, unitId) = (product.Id, unit.Id);
        }
        await using (var db = w.Db())
        {
            await new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(productId, unitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 7m, 3m, null), w.UserId);
        }

        await AssertEngineStateAsync(w);
        await using var check = w.Db();
        var item = await check.StockItems.SingleAsync(i => i.ProductId == productId);
        Assert.Equal((Store, 7m, 3m), (item.StockLocationId, item.QuantityOnHand, item.AverageCost));
        var balance = await check.BatchLocationBalances.SingleAsync(b => b.ProductId == productId);
        Assert.Equal((Store, 7m), (balance.StockLocationId, balance.RemainingBaseQty));
    }

    private static Sellora.Domain.Abstractions.ISalesService ListPricedSales(SelloraDbContext db) => Sellora.Tests.TestData.ListPricedSales.For(db);
}
