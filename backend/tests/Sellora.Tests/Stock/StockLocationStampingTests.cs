using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 8a/8b (design D2/D3/D5): a document posts at the working branch's own store (8a posted everything at the default store) and stamps its header, movements, batches and StockItem with that store's id. After every
/// writer: Σ balances per batch = batch remaining, StockItem QOH = Σ movements = Σ balances per (location, product), one location only.
/// </summary>
public class StockLocationStampingTests
{
    private static readonly Guid Store = DefaultTenant.MainStoreLocationId;

    private static async Task<Guid> BlrStoreAsync(BranchWorld w)
    {
        await using var db = w.Db();
        return await db.StockLocations.Where(l => l.BranchId == w.Blr && l.LocationType == Sellora.Domain.Inventory.StockLocationType.BranchStore).Select(l => l.Id).SingleAsync();
    }

    /// <summary>Per-(location, product) reconciliation and dual-write after a writer: QOH = Σ movements = Σ balances; Σ balances per batch = batch remaining.</summary>
    private static async Task AssertEngineStateAsync(BranchWorld w)
    {
        await using var db = w.Db();
        var batches = await db.PurchaseBatches.AsNoTracking().ToListAsync();
        var balances = await db.BatchLocationBalances.AsNoTracking().ToListAsync();
        var items = await db.StockItems.AsNoTracking().ToListAsync();
        var moves = await db.StockMovements.AsNoTracking().ToListAsync();

        foreach (var batch in batches)
        {
            Assert.Equal(batch.RemainingBaseQty, balances.Where(b => b.PurchaseBatchId == batch.Id).Sum(b => b.RemainingBaseQty));
        }
        foreach (var item in items)
        {
            Assert.Equal(item.QuantityOnHand, moves.Where(m => m.ProductId == item.ProductId && m.StockLocationId == item.StockLocationId).Sum(m => m.QuantityBaseUnits));
            Assert.Equal(item.QuantityOnHand, balances.Where(b => b.ProductId == item.ProductId && b.StockLocationId == item.StockLocationId).Sum(b => b.RemainingBaseQty));
        }
        Assert.All(moves, m => Assert.Contains(items, i => i.StockLocationId == m.StockLocationId && i.ProductId == m.ProductId));
        Assert.All(balances, bal => Assert.Equal(batches.Single(b => b.Id == bal.PurchaseBatchId).ProductId, bal.ProductId));
    }

    private static async Task<decimal> QohAsync(BranchWorld w, Guid location)
    {
        await using var db = w.Db();
        return await db.StockItems.Where(i => i.StockLocationId == location).Select(i => i.QuantityOnHand).SingleAsync();
    }

    [Fact]
    public async Task CreatePurchase_AtMain_PostsAtDefaultStoreAndStampsEverything()
    {
        var w = await BranchWorld.CreateAsync();

        var purchase = await w.PurchaseAsync(w.Main);

        Assert.Equal(Store, purchase.StockLocationId);
        await AssertEngineStateAsync(w);
        Assert.Equal(110m, await QohAsync(w, Store));
        await using var db = w.Db();
        Assert.Equal(2, await db.BatchLocationBalances.CountAsync(b => b.StockLocationId == Store));
    }

    // 8b CHANGED: a non-default branch now posts at ITS OWN store (8a: default store). Expectation updated per design D3.
    [Fact]
    public async Task CreatePurchase_AtNonDefaultBranch_PostsAtTheBranchStoreAndKeepsItsBranch()
    {
        var w = await BranchWorld.CreateAsync();
        await w.SeedBranchStockAsync(w.Blr, 100m);
        var blrStore = await BlrStoreAsync(w);

        var purchase = await w.PurchaseAsync(w.Blr);

        Assert.Equal((w.Blr, blrStore), (purchase.BranchId, purchase.StockLocationId));
        await AssertEngineStateAsync(w);
        Assert.Equal((100m, 110m), (await QohAsync(w, Store), await QohAsync(w, blrStore)));
    }

    [Fact]
    public async Task CreateSale_AtNonDefaultBranch_DrawsTheBranchStoreAndStampsInvoiceAndMovements()
    {
        var w = await BranchWorld.CreateAsync();
        await w.SeedBranchStockAsync(w.Blr, 100m);
        var blrStore = await BlrStoreAsync(w);

        var sale = await w.SaleAsync(w.Blr);

        Assert.Equal((w.Blr, blrStore), (sale.BranchId, sale.StockLocationId));
        await AssertEngineStateAsync(w);
        Assert.Equal((100m, 98m), (await QohAsync(w, Store), await QohAsync(w, blrStore)));
        await using var db = w.Db();
        Assert.Equal(98m, (await db.BatchLocationBalances.Where(b => b.StockLocationId == blrStore).ToListAsync()).Sum(b => b.RemainingBaseQty));
        Assert.Contains(await db.StockMovements.ToListAsync(), m => m.Type == StockMovementType.Sale && m.StockLocationId == blrStore);
    }

    [Fact]
    public async Task CreateSaleReturn_OfANonDefaultBranchSale_RestoresIntoTheBranchStoreAndStampsTheReturn()
    {
        var w = await BranchWorld.CreateAsync();
        await w.SeedBranchStockAsync(w.Blr, 100m);
        var blrStore = await BlrStoreAsync(w);
        var sale = await w.SaleAsync(w.Blr);
        SalesReturn ret;
        await using (var db = w.Db())
        {
            ret = await ListPricedSales(db).CreateReturnAsync(new CreateSaleReturnInput(sale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 1m)]), w.UserId);
        }

        Assert.Equal(blrStore, ret.StockLocationId);
        await AssertEngineStateAsync(w);
        Assert.Equal((100m, 99m), (await QohAsync(w, Store), await QohAsync(w, blrStore)));
    }

    [Fact]
    public async Task CancelSale_AtNonDefaultBranch_RestoresBranchStoreAndPostsSaleCancellationThere()
    {
        var w = await BranchWorld.CreateAsync();
        await w.SeedBranchStockAsync(w.Blr, 100m);
        var blrStore = await BlrStoreAsync(w);
        var sale = await w.SaleAsync(w.Blr);
        await using (var db = w.Db()) await ListPricedSales(db).CancelSaleAsync(sale.Id, w.UserId);

        await AssertEngineStateAsync(w);
        Assert.Equal((100m, 100m), (await QohAsync(w, Store), await QohAsync(w, blrStore)));
        await using var check = w.Db();
        Assert.Contains(await check.StockMovements.ToListAsync(), m => m.Type == StockMovementType.SaleCancellation && m.StockLocationId == blrStore);
    }

    [Fact]
    public async Task CreatePurchaseReturn_AndCancelPurchase_AtNonDefaultBranch_PostAtTheBranchStoreAndKeepBalancesInStep()
    {
        var w = await BranchWorld.CreateAsync();
        await w.SeedBranchStockAsync(w.Blr, 100m);
        var blrStore = await BlrStoreAsync(w);
        var purchase = await w.PurchaseAsync(w.Blr);
        PurchaseReturn ret;
        await using (var db = w.Db())
        {
            ret = await new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
                new CreatePurchaseReturnInput(purchase.Id, BranchWorld.Day, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 4m)]), w.UserId);
        }
        Assert.Equal(blrStore, ret.StockLocationId);
        await AssertEngineStateAsync(w);

        var untouched = await w.PurchaseAsync(w.Blr);
        await using (var db = w.Db()) await new PurchaseService(db, new ExchangeRateService(db)).CancelPurchaseAsync(untouched.Id, w.UserId);

        await AssertEngineStateAsync(w);
        Assert.Equal((100m, 106m), (await QohAsync(w, Store), await QohAsync(w, blrStore))); // 100 + 10 - 4 + 10 - 10 at BLR
        await using var check = w.Db();
        Assert.Contains(await check.BatchLocationBalances.ToListAsync(), b => b.RemainingBaseQty == 0m && b.StockLocationId == blrStore); // the cancelled batch keeps its zero row
    }

    [Fact]
    public async Task CreateAdjustment_AtNonDefaultBranch_IncreaseAndDecreasePostAtTheBranchStore()
    {
        var w = await BranchWorld.CreateAsync();
        await w.SeedBranchStockAsync(w.Blr, 100m);
        var blrStore = await BlrStoreAsync(w);
        StockAdjustment up, down;
        await using (var db = w.Db(w.Blr))
        {
            up = await new StockAdjustmentService(db, new FakeEffectivePermissions()).CreateAdjustmentAsync(
                new CreateAdjustmentInput(w.ProductId, w.ProductUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 5m, 12m, "x"), w.UserId);
        }
        await using (var db = w.Db(w.Blr))
        {
            down = await new StockAdjustmentService(db, new FakeEffectivePermissions()).CreateAdjustmentAsync(
                new CreateAdjustmentInput(w.ProductId, w.ProductUnitId, AdjustmentType.Damage, AdjustmentDirection.Decrease, 103m, null, "x"), w.UserId);
        }

        Assert.Equal((w.Blr, blrStore), (up.BranchId, up.StockLocationId));
        Assert.Equal((blrStore, 105m, 2m), (down.StockLocationId, down.BeforeQty, down.AfterQty));
        await AssertEngineStateAsync(w);
        Assert.Equal((100m, 2m), (await QohAsync(w, Store), await QohAsync(w, blrStore)));
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
