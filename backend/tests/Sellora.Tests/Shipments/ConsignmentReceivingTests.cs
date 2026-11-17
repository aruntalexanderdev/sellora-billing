using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Shipments;

/// <summary>
/// Phase 05 receiving (FI-D35/D36) on EF InMemory: verification validates and records without moving stock; posting moves ONLY the accepted quantity
/// Transit to the destination, exactly once; damaged / rejected / missing stay in Transit; consignment and shipment status machines; branch visibility.
/// Dispatch of 5 Box (50 pc) Main store -> BLR store unless a test says otherwise. Locks are not exercised here (integration tests).
/// </summary>
public class ConsignmentReceivingTests
{
    private static Task<string?> Field(Func<Task> act) => ShipmentWorld.FieldAsync(act);

    private static async Task<(ShipmentWorld W, Guid ShipmentId, Guid ConsignmentId, Guid LineId, Guid TransferId)> DispatchedAsync(decimal boxes = 5m)
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, t) = await w.ReadyAsync(boxes: boxes);
        var consignment = ready.Consignments.Single();
        await w.DispatchAsync(ready.Id);
        return (w, ready.Id, consignment.Id, consignment.Lines.Single().Id, t.Id);
    }

    private static async Task AssertAsync(ShipmentWorld w, string step)
    {
        await w.AssertReconciledAsync(step);
        await w.T.AssertInvariantsAsync(step);
    }

    // ---- verification -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Record_CreatesAVerifiedReceipt_AndMovesNoStock()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();

        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 3m, damaged: 1m, rejected: 0m, missing: 0m);

        Assert.Equal((ConsignmentReceiptStatus.Verified, 1), (receipt!.Status, receipt.Sequence));
        Assert.Equal(w.UserId, receipt.VerifiedByUserId);
        Assert.Null(receipt.PostedAt);
        var rl = receipt.Lines.Single();
        Assert.Equal((3m, 30m, 1m, 10m), (rl.AcceptedEnteredQty, rl.AcceptedBaseQty, rl.DamagedEnteredQty, rl.DamagedBaseQty));
        Assert.Equal(("box", 10m), (rl.UnitCode, rl.ConversionFactorToBase));

        var line = await w.LineAsync(lineId);
        Assert.Equal((30m, 10m, 0m, 0m, 0m), (line.AcceptedBaseQty, line.DamagedBaseQty, line.RejectedBaseQty, line.MissingBaseQty, line.PostedBaseQty));
        Assert.Equal((0m, 50m), (await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.Transit)));
        var after = await w.ReloadAsync(shipmentId);
        Assert.Equal(ConsignmentStatus.PartiallyReceived, after.Consignments.Single().Status);
        Assert.Equal(ShipmentStatus.Dispatched, after.Status);

        await using var db = w.T.Db();
        Assert.Contains(await db.AuditEvents.Select(e => e.Action).ToListAsync(), a => a == AuditActions.ConsignmentReceiptVerified);
        await AssertAsync(w, "verify");
    }

    [Fact]
    public async Task Record_CumulativeAccountedAboveDispatched_IsRefused_AcrossReceipts()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync(); // 5 Box dispatched

        Assert.Equal("lines[0].acceptedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 6m)));
        Assert.Equal("lines[0].acceptedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 3m, damaged: 2m, rejected: 1m)));

        await w.RecordAsync(consignmentId, lineId, accepted: 3m);
        Assert.Equal("lines[0].acceptedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, missing: 3m, accepted: 0m))); // 3 + 3 > 5
        await w.RecordAsync(consignmentId, lineId, accepted: 0m, damaged: 0m, rejected: 0m, missing: 2m);                              // exactly the rest

        Assert.Equal(2, (await w.ReceiptsAsync(consignmentId)).Count);
        Assert.Equal("lines[0].acceptedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 1m)));              // nothing left
        await AssertAsync(w, "accounted");
    }

    [Fact]
    public async Task Record_InputShape_UnknownDuplicateNegativeAllZeroAndFractional_AreRefusedOnTheirField()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();

        Assert.Equal("lines[0].consignmentLineId", await Field(() => w.RecordAsync(consignmentId, Guid.NewGuid(), accepted: 1m)));
        Assert.Equal("lines[0].acceptedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: -1m)));
        Assert.Equal("lines[0].damagedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 0m, damaged: -1m)));
        Assert.Equal("lines", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 0m)));                  // nothing to record
        Assert.Equal("lines[0].acceptedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 1.5m))); // Box has no decimals

        await using var db = w.T.Db(w.Blr);
        var duplicate = new RecordReceiptInput([new ReceiptLineInput(lineId, 1m, 0m, 0m, 0m), new ReceiptLineInput(lineId, 1m, 0m, 0m, 0m)], null);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new ConsignmentReceivingService(db, ShipmentWorld.Everything()).RecordAsync(consignmentId, duplicate, w.UserId));
        Assert.Equal("lines[1].consignmentLineId", ex.Field);
        Assert.Empty(await w.ReceiptsAsync(consignmentId));
    }

    [Fact]
    public async Task Record_BeforeDispatch_IsRefusedOnStatus_AndAnUnknownConsignmentIsNull()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, _) = await w.ReadyAsync(boxes: 5m);
        var c = ready.Consignments.Single();

        Assert.Equal("status", await Field(() => w.RecordAsync(c.Id, c.Lines.Single().Id, accepted: 1m)));
        Assert.Null(await w.RecordAsync(Guid.NewGuid(), c.Lines.Single().Id, accepted: 1m));
    }

    [Fact]
    public async Task Record_SameKey_ReturnsTheSameReceipt_AndRecordsOnce_AnotherConsignmentWithTheKeyIsRefused()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();

        var first = await w.RecordAsync(consignmentId, lineId, accepted: 2m, key: "k-rec");
        var replay = await w.RecordAsync(consignmentId, lineId, accepted: 2m, key: "k-rec");

        Assert.Equal(first!.Id, replay!.Id);
        Assert.Single(await w.ReceiptsAsync(consignmentId));
        Assert.Equal(20m, (await w.LineAsync(lineId)).AcceptedBaseQty);                                   // counted once

        var (w2, _, c2, l2, _) = await DispatchedAsync();
        await using var db = w.T.Db(w.Blr);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new ConsignmentReceivingService(db, ShipmentWorld.Everything()).RecordAsync(c2, new RecordReceiptInput([new ReceiptLineInput(l2, 1m, 0m, 0m, 0m)], null, "k-rec"), w.UserId));
        Assert.Equal("IdempotencyKey", ex.Field);
        Assert.NotNull(w2);
    }

    // ---- posting ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Post_MovesOnlyTheAcceptedQuantity_DamagedRejectedAndMissingStayInTransit()
    {
        var (w, shipmentId, consignmentId, lineId, transferId) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 2m, damaged: 1m, rejected: 1m, missing: 1m); // 5 Box: everything accounted

        // Verified, fully accounted but not posted: the consignment is not Received yet.
        Assert.Equal(ConsignmentStatus.PartiallyReceived, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status);

        var posted = await w.PostAsync(consignmentId, receipt!.Id);

        Assert.Equal(ConsignmentReceiptStatus.Posted, posted!.Status);
        Assert.NotNull(posted.PostedAt);
        Assert.Equal(w.UserId, posted.PostedByUserId);
        Assert.Equal((20m, 30m, 50m), (await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.Transit), await w.T.QohAsync(w.L.MainStore))); // 30 pc stay in Transit
        var line = await w.LineAsync(lineId);
        Assert.Equal((20m, 20m, 10m, 10m, 10m), (line.PostedBaseQty, line.AcceptedBaseQty, line.DamagedBaseQty, line.RejectedBaseQty, line.MissingBaseQty));

        var row = Assert.Single(await w.BatchRowsAsync());
        Assert.Equal((20m, 0m, 0m, 0m), (row.ReceivedBaseQty, row.DamagedBaseQty, row.WrittenOffBaseQty, row.ReturnedBaseQty)); // not written to the batch DamagedBaseQty
        var request = (await w.T.ReloadAsync(transferId)).Lines.Single();
        Assert.Equal((20m, 0m), (request.ReceivedBaseQty, request.DamagedBaseQty));

        var movements = (await w.MovementsAsync(transferId)).Where(m => m.StockLocationId != w.L.MainStore).ToList();
        Assert.Contains(movements, m => m.StockLocationId == w.L.BlrStore && m.QuantityBaseUnits == 20m && m.Type == StockMovementType.TransferIn);
        Assert.Contains(movements, m => m.StockLocationId == w.L.Transit && m.QuantityBaseUnits == -20m && m.Type == StockMovementType.TransferOut);

        var after = await w.ReloadAsync(shipmentId);
        Assert.Equal(ConsignmentStatus.Received, after.Consignments.Single().Status);
        Assert.Equal(ShipmentStatus.Delivered, after.Status);
        await using var db = w.T.Db();
        Assert.Contains(await db.AuditEvents.Select(e => e.Action).ToListAsync(), a => a == AuditActions.ConsignmentReceiptPosted);
        await AssertAsync(w, "post accepted only");
    }

    [Fact]
    public async Task Post_AVerifiedReceiptWithNoAcceptedQuantity_MovesNothing_ButStampsPosted()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 0m, damaged: 5m);

        var posted = await w.PostAsync(consignmentId, receipt!.Id);

        Assert.Equal(ConsignmentReceiptStatus.Posted, posted!.Status);
        Assert.Equal((0m, 50m), (await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.Transit)));
        await AssertAsync(w, "post nothing");
    }

    [Fact]
    public async Task MultiplePartialReceipts_PostedInAnyOrder_CompleteTheConsignmentOnTheLast()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();
        var r1 = await w.RecordAsync(consignmentId, lineId, accepted: 2m);
        var r2 = await w.RecordAsync(consignmentId, lineId, accepted: 2m, damaged: 1m);
        Assert.Equal(new[] { 1, 2 }, (await w.ReceiptsAsync(consignmentId)).Select(r => r.Sequence).ToArray());

        await w.PostAsync(consignmentId, r2!.Id);                                   // the later receipt first
        Assert.Equal((20m, 30m), (await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.Transit)));
        Assert.Equal(ConsignmentStatus.PartiallyReceived, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status); // r1 still Verified
        await AssertAsync(w, "r2 posted");

        await w.PostAsync(consignmentId, r1!.Id);
        Assert.Equal((40m, 10m), (await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.Transit)));                    // the damaged Box stays in Transit
        var done = await w.ReloadAsync(shipmentId);
        Assert.Equal(ConsignmentStatus.Received, done.Consignments.Single().Status);
        Assert.Equal(ShipmentStatus.Delivered, done.Status);
        Assert.Equal(40m, (await w.LineAsync(lineId)).PostedBaseQty);

        // Nothing is left to account for.
        Assert.Equal("status", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 1m)));
        await AssertAsync(w, "r1 posted");
    }

    [Fact]
    public async Task Post_Twice_MovesOnce_AndTheSameKeyReplaysTheReceipt()
    {
        var (w, _, consignmentId, lineId, transferId) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 3m);

        var first = await w.PostAsync(consignmentId, receipt!.Id, key: "k-post");
        var replay = await w.PostAsync(consignmentId, receipt.Id, key: "k-post");
        var again = await w.PostAsync(consignmentId, receipt.Id);                 // no key: idempotent by state

        Assert.Equal(first!.PostedAt, replay!.PostedAt);
        Assert.Equal(first.PostedAt, again!.PostedAt);
        Assert.Equal((30m, 20m), (await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.Transit)));
        Assert.Equal(30m, (await w.LineAsync(lineId)).PostedBaseQty);
        Assert.Equal(4, (await w.MovementsAsync(transferId)).Count);              // dispatch out/in + post out/in, once
        await AssertAsync(w, "post twice");

        var other = await w.RecordAsync(consignmentId, lineId, accepted: 1m);
        Assert.Equal("IdempotencyKey", await Field(() => w.PostAsync(consignmentId, other!.Id, key: "k-post")));
    }

    [Fact]
    public async Task Post_UnknownReceiptOrAnotherConsignmentsReceipt_IsNull()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 1m);

        Assert.Null(await w.PostAsync(consignmentId, Guid.NewGuid()));
        var (w2, _, otherConsignment, _, _) = await DispatchedAsync();
        Assert.Null(await w2.PostAsync(otherConsignment, receipt!.Id));
    }

    // ---- visibility ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receiving_NeedsTheDestinationToFit_AnotherBranchSeesA404_AndTheDestinationBranchCanReceive()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();

        // The source branch (Main) does not hold the destination: not found, not a 400 that leaks the consignment.
        Assert.Null(await w.RecordAsync(consignmentId, lineId, accepted: 1m, branch: w.Main, permissions: FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Receive")));
        Assert.Null(await w.RecordAsync(consignmentId, lineId, accepted: 1m, branch: w.Main, permissions: LocationWorld.Company()));
        Assert.Empty(await w.ReceiptsAsync(consignmentId));

        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 1m, branch: w.Blr, permissions: FakeScopedPermissions.AtBranch(w.Blr, "StockTransfers.Receive"));
        Assert.NotNull(receipt);
        Assert.Null(await w.PostAsync(consignmentId, receipt!.Id, branch: w.Main, permissions: FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Receive")));
        Assert.NotNull(await w.PostAsync(consignmentId, receipt.Id, branch: w.Blr, permissions: FakeScopedPermissions.AtBranch(w.Blr, "StockTransfers.Receive")));
    }

    // ---- multi-stop shipment ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MultiStopShipment_StaysOpenUntilEveryConsignmentIsReceived()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var t1 = await w.ApprovedPenAsync(3m);                                                  // Main -> BLR store
        var t2 = await w.ApprovedPenAsync(2m, destination: w.L.CompanyWarehouse);               // Main -> company warehouse
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, t1.Lines.Single().Id, w.T.Box, 3m);
        await w.AllocateAsync(s.Id, t2.Lines.Single().Id, w.T.Box, 2m);
        Guid vehicleId;
        await using (var db = w.T.Db()) vehicleId = await db.Vehicles.Where(v => v.StockLocationId == w.L.CentralVan).Select(v => v.Id).SingleAsync();
        var leg = (await w.AddOwnVehicleAsync(s.Id, vehicleId))!.Vehicles.Single().Id;
        var planned = await w.ReloadAsync(s.Id);
        foreach (var c in planned.Consignments)
        {
            await w.AssignAsync(s.Id, c.Id, leg);
            await w.LoadAsync(s.Id, c.Id, c.Lines.Single().Id, c.Lines.Single().EnteredQty);
        }
        await w.DispatchAsync(s.Id);
        var dispatched = await w.ReloadAsync(s.Id);
        Assert.All(dispatched.Consignments, c => Assert.Equal(ConsignmentStatus.InTransit, c.Status));
        var toBlr = dispatched.Consignments.Single(c => c.DestinationLocationId == w.L.BlrStore);
        var toCompany = dispatched.Consignments.Single(c => c.DestinationLocationId == w.L.CompanyWarehouse);
        Assert.Equal(StockTransferStatus.Fulfilled, (await w.T.ReloadAsync(t1.Id)).Status);
        Assert.Equal(StockTransferStatus.Fulfilled, (await w.T.ReloadAsync(t2.Id)).Status);

        var r1 = await w.RecordAsync(toBlr.Id, toBlr.Lines.Single().Id, accepted: 3m);
        await w.PostAsync(toBlr.Id, r1!.Id);
        var mid = await w.ReloadAsync(s.Id);
        Assert.Equal(ConsignmentStatus.Received, mid.Consignments.Single(c => c.Id == toBlr.Id).Status);
        Assert.Equal(ConsignmentStatus.InTransit, mid.Consignments.Single(c => c.Id == toCompany.Id).Status);
        Assert.Equal(ShipmentStatus.PartiallyDelivered, mid.Status);                         // open: the other stop is outstanding
        await AssertAsync(w, "first stop");

        var r2 = await w.RecordAsync(toCompany.Id, toCompany.Lines.Single().Id, accepted: 1m, damaged: 1m);
        await w.PostAsync(toCompany.Id, r2!.Id);
        var done = await w.ReloadAsync(s.Id);
        Assert.Equal(ShipmentStatus.Delivered, done.Status);
        Assert.Equal((30m, 10m, 10m, 50m), (await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.CompanyWarehouse), await w.T.QohAsync(w.L.Transit), await w.T.QohAsync(w.L.MainStore)));
        await AssertAsync(w, "second stop");
    }
}
