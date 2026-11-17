using System.Net;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.ShipmentHttp;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Functional-improvements Phase 05 (FI-D34..D37) over HTTP on PostgreSQL: shipment dispatch (FIFO to Transit, reservations consumed once, request status,
/// idempotency, one dispatch under a parallel hammer), consignment receipts (verify moves nothing, post moves only the accepted quantity once, partial and parallel
/// receipts), the shipment / consignment status machines, the transit views with destination isolation, permissions, and the reservation + transit reconciliation scripts.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ShipmentDispatchApiTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim RoleGate = new(1, 1);
    private static readonly Dictionary<string, Guid> RoleByName = [];

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string name, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        await RoleGate.WaitAsync();
        try
        {
            var key = name + ":" + branch;
            if (RoleByName.TryGetValue(key, out var id)) return id;
            var response = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest("SDP " + name + Guid.NewGuid().ToString("N")[..6], null, codes, scope, branch));
            Check(response, "create role " + name);
            return RoleByName[key] = IdOf(await JsonAsync(response));
        }
        finally { RoleGate.Release(); }
    }

    private static async Task<RestrictedUser> BranchUserAsync(RestrictedUserWorld r, Guid branch, params string[] codes)
    {
        var user = await r.CreateAsync("sd", await RoleAsync(r.World, "harmless", ["Sales.View"]), CompanyAccessScope.AssignedBranches, [branch], branch);
        var assigned = await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles",
            new AssignRoleRequest(await RoleAsync(r.World, "br" + string.Join("", codes.Select(c => c.Replace(".", ""))), codes, RoleScope.Branch, branch), null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        return user;
    }

    private static Task<HttpResponseMessage> DispatchAsync(ShipScene s, Guid shipmentId, string? key = null, HttpClient? client = null, Guid? branch = null) =>
        SendAsync(client ?? s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{shipmentId}/dispatch", null, branch ?? s.Src, key);

    private static Task<HttpResponseMessage> RecordAsync(HttpClient client, Guid branch, Guid consignmentId, Guid lineId, decimal accepted, decimal damaged = 0m, decimal rejected = 0m, decimal missing = 0m, string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{consignmentId}/receipts",
            new RecordReceiptRequest([new ReceiptLineRequest(lineId, accepted, damaged, rejected, missing)], "api test"), branch, key);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid branch, Guid consignmentId, Guid receiptId, string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{consignmentId}/receipts/{receiptId}/post", null, branch, key);

    private static Guid LineIdOf(JsonElement consignment) => consignment.GetProperty("lines")[0].GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> TransitAsync(HttpClient client, Guid branch, string view, string extra = "") =>
        (await OkAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/transit?view={view}&pageSize=100{extra}", null, branch))).GetProperty("items").EnumerateArray().ToList();

    private static async Task<string> StatusAsync(BranchApiWorld w, string table, Guid id) =>
        await ScalarAsync<string>(w.Host.Database, $"""SELECT "Status" FROM "{table}" WHERE "Id" = '{id}'""");

    private static async Task<decimal> TransitQohAsync(ShipScene s) =>
        await ScalarAsync<decimal>(s.W.Host.Database,
            $"SELECT COALESCE(SUM(i.\"QuantityOnHand\"), 0) FROM \"StockItems\" i JOIN \"StockLocations\" l ON l.\"Id\" = i.\"StockLocationId\" WHERE l.\"LocationType\" = 'Transit' AND i.\"ProductId\" = '{s.W.ProductId}'");

    // ---- 1. the whole flow --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_Receive_Post_ThroughTheApi_MovesStockOnce_DrivesBothStatusMachines_AndReconciles()
    {
        var s = await NewSceneAsync(postgres, 100m);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var transitBefore = await TransitQohAsync(s);
        var reqA = await RequestAsync(s, s.AStore, 30m);
        var reqB = await RequestAsync(s, s.BStore, 20m);
        var ready = await ReadyShipmentAsync(s, (reqA, 30m), (reqB, 20m));
        var sid = IdOf(ready);
        var (consA, consB) = (ConsignmentTo(ready, s.AStore), ConsignmentTo(ready, s.BStore));
        Assert.Equal(50m, await ReservedAsync(s));

        // ---- dispatch (Idempotency-Key replay returns the stored result)
        var key = "disp-" + Guid.NewGuid().ToString("N");
        var dispatched = await OkAsync(await DispatchAsync(s, sid, key));
        Assert.Equal("Dispatched", dispatched.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, dispatched.GetProperty("dispatched").ValueKind);
        Assert.All(dispatched.GetProperty("consignments").EnumerateArray(), c => Assert.Equal("InTransit", c.GetProperty("status").GetString()));
        Assert.Equal((50m, 0m, transitBefore + 50m), (await QohAsync(s), await ReservedAsync(s), await TransitQohAsync(s)));
        Assert.Equal("Fulfilled", await StatusAsync(s.W, "StockTransfers", IdOf(reqA)));
        Assert.Equal("Fulfilled", await StatusAsync(s.W, "StockTransfers", IdOf(reqB)));
        Assert.Equal("Consumed", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockReservations" WHERE "ConsignmentLineId" = '{LineIdOf(consA)}'"""));
        Assert.Equal(1L, await AuditCountAsync(s.W, sid, "shipment.dispatched"));

        var replay = await OkAsync(await DispatchAsync(s, sid, key));
        Assert.Equal("Dispatched", replay.GetProperty("status").GetString());
        Assert.Equal((50m, transitBefore + 50m), (await QohAsync(s), await TransitQohAsync(s)));
        Assert.Equal(1L, await AuditCountAsync(s.W, sid, "shipment.dispatched"));
        await AssertRefusedAsync(await DispatchAsync(s, sid), "status");                                       // without a key a second dispatch is a status error
        Assert.Equal(1L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" = '{LineIdOf(consA)}'"""));
        await AssertReservationInvariantsAsync(s, "dispatched");
        await AssertTransitReconciliationCleanAsync(s.W.Host.Database, "dispatched");

        // ---- transit views: the source sees both (outgoing), each destination only its own (incoming)
        var outgoing = await TransitAsync(s.W.Admin, s.Src, "outgoing");
        Assert.Contains(IdOfConsignment(consA), outgoing.Select(i => i.GetProperty("consignmentId").GetGuid()));
        Assert.Contains(IdOfConsignment(consB), outgoing.Select(i => i.GetProperty("consignmentId").GetGuid()));
        Assert.All(outgoing.Where(i => i.GetProperty("shipmentId").ValueKind != JsonValueKind.Null), i => Assert.Equal("outgoing", i.GetProperty("direction").GetString()));
        var readerA = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var readerB = await BranchUserAsync(r, s.B, "Shipments.View", "StockTransfers.Receive");
        var atA = await TransitAsync(readerA.Client, s.A, "incoming");
        Assert.Equal([IdOfConsignment(consA)], atA.Select(i => i.GetProperty("consignmentId").GetGuid()).ToList());
        Assert.Equal(JsonValueKind.Null, atA[0].GetProperty("shipmentId").ValueKind);
        Assert.Equal("incoming", atA[0].GetProperty("direction").GetString());
        Assert.Equal(30m, atA[0].GetProperty("lines")[0].GetProperty("dispatchedBaseQty").GetDecimal());
        Assert.Empty(await TransitAsync(readerA.Client, s.A, "outgoing"));
        Assert.Equal([IdOfConsignment(consB)], (await TransitAsync(readerB.Client, s.B, "current")).Select(i => i.GetProperty("consignmentId").GetGuid()).ToList());
        Assert.Contains(IdOfConsignment(consA), (await TransitAsync(s.W.Admin, s.A, "incoming")).Select(i => i.GetProperty("consignmentId").GetGuid()));
        Assert.Contains(IdOfConsignment(consA), (await TransitAsync(s.W.Admin, s.Src, "current", "&search=" + consA.GetProperty("consignmentNo").GetString())).Select(i => i.GetProperty("consignmentId").GetGuid()));
        Assert.Empty(await TransitAsync(s.W.Admin, s.Src, "current", "&search=zzz-nothing-matches"));

        // ---- verify at A: 20 accepted, 5 damaged, 5 missing; nothing moves
        var recorded = await RecordAsync(readerA.Client, s.A, IdOfConsignment(consA), LineIdOf(consA), accepted: 20m, damaged: 5m, missing: 5m, key: "rec-" + key);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var receipt = await JsonAsync(recorded);
        var receiptId = IdOf(receipt);
        Assert.Equal(("Verified", 1, "PartiallyReceived"), (receipt.GetProperty("status").GetString(), receipt.GetProperty("sequence").GetInt32(), receipt.GetProperty("consignmentStatus").GetString()));
        Assert.Equal(0m, await QohAsync(s, s.AStore));
        Assert.Equal(transitBefore + 50m, await TransitQohAsync(s));
        var recordedAgain = await JsonAsync(await RecordAsync(readerA.Client, s.A, IdOfConsignment(consA), LineIdOf(consA), accepted: 20m, damaged: 5m, missing: 5m, key: "rec-" + key));
        Assert.Equal(receiptId, IdOf(recordedAgain));                                                            // replay: the same receipt, counted once
        await AssertRefusedAsync(await RecordAsync(readerA.Client, s.A, IdOfConsignment(consA), LineIdOf(consA), accepted: 1m), "lines[0].acceptedQuantity"); // 31 > 30 dispatched
        Assert.Equal(HttpStatusCode.NotFound, (await RecordAsync(readerB.Client, s.B, IdOfConsignment(consA), LineIdOf(consA), accepted: 1m)).StatusCode);   // B cannot touch A's consignment
        // A company-level holder of StockTransfers.Receive (the admin) may receive at any branch's destination (FI-D35, like the legacy receive): it is validated, not hidden.
        await AssertRefusedAsync(await RecordAsync(s.W.Admin, s.Src, IdOfConsignment(consA), LineIdOf(consA), accepted: 1m), "lines[0].acceptedQuantity");

        var discrepancies = await TransitAsync(s.W.Admin, s.Src, "discrepancies");
        Assert.Contains(IdOfConsignment(consA), discrepancies.Select(i => i.GetProperty("consignmentId").GetGuid()));
        Assert.DoesNotContain(IdOfConsignment(consB), discrepancies.Select(i => i.GetProperty("consignmentId").GetGuid()));
        Assert.True(discrepancies.First(i => i.GetProperty("consignmentId").GetGuid() == IdOfConsignment(consA)).GetProperty("hasDiscrepancy").GetBoolean());

        // ---- post: only the accepted quantity moves, exactly once
        var posted = await OkAsync(await PostAsync(readerA.Client, s.A, IdOfConsignment(consA), receiptId, "post-" + key));
        Assert.Equal("Posted", posted.GetProperty("status").GetString());
        Assert.Equal((20m, transitBefore + 30m), (await QohAsync(s, s.AStore), await TransitQohAsync(s)));    // 10 pc (damaged + missing) stay in Transit
        Assert.Equal("Received", await StatusAsync(s.W, "Consignments", IdOfConsignment(consA)));
        Assert.Equal("PartiallyDelivered", await StatusAsync(s.W, "Shipments", sid));                         // B is still in transit
        await OkAsync(await PostAsync(readerA.Client, s.A, IdOfConsignment(consA), receiptId, "post-" + key)); // replay
        await OkAsync(await PostAsync(readerA.Client, s.A, IdOfConsignment(consA), receiptId));                // no key: idempotent by state
        Assert.Equal(20m, await QohAsync(s, s.AStore));
        Assert.Equal(1L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'TransferIn' AND "StockLocationId" = '{s.AStore}' AND "ReferenceNo" IS NOT NULL AND "Notes" LIKE '{consA.GetProperty("consignmentNo").GetString()}%'"""));
        Assert.Equal(0m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT COALESCE(SUM("DamagedBaseQty"), 0) FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" = '{LineIdOf(consA)}'""")); // not written to the batch counter

        var receipts = await OkAsync(await SendAsync(readerA.Client, HttpMethod.Get, $"/api/v1/consignments/{IdOfConsignment(consA)}/receipts", null, s.A));
        Assert.Equal(["Posted"], receipts.EnumerateArray().Select(x => x.GetProperty("status").GetString()).ToList());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/consignments/{IdOfConsignment(consA)}/receipts/{receiptId}", null, s.Src)).StatusCode); // the source reads it too
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(readerB.Client, HttpMethod.Get, $"/api/v1/consignments/{IdOfConsignment(consA)}/receipts", null, s.B)).StatusCode);

        // ---- B receives in two partial receipts, posted in turn
        var b1 = IdOf(await JsonAsync(await RecordAsync(readerB.Client, s.B, IdOfConsignment(consB), LineIdOf(consB), accepted: 12m)));
        var b2 = IdOf(await JsonAsync(await RecordAsync(readerB.Client, s.B, IdOfConsignment(consB), LineIdOf(consB), accepted: 8m)));
        await OkAsync(await PostAsync(readerB.Client, s.B, IdOfConsignment(consB), b2));
        Assert.Equal("PartiallyReceived", await StatusAsync(s.W, "Consignments", IdOfConsignment(consB)));
        Assert.Equal("PartiallyDelivered", await StatusAsync(s.W, "Shipments", sid));
        await OkAsync(await PostAsync(readerB.Client, s.B, IdOfConsignment(consB), b1));
        Assert.Equal(("Received", "Delivered"), (await StatusAsync(s.W, "Consignments", IdOfConsignment(consB)), await StatusAsync(s.W, "Shipments", sid)));
        Assert.Equal((20m, transitBefore + 10m), (await QohAsync(s, s.BStore), await TransitQohAsync(s)));

        var completed = (await TransitAsync(s.W.Admin, s.Src, "completed")).Select(i => i.GetProperty("consignmentId").GetGuid()).ToList();
        Assert.Contains(IdOfConsignment(consA), completed);
        Assert.Contains(IdOfConsignment(consB), completed);
        Assert.DoesNotContain(IdOfConsignment(consA), (await TransitAsync(s.W.Admin, s.Src, "current")).Select(i => i.GetProperty("consignmentId").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s.W.Admin, HttpMethod.Get, "/api/v1/transit?view=nonsense", null, s.Src)).StatusCode);

        await AssertReservationInvariantsAsync(s, "delivered");
        await AssertTransitReconciliationCleanAsync(s.W.Host.Database, "delivered");
    }

    private static Guid IdOfConsignment(JsonElement consignment) => consignment.GetProperty("id").GetGuid();

    // ---- 2. concurrency -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelDispatches_OfOneShipment_MoveTheStockExactlyOnce(bool withKey)
    {
        var s = await NewSceneAsync(postgres, 100m);
        var req = await RequestAsync(s, s.AStore, 40m);
        var ready = await ReadyShipmentAsync(s, (req, 40m));
        var sid = IdOf(ready);
        var key = withKey ? "hammer-" + Guid.NewGuid().ToString("N") : null;

        var responses = await ParallelAsync(Enumerable.Range(0, 6).Select(_ => (Func<Task<HttpResponseMessage>>)(() => DispatchAsync(s, sid, key))).ToArray());

        await AssertNo500Async("parallel dispatch", responses);
        var ok = responses.Count(x => x.StatusCode == HttpStatusCode.OK);
        Assert.Equal(withKey ? 6 : 1, ok);                                                                    // with a key every call replays the first; without one the rest are status errors
        Assert.All(responses.Where(x => x.StatusCode != HttpStatusCode.OK), x => Assert.Equal(HttpStatusCode.BadRequest, x.StatusCode));
        Assert.Equal((60m, 0m), (await QohAsync(s), await ReservedAsync(s)));
        Assert.Equal(1L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'TransferOut' AND "StockLocationId" = '{s.SrcStore}' AND "ProductId" = '{s.W.ProductId}' AND "Notes" LIKE '{ready.GetProperty("shipmentNo").GetString()}%'"""));
        await AssertReservationInvariantsAsync(s, "parallel dispatch");
        await AssertTransitReconciliationCleanAsync(s.W.Host.Database, "parallel dispatch");
    }

    [Fact]
    public async Task ParallelReceipts_AndPosts_NeverAccountForMoreThanWasDispatched_AndPostOnce()
    {
        var s = await NewSceneAsync(postgres, 100m);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var req = await RequestAsync(s, s.AStore, 10m);
        var ready = await ReadyShipmentAsync(s, (req, 10m));
        var cons = ConsignmentTo(ready, s.AStore);
        await OkAsync(await DispatchAsync(s, IdOf(ready)));
        var reader = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");

        // Four receipts of 4 each race for 10 dispatched: at most two fit.
        var recorded = await ParallelAsync(Enumerable.Range(0, 4).Select(_ => (Func<Task<HttpResponseMessage>>)(() => RecordAsync(reader.Client, s.A, IdOfConsignment(cons), LineIdOf(cons), accepted: 4m))).ToArray());
        await AssertNo500Async("parallel receipts", recorded);
        var created = recorded.Where(x => x.StatusCode == HttpStatusCode.Created).ToList();
        Assert.Equal(2, created.Count);
        Assert.All(recorded.Where(x => x.StatusCode != HttpStatusCode.Created), x => Assert.Equal(HttpStatusCode.BadRequest, x.StatusCode));
        Assert.Equal(8m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AcceptedBaseQty" FROM "ConsignmentLines" WHERE "Id" = '{LineIdOf(cons)}'"""));

        // The same receipt posted from many requests at once moves its 4 pieces once.
        var receiptId = IdOf(await JsonAsync(created[0]));
        var posts = await ParallelAsync(Enumerable.Range(0, 5).Select(_ => (Func<Task<HttpResponseMessage>>)(() => PostAsync(reader.Client, s.A, IdOfConsignment(cons), receiptId))).ToArray());
        await AssertNo500Async("parallel posts", posts);
        Assert.All(posts, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        Assert.Equal(4m, await QohAsync(s, s.AStore));
        Assert.Equal(4m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "PostedBaseQty" FROM "ConsignmentLines" WHERE "Id" = '{LineIdOf(cons)}'"""));
        await AssertTransitReconciliationCleanAsync(s.W.Host.Database, "parallel receipts");
    }

    [Fact]
    public async Task CloseOfTheRequest_RacingTheShipmentDispatch_EndsConsistently_NoCheckViolationOr500()
    {
        var s = await NewSceneAsync(postgres, 100m);
        for (var round = 0; round < 3; round++)
        {
            var req = await RequestAsync(s, s.AStore, 10m);
            var ready = await ReadyShipmentAsync(s, (req, 4m));
            var close = () => SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(req)}/close", new CloseStockTransferRequest("race"), s.Src);

            var results = await ParallelAsync(() => DispatchAsync(s, IdOf(ready)), close);
            await AssertNo500Async($"round {round}", results);

            // The dispatch always wins its shipment. The close is refused while 4 are still allocated (it queued first) or closes the undispatched 6 after the dispatch.
            Assert.Equal(HttpStatusCode.OK, results[0].StatusCode);
            var closed = results[1].StatusCode == HttpStatusCode.OK;
            Assert.True(closed || results[1].StatusCode == HttpStatusCode.BadRequest, $"close {(int)results[1].StatusCode}");
            Assert.Equal((closed ? "Closed" : "PartiallyFulfilled", closed ? 6m : 0m), (await StatusAsync(s.W, "StockTransfers", IdOf(req)),
                await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT SUM("ClosedBaseQty") FROM "StockTransferLines" WHERE "TransferId" = '{IdOf(req)}'""")));
            Assert.Equal(4m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT SUM("DispatchedBaseQty") FROM "StockTransferLines" WHERE "TransferId" = '{IdOf(req)}'"""));
        }
        await AssertReservationInvariantsAsync(s, "close vs dispatch");
        await AssertTransitReconciliationCleanAsync(s.W.Host.Database, "close vs dispatch");
    }

    // ---- 3. permissions and visibility --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_NeedsStockTransfersDispatch_AndReceiptsNeedStockTransfersReceive_AtTheRightBranch()
    {
        var s = await NewSceneAsync(postgres, 100m);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var req = await RequestAsync(s, s.AStore, 10m);
        var ready = await ReadyShipmentAsync(s, (req, 10m));
        var cons = ConsignmentTo(ready, s.AStore);

        var viewer = await BranchUserAsync(r, s.Src, "Shipments.View", "Shipments.Plan", "Shipments.Load");              // no StockTransfers.Dispatch
        Assert.Equal(HttpStatusCode.Forbidden, (await DispatchAsync(s, IdOf(ready), client: viewer.Client)).StatusCode);
        var dispatcher = await BranchUserAsync(r, s.Src, "Shipments.View", "StockTransfers.Dispatch");
        var otherBranch = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Dispatch");
        Assert.Equal(HttpStatusCode.NotFound, (await DispatchAsync(s, IdOf(ready), client: otherBranch.Client, branch: s.A)).StatusCode);   // another branch cannot even see the shipment
        Assert.Equal(HttpStatusCode.OK, (await DispatchAsync(s, IdOf(ready), client: dispatcher.Client)).StatusCode);

        var noReceive = await BranchUserAsync(r, s.A, "Shipments.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await RecordAsync(noReceive.Client, s.A, IdOfConsignment(cons), LineIdOf(cons), accepted: 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(noReceive.Client, s.A, IdOfConsignment(cons), Guid.NewGuid())).StatusCode);
        var wrongBranch = await BranchUserAsync(r, s.B, "StockTransfers.Receive");
        Assert.Equal(HttpStatusCode.NotFound, (await RecordAsync(wrongBranch.Client, s.B, IdOfConsignment(cons), LineIdOf(cons), accepted: 1m)).StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "ConsignmentReceipts" WHERE "ConsignmentId" = '{IdOfConsignment(cons)}'"""));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(wrongBranch.Client, HttpMethod.Post, $"/api/v1/consignments/{IdOfConsignment(cons)}/receipts", new { lines = Array.Empty<object>() }, s.B)).StatusCode);

        // A consignment of a shipment that was never dispatched cannot be received.
        var req2 = await RequestAsync(s, s.AStore, 5m);
        var undispatched = await ReadyShipmentAsync(s, (req2, 5m));
        var receiver = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        await AssertRefusedAsync(await RecordAsync(receiver.Client, s.A, IdOfConsignment(ConsignmentTo(undispatched, s.AStore)), LineIdOf(ConsignmentTo(undispatched, s.AStore)), accepted: 1m), "status");
        await AssertReservationInvariantsAsync(s, "permissions");
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field.ToLowerInvariant(), await ErrorFieldsAsync(response));
    }
}
