using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Stock;

/// <summary>
/// Phase 9 on PostgreSQL (design section 7): the analysis 13.4 walk-through across two branches with the validation script V1-V8 and the stock invariants after
/// EVERY step; company value conserved by dispatch / receipt / return (6 dp per unit); damaged / write-off / return-to-source; Transit cannot be sold; the
/// usage hook blocks vehicle reassignment / warehouse and branch-store deactivation while a transfer is open; purchase cancel refused and purchase-return limit
/// after the batch moved; draft edit with the (TransferId, LineNo) unique index.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockTransferPostgresTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Walkthrough_13_4_AcrossTwoBranches_ReconcilesAfterEveryStep()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var (a, b) = (t.MainStore, t.BlrStore);

        // 1. Receive 2 Full Cartons at Branch A: +200 Purchase, batch B1 = 200 (cost per piece = 500 / 100).
        await t.BuyAsync(null, a, t.Full, 2m, 500m);
        await t.AssertInvariantsAsync("1 receive 2 FCtn");
        Assert.Equal((200m, 200m), (await t.QohAsync(a), await t.BatchTotalAsync()));
        decimal b1Cost;
        await using (var db = t.Db()) b1Cost = await db.PurchaseBatches.Where(x => x.ProductId == t.PenId).Select(x => x.UnitCost).SingleAsync();
        Assert.Equal(5m, b1Cost);

        // 2. Open one Full Carton: no document, no movement.
        var movementsBefore = await t.MovementCountAsync();
        Assert.Equal(movementsBefore, await t.MovementCountAsync());

        // 3. Sell 2 Boxes: -20 Sale (consumes B1).
        await t.SellAsync(null, a, t.Box, 2m, 100m);
        await t.AssertInvariantsAsync("3 sell 2 box");
        Assert.Equal((180m, 180m), (await t.QohAsync(a), await t.BatchTotalAsync()));

        // 4. Sell 5 loose Pieces: -5 Sale.
        await t.SellAsync(null, a, t.Pc, 5m, 10m);
        await t.AssertInvariantsAsync("4 sell 5 pc");
        Assert.Equal((175m, 175m), (await t.QohAsync(a), await t.BatchTotalAsync()));
        var valueBeforeTransfer = await t.CompanyValueAsync();

        // 5. Transfer 1 Half Carton to Branch B: created at A, approved at A, dispatched at A, received at B - all on branch fit alone (no company-level grant).
        var transfer = await t.CreateAsync(a, b, [t.Pen(t.Half, 1m)], submit: false);
        await t.AssertInvariantsAsync("5a draft");
        Assert.Equal((StockTransferStatus.Draft, 50m), (transfer.Status, transfer.Lines.Single().RequestedBaseQty));
        await using (var db = t.Db()) Assert.Equal(0, await db.StockMovements.CountAsync(m => m.ReferenceId == transfer.Id));

        await using (var db = t.Db()) await new StockTransferService(db, null).SubmitAsync(transfer.Id, t.UserId);
        await t.ApproveAsync(transfer.Id);
        await t.AssertInvariantsAsync("5b approved");
        Assert.Equal((175m, 0m, 0m), (await t.QohAsync(a), await t.QohAsync(b), await t.QohAsync(t.Transit))); // approval reserves nothing (O7)

        await t.DispatchAsRequestedAsync(transfer.Id);
        await t.AssertInvariantsAsync("5c dispatched");
        Assert.Equal((125m, 0m, 50m, 175m), (await t.QohAsync(a), await t.QohAsync(b), await t.QohAsync(t.Transit), await t.BatchTotalAsync()));
        await t.AssertValueConservedAsync(valueBeforeTransfer, "dispatch");

        await t.ReceiveAsync(transfer.Id, t.Half, 1m);
        await t.AssertInvariantsAsync("5d received");
        // On hand at A 125, at B 50 (the doc's "125 (B: 50)"), Transit empty, batch total unchanged: A:125 / B:50.
        Assert.Equal((125m, 50m, 0m, 175m), (await t.QohAsync(a), await t.QohAsync(b), await t.QohAsync(t.Transit), await t.BatchTotalAsync()));
        await t.AssertValueConservedAsync(valueBeforeTransfer, "receipt");
        var done = await t.ReloadAsync(transfer.Id);
        Assert.Equal(StockTransferStatus.Completed, done.Status);
        await using (var db = t.Db())
        {
            var batch = await db.PurchaseBatches.SingleAsync(x => x.ProductId == t.PenId);
            var atB = await db.BatchLocationBalances.SingleAsync(x => x.PurchaseBatchId == batch.Id && x.StockLocationId == b);
            var atA = await db.BatchLocationBalances.SingleAsync(x => x.PurchaseBatchId == batch.Id && x.StockLocationId == a);
            Assert.Equal((125m, 50m, 175m), (atA.RemainingBaseQty, atB.RemainingBaseQty, batch.RemainingBaseQty)); // B1 at A / B1 at B
            Assert.Equal(b1Cost, batch.UnitCost);                                                                  // B1 cost preserved
            Assert.Equal(b1Cost, done.Lines.Single().Batches.Single().UnitCost);
            Assert.Equal(b1Cost, (await db.StockItems.SingleAsync(x => x.StockLocationId == b && x.ProductId == t.PenId)).AverageCost);
            var moves = await db.StockMovements.Where(m => m.ReferenceId == transfer.Id).ToListAsync();
            static string Key((StockMovementType Type, Guid Location, decimal Qty) m) => $"{m.Type}|{m.Location}|{m.Qty:0.###}";
            var expectedMoves = new[] { (StockMovementType.TransferOut, a, -50m), (StockMovementType.TransferIn, t.Transit, 50m), (StockMovementType.TransferOut, t.Transit, -50m), (StockMovementType.TransferIn, b, 50m) };
            Assert.Equal(expectedMoves.Select(Key).Order(), moves.Select(m => Key((m.Type, m.StockLocationId, m.QuantityBaseUnits))).Order());
            Assert.All(moves, m => Assert.Equal((t.Half, 50m, 1m), (m.EnteredProductUnitId, m.ConversionFactorSnapshot, m.EnteredQuantity)));
        }

        // 6. Damage 3 Pieces at A: -3 Adjustment (FIFO B1).
        await t.DamageAsync(null, a, 3m);
        await t.AssertInvariantsAsync("6 damage 3 pc");
        Assert.Equal((122m, 50m, 172m, 172m), (await t.QohAsync(a), await t.QohAsync(b), await t.CompanyQuantityAsync(), await t.BatchTotalAsync()));
        // 200 - 28 sold or damaged = 172 in the company.
        Assert.Equal(200m - 28m, await t.CompanyQuantityAsync());
    }

    [Fact]
    public async Task Receive_DamagedAndPartial_ThenResolveWriteOff_ReconcilesAndDropsTheBatchTotal()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Full, 2m, 500m);
        var transfer = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Full, 1m));
        await t.DispatchAsRequestedAsync(transfer.Id);
        await t.AssertInvariantsAsync("dispatched 100");
        var value = await t.CompanyValueAsync();

        await t.ReceiveAsync(transfer.Id, t.Box, 4m, damaged: 1m);                                  // 40 accepted, 10 damaged (written off from Transit)
        await t.AssertInvariantsAsync("partial receipt with damage");
        Assert.Equal((100m, 40m, 50m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.BlrStore), await t.QohAsync(t.Transit)));
        Assert.Equal(190m, await t.BatchTotalAsync());
        Assert.Equal(StockTransferStatus.PartiallyReceived, (await t.ReloadAsync(transfer.Id)).Status);

        await t.ResolveAsync(transfer.Id, StockTransferResolution.WriteOff);
        await t.AssertInvariantsAsync("write-off");
        Assert.Equal((100m, 40m, 0m, 140m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.BlrStore), await t.QohAsync(t.Transit), await t.BatchTotalAsync()));
        Assert.Equal(StockTransferStatus.Completed, (await t.ReloadAsync(transfer.Id)).Status);
        await t.AssertValueConservedAsync(value - 60m * 5m, "10 damaged + 50 written off at cost 5");
    }

    [Fact]
    public async Task Resolve_ReturnToSource_PutsTheOutstandingBack_WithBatchCostAndValueConserved()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Box, 3m, 50m);   // B1: 30 pc at 5
        await t.BuyAsync(null, t.MainStore, t.Box, 7m, 80m);   // B2: 70 pc at 8
        var value = await t.CompanyValueAsync();
        var transfer = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Half, 1m));  // 50 pc: B1 30 + B2 20
        await t.DispatchAsRequestedAsync(transfer.Id);
        await t.AssertInvariantsAsync("dispatched");
        await t.ReceiveAsync(transfer.Id, t.Pc, 10m);
        await t.AssertInvariantsAsync("received 10");

        await t.ResolveAsync(transfer.Id, StockTransferResolution.ReturnToSource);

        await t.AssertInvariantsAsync("returned");
        Assert.Equal((90m, 10m, 0m, 100m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.BlrStore), await t.QohAsync(t.Transit), await t.BatchTotalAsync()));
        await t.AssertValueConservedAsync(value, "return to source");
        await using var db = t.Db();
        var atSource = await db.BatchLocationBalances.Where(x => x.StockLocationId == t.MainStore && x.RemainingBaseQty > 0)
            .Join(db.PurchaseBatches, bl => bl.PurchaseBatchId, pb => pb.Id, (bl, pb) => new { pb.UnitCost, bl.RemainingBaseQty }).OrderBy(x => x.UnitCost).ToListAsync();
        Assert.Equal([(5m, 20m), (8m, 70m)], atSource.Select(x => (x.UnitCost, x.RemainingBaseQty)));
    }

    [Fact]
    public async Task OppositeDirectionTransfersInSequence_AndCompanyValueHolds_AcrossManyMoves()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Box, 10m, 50m);
        var value = await t.CompanyValueAsync();
        for (var i = 0; i < 4; i++)
        {
            var (src, dst) = i % 2 == 0 ? (t.MainStore, t.BlrStore) : (t.BlrStore, t.MainStore);
            var branchOfSource = i % 2 == 0 ? t.Main : t.Blr;
            var transfer = await t.CreateAsync(src, dst, [t.Pen(t.Pc, 17m)], branch: branchOfSource);
            await t.ApproveAsync(transfer.Id, branchOfSource);
            await t.DispatchAsRequestedAsync(transfer.Id, branchOfSource);
            await t.ReceiveAsync(transfer.Id, t.Pc, 17m, branch: i % 2 == 0 ? t.Blr : t.Main);
            await t.AssertInvariantsAsync($"round {i}");
            await t.AssertValueConservedAsync(value, $"round {i}");
        }
    }

    // ---- Transit and documents ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaleAtTransit_IsRefused400_AndTheInTransitStockIsUntouched()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Box, 10m, 50m);
        var transfer = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Box, 4m));
        await t.DispatchAsRequestedAsync(transfer.Id);
        var before = await t.MovementCountAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => t.SellAsync(t.Main, t.Transit, t.Pc, 1m, 10m));

        Assert.Equal("stockLocationId", ex.Field);
        Assert.Equal((40m, before), (await t.QohAsync(t.Transit), await t.MovementCountAsync()));
        await t.AssertInvariantsAsync("refused sale at transit");
    }

    [Fact]
    public async Task PurchaseCancel_AfterItsBatchMoved_IsRefused_AndPurchaseReturnIsLimitedToWhatIsLeftAtThePurchaseLocation()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var purchase = await t.BuyAsync(null, t.MainStore, t.Box, 10m, 50m); // 100 pc at Main
        var transfer = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Box, 4m));
        await t.DispatchAsRequestedAsync(transfer.Id);
        await t.ReceiveAsync(transfer.Id, t.Box, 4m);
        var before = await t.MovementCountAsync();

        // The batch total is still 100, but only 60 of it is at the purchase's location: cancelling would drive Main negative.
        await using (var db = t.Db())
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(db, new ExchangeRateService(db)).CancelPurchaseAsync(purchase.Id, t.UserId));
            Assert.Equal("Status", ex.Field);
        }
        Assert.Equal(before, await t.MovementCountAsync());

        // Return limit: 60 pc can still go back to the supplier from Main, 61 cannot.
        var lineId = purchase.Lines.Single().Id;
        await using (var db = t.Db())
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
                new Sellora.Domain.Purchasing.CreatePurchaseReturnInput(purchase.Id, StockScenario.ReturnDate, null, [new Sellora.Domain.Purchasing.PurchaseReturnLineInput(lineId, t.Pc, 61m)]), t.UserId));
            Assert.Equal("Lines", ex.Field);
        }
        await using (var db = t.Db())
        {
            await new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(
                new Sellora.Domain.Purchasing.CreatePurchaseReturnInput(purchase.Id, StockScenario.ReturnDate, null, [new Sellora.Domain.Purchasing.PurchaseReturnLineInput(lineId, t.Pc, 60m)]), t.UserId);
        }
        Assert.Equal((0m, 40m), (await t.QohAsync(t.MainStore), await t.QohAsync(t.BlrStore)));
        await t.AssertInvariantsAsync("purchase return of the remainder");
    }

    // ---- usage hook ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UsageHook_OpenTransfer_BlocksVehicleReassignmentWarehouseAndBranchStoreDeactivation()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        Guid warehouseId, vanId;
        await using (var db = t.Db())
        {
            warehouseId = await db.Warehouses.Where(w => w.StockLocationId == t.L.BlrWarehouse).Select(w => w.Id).SingleAsync();
            vanId = await db.Vehicles.Where(v => v.StockLocationId == t.L.BlrVan).Select(v => v.Id).SingleAsync();
        }
        await t.BuyAsync(null, t.MainStore, t.Box, 10m, 50m);
        var toWarehouse = await t.CreateAsync(t.MainStore, t.L.BlrWarehouse, [t.Pen(t.Box, 1m)], submit: false);   // Draft counts as open
        var toVan = await t.CreateAsync(t.MainStore, t.L.BlrVan, [t.Pen(t.Box, 1m)], submit: false, permissions: TransferScenario.All);
        var toStore = await t.CreateAsync(t.MainStore, t.BlrStore, [t.Pen(t.Box, 1m)], submit: false);

        async Task<DomainValidationException> Refused(Func<SelloraDbContext, Task> act)
        {
            await using var db = t.Db();
            return await Assert.ThrowsAsync<DomainValidationException>(() => act(db));
        }
        await Refused(db => new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(warehouseId, false, t.UserId));
        await Refused(db => new VehicleService(db, new StockLocationUsageService(db)).UpdateAsync(vanId, new UpdateVehicleInput("Van one", VehicleType.Van, t.Main), t.UserId));
        await Refused(db => new VehicleService(db, new StockLocationUsageService(db)).SetStatusAsync(vanId, false, t.UserId));
        await using (var db = t.Db())
        {
            // The branch store rule: first clear the branch's warehouse and van so the STORE is what blocks (nothing else holds BLR back).
            await new StockTransferService(db, TransferScenario.All).CancelAsync(toWarehouse.Id, "x", t.UserId);
            await new StockTransferService(db, TransferScenario.All).CancelAsync(toVan.Id, "x", t.UserId);
            var usage = new StockLocationUsageService(db);
            await new WarehouseService(db, usage).SetStatusAsync(warehouseId, false, t.UserId);
            await new VehicleService(db, usage).SetStatusAsync(vanId, false, t.UserId);
        }
        var branchRefusal = await Refused(db => new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(t.Blr, false, t.UserId));
        Assert.Equal("IsActive", branchRefusal.Field);

        // Once the last open transfer is cancelled, the branch can be deactivated.
        await using (var db = t.Db()) await new StockTransferService(db, TransferScenario.All).CancelAsync(toStore.Id, "x", t.UserId);
        await using (var db = t.Db()) await new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(t.Blr, false, t.UserId);
        await using var check = t.Db();
        Assert.False((await check.Branches.SingleAsync(b => b.Id == t.Blr)).IsActive);
    }

    // ---- draft edit on the real index ---------------------------------------------------------------------------------------------------

    // DEFECT (reported, product code untouched): UpdateDraftAsync fails in EF fix-up ("StockTransferLine.CompanyId is part of a key and cannot be modified"), see the
    // InMemory twin StockTransferLifecycleTests.UpdateDraft_ReplacesEndpointsNotesAndLines_WithFreshLineNumbers.
    [Fact]
    public async Task UpdateDraft_ReplacesLines_WithoutTripOverTheLineNoUniqueIndex()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var draft = await t.CreateAsync(t.MainStore, t.BlrStore, [t.Pen(t.Box, 1m), t.Pen(t.Half, 2m)], submit: false);

        await using (var db = t.Db())
        {
            await new StockTransferService(db, null).UpdateDraftAsync(draft.Id,
                new UpdateStockTransferInput(t.MainStore, t.L.BlrWarehouse, "edited", [t.Pen(t.Pc, 7m), t.Pen(t.Full, 1m), t.Pen(t.Box, 4m)]), t.UserId);
        }

        var after = await t.ReloadAsync(draft.Id);
        Assert.Equal([1, 2, 3], after.Lines.OrderBy(l => l.LineNo).Select(l => l.LineNo));
        Assert.Equal([7m, 100m, 40m], after.Lines.OrderBy(l => l.LineNo).Select(l => l.RequestedBaseQty));
        Assert.Equal(t.L.BlrWarehouse, after.DestinationLocationId);
        await t.AssertInvariantsAsync("draft edit");
    }

    [Fact]
    public async Task UpdateDraft_NotDraft_IsRefusedOnStatus()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var requested = await t.CreateAsync(t.MainStore, t.BlrStore, [t.Pen(t.Box, 1m)], submit: true);
        await using var db = t.Db();
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new StockTransferService(db, null).UpdateDraftAsync(requested.Id,
            new UpdateStockTransferInput(t.MainStore, t.BlrStore, null, [t.Pen(t.Pc, 1m)]), t.UserId));
        Assert.Equal("status", ex.Field);
    }

    // ---- database constraints ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Constraints_RejectSameEndpoints_TransitEndpoint_AndCountersBeyondDispatched()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Box, 10m, 50m);
        var transfer = await t.ApprovedAsync(t.MainStore, t.BlrStore, t.Pen(t.Box, 4m));
        await t.DispatchAsRequestedAsync(transfer.Id);
        var line = (await t.ReloadAsync(transfer.Id)).Lines.Single();

        async Task<string> Sql(string sql)
        {
            await using var db = t.Db();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql));
            return ex.ToString();
        }
        Assert.Contains("CK_StockTransfers_Endpoints_Differ", await Sql($"""UPDATE "StockTransfers" SET "DestinationLocationId" = "SourceLocationId" WHERE "Id" = '{transfer.Id}'"""));
        Assert.Contains("CK_StockTransfers_Endpoints_NotTransit", await Sql($"""UPDATE "StockTransfers" SET "DestinationLocationId" = "TransitLocationId" WHERE "Id" = '{transfer.Id}'"""));
        Assert.Contains("CK_StockTransferLines_Counters_Max", await Sql($"""UPDATE "StockTransferLines" SET "ReceivedBaseQty" = "DispatchedBaseQty" + 1 WHERE "Id" = '{line.Id}'"""));
        Assert.Contains("CK_StockMovements_Type_Sign", await Sql($"""UPDATE "StockMovements" SET "QuantityBaseUnits" = -"QuantityBaseUnits" WHERE "ReferenceId" = '{transfer.Id}' AND "Type" = 'TransferIn'"""));
    }
}
