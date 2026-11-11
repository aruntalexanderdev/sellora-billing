using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 10 (design D1, D2, D6), InMemory: where a sales return / cancellation puts goods back (invoice location while it is active and still the
/// invoice branch's, else the branch store for a vehicle, else 400; a cancellation never falls back), where a purchase return issues from
/// (requested location, fit rules, per-location balance) and idempotent replay of both returns.
/// </summary>
public class ReturnLocationRulesTests
{
    private const string Inactive = "The sale's stock location is inactive. Reactivate the location first.";
    private const string Reassigned = "The sale's vehicle has been reassigned to another branch. Reassign it back first, or record a sales return instead.";
    private static readonly IEffectivePermissionService CentralSales = LocationWorld.Company("Sales.Create");

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Hand-seeds 20 kg at the location and sells <paramref name="qty"/> from it as BLR (the van is BLR's, the central van needs the grant).</summary>
    private static async Task<SalesInvoice> SaleAtAsync(LocationWorld lw, Guid location, decimal qty = 5m, IEffectivePermissionService? permissions = null)
    {
        await using (var db = lw.W.Db()) await StockSeed.AddAsync(db, location, lw.W.ProductId, 20m, 10m, lw.W.UserId);
        return await lw.SellAsync(lw.W.Blr, location, qty, permissions);
    }

    private static async Task<SalesReturn> ReturnAsync(LocationWorld lw, SalesInvoice sale, decimal qty, string? key = null)
    {
        await using var db = lw.W.Db(lw.W.Blr);
        return await ListPricedSales.For(db).CreateReturnAsync(
            new CreateSaleReturnInput(sale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, qty)], IdempotencyKey: key), lw.W.UserId);
    }

    private static async Task CancelAsync(LocationWorld lw, SalesInvoice sale)
    {
        await using var db = lw.W.Db(lw.W.Blr);
        await ListPricedSales.For(db).CancelSaleAsync(sale.Id, lw.W.UserId);
    }

    private static async Task DeactivateLocationAsync(LocationWorld lw, Guid location)
    {
        await using var db = lw.W.Db();
        (await db.StockLocations.SingleAsync(l => l.Id == location)).IsActive = false;
        await db.SaveChangesAsync();
    }

    private static async Task DeactivateVehicleAsync(LocationWorld lw, Guid location)
    {
        await using var db = lw.W.Db();
        (await db.StockLocations.SingleAsync(l => l.Id == location)).IsActive = false;
        (await db.Vehicles.SingleAsync(v => v.StockLocationId == location)).IsActive = false;
        await db.SaveChangesAsync();
    }

    private static async Task ReassignVehicleAsync(LocationWorld lw, Guid location, Guid? branch)
    {
        await using var db = lw.W.Db();
        (await db.Vehicles.SingleAsync(v => v.StockLocationId == location)).AssignedBranchId = branch;
        await db.SaveChangesAsync();
    }

    private static async Task AssertRejectedAsync(Func<Task> act, string field, string? message = null)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        Assert.Equal(field, ex.Field);
        if (message is not null) Assert.Equal(message, ex.Message);
    }

    private static async Task<int> MovementCountAsync(LocationWorld lw)
    {
        await using var db = lw.W.Db();
        return await db.StockMovements.CountAsync();
    }

    // ---- D1: sales return ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateReturnAsync_InvoiceAtActiveVan_RestocksTheVanAndStampsIt()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);

        var ret = await ReturnAsync(lw, sale, 2m);

        Assert.Equal(lw.BlrVan, ret.StockLocationId);
        Assert.Equal((17m, 0m), (await lw.QohAsync(lw.BlrVan), await lw.QohAsync(lw.BlrStore)));
        await lw.AssertInvariantsAsync("return at the van");
        await using var db = lw.W.Db();
        var movement = await db.StockMovements.SingleAsync(m => m.Type == StockMovementType.SaleReturn);
        Assert.Equal((lw.BlrVan, 2m, ret.Id), (movement.StockLocationId, movement.QuantityBaseUnits, movement.ReferenceId));
    }

    [Fact]
    public async Task CreateReturnAsync_InvoiceAtInactiveStore_Is400OnStockLocationIdAndPostsNothing()
    {
        var lw = await LocationWorld.CreateAsync();
        await lw.W.SeedBranchStockAsync(lw.W.Blr, 20m);
        var sale = await lw.SellAsync(lw.W.Blr, lw.BlrStore, 5m);
        await DeactivateLocationAsync(lw, lw.BlrStore);
        var before = await MovementCountAsync(lw);

        await AssertRejectedAsync(() => ReturnAsync(lw, sale, 1m), "stockLocationId", Inactive);

        Assert.Equal(before, await MovementCountAsync(lw));
        Assert.Equal(15m, await lw.QohAsync(lw.BlrStore));
        await lw.AssertInvariantsAsync("refused return at an inactive store");
    }

    [Fact]
    public async Task CreateReturnAsync_InvoiceAtInactiveVan_FallsBackToTheInvoiceBranchStoreIntoTheConsumedBatches()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);
        await DeactivateVehicleAsync(lw, lw.BlrVan);

        var ret = await ReturnAsync(lw, sale, 3m);

        Assert.Equal(lw.BlrStore, ret.StockLocationId);
        Assert.Equal((15m, 3m), (await lw.QohAsync(lw.BlrVan), await lw.QohAsync(lw.BlrStore)));
        await lw.AssertInvariantsAsync("fallback to the store");
        await using var db = lw.W.Db();
        var movement = await db.StockMovements.SingleAsync(m => m.Type == StockMovementType.SaleReturn);
        Assert.Equal(lw.BlrStore, movement.StockLocationId);
        // The goods go back into the very batch the sale consumed (the van's seeded batch), now with a balance at the store.
        var consumption = await db.SalesInvoiceLineBatchConsumptions.SingleAsync();
        Assert.Equal((5m, 3m), (consumption.QtyBaseUnits, consumption.ReturnedQtyBaseUnits));
        var balance = await db.BatchLocationBalances.SingleAsync(b => b.PurchaseBatchId == consumption.PurchaseBatchId && b.StockLocationId == lw.BlrStore);
        Assert.Equal(3m, balance.RemainingBaseQty);
    }

    [Fact]
    public async Task CreateReturnAsync_VanReassignedToAnotherBranch_FallsBackToTheInvoiceBranchStoreNotTheNewBranch()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);
        await ReassignVehicleAsync(lw, lw.BlrVan, lw.W.Main);

        var ret = await ReturnAsync(lw, sale, 1m);

        Assert.Equal(lw.BlrStore, ret.StockLocationId);
        Assert.Equal((15m, 1m, 100m), (await lw.QohAsync(lw.BlrVan), await lw.QohAsync(lw.BlrStore), await lw.QohAsync(lw.MainStore)));
        await lw.AssertInvariantsAsync("fallback after reassignment");
    }

    [Fact]
    public async Task CreateReturnAsync_VanBecameCentral_StaysUsable()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);
        await ReassignVehicleAsync(lw, lw.BlrVan, null);

        var ret = await ReturnAsync(lw, sale, 1m);

        Assert.Equal(lw.BlrVan, ret.StockLocationId);
        await lw.AssertInvariantsAsync("central van");
    }

    [Fact]
    public async Task CreateReturnAsync_InvoiceAtCentralVehicle_RestocksTheCentralVehicle()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.CentralVan, 5m, CentralSales);

        var ret = await ReturnAsync(lw, sale, 2m);

        Assert.Equal(lw.CentralVan, ret.StockLocationId);
        Assert.Equal(17m, await lw.QohAsync(lw.CentralVan));
        await lw.AssertInvariantsAsync("central van return");
    }

    [Fact]
    public async Task CreateReturnAsync_InactiveVanAndInactiveStore_Is400()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);
        await DeactivateVehicleAsync(lw, lw.BlrVan);
        await DeactivateLocationAsync(lw, lw.BlrStore);

        await AssertRejectedAsync(() => ReturnAsync(lw, sale, 1m), "stockLocationId", Inactive);

        Assert.Equal(15m, await lw.QohAsync(lw.BlrVan));
        await lw.AssertInvariantsAsync("refused: nothing active");
    }

    [Fact]
    public async Task CreateReturnAsync_PartialReturnsAtDifferentLocations_EachStampsItsOwnLocationAndLimitsStayPerLine()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);

        var first = await ReturnAsync(lw, sale, 2m);
        await DeactivateVehicleAsync(lw, lw.BlrVan);
        var second = await ReturnAsync(lw, sale, 3m);

        Assert.Equal((lw.BlrVan, lw.BlrStore), (first.StockLocationId, second.StockLocationId));
        await AssertRejectedAsync(() => ReturnAsync(lw, sale, 1m), "Lines");
        await lw.AssertInvariantsAsync("two returns, two locations");
    }

    // ---- D1: cancellation never falls back ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelSaleAsync_InvoiceAtActiveVan_RestoresTheVanWithSaleCancellationMovements()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);

        await CancelAsync(lw, sale);

        Assert.Equal(20m, await lw.QohAsync(lw.BlrVan));
        await lw.AssertInvariantsAsync("cancel at the van");
        await using var db = lw.W.Db();
        Assert.All(await db.StockMovements.Where(m => m.Type == StockMovementType.SaleCancellation).ToListAsync(), m => Assert.Equal(lw.BlrVan, m.StockLocationId));
    }

    [Fact]
    public async Task CancelSaleAsync_VanInactive_Is400AndNeverFallsBackToTheStore()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);
        await DeactivateVehicleAsync(lw, lw.BlrVan);
        var before = await MovementCountAsync(lw);

        await AssertRejectedAsync(() => CancelAsync(lw, sale), "stockLocationId", Inactive);

        Assert.Equal(before, await MovementCountAsync(lw));
        Assert.Equal((15m, 0m), (await lw.QohAsync(lw.BlrVan), await lw.QohAsync(lw.BlrStore)));
        await using var db = lw.W.Db();
        Assert.Equal(SalesStatus.Completed, (await db.SalesInvoices.SingleAsync(i => i.Id == sale.Id)).Status);
    }

    [Fact]
    public async Task CancelSaleAsync_VanReassignedToAnotherBranch_Is400AndNeverFallsBack()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);
        await ReassignVehicleAsync(lw, lw.BlrVan, lw.W.Main);

        await AssertRejectedAsync(() => CancelAsync(lw, sale), "stockLocationId", Reassigned);

        Assert.Equal((15m, 0m), (await lw.QohAsync(lw.BlrVan), await lw.QohAsync(lw.BlrStore)));
        await lw.AssertInvariantsAsync("refused cancel after reassignment");
    }

    [Fact]
    public async Task CreateReturnAsync_IdempotencyKeyReusedForAnotherSale_Is400AndPostsNothing()
    {
        var lw = await LocationWorld.CreateAsync();
        var first = await SaleAtAsync(lw, lw.BlrVan);
        var second = await SaleAtAsync(lw, lw.BlrVan);
        await ReturnAsync(lw, first, 1m, key: "reuse-me");
        var qoh = await lw.QohAsync(lw.BlrVan);

        await AssertRejectedAsync(() => ReturnAsync(lw, second, 1m, key: "reuse-me"), "IdempotencyKey",
            "This Idempotency-Key was already used for a return against a different sale. Use a new key.");
        Assert.Equal(qoh, await lw.QohAsync(lw.BlrVan));
    }

    [Fact]
    public async Task CancelSaleAsync_InvoiceAtInactiveStore_Is400()
    {
        var lw = await LocationWorld.CreateAsync();
        await lw.W.SeedBranchStockAsync(lw.W.Blr, 20m);
        var sale = await lw.SellAsync(lw.W.Blr, lw.BlrStore, 5m);
        await DeactivateLocationAsync(lw, lw.BlrStore);

        await AssertRejectedAsync(() => CancelAsync(lw, sale), "stockLocationId", Inactive);
    }

    // ---- D2: purchase return source -------------------------------------------------------------------------------------------------------------

    private static IEffectivePermissionService BranchGrant(LocationWorld lw) => FakeScopedPermissions.AtBranch(lw.W.Blr, "Purchase.Return");
    private static IEffectivePermissionService CompanyGrant() => LocationWorld.Company("Purchase.Return");

    private static async Task<PurchaseReturn> PurchaseReturnAsync(
        LocationWorld lw, PurchaseOrder purchase, decimal qty, Guid? location, IEffectivePermissionService? permissions,
        IReadOnlySet<Guid>? allowed = null, string? key = null, RefundDetails? refund = null)
    {
        await using var db = lw.W.Db(lw.W.Blr, allowed);
        return await new PurchaseService(db, new ExchangeRateService(db), permissions).CreateReturnAsync(
            new CreatePurchaseReturnInput(purchase.Id, BranchWorld.Day, "back", [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, qty)], refund, location, key), lw.W.UserId);
    }

    private static async Task MoveAsync(TransferWorld tw, Guid source, Guid destination, decimal qty)
    {
        var line = TransferWorld.Line(tw.L.W.ProductId, tw.L.W.ProductUnitId, qty);
        var transfer = await tw.ApprovedAsync(source, destination, line);
        await tw.DispatchAsRequestedAsync(transfer.Id);
        var lineId = (await tw.ReloadAsync(transfer.Id)).Lines.Single().Id;
        await tw.ReceiveAsync(transfer.Id, lineId, tw.L.W.ProductUnitId, qty);
    }

    [Fact]
    public async Task CreateReturnAsync_PurchaseReturnWithoutLocation_IssuesFromThePurchaseLocation()
    {
        var lw = await LocationWorld.CreateAsync();
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);

        var ret = await PurchaseReturnAsync(lw, purchase, 4m, null, null);

        Assert.Equal(lw.BlrStore, ret.StockLocationId);
        Assert.Equal(6m, await lw.QohAsync(lw.BlrStore));
        await lw.AssertInvariantsAsync("default source");
    }

    [Fact]
    public async Task CreateReturnAsync_PurchaseReceivedIntoOwnWarehouse_DefaultsToTheWarehouse()
    {
        var lw = await LocationWorld.CreateAsync();
        var purchase = await lw.BuyAsync(lw.W.Blr, lw.BlrWarehouse, 10m, 10m, LocationWorld.Company("Purchase.Create"));

        var ret = await PurchaseReturnAsync(lw, purchase, 4m, null, BranchGrant(lw));

        Assert.Equal(lw.BlrWarehouse, ret.StockLocationId);
        Assert.Equal(6m, await lw.QohAsync(lw.BlrWarehouse));
        await lw.AssertInvariantsAsync("warehouse source");
    }

    [Fact]
    public async Task CreateReturnAsync_ExplicitOwnWarehouseHoldingTheBatch_IssuesThereAndKeepsThePurchaseReference()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.BlrWarehouse, 4m);

        var ret = await PurchaseReturnAsync(lw, purchase, 3m, lw.BlrWarehouse, BranchGrant(lw));

        Assert.Equal((lw.BlrWarehouse, purchase.Id), (ret.StockLocationId, ret.PurchaseOrderId));
        Assert.Equal((6m, 1m), (await lw.QohAsync(lw.BlrStore), await lw.QohAsync(lw.BlrWarehouse)));
        await tw.AssertInvariantsAsync("explicit own warehouse");
        await using var db = lw.W.Db();
        var movement = await db.StockMovements.SingleAsync(m => m.Type == StockMovementType.PurchaseReturn);
        Assert.Equal((lw.BlrWarehouse, -3m, ret.Id), (movement.StockLocationId, movement.QuantityBaseUnits, movement.ReferenceId));
    }

    [Fact]
    public async Task CreateReturnAsync_TransitLocation_Is400()
    {
        var lw = await LocationWorld.CreateAsync();
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);

        await AssertRejectedAsync(() => PurchaseReturnAsync(lw, purchase, 1m, lw.Transit, CompanyGrant()), "stockLocationId");
        await lw.AssertInvariantsAsync("transit refused");
    }

    [Fact]
    public async Task CreateReturnAsync_InactiveLocation_Is400()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.BlrWarehouse, 4m);
        await DeactivateLocationAsync(lw, lw.BlrWarehouse);

        await AssertRejectedAsync(() => PurchaseReturnAsync(lw, purchase, 1m, lw.BlrWarehouse, BranchGrant(lw)), "stockLocationId");
        Assert.Equal(4m, await lw.QohAsync(lw.BlrWarehouse));
    }

    [Fact]
    public async Task CreateReturnAsync_InactivePurchaseLocationWithoutExplicitLocation_Is400()
    {
        var lw = await LocationWorld.CreateAsync();
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await DeactivateLocationAsync(lw, lw.BlrStore);

        await AssertRejectedAsync(() => PurchaseReturnAsync(lw, purchase, 1m, null, null), "stockLocationId");
    }

    [Fact]
    public async Task CreateReturnAsync_AnotherBranchsLocationWithBranchScopedGrant_Is400()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.MainStore, 4m);

        await AssertRejectedAsync(() => PurchaseReturnAsync(lw, purchase, 2m, lw.MainStore, BranchGrant(lw)), "stockLocationId");
        await AssertRejectedAsync(() => PurchaseReturnAsync(lw, purchase, 2m, lw.MainStore, null), "stockLocationId");
        Assert.Equal(104m, await lw.QohAsync(lw.MainStore));
    }

    [Fact]
    public async Task CreateReturnAsync_AnotherBranchsLocationWithCompanyGrantAndAccess_IssuesThereAndKeepsReferenceAndPurchaseBranchMoney()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.MainStore, 4m);

        var ret = await PurchaseReturnAsync(lw, purchase, 3m, lw.MainStore, CompanyGrant());

        Assert.Equal((lw.MainStore, purchase.Id, lw.W.Blr), (ret.StockLocationId, ret.PurchaseOrderId, ret.BranchId));
        Assert.Equal((6m, 101m), (await lw.QohAsync(lw.BlrStore), await lw.QohAsync(lw.MainStore)));
        await tw.AssertInvariantsAsync("company-level return from another branch");
    }

    [Fact]
    public async Task CreateReturnAsync_CompanyGrantButRestrictedContextWithoutAccessToThatBranch_Is400()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.MainStore, 4m);

        await AssertRejectedAsync(
            () => PurchaseReturnAsync(lw, purchase, 2m, lw.MainStore, CompanyGrant(), new HashSet<Guid> { lw.W.Blr }), "stockLocationId");
        Assert.Equal(104m, await lw.QohAsync(lw.MainStore));
    }

    [Fact]
    public async Task CreateReturnAsync_CompanyWarehouse_NeedsTheCompanyLevelGrant()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.CompanyWarehouse, 4m);

        await AssertRejectedAsync(() => PurchaseReturnAsync(lw, purchase, 2m, lw.CompanyWarehouse, BranchGrant(lw)), "stockLocationId");
        var ret = await PurchaseReturnAsync(lw, purchase, 2m, lw.CompanyWarehouse, CompanyGrant());

        Assert.Equal(lw.CompanyWarehouse, ret.StockLocationId);
        Assert.Equal(2m, await lw.QohAsync(lw.CompanyWarehouse));
        await tw.AssertInvariantsAsync("company warehouse");
    }

    [Fact]
    public async Task CreateReturnAsync_AssignedVehicleHoldingTheBatch_IssuesFromTheVan()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.BlrVan, 4m);

        var ret = await PurchaseReturnAsync(lw, purchase, 4m, lw.BlrVan, BranchGrant(lw));

        Assert.Equal(lw.BlrVan, ret.StockLocationId);
        Assert.Equal(0m, await lw.QohAsync(lw.BlrVan));
        await tw.AssertInvariantsAsync("return from the van");
    }

    [Fact]
    public async Task CreateReturnAsync_MoreThanTheBatchHoldsAtTheChosenLocation_Is400AndPostsNothing()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.BlrWarehouse, 4m);
        var before = await MovementCountAsync(lw);

        // 10 were bought and 6 sit in the store, but only 4 of this batch are at the warehouse.
        await AssertRejectedAsync(() => PurchaseReturnAsync(lw, purchase, 5m, lw.BlrWarehouse, BranchGrant(lw)), "Lines");

        Assert.Equal(before, await MovementCountAsync(lw));
        Assert.Equal((6m, 4m), (await lw.QohAsync(lw.BlrStore), await lw.QohAsync(lw.BlrWarehouse)));
        await tw.AssertInvariantsAsync("over balance at the chosen location");
    }

    [Fact]
    public async Task CreateReturnAsync_ReturnFromAnotherLocation_HasTheSameCreditAndTaxAsAReturnAtThePurchaseLocation()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var p1 = await lw.BuyAsync(lw.W.Blr, null, 10m, 10m);
        var p2 = await lw.BuyAsync(lw.W.Blr, null, 10m, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.MainStore, 4m); // FIFO: the first purchase's batch moves

        var elsewhere = await PurchaseReturnAsync(lw, p1, 4m, lw.MainStore, CompanyGrant());
        var sameLocation = await PurchaseReturnAsync(lw, p2, 4m, null, CompanyGrant());

        Assert.Equal((sameLocation.TaxableTotal, sameLocation.TaxTotal, sameLocation.Total), (elsewhere.TaxableTotal, elsewhere.TaxTotal, elsewhere.Total));
        Assert.True(elsewhere.Total > 0m);
        await using var db = lw.W.Db();
        var (a, b) = (await db.PurchaseOrders.SingleAsync(p => p.Id == p1.Id), await db.PurchaseOrders.SingleAsync(p => p.Id == p2.Id));
        Assert.Equal((b.ReturnedAmount, b.OriginalReturnedAmount, b.NetTotal), (a.ReturnedAmount, a.OriginalReturnedAmount, a.NetTotal));
        Assert.Equal((lw.W.Blr, lw.W.Blr), (elsewhere.BranchId, sameLocation.BranchId));
        await tw.AssertInvariantsAsync("credit parity");
    }

    // ---- D6: idempotent replay -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateReturnAsync_SaleReturnReplayedWithTheSameKey_ReturnsTheSameDocumentAndDoesNotRestockTwice()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);

        var first = await ReturnAsync(lw, sale, 2m, "key-1");
        var replay = await ReturnAsync(lw, sale, 2m, "key-1");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(17m, await lw.QohAsync(lw.BlrVan));
        await using var db = lw.W.Db();
        Assert.Single(await db.SalesReturns.ToListAsync());
        Assert.Equal(2m, (await db.SalesInvoiceLineBatchConsumptions.SingleAsync()).ReturnedQtyBaseUnits);
        await lw.AssertInvariantsAsync("sale return replay");
    }

    [Fact]
    public async Task CreateReturnAsync_SaleReturnWithAnotherKey_IsANewDocument()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);

        var first = await ReturnAsync(lw, sale, 1m, "key-1");
        var second = await ReturnAsync(lw, sale, 1m, "key-2");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(17m, await lw.QohAsync(lw.BlrVan)); // 20 - 5 sold + 1 + 1
    }

    [Fact]
    public async Task CreateReturnAsync_SaleReturnWithoutKey_IsNeverReplayed()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);

        await ReturnAsync(lw, sale, 1m);
        await ReturnAsync(lw, sale, 1m);

        Assert.Equal(17m, await lw.QohAsync(lw.BlrVan));
    }

    [Fact]
    public async Task CreateReturnAsync_PurchaseReturnKeyReusedForAnotherPurchase_Is400AndIssuesNothing()
    {
        var lw = await LocationWorld.CreateAsync();
        var first = await lw.BuyAsync(lw.W.Blr, null, 10m);
        var second = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await PurchaseReturnAsync(lw, first, 4m, null, null, key: "pkey-reuse");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => PurchaseReturnAsync(lw, second, 4m, null, null, key: "pkey-reuse"));

        Assert.Equal("IdempotencyKey", ex.Field);
        Assert.Equal(16m, await lw.QohAsync(lw.BlrStore));
        await lw.AssertInvariantsAsync("purchase return key reuse");
    }

    [Fact]
    public async Task CreateReturnAsync_PurchaseReturnReplayedWithTheSameKey_ReturnsTheSameDocumentAndIssuesOnce()
    {
        var lw = await LocationWorld.CreateAsync();
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);

        var first = await PurchaseReturnAsync(lw, purchase, 4m, null, null, key: "pkey-1");
        var replay = await PurchaseReturnAsync(lw, purchase, 4m, null, null, key: "pkey-1");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(6m, await lw.QohAsync(lw.BlrStore));
        await using var db = lw.W.Db();
        Assert.Single(await db.PurchaseReturns.ToListAsync());
        Assert.Equal(first.Total, (await db.PurchaseOrders.SingleAsync(p => p.Id == purchase.Id)).ReturnedAmount);
        await lw.AssertInvariantsAsync("purchase return replay");
    }

    [Fact]
    public async Task CreateReturnAsync_SameKeyUsedForASaleReturnAndAPurchaseReturn_DoesNotCollideAcrossScopes()
    {
        var lw = await LocationWorld.CreateAsync();
        var sale = await SaleAtAsync(lw, lw.BlrVan);
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);

        var sr = await ReturnAsync(lw, sale, 1m, "shared");
        var pr = await PurchaseReturnAsync(lw, purchase, 1m, null, null, key: "shared");

        Assert.NotEqual(sr.Id, pr.Id);
        Assert.Equal(9m, await lw.QohAsync(lw.BlrStore));
        await lw.AssertInvariantsAsync("scoped keys");
    }
}
