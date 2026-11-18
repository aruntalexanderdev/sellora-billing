using System.Net;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.ReceiptHttp;
using static Sellora.IntegrationTests.TestData.ShipmentHttp;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Functional-improvements Phase 06 (FI-D40, FI-D41) over HTTP on PostgreSQL: dispositions of posted damaged / rejected / missing quantity (write off, return to source,
/// accept to destination) against the real stock effect, their refusals, parallel and idempotent behaviour, and the completion of consignment (Reconciled) and shipment
/// (Completed) with the transit views. Every scenario ends with both reconciliation scripts (transit queries 1-9, reservations) and the negative-stock checks.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConsignmentDispositionApiTests(PostgresFixture postgres, Phase06Fixture main) : IClassFixture<Phase06Fixture>
{
    private static readonly string[] Dispositioned = ["DispositionedDamagedBaseQty", "DispositionedRejectedBaseQty", "DispositionedMissingBaseQty"];

    /// <summary>Stock 100, <paramref name="dispatched"/> dispatched to A, one receipt recorded and POSTED (Direct policy) by the company-level admin.</summary>
    private async Task<DispatchedScene> PostedAsync(decimal accepted, decimal damaged = 0m, decimal rejected = 0m, decimal missing = 0m, decimal dispatched = 30m)
    {
        var d = await DispatchedAsync(main.World, 100m, dispatched);
        var rid = await RecordOkAsync(d.S.W.Admin, d.S.Src, d, accepted, damaged, rejected, missing);
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(d.S.W.Admin, d.S.Src, d, rid)).StatusCode);
        return d;
    }

    private static Task<HttpResponseMessage> DisposeAsync(DispatchedScene d, DispositionBucket bucket, DispositionAction action, decimal qty, string? key = null) =>
        DispositionAsync(d.S.W.Admin, d.S.Src, d, bucket, action, qty, key: key);

    private static async Task<Guid> TransitLocationAsync(ShipScene s) =>
        await ScalarAsync<Guid>(s.W.Host.Database, $"""SELECT "Id" FROM "StockLocations" WHERE "CompanyId" = '{s.W.CompanyId}' AND "LocationType" = 'Transit'""");

    private static async Task<decimal> RequestLineAsync(DispatchedScene d, string column) =>
        await ScalarAsync<decimal>(d.S.W.Host.Database, $"""SELECT COALESCE(SUM("{column}"), 0) FROM "StockTransferLines" WHERE "TransferId" = '{d.RequestId}'""");

    /// <summary>What the transfer views show as outstanding (dispatched less every settled counter incl. accepted-correction); equals what the transfer still holds in Transit.</summary>
    private static async Task<decimal> OutstandingAsync(DispatchedScene d) =>
        await ScalarAsync<decimal>(d.S.W.Host.Database, $"""
            SELECT SUM("DispatchedBaseQty" - "ReceivedBaseQty" - "DamagedBaseQty" - "WrittenOffBaseQty" - "ReturnedBaseQty" - "AcceptedCorrectionBaseQty") FROM "StockTransferLines" WHERE "TransferId" = '{d.RequestId}'
            """);

    private static async Task AssertNothingOpenedAsync(DispatchedScene d, decimal transitBefore, string what)
    {
        Assert.True(0L == await DispositionCountAsync(d), what + ": a disposition row was written");
        foreach (var column in Dispositioned) Assert.True(0m == await LineAsync(d, column), $"{what}: {column} moved");
        Assert.True(transitBefore == await TransitQohAsync(d.S), what + ": Transit moved");
    }

    // ---- 6. each action against the real stock effect -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DispositionBucket.Damaged, DispositionAction.WriteOff, 4)]
    [InlineData(DispositionBucket.Rejected, DispositionAction.ReturnToSource, 3)]
    [InlineData(DispositionBucket.Missing, DispositionAction.AcceptToDestination, 3)]
    public async Task Disposition_Action_HasTheExpectedStockBatchAndLedgerEffect(DispositionBucket bucket, DispositionAction action, int quantity)
    {
        var qty = (decimal)quantity;
        var d = await PostedAsync(accepted: 20m, damaged: 4m, rejected: 3m, missing: 3m);
        var s = d.S;
        var transit = await TransitLocationAsync(s);
        var (srcBefore, aBefore, transitBefore) = (await QohAsync(s), await QohAsync(s, s.AStore), await TransitQohAsync(s));
        Assert.Equal((70m, 20m), (srcBefore, aBefore));                                    // 30 left the source; 20 arrived; 10 sit in Transit
        var batchTransitBefore = await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT COALESCE(SUM("RemainingBaseQty"), 0) FROM "BatchLocationBalances" WHERE "StockLocationId" = '{transit}'""");

        var outstandingBefore = await OutstandingAsync(d);
        var response = await DisposeAsync(d, bucket, action, qty, NewKey("disp"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(1, body.GetProperty("sequence").GetInt32());

        var srcDelta = action == DispositionAction.ReturnToSource ? qty : 0m;
        var aDelta = action == DispositionAction.AcceptToDestination ? qty : 0m;
        Assert.Equal((srcBefore + srcDelta, aBefore + aDelta, transitBefore - qty), (await QohAsync(s), await QohAsync(s, s.AStore), await TransitQohAsync(s)));
        Assert.Equal(batchTransitBefore - qty, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT COALESCE(SUM("RemainingBaseQty"), 0) FROM "BatchLocationBalances" WHERE "StockLocationId" = '{transit}'"""));

        // Batch counters: exactly the one matching the action moved; the legacy Damaged counter is never written by shipment receiving or dispositions.
        Assert.Equal((action == DispositionAction.WriteOff ? qty : 0m, action == DispositionAction.ReturnToSource ? qty : 0m, action == DispositionAction.AcceptToDestination ? qty : 0m, 0m),
            (await BatchRowsAsync(d, "WrittenOffBaseQty"), await BatchRowsAsync(d, "ReturnedBaseQty"), await BatchRowsAsync(d, "AcceptedCorrectionBaseQty"), await BatchRowsAsync(d, "DamagedBaseQty")));
        Assert.Equal(20m, await BatchRowsAsync(d, "ReceivedBaseQty"));
        Assert.Equal((action == DispositionAction.WriteOff ? qty : 0m, action == DispositionAction.ReturnToSource ? qty : 0m),
            (await RequestLineAsync(d, "WrittenOffBaseQty"), await RequestLineAsync(d, "ReturnedBaseQty")));
        // The request line mirrors the batch accepted-correction counter, and its outstanding (what the transfer views show) equals what Transit still holds.
        Assert.Equal(action == DispositionAction.AcceptToDestination ? qty : 0m, await RequestLineAsync(d, "AcceptedCorrectionBaseQty"));
        Assert.Equal(outstandingBefore - qty, await OutstandingAsync(d));

        // Consignment line counters: only the dispositioned bucket moved.
        var expected = new[] { bucket == DispositionBucket.Damaged ? qty : 0m, bucket == DispositionBucket.Rejected ? qty : 0m, bucket == DispositionBucket.Missing ? qty : 0m };
        Assert.Equal((expected[0], expected[1], expected[2]), (await LineAsync(d, Dispositioned[0]), await LineAsync(d, Dispositioned[1]), await LineAsync(d, Dispositioned[2])));

        // The ledger: this disposition's movements only.
        if (action == DispositionAction.WriteOff)
        {
            Assert.Equal(-qty, await MovementsAsync(d, "Adjustment", transit, "disposition"));
            Assert.Equal("Loss", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "ReasonCode" FROM "StockMovements" WHERE "Notes" LIKE '{d.ConsNo} disposition 1' AND "Type" = 'Adjustment' LIMIT 1"""));
            Assert.Equal(0L, await MovementCountAsync(d, "TransferIn", s.SrcStore, "disposition") + await MovementCountAsync(d, "TransferIn", s.AStore, "disposition"));
        }
        else
        {
            var target = action == DispositionAction.ReturnToSource ? s.SrcStore : s.AStore;
            Assert.Equal((-qty, qty, 0L), (await MovementsAsync(d, "TransferOut", transit, "disposition"), await MovementsAsync(d, "TransferIn", target, "disposition"),
                await MovementCountAsync(d, "Adjustment", transit, "disposition")));
        }
        Assert.Equal(1L, await AuditAsync(d, "consignment.dispositioned"));

        // Other discrepancies are still open: accounted + posted but not Reconciled; the shipment is Delivered, not Completed.
        Assert.Equal(("Received", "Delivered"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));

        var list = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/consignments/{d.ConsId}/dispositions", null, s.Src));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal((bucket.ToString(), action.ToString(), qty), (list[0].GetProperty("lines")[0].GetProperty("bucket").GetString(), list[0].GetProperty("lines")[0].GetProperty("action").GetString(),
            list[0].GetProperty("lines")[0].GetProperty("quantity").GetDecimal()));
        await AssertCleanAsync(s, $"{action} {bucket}");
    }

    // ---- refusals -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disposition_OverTheBucket_Refused_AndSoIsQuantityThatIsNotPostedOrNotRecorded()
    {
        var d = await PostedAsync(accepted: 20m, damaged: 4m, rejected: 0m, missing: 6m);
        var s = d.S;
        var transitBefore = await TransitQohAsync(s);

        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 5m), "lines[0].quantity");           // 5 > 4
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Rejected, DispositionAction.WriteOff, 1m), "lines[0].quantity");          // that bucket is empty
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Missing, DispositionAction.ReturnToSource, 6.5m), "lines[0].quantity");
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 0m), "lines[0].quantity");           // > 0
        await AssertRefusedAsync(await DispositionAsync(s.W.Admin, s.Src, d, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m, reason: ""), "reason");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/dispositions",
            new RecordDispositionRequest("x", [new DispositionLineRequest(Guid.NewGuid(), DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)]), s.Src, NewKey()), "lines[0].consignmentLineId");
        // Two actions on one bucket that together exceed it, in a single request: all or nothing.
        var together = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/dispositions", new RecordDispositionRequest("x",
            [new DispositionLineRequest(d.LineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 3m), new DispositionLineRequest(d.LineId, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 3m)]), s.Src, NewKey());
        await AssertRefusedAsync(together, "lines[0].quantity");
        // The same (line, bucket, action) twice in one request.
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/dispositions", new RecordDispositionRequest("x",
            [new DispositionLineRequest(d.LineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m), new DispositionLineRequest(d.LineId, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)]), s.Src, NewKey()), "lines[1].consignmentLineId");
        await AssertNothingOpenedAsync(d, transitBefore, "refused requests");

        // Cumulative: 3 of 4 go, then 2 more are refused (1 left), then exactly 1 goes.
        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 3m)).StatusCode);
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 2m), "lines[0].quantity");
        Assert.Equal((3m, 1L), (await LineAsync(d, "DispositionedDamagedBaseQty"), await DispositionCountAsync(d)));
        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 1m)).StatusCode);
        Assert.Equal((4m, 2L, transitBefore - 4m), (await LineAsync(d, "DispositionedDamagedBaseQty"), await DispositionCountAsync(d), await TransitQohAsync(s)));
        await AssertCleanAsync(s, "over-disposition");
    }

    [Fact]
    public async Task Disposition_OfUnpostedOrUnrecordedQuantity_IsRefused_UntilTheReceiptIsPosted()
    {
        // 5 accepted + 5 damaged are RECORDED (Verified, not posted); 20 of the 30 dispatched are not recorded at all.
        var d = await DispatchedAsync(main.World, 100m, 30m);
        var s = d.S;
        var rid = await RecordOkAsync(s.W.Admin, s.Src, d, accepted: 5m, damaged: 5m);
        var transitBefore = await TransitQohAsync(s);

        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m), "lines[0].quantity");         // recorded, not posted
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Missing, DispositionAction.WriteOff, 1m), "lines[0].quantity");         // never recorded as missing
        await AssertNothingOpenedAsync(d, transitBefore, "unposted");

        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(s.W.Admin, s.Src, d, rid)).StatusCode);
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 6m), "lines[0].quantity");
        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 5m)).StatusCode);

        // A second receipt records the rest as missing: unposted again, so still not dispositionable until it is posted.
        var rid2 = await RecordOkAsync(s.W.Admin, s.Src, d, accepted: 0m, missing: 20m);
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Missing, DispositionAction.WriteOff, 1m), "lines[0].quantity");
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(s.W.Admin, s.Src, d, rid2)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(d, DispositionBucket.Missing, DispositionAction.ReturnToSource, 20m)).StatusCode);
        Assert.Equal(("Reconciled", "Completed"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        await AssertCleanAsync(s, "unposted disposition");
    }

    [Fact]
    public async Task Disposition_OfAConsignmentThatNeedsNone_OrIsAlreadyReconciled_IsRefused()
    {
        var d = await PostedAsync(accepted: 10m, dispatched: 10m);             // no discrepancy: straight to Reconciled at its last post
        Assert.Equal(("Reconciled", "Completed"), (await ConsignmentStatusAsync(d.S.W, d.ConsId), await ShipmentStatusAsync(d.S.W, d.ShipmentId)));
        var transitBefore = await TransitQohAsync(d.S);
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m), "status");
        await AssertNothingOpenedAsync(d, transitBefore, "reconciled");

        var undispatched = await ReadySceneAsync(postgres, 50m, 5m, (main.World).W);          // loaded, never dispatched: nothing to disposition
        var shipment = await OkAsync(await GetShipmentAsync(undispatched.S, undispatched.ShipmentId));
        var cons = ConsignmentTo(shipment, undispatched.S.AStore);
        var line = LineOfConsignment(cons).GetProperty("id").GetGuid();
        var refused = await SendAsync(undispatched.S.W.Admin, HttpMethod.Post, $"/api/v1/consignments/{IdOf(cons)}/dispositions",
            new RecordDispositionRequest("x", [new DispositionLineRequest(line, DispositionBucket.Missing, DispositionAction.WriteOff, 1m)]), undispatched.S.Src, NewKey());
        await AssertRefusedAsync(refused, "status");
        await AssertCleanAsync(undispatched.S, "undispatched disposition");
        await AssertCleanAsync(d.S, "reconciled disposition");
    }

    // ---- parallel and idempotent ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 1)]       // three of 4 each: only one fits
    [InlineData(1, 4)]       // one of 4 each, six requests: exactly four fit
    public async Task ParallelDispositions_OfTheSameBucket_NeverExceedIt_TheRestAreRefused(int each, int expectedCreated)
    {
        var d = await PostedAsync(accepted: 26m, damaged: 4m);
        var s = d.S;
        var transitBefore = await TransitQohAsync(s);
        DispositionAction[] actions = [DispositionAction.WriteOff, DispositionAction.ReturnToSource, DispositionAction.AcceptToDestination];

        var responses = await ParallelAsync(Enumerable.Range(0, 6).Select(i => (Func<Task<HttpResponseMessage>>)(() => DisposeAsync(d, DispositionBucket.Damaged, actions[i % 3], each))).ToArray());

        await AssertNo500Async(s.W, "parallel dispositions", responses);
        Assert.Equal(expectedCreated, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(x => x.StatusCode != HttpStatusCode.Created), x => Assert.Equal(HttpStatusCode.BadRequest, x.StatusCode));
        var done = expectedCreated * each;
        Assert.Equal((decimal)done, await LineAsync(d, "DispositionedDamagedBaseQty"));
        Assert.Equal((long)expectedCreated, await DispositionCountAsync(d));
        Assert.Equal((decimal)done, await BatchRowsAsync(d, "WrittenOffBaseQty") + await BatchRowsAsync(d, "ReturnedBaseQty") + await BatchRowsAsync(d, "AcceptedCorrectionBaseQty"));
        Assert.Equal(transitBefore - done, await TransitQohAsync(s));
        Assert.True(await LineAsync(d, "DispositionedDamagedBaseQty") <= await LineAsync(d, "DamagedBaseQty"), "dispositioned exceeds the bucket");
        await AssertCleanAsync(s, "parallel dispositions");
    }

    [Fact]
    public async Task Disposition_RetriedWithTheSameKey_IsIdempotent_AlsoInParallel_AndTheKeyIsBoundToItsConsignment()
    {
        var d = await PostedAsync(accepted: 20m, damaged: 10m);
        var s = d.S;
        var transitBefore = await TransitQohAsync(s);
        var key = NewKey("idem");

        var first = await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 4m, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = IdOf(await JsonAsync(first));
        var replay = await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 4m, key);
        Assert.Equal((HttpStatusCode.Created, id), (replay.StatusCode, IdOf(await JsonAsync(replay))));
        Assert.Equal((1L, 4m, transitBefore - 4m, 1L), (await DispositionCountAsync(d), await LineAsync(d, "DispositionedDamagedBaseQty"), await TransitQohAsync(s), await AuditAsync(d, "consignment.dispositioned")));

        var parallelKey = NewKey("idem-par");
        var responses = await ParallelAsync(Enumerable.Range(0, 5).Select(_ => (Func<Task<HttpResponseMessage>>)(() => DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 3m, parallelKey))).ToArray());
        await AssertNo500Async(s.W, "parallel same-key dispositions", responses);
        Assert.All(responses, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
        var ids = (await Task.WhenAll(responses.Select(async x => IdOf(await JsonAsync(x))))).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal((2L, 7m, transitBefore - 7m), (await DispositionCountAsync(d), await LineAsync(d, "DispositionedDamagedBaseQty"), await TransitQohAsync(s)));
        Assert.Equal(3m, await BatchRowsAsync(d, "ReturnedBaseQty"));

        // The same key on another consignment is refused, and moves nothing there.
        var other = await PostedAsync(accepted: 10m, damaged: 5m, dispatched: 15m);
        var otherTransit = await TransitQohAsync(other.S);
        await AssertRefusedAsync(await DisposeAsync(other, DispositionBucket.Damaged, DispositionAction.WriteOff, 5m, key), "idempotencyKey");
        Assert.Equal((0L, otherTransit), (await DispositionCountAsync(other), await TransitQohAsync(other.S)));
        await AssertCleanAsync(s, "idempotent dispositions");
        await AssertCleanAsync(other.S, "idempotent dispositions (other)");
    }

    // ---- 7. completion --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Completion_ConsignmentIsReconciledOnlyWhenEverythingIsAccountedAndDispositioned_TransitViewsFollow()
    {
        var d = await DispatchedAsync(main.World, 100m, 30m);
        var s = d.S;
        var admin = s.W.Admin;

        async Task<(bool Current, bool Discrepancies, bool Completed)> ViewsAsync() => (
            (await TransitIdsAsync(admin, s.Src, "current")).Contains(d.ConsId),
            (await TransitIdsAsync(admin, s.Src, "discrepancies")).Contains(d.ConsId),
            (await TransitIdsAsync(admin, s.Src, "completed")).Contains(d.ConsId));

        // Nothing recorded: InTransit, not completed.
        Assert.Equal(("InTransit", "Dispatched"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        Assert.Equal((true, false, false), await ViewsAsync());

        // 10 accepted posted: partially received, still current.
        var r1 = await RecordOkAsync(admin, s.Src, d, accepted: 10m);
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(admin, s.Src, d, r1)).StatusCode);
        Assert.Equal(("PartiallyReceived", "Dispatched"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        Assert.Equal((true, false, false), await ViewsAsync());

        // 10 more recorded but NOT posted, the last 10 as 5 damaged + 5 rejected: all accounted, yet not Received (unposted receipt).
        var r2 = await RecordOkAsync(admin, s.Src, d, accepted: 10m);
        var r3 = await RecordOkAsync(admin, s.Src, d, accepted: 0m, damaged: 5m, rejected: 5m);
        Assert.Equal(("PartiallyReceived", "Dispatched"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(admin, s.Src, d, r2)).StatusCode);
        Assert.Equal("PartiallyReceived", await ConsignmentStatusAsync(s.W, d.ConsId));                    // r3 still unposted
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(admin, s.Src, d, r3)).StatusCode);

        // Received = accounted + posted, discrepancies open: Delivered shipment, shown under discrepancies, NOT completed.
        Assert.Equal(("Received", "Delivered"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        Assert.Equal((false, true, false), await ViewsAsync());
        await AssertCleanAsync(s, "received, discrepancies open");

        // Partial disposition of one bucket, then the other bucket: still not reconciled while any unit is open.
        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 5m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(d, DispositionBucket.Rejected, DispositionAction.ReturnToSource, 4m)).StatusCode);
        Assert.Equal(("Received", "Delivered"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        Assert.Equal((false, true, false), await ViewsAsync());

        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(d, DispositionBucket.Rejected, DispositionAction.AcceptToDestination, 1m)).StatusCode);
        Assert.Equal(("Reconciled", "Completed"), (await ConsignmentStatusAsync(s.W, d.ConsId), await ShipmentStatusAsync(s.W, d.ShipmentId)));
        Assert.Equal((false, false, true), await ViewsAsync());
        Assert.Equal((5m, 5m, 0m), (await LineAsync(d, "DispositionedDamagedBaseQty"), await LineAsync(d, "DispositionedRejectedBaseQty"), await LineAsync(d, "DispositionedMissingBaseQty")));

        // Final stock: source 70 + 4 returned; destination 20 + 1 accepted; 5 written off; Transit empty of this consignment.
        Assert.Equal((74m, 21m), (await QohAsync(s), await QohAsync(s, s.AStore)));
        await AssertRefusedAsync(await DisposeAsync(d, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m), "status");
        await AssertCleanAsync(s, "reconciled");
    }

    [Fact]
    public async Task Completion_ShipmentIsCompletedOnlyWhenAllItsConsignmentsAreReconciled()
    {
        var s = await NewSceneAsync(postgres, 100m, (main.World).W);
        var reqA = await RequestAsync(s, s.AStore, 20m);
        var reqB = await RequestAsync(s, s.BStore, 10m);
        var ready = await ReadyShipmentAsync(s, (reqA, 20m), (reqB, 10m));
        var sid = IdOf(ready);
        var shipment = await OkAsync(await DispatchShipmentAsync(s, sid));
        DispatchedScene Scene(Guid store, JsonElement request)
        {
            var c = ConsignmentTo(shipment, store);
            return new DispatchedScene(s, sid, IdOf(c), LineOfConsignment(c).GetProperty("id").GetGuid(), c.GetProperty("consignmentNo").GetString()!, IdOf(request));
        }
        var (a, b) = (Scene(s.AStore, reqA), Scene(s.BStore, reqB));
        var admin = s.W.Admin;

        // A: received in full, no discrepancy -> Reconciled at once; B is still in transit -> shipment PartiallyDelivered.
        var ra = await RecordOkAsync(admin, s.Src, a, accepted: 20m);
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(admin, s.Src, a, ra)).StatusCode);
        Assert.Equal(("Reconciled", "InTransit", "PartiallyDelivered"), (await ConsignmentStatusAsync(s.W, a.ConsId), await ConsignmentStatusAsync(s.W, b.ConsId), await ShipmentStatusAsync(s.W, sid)));
        Assert.Contains(a.ConsId, await TransitIdsAsync(admin, s.Src, "completed"));
        Assert.DoesNotContain(b.ConsId, await TransitIdsAsync(admin, s.Src, "completed"));

        // B: 8 accepted + 2 damaged -> Received (discrepancy open) -> shipment Delivered, not Completed.
        var rb = await RecordOkAsync(admin, s.Src, b, accepted: 8m, damaged: 2m);
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(admin, s.Src, b, rb)).StatusCode);
        Assert.Equal(("Reconciled", "Received", "Delivered"), (await ConsignmentStatusAsync(s.W, a.ConsId), await ConsignmentStatusAsync(s.W, b.ConsId), await ShipmentStatusAsync(s.W, sid)));
        Assert.DoesNotContain(b.ConsId, await TransitIdsAsync(admin, s.Src, "completed"));
        Assert.Contains(b.ConsId, await TransitIdsAsync(admin, s.Src, "discrepancies"));
        Assert.DoesNotContain(a.ConsId, await TransitIdsAsync(admin, s.Src, "discrepancies"));
        await AssertCleanAsync(s, "delivered, discrepancy open");

        // Dispositioning B's damaged units completes it, and with it the shipment.
        Assert.Equal(HttpStatusCode.Created, (await DisposeAsync(b, DispositionBucket.Damaged, DispositionAction.ReturnToSource, 2m)).StatusCode);
        Assert.Equal(("Reconciled", "Reconciled", "Completed"), (await ConsignmentStatusAsync(s.W, a.ConsId), await ConsignmentStatusAsync(s.W, b.ConsId), await ShipmentStatusAsync(s.W, sid)));
        Assert.DoesNotContain(b.ConsId, await TransitIdsAsync(admin, s.Src, "discrepancies"));
        Assert.Contains(b.ConsId, await TransitIdsAsync(admin, s.Src, "completed"));
        Assert.Equal((72m, 20m, 8m), (await QohAsync(s), await QohAsync(s, s.AStore), await QohAsync(s, s.BStore)));   // 100 - 30 dispatched + 2 returned
        await AssertCleanAsync(s, "completed shipment");
    }
}
