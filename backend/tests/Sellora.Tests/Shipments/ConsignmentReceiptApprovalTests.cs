using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Shipments;

/// <summary>
/// Phase 06 (FI-D39..D41) on EF InMemory: receipt policy snapshot, direct vs supervisor flow, separation of duties, reject, dispositions (write-off / return /
/// accept), over-disposition, posted-only quantity, Reconciled / Completed transitions and idempotency. 5 Box (50 pc) Main store -> BLR store dispatched. Locks are
/// not exercised here (integration tests); transit reconciliation is asserted after every step through the world invariants.
/// </summary>
public class ConsignmentReceiptApprovalTests
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

    private static async Task<(decimal Main, decimal Blr, decimal Transit)> QohAsync(ShipmentWorld w) =>
        (await w.T.QohAsync(w.L.MainStore), await w.T.QohAsync(w.L.BlrStore), await w.T.QohAsync(w.L.Transit));

    /// <summary>Records and posts accepted 2 / damaged 1 / rejected 1 / missing 1 Box (5 Box dispatched): Transit keeps 30 pc, BLR holds 20.</summary>
    private static async Task<(ShipmentWorld W, Guid ShipmentId, Guid ConsignmentId, Guid LineId, Guid TransferId)> PostedWithDiscrepanciesAsync()
    {
        var d = await DispatchedAsync();
        var receipt = await d.W.RecordAsync(d.ConsignmentId, d.LineId, accepted: 2m, damaged: 1m, rejected: 1m, missing: 1m);
        await d.W.PostAsync(d.ConsignmentId, receipt!.Id);
        return d;
    }

    // ---- policy snapshot and flows ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Record_UnderDirect_IsVerified_AndSnapshotsThePolicy()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        await w.SetPolicyAsync(ReceiptPostingMode.Direct, separation: true);

        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 2m);

        Assert.Equal((ConsignmentReceiptStatus.Verified, ReceiptPostingMode.Direct, true), (receipt!.Status, receipt.PolicyMode, receipt.RequireSeparation));
    }

    [Fact]
    public async Task Record_UnderSupervisorApproval_IsPendingApproval_AndNoStockMoves()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();
        await w.SetPolicyAsync(ReceiptPostingMode.SupervisorApproval, separation: false);

        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 5m);

        Assert.Equal((ConsignmentReceiptStatus.PendingApproval, ReceiptPostingMode.SupervisorApproval, false), (receipt!.Status, receipt.PolicyMode, receipt.RequireSeparation));
        Assert.Equal((50m, 0m, 50m), await QohAsync(w));
        Assert.Equal(ConsignmentStatus.PartiallyReceived, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status); // accounted but not posted
        await AssertAsync(w, "pending");
    }

    [Fact]
    public async Task Supervisor_PostIsRefused_ApproveNeedsTheApproveCode_AndPostsExactlyOnce()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();
        await w.SetPolicyAsync(ReceiptPostingMode.SupervisorApproval, separation: false);
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 4m, damaged: 1m);

        Assert.Equal("status", await Field(() => w.PostAsync(consignmentId, receipt!.Id)));                          // legacy post refused (400)
        Assert.Null(await w.ApproveAsync(consignmentId, receipt!.Id, permissions: ShipmentWorld.ReceiverOnly()));      // a Receive holder is not an approver: 404
        Assert.Equal((50m, 0m, 50m), await QohAsync(w));

        var approved = await w.ApproveAsync(consignmentId, receipt.Id, permissions: ShipmentWorld.ApproverOnly());
        Assert.Equal(ConsignmentReceiptStatus.Posted, approved!.Status);
        Assert.Equal(w.UserId, approved.PostedByUserId);
        Assert.Equal((50m, 40m, 10m), await QohAsync(w));
        Assert.Equal(ConsignmentStatus.Received, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status);   // discrepancy still open

        var again = await w.ApproveAsync(consignmentId, receipt.Id, permissions: ShipmentWorld.ApproverOnly());       // exactly once: a no-op
        Assert.Equal(approved.Id, again!.Id);
        Assert.Equal((50m, 40m, 10m), await QohAsync(w));

        await using var db = w.T.Db();
        var actions = await db.AuditEvents.Select(e => e.Action).ToListAsync();
        Assert.Single(actions, a => a == AuditActions.ConsignmentReceiptApproved);
        Assert.DoesNotContain(AuditActions.ConsignmentReceiptPosted, actions);
        await AssertAsync(w, "approved");
    }

    [Fact]
    public async Task Approve_OfADirectReceipt_IsRefused_AndAnUnknownReceiptIsNull()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 5m);

        Assert.Equal("status", await Field(() => w.ApproveAsync(consignmentId, receipt!.Id)));
        Assert.Null(await w.ApproveAsync(consignmentId, Guid.NewGuid()));
        Assert.Null(await w.ApproveAsync(Guid.NewGuid(), receipt!.Id));
    }

    [Fact]
    public async Task Direct_PostStillNeedsReceive_AtTheDestination()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 5m);

        Assert.Null(await w.PostAsync(consignmentId, receipt!.Id, permissions: ShipmentWorld.ApproverOnly()));
        Assert.Equal((50m, 0m, 50m), await QohAsync(w));
        Assert.Equal(ConsignmentReceiptStatus.Posted, (await w.PostAsync(consignmentId, receipt.Id, permissions: ShipmentWorld.ReceiverOnly()))!.Status);
    }

    [Fact]
    public async Task SeparationOfDuties_TheRecorderCannotPost_AnotherUserCan()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        await w.SetPolicyAsync(ReceiptPostingMode.Direct, separation: true);
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 5m);

        Assert.Equal("receiptId", await Field(() => w.PostAsync(consignmentId, receipt!.Id)));                         // same user
        Assert.Equal((50m, 0m, 50m), await QohAsync(w));

        var other = Guid.NewGuid();
        var posted = await w.PostAsync(consignmentId, receipt!.Id, user: other);
        Assert.Equal((ConsignmentReceiptStatus.Posted, other), (posted!.Status, posted.PostedByUserId));
        Assert.Equal((50m, 50m, 0m), await QohAsync(w));
        await AssertAsync(w, "separation direct");
    }

    [Fact]
    public async Task SeparationOfDuties_TheRecorderCannotApprove_UnderSupervisorApproval()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        await w.SetPolicyAsync(ReceiptPostingMode.SupervisorApproval, separation: true);
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 5m);

        Assert.Equal("receiptId", await Field(() => w.ApproveAsync(consignmentId, receipt!.Id)));
        Assert.Equal(ConsignmentReceiptStatus.PendingApproval, (await w.ReceiptsAsync(consignmentId)).Single().Status);

        var approver = Guid.NewGuid();
        var approved = await w.ApproveAsync(consignmentId, receipt!.Id, user: approver);
        Assert.Equal(approver, approved!.PostedByUserId);
        Assert.Equal(w.UserId, approved.VerifiedByUserId);
        await AssertAsync(w, "separation supervisor");
    }

    [Fact]
    public async Task Snapshot_ASettingChangedMidFlight_NeverAltersAnExistingReceipt()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var directReceipt = await w.RecordAsync(consignmentId, lineId, accepted: 2m);                       // Direct, no separation
        await w.SetPolicyAsync(ReceiptPostingMode.SupervisorApproval, separation: true);
        var supervisedReceipt = await w.RecordAsync(consignmentId, lineId, accepted: 3m);                   // Supervisor, separation
        await w.SetPolicyAsync(ReceiptPostingMode.Direct, separation: false);                               // flipped back

        // The first receipt still posts directly by its own recorder (snapshot: Direct / no separation) ...
        Assert.Equal(ConsignmentReceiptStatus.Posted, (await w.PostAsync(consignmentId, directReceipt!.Id))!.Status);
        // ... the second still needs approval by someone else, although the company is Direct now.
        Assert.Equal("status", await Field(() => w.PostAsync(consignmentId, supervisedReceipt!.Id)));
        Assert.Equal("receiptId", await Field(() => w.ApproveAsync(consignmentId, supervisedReceipt!.Id)));
        Assert.Equal(ConsignmentReceiptStatus.Posted, (await w.ApproveAsync(consignmentId, supervisedReceipt!.Id, user: Guid.NewGuid()))!.Status);
        Assert.Equal((50m, 50m, 0m), await QohAsync(w));
        await AssertAsync(w, "snapshot");
    }

    // ---- reject -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reject_FreesTheQuantity_SoACorrectedReceiptCanBeRecorded_AndIsIdempotent()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();
        var wrong = await w.RecordAsync(consignmentId, lineId, accepted: 5m);
        Assert.Equal("lines[0].acceptedQuantity", await Field(() => w.RecordAsync(consignmentId, lineId, accepted: 5m)));   // everything is accounted by the wrong receipt

        var rejected = await w.RejectAsync(consignmentId, wrong!.Id, "counted wrongly");

        Assert.Equal(ConsignmentReceiptStatus.RejectedForCorrection, rejected!.Status);
        Assert.Equal(("counted wrongly", w.UserId), (rejected.RejectionReason, rejected.RejectedByUserId));
        Assert.NotNull(rejected.RejectedAt);
        var line = await w.LineAsync(lineId);
        Assert.Equal((0m, 0m, 0m, 0m, 0m), (line.AcceptedBaseQty, line.DamagedBaseQty, line.RejectedBaseQty, line.MissingBaseQty, line.PostedBaseQty));
        Assert.Equal(ConsignmentStatus.InTransit, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status);
        Assert.Equal((50m, 0m, 50m), await QohAsync(w));

        var again = await w.RejectAsync(consignmentId, wrong.Id, "counted wrongly");                       // repeat: a no-op (counters not decremented twice)
        Assert.Equal(rejected.Id, again!.Id);
        Assert.Equal(0m, (await w.LineAsync(lineId)).AcceptedBaseQty);

        var corrected = await w.RecordAsync(consignmentId, lineId, accepted: 4m, damaged: 1m);             // the freed quantity is recordable again, sequence continues
        Assert.Equal(2, corrected!.Sequence);
        await w.PostAsync(consignmentId, corrected.Id);
        Assert.Equal(ConsignmentStatus.Received, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status);           // the rejected receipt does not block completion
        Assert.Equal((50m, 40m, 10m), await QohAsync(w));

        await using var db = w.T.Db();
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.ConsignmentReceiptRejected);
        Assert.Equal("counted wrongly", audit.Reason);
        await AssertAsync(w, "rejected then corrected");
    }

    [Fact]
    public async Task Reject_APostedReceipt_ANoReason_AndAnotherKeyTarget_AreRefused()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var r1 = await w.RecordAsync(consignmentId, lineId, accepted: 2m);
        var r2 = await w.RecordAsync(consignmentId, lineId, accepted: 1m);
        await w.PostAsync(consignmentId, r1!.Id);

        Assert.Equal("status", await Field(() => w.RejectAsync(consignmentId, r1.Id)));                    // posted receipts are immutable
        Assert.Equal("reason", await Field(() => w.RejectAsync(consignmentId, r2!.Id, reason: "  ")));
        Assert.Equal("reason", await Field(() => w.RejectAsync(consignmentId, r2!.Id, reason: new string('x', 501))));
        Assert.Null(await w.RejectAsync(consignmentId, Guid.NewGuid()));

        await w.RejectAsync(consignmentId, r2!.Id, key: "k-rej");
        Assert.Equal("IdempotencyKey", await Field(() => w.RejectAsync(consignmentId, r1.Id, key: "k-rej")));
        Assert.Equal(ConsignmentReceiptStatus.RejectedForCorrection, (await w.RejectAsync(consignmentId, r2.Id, key: "k-rej"))!.Status); // replay
        await AssertAsync(w, "reject refusals");
    }

    [Fact]
    public async Task Reject_NeedsReceiveUnderDirect_AndApproveReceiptUnderSupervisor()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var direct = await w.RecordAsync(consignmentId, lineId, accepted: 1m);
        Assert.Null(await w.RejectAsync(consignmentId, direct!.Id, permissions: ShipmentWorld.ApproverOnly()));
        Assert.Equal(ConsignmentReceiptStatus.Verified, (await w.ReceiptsAsync(consignmentId)).Single().Status);
        await w.RejectAsync(consignmentId, direct.Id, permissions: ShipmentWorld.ReceiverOnly());

        await w.SetPolicyAsync(ReceiptPostingMode.SupervisorApproval, separation: false);
        var pending = await w.RecordAsync(consignmentId, lineId, accepted: 1m);
        Assert.Null(await w.RejectAsync(consignmentId, pending!.Id, permissions: ShipmentWorld.ReceiverOnly()));
        Assert.Equal(ConsignmentReceiptStatus.PendingApproval, (await w.ReceiptsAsync(consignmentId)).Last().Status);
        Assert.Equal(ConsignmentReceiptStatus.RejectedForCorrection, (await w.RejectAsync(consignmentId, pending.Id, permissions: ShipmentWorld.ApproverOnly()))!.Status);
        await AssertAsync(w, "reject permissions");
    }

    // ---- dispositions -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disposition_WriteOff_LeavesTransitAsALoss_AndBumpsTheCounters()
    {
        var (w, shipmentId, consignmentId, lineId, transferId) = await PostedWithDiscrepanciesAsync();
        Assert.Equal((50m, 20m, 30m), await QohAsync(w));

        var disposition = await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, "broken in transit");

        Assert.Equal((1, "broken in transit"), (disposition!.Sequence, disposition.Reason));
        var dl = disposition.Lines.Single();
        Assert.Equal((DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, 10m, "box", 10m), (dl.Bucket, dl.Action, dl.EnteredQty, dl.BaseQty, dl.UnitCode, dl.ConversionFactorToBase));
        Assert.Equal((50m, 20m, 20m), await QohAsync(w));                                                    // the written-off Box leaves the company
        var line = await w.LineAsync(lineId);
        Assert.Equal((10m, 0m, 0m), (line.DispositionedDamagedBaseQty, line.DispositionedRejectedBaseQty, line.DispositionedMissingBaseQty));
        Assert.Equal((10m, 0m, 0m, 20m), (await w.BatchRowsAsync()).Aggregate((w: 0m, r: 0m, a: 0m, rec: 0m), (acc, b) => (acc.w + b.WrittenOffBaseQty, acc.r + b.ReturnedBaseQty, acc.a + b.AcceptedCorrectionBaseQty, acc.rec + b.ReceivedBaseQty)));
        Assert.Equal((10m, 0m), ((await w.T.ReloadAsync(transferId)).Lines.Single().WrittenOffBaseQty, (await w.T.ReloadAsync(transferId)).Lines.Single().ReturnedBaseQty));

        var movements = (await w.MovementsAsync(transferId)).Where(m => m.Type == StockMovementType.Adjustment).ToList();
        var loss = Assert.Single(movements);
        Assert.Equal((w.L.Transit, -10m, AdjustmentType.Loss), (loss.StockLocationId, loss.QuantityBaseUnits, loss.ReasonCode));
        Assert.Equal(ConsignmentStatus.Received, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status); // rejected + missing still open
        await AssertAsync(w, "write-off");
    }

    [Fact]
    public async Task Disposition_ReturnToSource_MovesTransitBackToTheShipmentSource()
    {
        var (w, _, consignmentId, lineId, transferId) = await PostedWithDiscrepanciesAsync();

        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Rejected, DispositionAction.ReturnToSource, 1m);

        Assert.Equal((60m, 20m, 20m), await QohAsync(w));
        var line = await w.LineAsync(lineId);
        Assert.Equal((0m, 10m, 0m), (line.DispositionedDamagedBaseQty, line.DispositionedRejectedBaseQty, line.DispositionedMissingBaseQty));
        var rows = await w.BatchRowsAsync();
        Assert.Equal((0m, 10m, 0m), (rows.Sum(b => b.WrittenOffBaseQty), rows.Sum(b => b.ReturnedBaseQty), rows.Sum(b => b.AcceptedCorrectionBaseQty)));
        Assert.Equal(10m, (await w.T.ReloadAsync(transferId)).Lines.Single().ReturnedBaseQty);
        var moves = (await w.MovementsAsync(transferId)).Where(m => m.Notes != null && m.Notes.Contains("disposition")).ToList();
        Assert.Contains(moves, m => m.StockLocationId == w.L.Transit && m.QuantityBaseUnits == -10m && m.Type == StockMovementType.TransferOut);
        Assert.Contains(moves, m => m.StockLocationId == w.L.MainStore && m.QuantityBaseUnits == 10m && m.Type == StockMovementType.TransferIn);
        await AssertAsync(w, "return");
    }

    [Fact]
    public async Task Disposition_AcceptToDestination_MovesTransitToTheDestination_WithoutTouchingReceivedCounters()
    {
        var (w, _, consignmentId, lineId, transferId) = await PostedWithDiscrepanciesAsync();

        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Missing, DispositionAction.AcceptToDestination, 1m, "found in the van");

        Assert.Equal((50m, 30m, 20m), await QohAsync(w));
        var line = await w.LineAsync(lineId);
        Assert.Equal((0m, 0m, 10m), (line.DispositionedDamagedBaseQty, line.DispositionedRejectedBaseQty, line.DispositionedMissingBaseQty));
        Assert.Equal(20m, line.PostedBaseQty);                                                               // posted accepted quantity unchanged
        var rows = await w.BatchRowsAsync();
        Assert.Equal((20m, 10m), (rows.Sum(b => b.ReceivedBaseQty), rows.Sum(b => b.AcceptedCorrectionBaseQty)));
        Assert.Equal(0m, rows.Sum(b => b.DamagedBaseQty));
        var moves = (await w.MovementsAsync(transferId)).Where(m => m.Notes != null && m.Notes.Contains("disposition")).ToList();
        Assert.Contains(moves, m => m.StockLocationId == w.L.BlrStore && m.QuantityBaseUnits == 10m && m.Type == StockMovementType.TransferIn);

        // The request line mirrors the batch counter, so its outstanding equals the batch rows' and what Transit still holds (30 - 10).
        var requestLine = (await w.T.ReloadAsync(transferId)).Lines.Single();
        Assert.Equal(10m, requestLine.AcceptedCorrectionBaseQty);
        var lineOutstanding = requestLine.DispatchedBaseQty - requestLine.ReceivedBaseQty - requestLine.DamagedBaseQty - requestLine.WrittenOffBaseQty - requestLine.ReturnedBaseQty - requestLine.AcceptedCorrectionBaseQty;
        Assert.Equal(20m, lineOutstanding);
        Assert.Equal(lineOutstanding, rows.Sum(b => b.DispatchedBaseQty - b.ReceivedBaseQty - b.DamagedBaseQty - b.WrittenOffBaseQty - b.ReturnedBaseQty - b.AcceptedCorrectionBaseQty));
        Assert.Equal(lineOutstanding, (await QohAsync(w)).Transit);
        await AssertAsync(w, "accept");
    }

    [Fact]
    public async Task Disposition_AcceptingEveryOpenBucket_LeavesTheRequestLineWithNothingOutstanding()
    {
        var (w, _, consignmentId, lineId, transferId) = await PostedWithDiscrepanciesAsync();

        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.AcceptToDestination, 1m, "all fine");
        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Rejected, DispositionAction.AcceptToDestination, 1m, "all fine");
        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Missing, DispositionAction.AcceptToDestination, 1m, "all fine");

        var requestLine = (await w.T.ReloadAsync(transferId)).Lines.Single();
        Assert.Equal(30m, requestLine.AcceptedCorrectionBaseQty);
        Assert.Equal(0m, requestLine.DispatchedBaseQty - requestLine.ReceivedBaseQty - requestLine.DamagedBaseQty - requestLine.WrittenOffBaseQty - requestLine.ReturnedBaseQty - requestLine.AcceptedCorrectionBaseQty);
        Assert.Equal((50m, 50m, 0m), await QohAsync(w));
        await AssertAsync(w, "accept all");
    }

    [Fact]
    public async Task Dispositions_ResolvingEveryBucket_ReconcilesTheConsignment_AndCompletesTheShipment()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await PostedWithDiscrepanciesAsync();

        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m);
        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Rejected, DispositionAction.ReturnToSource, 1m);
        var mid = await w.ReloadAsync(shipmentId);
        Assert.Equal((ConsignmentStatus.Received, ShipmentStatus.Delivered), (mid.Consignments.Single().Status, mid.Status));   // missing still open

        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Missing, DispositionAction.AcceptToDestination, 1m);

        var done = await w.ReloadAsync(shipmentId);
        Assert.Equal((ConsignmentStatus.Reconciled, ShipmentStatus.Completed), (done.Consignments.Single().Status, done.Status));
        Assert.Equal((60m, 30m, 0m), await QohAsync(w));                                                    // 100 - 10 written off
        Assert.Equal(new[] { 1, 2, 3 }, (await w.DispositionsAsync(consignmentId)).Select(d => d.Sequence).ToArray());
        await using var db = w.T.Db();
        Assert.Equal(3, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.ConsignmentDispositioned));
        await AssertAsync(w, "reconciled");
    }

    [Fact]
    public async Task Disposition_SplitAcrossActionsInOneRequest_DrawsTheBucketOnce()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 2m, damaged: 3m);
        await w.PostAsync(consignmentId, receipt!.Id);

        await w.DispositionAsync(consignmentId, [
            new DispositionLineInput(lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m),
            new DispositionLineInput(lineId, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 1m),
            new DispositionLineInput(lineId, DispositionBucket.Damaged, DispositionAction.AcceptToDestination, 1m)]);

        Assert.Equal((60m, 30m, 0m), await QohAsync(w));
        Assert.Equal(30m, (await w.LineAsync(lineId)).DispositionedDamagedBaseQty);
        Assert.Equal(ConsignmentStatus.Reconciled, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status);
        await AssertAsync(w, "split");
    }

    [Fact]
    public async Task Disposition_AboveTheBucket_Fractional_Duplicate_OrUnknownLine_IsRefused_AndChangesNothing()
    {
        var (w, _, consignmentId, lineId, _) = await PostedWithDiscrepanciesAsync();                         // damaged 1 Box

        Assert.Equal("lines[0].quantity", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 2m)));
        Assert.Equal("lines[1].quantity", await Field(() => w.DispositionAsync(consignmentId, [
            new DispositionLineInput(lineId, DispositionBucket.Rejected, DispositionAction.WriteOff, 1m),
            new DispositionLineInput(lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m),
            new DispositionLineInput(lineId, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 1m)])));        // 1 + 1 > damaged 1, in one request
        Assert.Equal("lines[0].quantity", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 0.5m))); // Box has no decimals
        Assert.Equal("lines[0].quantity", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 0m)));
        Assert.Equal("lines[1].consignmentLineId", await Field(() => w.DispositionAsync(consignmentId, [
            new DispositionLineInput(lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m),
            new DispositionLineInput(lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)])));
        Assert.Equal("lines[0].consignmentLineId", await Field(() => w.DispositionAsync(consignmentId, Guid.NewGuid(), DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)));
        Assert.Equal("reason", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, reason: " ")));
        Assert.Equal("lines", await Field(() => w.DispositionAsync(consignmentId, [], "x")));

        Assert.Empty(await w.DispositionsAsync(consignmentId));
        Assert.Equal((50m, 20m, 30m), await QohAsync(w));

        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m);                  // exactly the bucket
        Assert.Equal("lines[0].quantity", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 1m))); // cumulative
        await AssertAsync(w, "over-disposition");
    }

    [Fact]
    public async Task Disposition_OnlyPostedReceiptQuantityIsDispositionable()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        var r1 = await w.RecordAsync(consignmentId, lineId, accepted: 2m, damaged: 1m);
        var r2 = await w.RecordAsync(consignmentId, lineId, accepted: 1m, damaged: 1m);                      // both Verified, nothing posted yet

        Assert.Equal("lines[0].quantity", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)));

        await w.PostAsync(consignmentId, r1!.Id);
        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m);                  // r1's damaged Box
        Assert.Equal("lines[0].quantity", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m))); // r2's is still unposted

        await w.PostAsync(consignmentId, r2!.Id);
        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m);
        Assert.Equal(20m, (await w.LineAsync(lineId)).DispositionedDamagedBaseQty);
        await AssertAsync(w, "posted only");
    }

    [Fact]
    public async Task Disposition_NeedsCompanyLevelResolve_AndADispatchedConsignment()
    {
        var (w, _, consignmentId, lineId, _) = await PostedWithDiscrepanciesAsync();

        var branchOnly = FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Resolve");
        Assert.Null(await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, permissions: branchOnly)); // no company-level Resolve = 404, not a 400 that reveals the consignment exists
        Assert.Null(await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, permissions: ShipmentWorld.ApproverOnly())); // no company-level Resolve = 404, not a 400 that reveals the consignment exists
        Assert.Null(await w.DispositionAsync(Guid.NewGuid(), lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m));
        Assert.Empty(await w.DispositionsAsync(consignmentId));

        var w2 = await ShipmentWorld.CreateAsync(100m);
        var (ready, _) = await w2.ReadyAsync(boxes: 2m);
        var undispatched = ready.Consignments.Single();
        Assert.Equal("status", await Field(() => w2.DispositionAsync(undispatched.Id, undispatched.Lines.Single().Id, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)));
    }

    // ---- idempotency --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disposition_SameKey_ReturnsTheSameDisposition_AndAppliesOnce_AnotherConsignmentIsRefused()
    {
        var (w, _, consignmentId, lineId, _) = await PostedWithDiscrepanciesAsync();

        var first = await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, key: "k-disp");
        var replay = await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, key: "k-disp");

        Assert.Equal(first!.Id, replay!.Id);
        Assert.Single(await w.DispositionsAsync(consignmentId));
        Assert.Equal(10m, (await w.LineAsync(lineId)).DispositionedDamagedBaseQty);
        Assert.Equal((50m, 20m, 20m), await QohAsync(w));

        Assert.Equal("IdempotencyKey", await Field(() => w.DispositionAsync(Guid.NewGuid(), lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, key: "k-disp")));
        await AssertAsync(w, "disposition replay");
    }

    [Fact]
    public async Task Approve_SameKey_ReplaysWithoutPostingAgain()
    {
        var (w, _, consignmentId, lineId, _) = await DispatchedAsync();
        await w.SetPolicyAsync(ReceiptPostingMode.SupervisorApproval, separation: false);
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 5m);

        var first = await w.ApproveAsync(consignmentId, receipt!.Id, key: "k-app");
        var replay = await w.ApproveAsync(consignmentId, receipt.Id, key: "k-app");

        Assert.Equal(first!.Id, replay!.Id);
        Assert.Equal((50m, 50m, 0m), await QohAsync(w));
        Assert.Equal("IdempotencyKey", await Field(() => w.ApproveAsync(consignmentId, Guid.NewGuid(), key: "k-app")));
        await AssertAsync(w, "approve replay");
    }

    // ---- status machine -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoDiscrepancy_GoesStraightToReconciled_AtTheLastPost_AndTheShipmentIsCompleted()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();
        var r1 = await w.RecordAsync(consignmentId, lineId, accepted: 2m);
        var r2 = await w.RecordAsync(consignmentId, lineId, accepted: 3m);

        await w.PostAsync(consignmentId, r1!.Id);
        Assert.Equal(ConsignmentStatus.PartiallyReceived, (await w.ReloadAsync(shipmentId)).Consignments.Single().Status);
        await w.PostAsync(consignmentId, r2!.Id);

        var done = await w.ReloadAsync(shipmentId);
        Assert.Equal((ConsignmentStatus.Reconciled, ShipmentStatus.Completed), (done.Consignments.Single().Status, done.Status));
        Assert.Equal("status", await Field(() => w.DispositionAsync(consignmentId, lineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m))); // nothing left, and not dispositionable
        await AssertAsync(w, "straight to reconciled");
    }

    [Fact]
    public async Task Shipment_IsDeliveredNotCompleted_WhileAnyConsignmentHasOpenDiscrepancy()
    {
        var (w, shipmentId, consignmentId, lineId, _) = await DispatchedAsync();
        var receipt = await w.RecordAsync(consignmentId, lineId, accepted: 4m, missing: 1m);
        await w.PostAsync(consignmentId, receipt!.Id);

        var delivered = await w.ReloadAsync(shipmentId);
        Assert.Equal((ConsignmentStatus.Received, ShipmentStatus.Delivered), (delivered.Consignments.Single().Status, delivered.Status));

        await w.DispositionAsync(consignmentId, lineId, DispositionBucket.Missing, DispositionAction.WriteOff, 1m);
        Assert.Equal(ShipmentStatus.Completed, (await w.ReloadAsync(shipmentId)).Status);
        await AssertAsync(w, "completed after disposition");
    }
}
