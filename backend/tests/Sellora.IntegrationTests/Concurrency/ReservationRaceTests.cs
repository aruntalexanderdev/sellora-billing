using System.Net;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Api;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.ShipmentHttp;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 04 reservations under concurrency (FI-D5, FI-D30, FI-D33) through the real HTTP pipeline on PostgreSQL. Every assertion holds for every interleaving:
/// no 5xx (a CHECK violation, deadlock or unique-key failure would surface as one), losers are clean 400s, StockItems.ReservedQuantity equals the sum of Active
/// reservations, 0 &lt;= reserved &lt;= on hand, and the FI-D5 reconciliation script (queries 1-3) returns no rows. Races are repeated on fresh scenes to vary the interleaving.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReservationRaceTests(PostgresFixture postgres)
{
    private const int Rounds = 3;

    private static int Code(HttpResponseMessage r) => (int)r.StatusCode;
    private static int Count(IEnumerable<HttpResponseMessage> rs, HttpStatusCode code) => rs.Count(r => r.StatusCode == code);

    private static async Task AssertCleanAsync(ShipScene s, string when, params HttpResponseMessage[] responses)
    {
        await AssertNo500Async(when, responses);
        await AssertReservationInvariantsAsync(s, when);
    }

    private static Task<HttpResponseMessage> SaleAsync(ShipScene s, decimal pieces) =>
        SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(s.W.CustomerId, Today, PaymentMethod.Cash, 0m,
            [new CreateSaleLineRequest(s.W.ProductId, s.Pc, pieces, 20m, 0)]), s.Src);

    // ---- concurrent allocations -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentAllocations_ExceedingAvailable_ExactlyTheAllowedOnesSucceed_OthersAreCleanRefusals()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var s = await NewSceneAsync(postgres, 10m);
            var requests = new List<JsonElement>();
            for (var i = 0; i < 6; i++) requests.Add(await RequestAsync(s, s.AStore, 3m));
            var shipments = new[] { IdOf(await NewShipmentAsync(s)), IdOf(await NewShipmentAsync(s)) };

            var responses = await ParallelAsync(requests.Select((req, i) => (Func<Task<HttpResponseMessage>>)(() => AllocateAsync(s, shipments[i % 2], LineOf(req), 3m))).ToArray());

            await AssertCleanAsync(s, $"round {round}: 6 x 3 against 10", responses);
            Assert.True(Count(responses, HttpStatusCode.OK) == 3 && Count(responses, HttpStatusCode.BadRequest) == 3,
                $"round {round}: expected 3 OK + 3 x 400, got [{string.Join(",", responses.Select(Code))}]");
            Assert.Equal(9m, await ReservedAsync(s));
            Assert.Equal(10m, await QohAsync(s));
        }
    }

    [Fact]
    public async Task ConcurrentAllocations_ToTheSameDestination_OnOneShipment_ShareOneConsignment()
    {
        var s = await NewSceneAsync(postgres, 20m);
        var requests = new List<JsonElement>();
        for (var i = 0; i < 4; i++) requests.Add(await RequestAsync(s, s.AStore, 3m));
        var sid = IdOf(await NewShipmentAsync(s));

        var responses = await ParallelAsync(requests.Select(req => (Func<Task<HttpResponseMessage>>)(() => AllocateAsync(s, sid, LineOf(req), 3m))).ToArray());

        await AssertCleanAsync(s, "same-destination allocations", responses);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var detail = await OkAsync(await GetShipmentAsync(s, sid));
        var consignment = Assert.Single(detail.GetProperty("consignments").EnumerateArray());
        Assert.Equal(4, consignment.GetProperty("lines").GetArrayLength());
        Assert.Equal(12m, await ReservedAsync(s));
        Assert.Equal(1L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "Consignments" WHERE "ShipmentId" = '{sid}'"""));
    }

    [Fact]
    public async Task ConcurrentAllocationsOfTheSameRequestLine_NeverExceedTheRequest()
    {
        var s = await NewSceneAsync(postgres, 50m);
        var req = await RequestAsync(s, s.AStore, 10m);
        var shipments = new[] { IdOf(await NewShipmentAsync(s)), IdOf(await NewShipmentAsync(s)), IdOf(await NewShipmentAsync(s)) };

        var responses = await ParallelAsync(Enumerable.Range(0, 6).Select(i => (Func<Task<HttpResponseMessage>>)(() => AllocateAsync(s, shipments[i % 3], LineOf(req), 4m))).ToArray());

        await AssertCleanAsync(s, "same request line", responses);
        Assert.Equal(2, Count(responses, HttpStatusCode.OK));              // 4 + 4 <= 10 < 12
        Assert.Equal(4, Count(responses, HttpStatusCode.BadRequest));
        Assert.Equal(8m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AllocatedBaseQty" FROM "StockTransferLines" WHERE "Id" = '{LineOf(req)}'"""));
        Assert.Equal(8m, await ReservedAsync(s));
    }

    // ---- allocation vs other stock writers -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AllocateVsSale_CombinedOverStock_ExactlyOneSucceeds()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var s = await NewSceneAsync(postgres, 10m);
            var req = await RequestAsync(s, s.AStore, 10m);
            var sid = IdOf(await NewShipmentAsync(s));

            var responses = await ParallelAsync(() => AllocateAsync(s, sid, LineOf(req), 6m), () => SaleAsync(s, 6m));

            await AssertCleanAsync(s, $"round {round}: allocate 6 vs sale 6", responses);
            var (allocate, sale) = (responses[0], responses[1]);
            Assert.True(allocate.IsSuccessStatusCode ^ sale.IsSuccessStatusCode, $"round {round}: allocate {Code(allocate)}, sale {Code(sale)}: exactly one must win");
            Assert.Equal(HttpStatusCode.BadRequest, (allocate.IsSuccessStatusCode ? sale : allocate).StatusCode);
            var (qoh, reserved) = (await QohAsync(s), await ReservedAsync(s));
            Assert.Equal(allocate.IsSuccessStatusCode ? (10m, 6m) : (4m, 0m), (qoh, reserved));
        }
    }

    [Fact]
    public async Task AllocateVsSale_FittingTogether_BothSucceed()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var req = await RequestAsync(s, s.AStore, 10m);
        var sid = IdOf(await NewShipmentAsync(s));

        var responses = await ParallelAsync(() => AllocateAsync(s, sid, LineOf(req), 6m), () => SaleAsync(s, 4m));

        await AssertCleanAsync(s, "allocate 6 + sale 4", responses);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK], responses.Select(r => r.StatusCode));
        Assert.Equal((6m, 6m), (await QohAsync(s), await ReservedAsync(s)));
    }

    // ---- allocation vs a SHIPMENT dispatch of the same stock (FI-D34, FI-D38 e: replaces AllocateVsLegacyDispatch) ----------------------------------------------
    // 10 on hand, shipment 1 of 6 ready (reserved 6): free is 4 before the dispatch and 4 after it (on hand 4, reserved 0), so another shipment can allocate exactly 4.

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task AllocateVsDispatchOfAnotherShipment_AgainstTheStockBeingDispatched_NeverOverAllocates(int pieces, bool fits)
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (s, sid, _) = await ReadySceneAsync(postgres, 10m, 6m);
            var other = await RequestAsync(s, s.BStore, 10m);
            var otherShipment = IdOf(await NewShipmentAsync(s));

            var responses = await ParallelAsync(() => DispatchShipmentAsync(s, sid), () => AllocateAsync(s, otherShipment, LineOf(other), pieces));

            await AssertCleanAsync(s, $"round {round}: allocate {pieces} vs dispatch", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Equal(fits, responses[1].IsSuccessStatusCode);
            if (!fits) Assert.Equal(HttpStatusCode.BadRequest, responses[1].StatusCode);
            Assert.Equal((4m, fits ? (decimal)pieces : 0m), (await QohAsync(s), await ReservedAsync(s)));
            await AssertTransitReconciliationCleanAsync(s.W.Host.Database, $"round {round}: allocate vs dispatch");
        }
    }

    [Fact]
    public async Task ConcurrentAllocationsVsDispatchOfAnotherShipment_CombinedOverTheFreeStock_ExactlyOneAllocationWins()
    {
        var (s, sid, _) = await ReadySceneAsync(postgres, 10m, 6m);
        var (r1, r2) = (await RequestAsync(s, s.BStore, 5m), await RequestAsync(s, s.BStore, 5m));
        var otherShipment = IdOf(await NewShipmentAsync(s));

        var responses = await ParallelAsync(() => DispatchShipmentAsync(s, sid), () => AllocateAsync(s, otherShipment, LineOf(r1), 3m), () => AllocateAsync(s, otherShipment, LineOf(r2), 3m));

        await AssertCleanAsync(s, "two allocations vs dispatch", responses);
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(1, responses.Skip(1).Count(r => r.IsSuccessStatusCode));                        // 4 free: one 3 fits, two do not
        Assert.All(responses.Skip(1).Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal((4m, 3m), (await QohAsync(s), await ReservedAsync(s)));
        await AssertTransitReconciliationCleanAsync(s.W.Host.Database, "two allocations vs dispatch");
    }

    [Fact]
    public async Task AllocateVsCancelOfTheRequest_ExactlyOneWins_AndAnAllocatedRequestIsNeverCancelled()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var s = await NewSceneAsync(postgres, 10m);
            var req = await RequestAsync(s, s.AStore, 5m);
            var sid = IdOf(await NewShipmentAsync(s));

            var responses = await ParallelAsync(() => AllocateAsync(s, sid, LineOf(req), 5m),
                () => SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(req)}/cancel", new StockTransferReasonRequest("race"), s.Src));

            await AssertCleanAsync(s, $"round {round}: allocate vs request cancel", responses);
            Assert.True(responses[0].IsSuccessStatusCode ^ responses[1].IsSuccessStatusCode, $"round {round}: allocate {Code(responses[0])}, cancel {Code(responses[1])}");
            var status = await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{IdOf(req)}'""");
            Assert.Equal(responses[0].IsSuccessStatusCode ? ("Approved", 5m) : ("Cancelled", 0m), (status, await ReservedAsync(s)));
        }
    }

    // ---- cancel / release vs allocate / reduce -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ShipmentCancelVsAllocate_NoDeadlock_EndsCancelledWithNothingReserved()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var s = await NewSceneAsync(postgres, 10m);
            var first = await RequestAsync(s, s.AStore, 4m); var more = await RequestAsync(s, s.BStore, 4m); var other = await RequestAsync(s, s.AStore, 2m);
            var sid = IdOf(await NewShipmentAsync(s));
            await OkAsync(await AllocateAsync(s, sid, LineOf(first), 3m));

            var responses = await ParallelAsync(
                () => CancelShipmentAsync(s, sid, "race"),
                () => AllocateAsync(s, sid, LineOf(first), 1m),
                () => AllocateAsync(s, sid, LineOf(more), 4m),
                () => AllocateAsync(s, sid, LineOf(other), 2m));

            await AssertCleanAsync(s, $"round {round}: cancel vs allocate", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.All(responses.Skip(1), r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"{Code(r)}"));
            Assert.Equal("Cancelled", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "Shipments" WHERE "Id" = '{sid}'"""));
            Assert.Equal(0m, await ReservedAsync(s));        // whichever order: an allocation that got in first is released by the cancel, a later one is refused
            Assert.Equal(0L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "StockReservations" WHERE "SourceLocationId" = '{s.SrcStore}' AND "Status" = 'Active'"""));
            Assert.Equal(0m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT COALESCE(SUM("AllocatedBaseQty"), 0) FROM "StockTransferLines" WHERE "Id" IN ('{LineOf(first)}', '{LineOf(more)}', '{LineOf(other)}')"""));
        }
    }

    [Fact]
    public async Task ConcurrentReleaseAndReduce_OfOneReservation_EndReleased_WithNoNegativeOrDoubleRelease()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var s = await NewSceneAsync(postgres, 10m);
            var req = await RequestAsync(s, s.AStore, 10m);
            var sid = IdOf(await NewShipmentAsync(s));
            var allocated = await OkAsync(await AllocateAsync(s, sid, LineOf(req), 6m));
            var line = LineOfConsignment(ConsignmentTo(allocated, s.AStore));
            var (lineId, reservationId) = (IdOf(line), line.GetProperty("reservationId").GetGuid());

            var responses = await ParallelAsync(
                () => SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-reservations/{reservationId}/release", new ReleaseStockReservationRequest("race"), s.Src),
                () => SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/allocations/{lineId}", new ChangeAllocationRequest(2m), s.Src),
                () => SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-reservations/{reservationId}/release", new ReleaseStockReservationRequest("race 2"), s.Src));

            await AssertCleanAsync(s, $"round {round}: release vs reduce", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Equal(HttpStatusCode.OK, responses[2].StatusCode);       // the loser of the two releases is idempotent
            Assert.True(responses[1].StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"reduce answered {Code(responses[1])}");
            Assert.Equal(0m, await ReservedAsync(s));
            Assert.Equal("Released", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockReservations" WHERE "Id" = '{reservationId}'"""));
            Assert.Equal(0m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AllocatedBaseQty" FROM "StockTransferLines" WHERE "Id" = '{LineOf(req)}'"""));
            Assert.Equal(1L, await AuditCountAsync(s.W, reservationId, "stock_reservation.released"));
        }
    }

    [Fact]
    public async Task ConcurrentReductions_OfDifferentLines_OnOneShipment_KeepTheSumsExact()
    {
        var s = await NewSceneAsync(postgres, 20m);
        var reqs = new List<JsonElement>();
        for (var i = 0; i < 4; i++) reqs.Add(await RequestAsync(s, i % 2 == 0 ? s.AStore : s.BStore, 5m));
        var sid = IdOf(await NewShipmentAsync(s));
        var lines = new List<Guid>();
        foreach (var req in reqs)
        {
            var detail = await OkAsync(await AllocateAsync(s, sid, LineOf(req), 5m));
            lines = detail.GetProperty("consignments").EnumerateArray().SelectMany(c => c.GetProperty("lines").EnumerateArray()).Select(IdOf).ToList();
        }
        Assert.Equal(20m, await ReservedAsync(s));

        var responses = await ParallelAsync(lines.Select((l, i) => (Func<Task<HttpResponseMessage>>)(() => i % 2 == 0
            ? SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/allocations/{l}", new ChangeAllocationRequest(2m), s.Src)
            : SendAsync(s.W.Admin, HttpMethod.Delete, $"/api/v1/shipments/{sid}/allocations/{l}", null, s.Src))).ToArray());

        await AssertCleanAsync(s, "parallel reductions", responses);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(4m, await ReservedAsync(s));         // two lines at 2, two cancelled
    }

    // ---- idempotent replays under concurrency ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentReplaysOfOneAllocationKey_ApplyOnce_AndNeverFail()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var s = await NewSceneAsync(postgres, 10m);
            var req = await RequestAsync(s, s.AStore, 10m);
            var sid = IdOf(await NewShipmentAsync(s));
            var key = "race-" + Guid.NewGuid();

            var responses = await ParallelAsync(Enumerable.Range(0, 5).Select(_ => (Func<Task<HttpResponseMessage>>)(() => AllocateAsync(s, sid, LineOf(req), 4m, key))).ToArray());

            await AssertCleanAsync(s, $"round {round}: replayed allocation", responses);
            Assert.True(responses.All(r => r.StatusCode == HttpStatusCode.OK), $"round {round}: [{string.Join(",", responses.Select(Code))}] - every replay must answer the first result");
            Assert.Equal(4m, await ReservedAsync(s));                            // once, not 4 x 5
            Assert.Equal(1L, await AuditCountAsync(s.W, sid, "shipment.allocated"));
        }
    }

    [Fact]
    public async Task ConcurrentReplaysOfOneCreateKey_MakeOneShipment()
    {
        var s = await NewSceneAsync(postgres, 0m);
        var key = "create-" + Guid.NewGuid();

        var responses = await ParallelAsync(Enumerable.Range(0, 5).Select(_ => (Func<Task<HttpResponseMessage>>)(() => CreateShipmentAsync(s, key: key))).ToArray());

        await AssertNo500Async("replayed create", responses);
        Assert.True(responses.All(r => r.IsSuccessStatusCode), $"[{string.Join(",", responses.Select(Code))}]");
        var ids = new HashSet<Guid>();
        foreach (var r in responses) ids.Add(IdOf(await JsonAsync(r)));
        Assert.Single(ids);
        Assert.Equal(1L, await AuditCountAsync(s.W, ids.Single(), "shipment.created"));
    }

    [Fact]
    public async Task ConcurrentReplaysOfOneCancelKey_ReleaseOnce()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var req = await RequestAsync(s, s.AStore, 10m);
        var sid = IdOf(await NewShipmentAsync(s));
        await OkAsync(await AllocateAsync(s, sid, LineOf(req), 7m));
        var key = "cancel-" + Guid.NewGuid();

        var responses = await ParallelAsync(Enumerable.Range(0, 4).Select(_ => (Func<Task<HttpResponseMessage>>)(() => CancelShipmentAsync(s, sid, "race", key))).ToArray());

        await AssertCleanAsync(s, "replayed cancel", responses);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(0m, await ReservedAsync(s));
        Assert.Equal(1L, await AuditCountAsync(s.W, sid, "shipment.cancelled"));
    }

    // ---- purchase cancellation vs allocation ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PurchaseCancelVsAllocate_NeverStrandsAReservation_NeverA500()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var s = await NewSceneAsync(postgres, 0m);
            var purchaseId = IdOf(await OkAsync(await s.W.PurchaseAsync(s.Src, qty: 10m)));
            var req = await RequestAsync(s, s.AStore, 10m);
            var sid = IdOf(await NewShipmentAsync(s));

            var responses = await ParallelAsync(
                () => SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/purchases/{purchaseId}/cancel", null, s.Src),
                () => AllocateAsync(s, sid, LineOf(req), 8m));

            await AssertCleanAsync(s, $"round {round}: purchase cancel vs allocate", responses);
            Assert.True(responses[0].IsSuccessStatusCode ^ responses[1].IsSuccessStatusCode, $"round {round}: cancel {Code(responses[0])}, allocate {Code(responses[1])}: exactly one can win");
            Assert.Equal(responses[0].IsSuccessStatusCode ? (0m, 0m) : (10m, 8m), (await QohAsync(s), await ReservedAsync(s)));
        }
    }
}
