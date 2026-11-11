using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Payments;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Stock;

/// <summary>
/// Phase 10 end to end on PostgreSQL (design section 7 WP3, steps 1-11): purchase at A's store, transfer to A's van V, van sale paid into A's "van cash" account,
/// sales return restocking at V with the refund paid from the van account, damage at V, unload V to the store, cash settlement by account transfer, V reassigned
/// to B and a further return restocking at A's store, a transfer to B's store and a purchase return from B's store (refused for an A-only user, allowed company-level,
/// purchase reference kept), purchase cancel refused, company value conserved. After EVERY step: the stock invariants, the transfer validation V1-V8 and the van
/// validation R1-R5 (each statement must return no rows).
/// </summary>
[Collection(PostgresCollection.Name)]
public class VanOperationsEndToEndTests(PostgresFixture postgres)
{
    [Fact]
    public async Task VanLifecycle_PurchaseTransferVanSaleReturnDamageUnloadSettleReassignAndPurchaseReturnFromAnotherBranch_ReconcilesAfterEveryStep()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var (a, b, aStore, bStore) = (t.Main, t.Blr, t.MainStore, t.BlrStore);
        var (vehicleId, v) = await t.CreateVanAsync("KA01", a);
        var vanCash = await t.CreateCashAccountAsync("Van KA01 cash", a);
        var branchCash = await t.CreateCashAccountAsync("A branch cash", a);
        await t.AssertAllAsync("0 setup");

        // 1. Purchase 100 pieces at A's store (cost 8 per piece).
        var purchase = await t.BuyAsync(null, aStore, t.Pc, 100m, 8m);
        await t.AssertAllAsync("1 purchase 100 at A");
        Assert.Equal((100m, 0m), (await t.QohAsync(aStore), await t.QohAsync(v)));

        // 2. Transfer 40 to the van V (A's): approve / dispatch / receive.
        await t.MoveAsync(aStore, v, 40m, a, a);
        await t.AssertAllAsync("2 transfer 40 to V");
        Assert.Equal((60m, 40m, 0m), (await t.QohAsync(aStore), await t.QohAsync(v), await t.QohAsync(t.Transit)));

        // 3. Van sale of 30 at V (A's INV series), paid in full into the A-branch van cash account.
        var sale = await t.SellPiecesAsync(a, v, 30m, vanCash, payInFull: true);
        await t.AssertAllAsync("3 van sale 30");
        Assert.Equal((v, a), (sale.StockLocationId, sale.BranchId));
        Assert.StartsWith("INV", sale.InvoiceNo);
        Assert.Equal((10m, 60m), (await t.QohAsync(v), await t.QohAsync(aStore)));
        var grand = sale.GrandTotal;
        Assert.Equal(grand, sale.PaidAmount);
        Assert.Equal(grand, await t.AccountBalanceAsync(vanCash));
        await using (var db = t.Db())
        {
            var line = await db.SalesInvoiceLines.AsNoTracking().SingleAsync(l => l.SalesInvoiceId == sale.Id);
            var consumptions = await db.SalesInvoiceLineBatchConsumptions.AsNoTracking().Where(c => c.SalesInvoiceLineId == line.Id).ToListAsync();
            var batchCosts = await db.PurchaseBatches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.UnitCost);
            var cogs = consumptions.Sum(c => c.QtyBaseUnits * batchCosts[c.PurchaseBatchId]);
            Assert.Equal(240m, cogs); // 30 pieces at 8
            Assert.Equal(cogs, line.CostOfGoodsSold);
        }

        // 4. Sales return of 5 restocks at V; the refund is paid from the van account.
        var refundAmount = Math.Round(grand * 5m / 30m, 2);
        var firstReturn = await t.ReturnPiecesAsync(a, sale, 5m, new RefundDetails(refundAmount, PaymentMethod.Cash, vanCash));
        await t.AssertAllAsync("4 return 5 at V");
        Assert.Equal(v, firstReturn.StockLocationId);
        Assert.Equal((15m, 60m), (await t.QohAsync(v), await t.QohAsync(aStore)));
        Assert.Equal(grand - refundAmount, await t.AccountBalanceAsync(vanCash));

        // 5. Damage adjustment of 2 at V.
        await t.DamageAsync(a, v, 2m);
        await t.AssertAllAsync("5 damage 2 at V");
        Assert.Equal(13m, await t.QohAsync(v));

        // 6. Unload the 13 left on V to A's store: V = 0, store = 73.
        await t.MoveAsync(v, aStore, 13m, a, a);
        await t.AssertAllAsync("6 unload 13");
        Assert.Equal((0m, 73m, 0m), (await t.QohAsync(v), await t.QohAsync(aStore), await t.QohAsync(t.Transit)));

        // 7. Settle the van cash to the branch cash account: van account 0; PaidAmount = payments - refunds.
        var cash = await t.AccountBalanceAsync(vanCash);
        await using (var db = t.Db(a)) await new FinancialAccountService(db).TransferAsync(new TransferInput(vanCash, branchCash, cash, VanOps.Day, "end of day"), t.UserId);
        await t.AssertAllAsync("7 settle cash");
        Assert.Equal((0m, cash), (await t.AccountBalanceAsync(vanCash), await t.AccountBalanceAsync(branchCash)));
        await using (var db = t.Db())
        {
            var invoice = await db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == sale.Id);
            var payments = await db.FinancialTransactions.AsNoTracking().Where(x => x.Type == FinancialTransactionType.CustomerPayment && x.Status == FinancialTransactionStatus.Active).SumAsync(x => x.BaseAmount);
            var refunds = await db.FinancialTransactions.AsNoTracking().Where(x => x.Type == FinancialTransactionType.CustomerRefund && x.Status == FinancialTransactionStatus.Active).SumAsync(x => x.BaseAmount);
            Assert.Equal(payments - refunds, invoice.PaidAmount);
            Assert.Equal(grand - refundAmount, invoice.PaidAmount);
        }

        // 8. Reassign V to B (it is empty and has no open transfer), then return 1 more: V is no longer A's, so the goods go to A's store.
        await using (var db = t.Db()) await new VehicleService(db, new StockLocationUsageService(db)).UpdateAsync(vehicleId, new UpdateVehicleInput("Van KA01", VehicleType.Van, b), t.UserId);
        await t.AssertAllAsync("8a reassign V to B");
        var secondReturn = await t.ReturnPiecesAsync(a, sale, 1m);
        await t.AssertAllAsync("8b return 1 after reassignment");
        Assert.Equal(aStore, secondReturn.StockLocationId);
        Assert.Equal((0m, 74m), (await t.QohAsync(v), await t.QohAsync(aStore)));
        await using (var db = t.Db())
        {
            var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.ReferenceId == secondReturn.Id);
            Assert.Equal((StockMovementType.SaleReturn, aStore, 1m), (movement.Type, movement.StockLocationId, movement.QuantityBaseUnits));
        }

        // 9. Transfer 20 from A's store to B's store.
        await t.MoveAsync(aStore, bStore, 20m, a, b);
        await t.AssertAllAsync("9 transfer 20 A to B");
        Assert.Equal((54m, 20m), (await t.QohAsync(aStore), await t.QohAsync(bStore)));

        // 10. Purchase return of 10 from B's store (the purchase belongs to A): refused for an A-only user, allowed company-level; the purchase reference is kept.
        var before = await t.MovementCountAsync();
        var aOnly = new BranchPermissions(a, "Purchase.Return", "Purchase.View");
        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => t.ReturnPurchaseAsync(a, purchase, 10m, bStore, aOnly));
        Assert.Equal("stockLocationId", refused.Field);
        Assert.Equal((before, 20m), (await t.MovementCountAsync(), await t.QohAsync(bStore)));
        await t.AssertAllAsync("10a purchase return refused for an A-only user");

        var purchaseReturn = await t.ReturnPurchaseAsync(a, purchase, 10m, bStore, new FakePermissions("Purchase.Return"));
        await t.AssertAllAsync("10b purchase return from B's store");
        Assert.Equal((bStore, purchase.Id, a), (purchaseReturn.StockLocationId, purchaseReturn.PurchaseOrderId, purchaseReturn.BranchId));
        Assert.Equal((54m, 10m), (await t.QohAsync(aStore), await t.QohAsync(bStore)));

        // 11. The purchase can no longer be cancelled; company value is conserved within 6 dp (one batch at 8 per piece).
        await using (var db = t.Db(a)) await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(db, new ExchangeRateService(db), null).CancelPurchaseAsync(purchase.Id, t.UserId));
        await t.AssertAllAsync("11 purchase cancel refused");
        Assert.Equal(64m, await t.CompanyQuantityAsync()); // 100 - 30 + 6 returned - 2 damaged - 10 returned to the supplier
        await t.AssertValueConservedAsync(64m * 8m, "final");
        Assert.Equal(64m, await t.BatchTotalAsync());
    }

    [Fact]
    public async Task SalesReturnAtAnInactiveStoreOfAStoreSale_IsRefusedAndNothingMoves_ValidationStaysClean()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 50m, 8m);
        var sale = await t.SellPiecesAsync(t.Main, t.MainStore, 10m);
        await using (var db = t.Db()) await db.Database.ExecuteSqlRawAsync("""UPDATE "StockLocations" SET "IsActive" = false WHERE "Id" = {0}""", t.MainStore);
        var before = await t.MovementCountAsync();

        var refused = await Assert.ThrowsAsync<DomainValidationException>(() => t.ReturnPiecesAsync(t.Main, sale, 1m));

        Assert.Equal(("stockLocationId", "The sale's stock location is inactive. Reactivate the location first."), (refused.Field, refused.Message));
        Assert.Equal(before, await t.MovementCountAsync());
        await t.AssertAllAsync("refused return at an inactive store");
    }

    [Fact]
    public async Task SalesReturnFromAnInactiveVan_FallsBackToTheStore_AndCancelNeverFallsBack()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var (vehicleId, v) = await t.CreateVanAsync("KA02", t.Main);
        await t.BuyAsync(null, t.MainStore, t.Pc, 50m, 8m);
        await t.MoveAsync(t.MainStore, v, 20m, t.Main, t.Main);
        var returned = await t.SellPiecesAsync(t.Main, v, 10m);
        var cancelled = await t.SellPiecesAsync(t.Main, v, 10m);
        await t.AssertAllAsync("setup");

        // V is empty (everything sold): it can be deactivated, as an operator would when the van is retired.
        await using (var db = t.Db()) await new VehicleService(db, new StockLocationUsageService(db)).SetStatusAsync(vehicleId, false, "test", t.UserId);

        var ret = await t.ReturnPiecesAsync(t.Main, returned, 4m);

        Assert.Equal(t.MainStore, ret.StockLocationId);
        Assert.Equal((0m, 34m), (await t.QohAsync(v), await t.QohAsync(t.MainStore)));
        await t.AssertAllAsync("fallback to the store");

        var before = await t.MovementCountAsync();
        await using (var db = t.Db(t.Main))
        {
            var refused = await Assert.ThrowsAsync<DomainValidationException>(() => new SalesService(db, null).CancelSaleAsync(cancelled.Id, t.UserId));
            Assert.Equal("stockLocationId", refused.Field);
        }
        Assert.Equal((before, 0m), (await t.MovementCountAsync(), await t.QohAsync(v)));
        await using (var db = t.Db()) Assert.Equal(SalesStatus.Completed, (await db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == cancelled.Id)).Status);
        await t.AssertAllAsync("cancel refused, no fallback");
    }
}
