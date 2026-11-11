using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Stock;

/// <summary>
/// Phase 8a engine on PostgreSQL (owner O1 = A: behaviour identical, everything posts at the company's default store). After EVERY writer -
/// purchase, purchase return, purchase cancellation, sale, sale return, sale cancellation, adjustment increase / decrease / opening - the
/// dual-written balances equal the batch totals (V5), QOH = Σ movements = Σ balances per (location, product) (V3/V4), balances belong to
/// their batch's product (V6), and every stock row and document is stamped with the default store. FIFO draws across batches in the same
/// order as before. Each step runs on its own context, like separate API requests.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockLocationEngineTests(PostgresFixture postgres)
{
    private static readonly Guid Store = DefaultTenant.MainStoreLocationId;

    private async Task<(TestDatabase Database, StockScenario Scenario)> StartAsync(decimal? bagPrice = 600m)
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var scenario = await StockScenario.SeedAsync(db);
        if (bagPrice is { } price) await StockScenario.EnsureListPriceAsync(db, scenario.UserId, scenario.BagModeId, price);
        return (database, scenario);
    }

    /// <summary>Every invariant (incl. the per-location and dual-write ones) plus "everything at the default store" after a step.</summary>
    private static async Task AssertEngineAsync(TestDatabase database, string step)
    {
        await using var db = database.CreateDbContext();
        var violations = await StockInvariants.FindViolationsAsync(db);
        Assert.True(violations.Count == 0, $"after {step}: " + string.Join("; ", violations.Select(v => $"{v.Key}: {string.Join(", ", v.Value)}")));
        var outside = await StockInvariants.QueryIdsAsync(db, StockInvariants.NotAtDefaultStoreSql);
        Assert.True(outside.Count == 0, $"after {step}: rows outside the default store: {string.Join(", ", outside)}");
    }

    private sealed record BatchRow(Guid Id, decimal Purchased, decimal Remaining, decimal Balance, Guid Origin, Guid BalanceLocation);

    private static async Task<List<BatchRow>> BatchesAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var rows = await (from b in db.PurchaseBatches.AsNoTracking()
                          join l in db.BatchLocationBalances.AsNoTracking() on b.Id equals l.PurchaseBatchId into ls
                          from l in ls.DefaultIfEmpty()
                          orderby b.CreatedAt, b.BatchSequence
                          select new BatchRow(b.Id, b.PurchasedBaseQty, b.RemainingBaseQty, l == null ? -1m : l.RemainingBaseQty, b.OriginStockLocationId, l == null ? Guid.Empty : l.StockLocationId)).ToListAsync();
        return rows;
    }

    private static async Task<StockItem> ItemAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return await db.StockItems.AsNoTracking().SingleAsync();
    }

    private static async Task<List<StockMovement>> MovementsAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return await db.StockMovements.AsNoTracking().OrderBy(m => m.OccurredAt).ToListAsync();
    }

    private static PurchaseService Purchases(SelloraDbContext db) => new(db, new ExchangeRateService(db));

    // ---- purchase ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreatePurchase_FirstStock_CreatesItemBatchBalanceMovementAndHeaderAtTheDefaultStore()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        PurchaseOrder po;
        await using (var db = database.CreateDbContext()) po = await s.PurchaseBagsAsync(db, 3, 580);

        await AssertEngineAsync(database, "purchase");
        var batch = Assert.Single(await BatchesAsync(database));
        Assert.Equal((30m, 30m, 30m, Store, Store), (batch.Purchased, batch.Remaining, batch.Balance, batch.Origin, batch.BalanceLocation));
        var item = await ItemAsync(database);
        Assert.Equal((Store, 30m, 58m), (item.StockLocationId, item.QuantityOnHand, item.AverageCost));
        Assert.All(await MovementsAsync(database), m => Assert.Equal(Store, m.StockLocationId));
        Assert.Equal(Store, po.StockLocationId);
        await using var check = database.CreateDbContext();
        Assert.Equal(Store, await check.PurchaseOrders.Select(p => p.StockLocationId).SingleAsync());
    }

    // ---- purchase return / cancellation -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CreatePurchaseReturn_ReducesBatchAndBalanceTogetherAndStampsTheReturn()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        PurchaseOrder po;
        await using (var db = database.CreateDbContext()) po = await s.PurchaseBagsAsync(db, 5, 580);
        await using (var db = database.CreateDbContext())
            await Purchases(db).CreateReturnAsync(new CreatePurchaseReturnInput(po.Id, StockScenario.ReturnDate, "damaged", [new PurchaseReturnLineInput(po.Lines.Single().Id, null, 2m)]), s.UserId);

        await AssertEngineAsync(database, "purchase return");
        var batch = Assert.Single(await BatchesAsync(database));
        Assert.Equal((30m, 30m), (batch.Remaining, batch.Balance));
        Assert.Equal(30m, (await ItemAsync(database)).QuantityOnHand);
        await using var check = database.CreateDbContext();
        Assert.Equal(Store, await check.PurchaseReturns.Select(p => p.StockLocationId).SingleAsync());
        Assert.Equal(-20m, await check.StockMovements.Where(m => m.Type == StockMovementType.PurchaseReturn).SumAsync(m => m.QuantityBaseUnits));
    }

    [Fact]
    public async Task CancelPurchase_UntouchedBatch_ZeroesBatchAndBalanceKeepsTheZeroRowAndPostsCancellation()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 3, 580);
        PurchaseOrder second;
        await using (var db = database.CreateDbContext()) second = await s.PurchaseBagsAsync(db, 2, 620);
        await using (var db = database.CreateDbContext()) await Purchases(db).CancelPurchaseAsync(second.Id, s.UserId);

        await AssertEngineAsync(database, "purchase cancellation");
        var batches = await BatchesAsync(database);
        Assert.Equal(new[] { (30m, 30m), (0m, 0m) }, batches.Select(b => (b.Remaining, b.Balance)));
        var item = await ItemAsync(database);
        Assert.Equal((30m, 58m), (item.QuantityOnHand, item.AverageCost));
        Assert.Equal(-20m, (await MovementsAsync(database)).Single(m => m.Type == StockMovementType.PurchaseCancellation).QuantityBaseUnits);
    }

    [Fact]
    public async Task CancelPurchase_BatchPartlyConsumed_IsRefusedAndNothingChanges()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        PurchaseOrder po;
        await using (var db = database.CreateDbContext()) po = await s.PurchaseBagsAsync(db, 3, 580);
        await using (var db = database.CreateDbContext()) await ListPriced(db).CreateSaleAsync(Sale(s, s.LooseKgModeId, 4m, StockScenario.LooseKgPrice), s.UserId);

        await using (var db = database.CreateDbContext())
            await Assert.ThrowsAsync<Sellora.Domain.Abstractions.DomainValidationException>(() => Purchases(db).CancelPurchaseAsync(po.Id, s.UserId));

        await AssertEngineAsync(database, "refused purchase cancellation");
        Assert.Equal((26m, 26m), (await BatchesAsync(database)).Select(b => (b.Remaining, b.Balance)).Single());
    }

    // ---- sale / sale return / sale cancellation ---------------------------------------------------------------------------------

    private static Sellora.Domain.Abstractions.ISalesService ListPriced(SelloraDbContext db) => new SalesService(db);

    private static CreateSaleInput Sale(StockScenario s, Guid unit, decimal qty, decimal price) =>
        new(s.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0, [new SaleLineInput(s.ProductId, unit, qty, price, 0)]);

    [Fact]
    public async Task CreateSale_AcrossTwoBatches_DrawsFifoAndKeepsBalancesWithBatchesAtTheSameLocation()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 3, 580); // batch 1: 30 kg @ 58
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 2, 620); // batch 2: 20 kg @ 62
        SalesInvoice sale;
        await using (var db = database.CreateDbContext()) sale = await s.SellLooseKgAsync(db, 35);

        await AssertEngineAsync(database, "sale");
        var batches = await BatchesAsync(database);
        Assert.Equal(new[] { (30m, 0m, 0m), (20m, 15m, 15m) }, batches.Select(b => (b.Purchased, b.Remaining, b.Balance))); // oldest first, then the next
        Assert.Equal(15m, (await ItemAsync(database)).QuantityOnHand);
        await using var check = database.CreateDbContext();
        Assert.Equal(Store, await check.SalesInvoices.Select(i => i.StockLocationId).SingleAsync());
        var consumptions = await check.SalesInvoiceLineBatchConsumptions.AsNoTracking().Where(c => c.SalesInvoiceLineId == sale.Lines.Single().Id).OrderBy(c => c.PurchaseBatch.CreatedAt).ToListAsync();
        Assert.Equal(new[] { 30m, 5m }, consumptions.Select(c => c.QtyBaseUnits));
        Assert.Equal(new[] { 58m, 62m }, consumptions.Select(c => c.UnitCost));
        Assert.Equal(-35m, (await MovementsAsync(database)).Where(m => m.Type == StockMovementType.Sale).Sum(m => m.QuantityBaseUnits));
    }

    [Fact]
    public async Task CreateSale_MoreThanStock_IsRefusedAndBalancesUntouched()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 1, 580);

        await using (var db = database.CreateDbContext())
            await Assert.ThrowsAsync<Sellora.Domain.Abstractions.DomainValidationException>(() => s.SellLooseKgAsync(db, 10.5m));

        await AssertEngineAsync(database, "refused sale");
        Assert.Equal((10m, 10m), (await BatchesAsync(database)).Select(b => (b.Remaining, b.Balance)).Single());
    }

    [Fact]
    public async Task CreateSaleReturn_RestoresIntoTheConsumedBatchesAndTheirBalancesAndStampsTheReturn()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 3, 580);
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 2, 620);
        SalesInvoice sale;
        await using (var db = database.CreateDbContext()) sale = await s.SellLooseKgAsync(db, 35);
        await using (var db = database.CreateDbContext()) await s.ReturnAsync(db, sale.Id, sale.Lines.Single().Id, 7.5m);

        await AssertEngineAsync(database, "sale return");
        var batches = await BatchesAsync(database);
        Assert.Equal(22.5m, batches.Sum(b => b.Remaining));
        Assert.All(batches, b => Assert.Equal(b.Remaining, b.Balance));
        Assert.All(batches, b => Assert.InRange(b.Remaining, 0m, b.Purchased));
        Assert.Equal(22.5m, (await ItemAsync(database)).QuantityOnHand);
        await using var check = database.CreateDbContext();
        Assert.Equal(Store, await check.SalesReturns.Select(r => r.StockLocationId).SingleAsync());
        Assert.Equal(7.5m, await check.SalesInvoiceLineBatchConsumptions.SumAsync(c => c.ReturnedQtyBaseUnits));
    }

    [Fact]
    public async Task CancelSale_PutsEverythingBackIntoTheSameBatchesAndBalances()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 3, 580);
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 2, 620);
        var before = (await BatchesAsync(database)).Select(b => (b.Remaining, b.Balance)).ToList();
        SalesInvoice sale;
        await using (var db = database.CreateDbContext()) sale = await s.SellLooseKgAsync(db, 35);
        await using (var db = database.CreateDbContext()) await new SalesService(db).CancelSaleAsync(sale.Id, s.UserId);

        await AssertEngineAsync(database, "sale cancellation");
        Assert.Equal(before, (await BatchesAsync(database)).Select(b => (b.Remaining, b.Balance)).ToList());
        Assert.Equal(50m, (await ItemAsync(database)).QuantityOnHand);
        Assert.Equal(35m, (await MovementsAsync(database)).Where(m => m.Type == StockMovementType.SaleCancellation).Sum(m => m.QuantityBaseUnits));
    }

    // ---- adjustments --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAdjustment_OpeningStockIncreaseAndDecreaseAcrossBatches_KeepBalancesInStepAndStampTheHeaders()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        Task AdjustBags(SelloraDbContext db, AdjustmentType t, AdjustmentDirection d, decimal bags, decimal? cost) =>
            new StockAdjustmentService(db).CreateAdjustmentAsync(new CreateAdjustmentInput(s.ProductId, s.BagModeId, t, d, bags, cost, "x"), s.UserId);

        await using (var db = database.CreateDbContext()) await AdjustBags(db, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 2, 575m); // +20 kg
        await AssertEngineAsync(database, "opening stock");
        await using (var db = database.CreateDbContext()) await s.AdjustAsync(db, AdjustmentType.Correction, AdjustmentDirection.Increase, 5m, 60m);   // +5 kg, own batch
        await AssertEngineAsync(database, "adjustment increase");
        await using (var db = database.CreateDbContext()) await s.AdjustAsync(db, AdjustmentType.Damage, AdjustmentDirection.Decrease, 22m);           // -22 kg across both batches
        await AssertEngineAsync(database, "adjustment decrease");

        var batches = await BatchesAsync(database);
        Assert.Equal(new[] { (20m, 0m, 0m), (5m, 3m, 3m) }, batches.Select(b => (b.Purchased, b.Remaining, b.Balance)));
        Assert.All(batches, b => Assert.Equal((Store, Store), (b.Origin, b.BalanceLocation)));
        Assert.Equal(3m, (await ItemAsync(database)).QuantityOnHand);
        await using var check = database.CreateDbContext();
        var adjustments = await check.StockAdjustments.AsNoTracking().OrderBy(a => a.AdjustmentNo).ToListAsync();
        Assert.All(adjustments, a => Assert.Equal(Store, a.StockLocationId));
        Assert.Equal(new[] { (0m, 20m), (20m, 25m), (25m, 3m) }, adjustments.Select(a => (a.BeforeQty, a.AfterQty)));
    }

    [Fact]
    public async Task CreateAdjustment_DecreaseMoreThanStock_IsRefusedAndBalancesUntouched()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 1, 580);

        await using (var db = database.CreateDbContext())
            await Assert.ThrowsAsync<Sellora.Domain.Abstractions.DomainValidationException>(() => s.AdjustAsync(db, AdjustmentType.Loss, AdjustmentDirection.Decrease, 10.001m));

        await AssertEngineAsync(database, "refused adjustment");
        Assert.Equal((10m, 10m), (await BatchesAsync(database)).Select(b => (b.Remaining, b.Balance)).Single());
    }

    // ---- lifecycle walk-through: invariants after EVERY step ---------------------------------------------------------------------

    [Fact]
    public async Task Lifecycle_EveryWriterInSequence_EngineInvariantsHoldAfterEachStep()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        async Task Step(string name, Func<SelloraDbContext, Task> action)
        {
            await using (var db = database.CreateDbContext()) await action(db);
            await AssertEngineAsync(database, name);
        }
        PurchaseOrder p1 = null!, p2 = null!;
        SalesInvoice s1 = null!, s2 = null!;

        await Step("opening stock", db => new StockAdjustmentService(db).CreateAdjustmentAsync(new CreateAdjustmentInput(s.ProductId, s.BagModeId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 2m, 575m, null), s.UserId));
        await Step("purchase 1", async db => p1 = await s.PurchaseBagsAsync(db, 3, 580));
        await Step("purchase 2", async db => p2 = await s.PurchaseBagsAsync(db, 2, 600));
        await Step("sale 1", async db => s1 = await new SalesService(db).CreateSaleAsync(Sale(s, s.BagModeId, 2m, 600m), s.UserId));
        await Step("sale return (kg)", db => new SalesService(db).CreateReturnAsync(
            new CreateSaleReturnInput(s1.Id, StockScenario.ReturnDate, null, [new SaleReturnLineInput(s1.Lines.Single().Id, s.LooseKgModeId, 5m)]), s.UserId));
        await Step("sale 2", async db => s2 = await s.SellLooseKgAsync(db, 12));
        await Step("sale 2 cancellation", db => new SalesService(db).CancelSaleAsync(s2.Id, s.UserId));
        await Step("purchase return", db => Purchases(db).CreateReturnAsync(new CreatePurchaseReturnInput(p1.Id, StockScenario.ReturnDate, null, [new PurchaseReturnLineInput(p1.Lines.Single().Id, null, 1m)]), s.UserId));
        await Step("adjustment decrease", db => s.AdjustAsync(db, AdjustmentType.Loss, AdjustmentDirection.Decrease, 3.5m));
        await Step("adjustment increase", db => s.AdjustAsync(db, AdjustmentType.Correction, AdjustmentDirection.Increase, 1.25m, 55m));
        await Step("purchase 2 cancellation", db => Purchases(db).CancelPurchaseAsync(p2.Id, s.UserId));

        var item = await ItemAsync(database);
        Assert.Equal(Store, item.StockLocationId);
        await using var check = database.CreateDbContext();
        Assert.Equal(Store, await check.StockItems.Select(i => i.StockLocationId).Distinct().SingleAsync());
        Assert.Equal(1, await check.StockMovements.Select(m => m.StockLocationId).Distinct().CountAsync());
    }

    // ---- adjustment resolves its location BEFORE it locks any stock ---------------------------------------------------------------

    [Fact]
    public async Task CreateAdjustment_BranchResolutionFailsWhileTheStockRowIsLocked_FailsWithoutWaiting()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 2, 580); // the StockItems row exists
        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockItems", "ProductId", s.ProductId));

        // An unknown X-Branch-Id: location/branch resolution fails. It must do so before touching the locked StockItems row.
        var writer = await race.StartAsync("bad branch", db => s.AdjustAsync(db, AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 5m), branchId: Guid.NewGuid());

        var blocked = await race.WaitBlockedOrFinishedAsync(writer);
        var outcome = await writer.Outcome;
        await race.ReleaseAsync();
        Assert.False(blocked, "The adjustment waited on the StockItems lock before it resolved its branch/location.");
        Assert.IsType<Sellora.Domain.Abstractions.DomainValidationException>(outcome);
    }

    [Fact]
    public async Task CreateAdjustment_ValidBranchWhileTheStockRowIsLocked_WaitsOnTheStockItemsRow()
    {
        var (database, s) = await StartAsync();
        await using var _ = database;
        await using (var db = database.CreateDbContext()) await s.PurchaseBagsAsync(db, 2, 580);
        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockItems", "ProductId", s.ProductId));

        var writer = await race.StartAsync("adjust", db => s.AdjustAsync(db, AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 5m));
        await race.WaitBlockedAsync(writer, "%FOR UPDATE%", "Positive control for the test above:");
        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, o => Assert.True(o is null, o?.ToString()));
        await AssertEngineAsync(database, "adjustment after the gate");
    }
}
