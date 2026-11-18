using System.Net;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.ReceiptHttp;
using static Sellora.IntegrationTests.TestData.ShipmentHttp;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Functional-improvements Phase 06 (FI-D39) over HTTP on PostgreSQL: the company receipt policy (Direct / SupervisorApproval), separation of duties (service + the DB check),
/// the per-receipt policy snapshot, the permission matrix of approve / reject, and reject (frees the quantity, races an approve with exactly one winner).
/// The policy is a company setting of the shared world: every test restores Direct / no separation, and tests of this collection run one after another.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReceiptApprovalApiTests(PostgresFixture postgres, Phase06Fixture main) : IClassFixture<Phase06Fixture>
{
    // ---- 1. supervisor mode end to end ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SupervisorMode_RecordPendingApproval_PostRefused_Approve_MovesStockToTheDestinationExactlyOnce()
    {
        var world = main.World;
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 30m);
        var s = d.S;
        var recorder = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var approver = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        await using var policy = await UsePolicyAsync(s.W, ReceiptPostingMode.SupervisorApproval);
        var transitBefore = await TransitQohAsync(s);

        var recorded = await RecordAsync(recorder.Client, s.A, d, accepted: 30m);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var body = await BranchApiWorld.JsonAsync(recorded);
        var rid = IdOf(body);
        Assert.Equal("PendingApproval", body.GetProperty("status").GetString());
        Assert.Equal(("PendingApproval", "SupervisorApproval"), (await ReceiptStatusAsync(s.W, rid),
            await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "PolicyMode" FROM "ConsignmentReceipts" WHERE "Id" = '{rid}'""")));
        Assert.Equal((0m, transitBefore), (await QohAsync(s, s.AStore), await TransitQohAsync(s)));          // recording moves nothing

        // The legacy post is refused for such a receipt - for the recorder, a Receive-only holder and the company-level admin alike.
        await AssertRefusedAsync(await PostReceiptAsync(recorder.Client, s.A, d, rid), "status");
        await AssertRefusedAsync(await PostReceiptAsync(approver.Client, s.A, d, rid), "status");
        await AssertRefusedAsync(await PostReceiptAsync(s.W.Admin, s.Src, d, rid), "status");
        Assert.Equal(("PendingApproval", 0m), (await ReceiptStatusAsync(s.W, rid), await QohAsync(s, s.AStore)));
        Assert.Equal("PartiallyReceived", await ConsignmentStatusAsync(s.W, d.ConsId));                              // accounted but not posted: never Received / Reconciled

        var key = NewKey("approve");
        var approved = await BranchApiWorld.JsonAsync(await ApproveAsync(approver.Client, s.A, d, rid, key));
        Assert.Equal("Posted", approved.GetProperty("status").GetString());
        Assert.Equal((30m, transitBefore - 30m, 70m), (await QohAsync(s, s.AStore), await TransitQohAsync(s), await QohAsync(s)));
        Assert.Equal(30m, await MovementsAsync(d, "TransferIn", s.AStore, "receipt"));
        Assert.Equal(-30m, await MovementsAsync(d, "TransferOut", await TransitLocationAsync(s), "receipt"));
        Assert.Equal(("Reconciled", "Completed"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        Assert.Equal(1L, await AuditAsync(d, "consignment.receipt_approved"));

        // A repeat (same key, another key, no key) is a no-op; the posted receipt is immutable.
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(approver.Client, s.A, d, rid, key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(approver.Client, s.A, d, rid)).StatusCode);
        await AssertRefusedAsync(await RejectAsync(approver.Client, s.A, d, rid), "status");
        Assert.Equal((30m, 1L), (await QohAsync(s, s.AStore), await AuditAsync(d, "consignment.receipt_approved")));
        await AssertCleanAsync(s, "supervisor end to end");
    }

    [Fact]
    public async Task SupervisorMode_ParallelApprovals_AndRetriesOfTheSameKey_PostExactlyOnce()
    {
        var world = main.World;
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 10m);
        var s = d.S;
        var recorder = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var approver = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.ApproveReceipt");
        await using var policy = await UsePolicyAsync(s.W, ReceiptPostingMode.SupervisorApproval);
        var rid = await RecordOkAsync(recorder.Client, s.A, d, accepted: 10m);
        var transitBefore = await TransitQohAsync(s);
        var sharedKey = NewKey("same");

        var work = new List<Func<Task<HttpResponseMessage>>>();
        for (var i = 0; i < 6; i++) work.Add(() => ApproveAsync(approver.Client, s.A, d, rid));                       // distinct keys
        for (var i = 0; i < 4; i++) work.Add(() => ApproveAsync(approver.Client, s.A, d, rid, sharedKey));            // the same key retried
        var responses = await ParallelAsync(work.ToArray());

        await AssertNo500Async(s.W, "parallel approvals", responses);
        Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));                                    // the loser of the lock sees Posted and no-ops
        Assert.Equal((10m, transitBefore - 10m), (await QohAsync(s, s.AStore), await TransitQohAsync(s)));
        Assert.Equal((10m, 10m), (await LineAsync(d, "PostedBaseQty"), await BatchRowsAsync(d, "ReceivedBaseQty")));
        Assert.Equal((1L, 1L), (await MovementCountAsync(d, "TransferIn", s.AStore, "receipt"), await AuditAsync(d, "consignment.receipt_approved")));
        Assert.Equal("Posted", await ReceiptStatusAsync(s.W, rid));
        await AssertCleanAsync(s, "parallel approvals");
    }

    // ---- 2. separation of duties ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReceiptPostingMode.SupervisorApproval)]
    [InlineData(ReceiptPostingMode.Direct)]
    public async Task SeparationOfDuties_TheRecorderCannotApproveOrPost_AnotherUserCan(ReceiptPostingMode mode)
    {
        var world = main.World;
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 20m);
        var s = d.S;
        var recorder = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        var other = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        await using var policy = await UsePolicyAsync(s.W, mode, separation: true);
        var supervisor = mode == ReceiptPostingMode.SupervisorApproval;
        var rid = await RecordOkAsync(recorder.Client, s.A, d, accepted: 20m);

        // The recorder: 400 on the receipt, nothing posted (a repeat with the same key replays the refusal, never a post).
        var attempt = supervisor ? await ApproveAsync(recorder.Client, s.A, d, rid) : await PostReceiptAsync(recorder.Client, s.A, d, rid);
        await AssertRefusedAsync(attempt, "receiptId");
        Assert.Equal((supervisor ? "PendingApproval" : "Verified", 0m, 0m), (await ReceiptStatusAsync(s.W, rid), await QohAsync(s, s.AStore), await LineAsync(d, "PostedBaseQty")));
        Assert.Equal(0L, await AuditAsync(d, supervisor ? "consignment.receipt_approved" : "consignment.receipt_posted"));

        var ok = supervisor ? await ApproveAsync(other.Client, s.A, d, rid) : await PostReceiptAsync(other.Client, s.A, d, rid);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(("Posted", 20m), (await ReceiptStatusAsync(s.W, rid), await QohAsync(s, s.AStore)));
        Assert.Equal(other.Id, await ScalarAsync<Guid>(s.W.Host.Database, $"""SELECT "PostedByUserId" FROM "ConsignmentReceipts" WHERE "Id" = '{rid}'"""));
        await AssertCleanAsync(s, "separation of duties " + mode);
    }

    [Fact]
    public async Task SeparationOfDuties_TheDatabaseCheckRefusesAPosterEqualToTheRecorder()
    {
        var world = main.World;
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 10m);
        var s = d.S;
        var recorder = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        await using var policy = await UsePolicyAsync(s.W, ReceiptPostingMode.Direct, separation: true);
        var rid = await RecordOkAsync(recorder.Client, s.A, d, accepted: 10m);
        Assert.True(await ScalarAsync<bool>(s.W.Host.Database, $"""SELECT "RequireSeparation" FROM "ConsignmentReceipts" WHERE "Id" = '{rid}'"""));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(s.W.Host.Database,
            $"""UPDATE "ConsignmentReceipts" SET "Status" = 'Posted', "PostedAt" = now(), "PostedByUserId" = "VerifiedByUserId" WHERE "Id" = '{rid}'"""));
        Assert.Equal(("23514", "CK_ConsignmentReceipts_Separation"), (ex.SqlState, ex.ConstraintName));
        Assert.Equal("Verified", await ReceiptStatusAsync(s.W, rid));

        // Without the flag on the row the same poster is allowed by the constraint (the check is exactly the snapshot): prove it on a receipt recorded without separation.
        await SetPolicyAsync(s.W, ReceiptPostingMode.Direct, false);
        var d2 = await DispatchedAsync(world, 100m, 10m);
        var rid2 = await RecordOkAsync(s.W.Admin, d2.S.Src, d2, accepted: 10m);
        await ExecuteAsync(s.W.Host.Database, $"""UPDATE "ConsignmentReceipts" SET "PostedAt" = now(), "PostedByUserId" = "VerifiedByUserId", "Status" = 'Posted' WHERE "Id" = '{rid2}'""");
        Assert.Equal("Posted", await ReceiptStatusAsync(s.W, rid2));
        // (that hand-posted receipt moved no stock: the transit script would flag it, so undo it before the closing reconciliation)
        await ExecuteAsync(s.W.Host.Database, $"""UPDATE "ConsignmentReceipts" SET "Status" = 'Verified', "PostedAt" = NULL, "PostedByUserId" = NULL WHERE "Id" = '{rid2}'""");
        await AssertCleanAsync(s, "separation check");
    }

    // ---- 3. the policy snapshot -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PolicySnapshot_DirectReceiptStillPostsDirectly_AfterTheSettingSwitchesToSupervisor()
    {
        var world = main.World;
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 30m);
        var s = d.S;
        var clerk = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var approver = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.ApproveReceipt");
        await SetPolicyAsync(s.W, ReceiptPostingMode.Direct, false);
        try
        {
            var r1 = await RecordOkAsync(clerk.Client, s.A, d, accepted: 10m);
            var r2 = await RecordOkAsync(clerk.Client, s.A, d, accepted: 10m);
            await SetPolicyAsync(s.W, ReceiptPostingMode.SupervisorApproval, true);
            var r3 = await RecordOkAsync(clerk.Client, s.A, d, accepted: 10m);

            Assert.Equal(("Verified", "Verified", "PendingApproval"), (await ReceiptStatusAsync(s.W, r1), await ReceiptStatusAsync(s.W, r2), await ReceiptStatusAsync(s.W, r3)));
            Assert.Equal(new[] { "Direct", "Direct", "SupervisorApproval" }, await Task.WhenAll(new[] { r1, r2, r3 }.Select(id => ScalarAsync<string>(s.W.Host.Database, $"""SELECT "PolicyMode" FROM "ConsignmentReceipts" WHERE "Id" = '{id}'"""))));
            Assert.Equal(new[] { false, false, true }, await Task.WhenAll(new[] { r1, r2, r3 }.Select(id => ScalarAsync<bool>(s.W.Host.Database, $"""SELECT "RequireSeparation" FROM "ConsignmentReceipts" WHERE "Id" = '{id}'"""))));

            // The old Direct receipts post directly (the recorder may - separation was off then); approve does not apply to them. The new one is the reverse.
            await AssertRefusedAsync(await ApproveAsync(approver.Client, s.A, d, r1), "status");
            Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(clerk.Client, s.A, d, r1)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(clerk.Client, s.A, d, r2)).StatusCode);
            await AssertRefusedAsync(await PostReceiptAsync(clerk.Client, s.A, d, r3), "status");
            Assert.Equal(20m, await QohAsync(s, s.AStore));
            Assert.Equal("PartiallyReceived", await ConsignmentStatusAsync(s.W, d.ConsId));
            Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(approver.Client, s.A, d, r3)).StatusCode);
            Assert.Equal((30m, "Reconciled", "Completed"), (await QohAsync(s, s.AStore), await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
            await AssertCleanAsync(s, "snapshot direct then supervisor");
        }
        finally { await SetPolicyAsync(s.W, ReceiptPostingMode.Direct, false); }
    }

    [Fact]
    public async Task PolicySnapshot_SupervisorReceiptStillNeedsApproval_AfterTheSettingSwitchesToDirect_AndKeepsItsSeparationFlag()
    {
        var world = main.World;
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 20m);
        var s = d.S;
        var recorder = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        var other = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        await SetPolicyAsync(s.W, ReceiptPostingMode.SupervisorApproval, true);
        try
        {
            var r1 = await RecordOkAsync(recorder.Client, s.A, d, accepted: 10m);
            await SetPolicyAsync(s.W, ReceiptPostingMode.Direct, false);
            var r2 = await RecordOkAsync(recorder.Client, s.A, d, accepted: 10m);
            Assert.Equal(("PendingApproval", "Verified"), (await ReceiptStatusAsync(s.W, r1), await ReceiptStatusAsync(s.W, r2)));

            await AssertRefusedAsync(await PostReceiptAsync(other.Client, s.A, d, r1), "status");                       // still needs approval
            await AssertRefusedAsync(await ApproveAsync(recorder.Client, s.A, d, r1), "receiptId");                     // and still separated, though the setting is now off
            await AssertRefusedAsync(await ApproveAsync(other.Client, s.A, d, r2), "status");                           // r2 is Direct
            Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(recorder.Client, s.A, d, r2)).StatusCode);          // r2 was recorded without separation
            Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(other.Client, s.A, d, r1)).StatusCode);
            Assert.Equal((20m, "Reconciled"), (await QohAsync(s, s.AStore), await ConsignmentStatusAsync(s.W, d.ConsId)));
            await AssertCleanAsync(s, "snapshot supervisor then direct");
        }
        finally { await SetPolicyAsync(s.W, ReceiptPostingMode.Direct, false); }
    }

    // ---- 4. permission matrix -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Permissions_ApproveAndReject_NeedApproveReceiptAtTheDestination_AndAreHiddenElsewhere()
    {
        await using var world = await Phase06World.FreshAsync(postgres);
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 20m);
        var s = d.S;
        var clerk = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var approverA = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.ApproveReceipt");
        var approverB = await BranchUserAsync(r, s.B, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        var nobody = await BranchUserAsync(r, s.A, "Shipments.View");
        await using var policy = await UsePolicyAsync(s.W, ReceiptPostingMode.SupervisorApproval);
        var rid = await RecordOkAsync(clerk.Client, s.A, d, accepted: 10m);
        var rid2 = await RecordOkAsync(clerk.Client, s.A, d, accepted: 10m);

        // Approve: no ApproveReceipt at all -> 403 (policy); ApproveReceipt at the wrong destination -> 404 (hidden); a Receive holder has no approve right.
        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(clerk.Client, s.A, d, rid)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(nobody.Client, s.A, d, rid)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(approverB.Client, s.B, d, rid)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(approverA.Client, s.B, d, rid)).StatusCode);         // a working branch outside the user's allocation is refused at the door
        // Supervisor reject of a SupervisorApproval receipt needs ApproveReceipt at the destination: a Receive holder and the wrong branch see a 404 and change nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(clerk.Client, s.A, d, rid)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(approverB.Client, s.B, d, rid)).StatusCode);
        Assert.Equal(("PendingApproval", 0m, 0m), (await ReceiptStatusAsync(s.W, rid), await QohAsync(s, s.AStore), await LineAsync(d, "PostedBaseQty")));
        Assert.Equal(0L, await AuditAsync(d, "consignment.receipt_rejected"));

        // The ApproveReceipt holder at A (no Receive) can reject the second receipt; the company-level admin may approve the first at any destination.
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(approverA.Client, s.A, d, rid2)).StatusCode);
        Assert.Equal("RejectedForCorrection", await ReceiptStatusAsync(s.W, rid2));
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(s.W.Admin, s.Src, d, rid)).StatusCode);
        Assert.Equal((10m, "PartiallyReceived"), (await QohAsync(s, s.AStore), await ConsignmentStatusAsync(s.W, d.ConsId)));
        await AssertCleanAsync(s, "permission matrix (supervisor)");
    }

    [Fact]
    public async Task Permissions_DirectReceipt_IsRejectedByAReceiveHolder_NotByAnApproverAlone_AndApproveDoesNotApply()
    {
        await using var world = await Phase06World.FreshAsync(postgres);
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 20m);
        var s = d.S;
        var clerk = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var approverOnly = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.ApproveReceipt");
        var otherReceiver = await BranchUserAsync(r, s.B, "Shipments.View", "StockTransfers.Receive");
        await SetPolicyAsync(s.W, ReceiptPostingMode.Direct, false);
        var rid = await RecordOkAsync(clerk.Client, s.A, d, accepted: 20m);

        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(approverOnly.Client, s.A, d, rid)).StatusCode);        // Direct reject needs Receive
        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(otherReceiver.Client, s.B, d, rid)).StatusCode);       // a Receive holder at another destination
        await AssertRefusedAsync(await ApproveAsync(approverOnly.Client, s.A, d, rid), "status");                        // approve is for SupervisorApproval receipts only
        Assert.Equal("Verified", await ReceiptStatusAsync(s.W, rid));
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(clerk.Client, s.A, d, rid)).StatusCode);
        Assert.Equal("RejectedForCorrection", await ReceiptStatusAsync(s.W, rid));
        await AssertCleanAsync(s, "permission matrix (direct)");
    }

    [Fact]
    public async Task Permissions_Dispositions_NeedCompanyLevelResolve()
    {
        var world = main.World;
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 10m);
        var s = d.S;
        var clerk = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        await SetPolicyAsync(s.W, ReceiptPostingMode.Direct, false);
        var rid = await RecordOkAsync(clerk.Client, s.A, d, accepted: 6m, damaged: 4m);
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(clerk.Client, s.A, d, rid)).StatusCode);
        var transitBefore = await TransitQohAsync(s);

        var noResolve = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        Assert.Equal(HttpStatusCode.Forbidden, (await DispositionAsync(noResolve.Client, s.A, d, DispositionBucket.Damaged, DispositionAction.WriteOff, 4m)).StatusCode);

        // Resolve is a company-wide permission (a branch role cannot even hold it). A company-role holder allocated only to the destination branch cannot touch a
        // shipment whose source is outside its allocation: refused by the service, never a 5xx, nothing changes.
        var destinationOnly = await CompanyRoleUserAsync(r, [s.A], "Shipments.View", "StockTransfers.Resolve");
        var refused = await DispositionAsync(destinationOnly.Client, s.A, d, DispositionBucket.Damaged, DispositionAction.WriteOff, 4m);
        Assert.Contains(refused.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Equal((0L, 0m, transitBefore), (await DispositionCountAsync(d), await LineAsync(d, "DispositionedDamagedBaseQty"), await TransitQohAsync(s)));
        var bothEnds = await CompanyRoleUserAsync(r, [s.Src, s.A], "Shipments.View", "StockTransfers.Resolve");
        Assert.Equal(HttpStatusCode.Created, (await DispositionAsync(bothEnds.Client, s.Src, d, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)).StatusCode);
        Assert.Equal((1L, 1m, transitBefore - 1m), (await DispositionCountAsync(d), await LineAsync(d, "DispositionedDamagedBaseQty"), await TransitQohAsync(s)));
        transitBefore -= 1m;

        // The company-level holder (admin) succeeds.
        Assert.Equal(HttpStatusCode.Created, (await DispositionAsync(s.W.Admin, s.Src, d, DispositionBucket.Damaged, DispositionAction.WriteOff, 3m)).StatusCode);
        Assert.Equal((2L, 4m, transitBefore - 3m), (await DispositionCountAsync(d), await LineAsync(d, "DispositionedDamagedBaseQty"), await TransitQohAsync(s)));
        await AssertCleanAsync(s, "disposition permissions");
    }

    // ---- 5. reject ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReceiptPostingMode.Direct)]
    [InlineData(ReceiptPostingMode.SupervisorApproval)]
    public async Task Reject_FreesTheQuantity_AllowsACorrectedReceiptUpToTheDispatched_AndKeepsTheRejectedRowImmutable(ReceiptPostingMode mode)
    {
        await using var world = await Phase06World.FreshAsync(postgres);
        var r = world.R;
        var d = await DispatchedAsync(world, 100m, 10m);
        var s = d.S;
        var supervisor = mode == ReceiptPostingMode.SupervisorApproval;
        var clerk = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var approver = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
        await using var policy = await UsePolicyAsync(s.W, mode);
        var rejecter = supervisor ? approver.Client : clerk.Client;

        var wrong = await RecordOkAsync(clerk.Client, s.A, d, accepted: 7m, damaged: 3m);
        Assert.Equal((7m, 3m), (await LineAsync(d, "AcceptedBaseQty"), await LineAsync(d, "DamagedBaseQty")));
        await AssertRefusedAsync(await RecordAsync(clerk.Client, s.A, d, accepted: 1m), "lines[0].acceptedQuantity");   // fully accounted by the unposted receipt

        await AssertRefusedAsync(await RejectAsync(rejecter, s.A, d, wrong, reason: ""), "reason");                       // a reason is required
        var key = NewKey("reject");
        var rejected = await RejectAsync(rejecter, s.A, d, wrong, "miscounted", key);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("RejectedForCorrection", (await BranchApiWorld.JsonAsync(rejected)).GetProperty("status").GetString());
        Assert.Equal((0m, 0m, 0m, "InTransit"), (await LineAsync(d, "AcceptedBaseQty"), await LineAsync(d, "DamagedBaseQty"), await LineAsync(d, "PostedBaseQty"), await ConsignmentStatusAsync(s.W, d.ConsId)));
        Assert.Equal("Dispatched", await ShipmentStatusAsync(s.W, d.ShipmentId));
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(rejecter, s.A, d, wrong, "miscounted", key)).StatusCode);       // replay
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(rejecter, s.A, d, wrong, "again")).StatusCode);                 // idempotent by state
        Assert.Equal(1L, await AuditAsync(d, "consignment.receipt_rejected"));
        Assert.Equal("miscounted", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "RejectionReason" FROM "ConsignmentReceipts" WHERE "Id" = '{wrong}'"""));

        // A rejected receipt can be neither posted nor approved; its recorded lines stay as they were.
        await AssertRefusedAsync(supervisor ? await ApproveAsync(approver.Client, s.A, d, wrong) : await PostReceiptAsync(clerk.Client, s.A, d, wrong), "status");
        Assert.Equal(0m, await QohAsync(s, s.AStore));
        Assert.Equal(7m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AcceptedBaseQty" FROM "ConsignmentReceiptLines" WHERE "ReceiptId" = '{wrong}'"""));

        // A corrected receipt up to the dispatched quantity (and no further), then posted.
        await AssertRefusedAsync(await RecordAsync(clerk.Client, s.A, d, accepted: 10m, damaged: 1m), "lines[0].acceptedQuantity");
        var corrected = await RecordOkAsync(clerk.Client, s.A, d, accepted: 10m);
        Assert.Equal(2, await ScalarAsync<int>(s.W.Host.Database, $"""SELECT "Sequence" FROM "ConsignmentReceipts" WHERE "Id" = '{corrected}'"""));
        var posted = supervisor ? await ApproveAsync(approver.Client, s.A, d, corrected) : await PostReceiptAsync(clerk.Client, s.A, d, corrected);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal((10m, 10m, "Reconciled", "Completed"), (await QohAsync(s, s.AStore), await LineAsync(d, "PostedBaseQty"), await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        await AssertRefusedAsync(await RejectAsync(rejecter, s.A, d, corrected), "status");                                 // posted = immutable
        Assert.Equal("Posted", await ReceiptStatusAsync(s.W, corrected));
        await AssertCleanAsync(s, "reject " + mode);
    }

    [Theory]
    [InlineData(ReceiptPostingMode.SupervisorApproval)]
    [InlineData(ReceiptPostingMode.Direct)]
    public async Task Reject_RacingAnApproveOrPost_OfTheSameReceipt_HasExactlyOneWinner_AndConsistentCounters(ReceiptPostingMode mode)
    {
        var supervisor = mode == ReceiptPostingMode.SupervisorApproval;
        await using var world = await Phase06World.FreshAsync(postgres);
        var r = world.R;
        await using var policy = await UsePolicyAsync(world.W, mode);
        for (var round = 0; round < 3; round++)
        {
            var d = await DispatchedAsync(world, 100m, 10m);
            var s = d.S;
            var actor = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive", "StockTransfers.ApproveReceipt");
            var rid = await RecordOkAsync(actor.Client, s.A, d, accepted: 8m, damaged: 2m);
            var transitBefore = await TransitQohAsync(s);

            var work = new List<Func<Task<HttpResponseMessage>>>();
            for (var i = 0; i < 3; i++)
            {
                work.Add(() => supervisor ? ApproveAsync(actor.Client, s.A, d, rid) : PostReceiptAsync(actor.Client, s.A, d, rid));
                work.Add(() => RejectAsync(actor.Client, s.A, d, rid));
            }
            var responses = await ParallelAsync(work.ToArray());
            await AssertNo500Async(s.W, $"{mode} round {round}", responses);

            var status = await ReceiptStatusAsync(s.W, rid);
            var winners = responses.Where((x, i) => i % 2 == 0).ToList();      // approve / post
            var rejectors = responses.Where((x, i) => i % 2 == 1).ToList();
            if (status == "Posted")
            {
                Assert.All(winners, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
                Assert.All(rejectors, x => Assert.Equal(HttpStatusCode.BadRequest, x.StatusCode));
                Assert.Equal((8m, 8m, 8m, transitBefore - 8m), (await QohAsync(s, s.AStore), await LineAsync(d, "AcceptedBaseQty"), await LineAsync(d, "PostedBaseQty"), await TransitQohAsync(s)));
                Assert.Equal(2m, await LineAsync(d, "DamagedBaseQty"));
            }
            else
            {
                Assert.Equal("RejectedForCorrection", status);
                Assert.All(winners, x => Assert.Equal(HttpStatusCode.BadRequest, x.StatusCode));
                Assert.All(rejectors, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
                Assert.Equal((0m, 0m, 0m, 0m, transitBefore), (await QohAsync(s, s.AStore), await LineAsync(d, "AcceptedBaseQty"), await LineAsync(d, "DamagedBaseQty"), await LineAsync(d, "PostedBaseQty"), await TransitQohAsync(s)));
            }
            Assert.Equal(1L, await AuditAsync(d, "consignment.receipt_rejected") + await AuditAsync(d, supervisor ? "consignment.receipt_approved" : "consignment.receipt_posted"));
            await AssertCleanAsync(s, $"reject race {mode} round {round} ({status})");
        }
    }

    private static Task<Guid> TransitLocationAsync(ShipScene s) =>
        ScalarAsync<Guid>(s.W.Host.Database, $"""SELECT "Id" FROM "StockLocations" WHERE "CompanyId" = '{s.W.CompanyId}' AND "LocationType" = 'Transit'""");
}
