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
/// Functional-improvements Phase 04 (FI-D4..D7, FI-D29..D33) over HTTP on PostgreSQL: grouping of approved requests into per-destination consignments with
/// destination isolation, reservations that stop overselling and are released on cancellation / release, own-fleet and external transport with per-mode
/// reference validation and audit, the allocation / loading limits, the legacy dispatch and cancel guards, a purchase cancellation that would strand a
/// reservation, permissions and another company's ids. After every scenario the reservation reconciliation (queries 1-3) returns no rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ShipmentsApiTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim RoleGate = new(1, 1);
    private static readonly Dictionary<string, Guid> RoleByName = [];

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string name, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        await RoleGate.WaitAsync();
        try
        {
            var key = name + ":" + branch;
            if (RoleByName.TryGetValue(key, out var id)) return id;
            var response = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest("SHP " + name + Guid.NewGuid().ToString("N")[..6], null, codes, scope, branch));
            Check(response, "create role " + name);
            return RoleByName[key] = IdOf(await JsonAsync(response));
        }
        finally { RoleGate.Release(); }
    }

    /// <summary>A user at <paramref name="branch"/> whose <paramref name="codes"/> are held by a role scoped to that branch only.</summary>
    private static async Task<RestrictedUser> BranchUserAsync(RestrictedUserWorld r, Guid branch, params string[] codes)
    {
        var user = await r.CreateAsync("sh", await RoleAsync(r.World, "harmless", ["Sales.View"]), CompanyAccessScope.AssignedBranches, [branch], branch);
        var assigned = await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles",
            new AssignRoleRequest(await RoleAsync(r.World, "br" + string.Join("", codes.Select(c => c.Replace(".", ""))), codes, RoleScope.Branch, branch), null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        return user;
    }

    private static async Task<HttpResponseMessage> SaleAtAsync(ShipScene s, decimal pieces) =>
        await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(s.W.CustomerId, Today, PaymentMethod.Cash, 0m,
            [new CreateSaleLineRequest(s.W.ProductId, s.Pc, pieces, 20m, 0)]), s.Src);

    private static async Task<List<string>> FieldsAsync(HttpResponseMessage response) => await ErrorFieldsAsync(response);

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field.ToLowerInvariant(), await FieldsAsync(response));
    }

    // ---- 1. grouping + destination isolation --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoRequestsToA_OneToB_OnOneShipment_MakeTwoConsignments_AndTheDestinationBReaderSeesOnlyItsOwn()
    {
        var s = await NewSceneAsync(postgres);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var a1 = await RequestAsync(s, s.AStore, 3m); var a2 = await RequestAsync(s, s.AStore, 2m); var b1 = await RequestAsync(s, s.BStore, 4m);
        var shipment = await NewShipmentAsync(s);
        var sid = IdOf(shipment);
        foreach (var (req, qty) in new[] { (a1, 3m), (a2, 2m), (b1, 4m) }) await OkAsync(await AllocateAsync(s, sid, LineOf(req), qty));

        var detail = await OkAsync(await GetShipmentAsync(s, sid));
        Assert.Equal(2, detail.GetProperty("consignments").GetArrayLength());
        var consA = ConsignmentTo(detail, s.AStore); var consB = ConsignmentTo(detail, s.BStore);
        Assert.Equal(2, consA.GetProperty("lines").GetArrayLength());
        Assert.Equal(1, consB.GetProperty("lines").GetArrayLength());
        Assert.NotEqual(IdOf(consA), IdOf(consB));
        Assert.Equal(9m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "grouped allocations");

        // The destination-B reader (a Branch-scoped role at B) sees B's consignment and nothing else.
        var reader = await BranchUserAsync(r, s.B, "Shipments.View", "StockTransfers.Receive");
        var incoming = await OkAsync(await SendAsync(reader.Client, HttpMethod.Get, "/api/v1/consignments?direction=incoming&pageSize=100", null, s.B));
        Assert.Equal([IdOf(consB)], incoming.GetProperty("items").EnumerateArray().Select(IdOf).ToList());
        Assert.Equal(JsonValueKind.Null, incoming.GetProperty("items")[0].GetProperty("shipmentId").ValueKind);
        var outgoing = await OkAsync(await SendAsync(reader.Client, HttpMethod.Get, "/api/v1/consignments?direction=outgoing&pageSize=100", null, s.B));
        Assert.Empty(outgoing.GetProperty("items").EnumerateArray());

        var own = await SendAsync(reader.Client, HttpMethod.Get, $"/api/v1/consignments/{IdOf(consB)}", null, s.B);
        var ownBody = await own.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var ownJson = JsonSerializer.Deserialize<JsonElement>(ownBody);
        Assert.Equal("incoming", ownJson.GetProperty("direction").GetString());
        Assert.Equal(JsonValueKind.Null, ownJson.GetProperty("shipmentId").ValueKind);
        Assert.Equal(s.BStore, ownJson.GetProperty("destination").GetProperty("id").GetGuid());
        Assert.Single(ownJson.GetProperty("lines").EnumerateArray());
        Assert.DoesNotContain("cost", ownBody, StringComparison.OrdinalIgnoreCase);                  // no costs
        Assert.DoesNotContain(consA.GetProperty("consignmentNo").GetString()!, ownBody);            // nothing of A's consignment
        Assert.DoesNotContain(s.AStore.ToString(), ownBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sid.ToString(), ownBody, StringComparison.OrdinalIgnoreCase);         // no shipment id either

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(reader.Client, HttpMethod.Get, $"/api/v1/consignments/{IdOf(consA)}", null, s.B)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetShipmentAsync(s, sid, reader.Client, s.B)).StatusCode);
        var shipments = await OkAsync(await SendAsync(reader.Client, HttpMethod.Get, "/api/v1/shipments?pageSize=100", null, s.B));
        Assert.DoesNotContain(sid, shipments.GetProperty("items").EnumerateArray().Select(IdOf));
        var reservations = await OkAsync(await SendAsync(reader.Client, HttpMethod.Get, "/api/v1/stock-reservations?pageSize=100", null, s.B));
        Assert.Empty(reservations.GetProperty("items").EnumerateArray());

        // The A reader sees A's consignment (two lines) only; the source (admin) sees both, outgoing, with the shipment.
        var readerA = await BranchUserAsync(r, s.A, "Shipments.View", "StockTransfers.Receive");
        var atA = await OkAsync(await SendAsync(readerA.Client, HttpMethod.Get, "/api/v1/consignments?direction=incoming&pageSize=100", null, s.A));
        Assert.Equal([IdOf(consA)], atA.GetProperty("items").EnumerateArray().Select(IdOf).ToList());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(readerA.Client, HttpMethod.Get, $"/api/v1/consignments/{IdOf(consB)}", null, s.A)).StatusCode);
        var asSource = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/consignments/{IdOf(consB)}", null, s.Src));
        Assert.Equal("outgoing", asSource.GetProperty("direction").GetString());
        Assert.Equal(sid, asSource.GetProperty("shipmentId").GetGuid());
        var outgoingList = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/consignments?direction=outgoing&shipmentId={sid}", null, s.Src));
        Assert.Equal(new[] { IdOf(consA), IdOf(consB) }.Order(), outgoingList.GetProperty("items").EnumerateArray().Select(IdOf).Order());
    }

    // ---- 2. reservations prevent overselling, released on cancellation -------------------------------------------------------------------

    [Fact]
    public async Task Reservation_Allocate8Of10_BlocksASaleOf3_ReducingTheAllocationAllowsIt_CancellingReleasesEverything()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var req = await RequestAsync(s, s.AStore, 10m);
        var sid = IdOf(await NewShipmentAsync(s));
        var allocated = await OkAsync(await AllocateAsync(s, sid, LineOf(req), 8m));
        var line = LineOfConsignment(ConsignmentTo(allocated, s.AStore));
        var lineId = IdOf(line);
        Assert.Equal((8m, 8m), (line.GetProperty("allocatedBaseQty").GetDecimal(), line.GetProperty("reservedBaseQty").GetDecimal()));
        Assert.Equal((10m, 8m), (await QohAsync(s), await ReservedAsync(s)));
        await AssertReservationInvariantsAsync(s, "after allocate");

        var oversell = await SaleAtAsync(s, 3m);                       // available = 10 - 8 = 2
        Assert.Equal(HttpStatusCode.BadRequest, oversell.StatusCode);
        Assert.Contains("2", await oversell.Content.ReadAsStringAsync());
        Assert.Equal((10m, 8m), (await QohAsync(s), await ReservedAsync(s)));
        Assert.Equal(HttpStatusCode.OK, (await SaleAtAsync(s, 2m)).StatusCode); // exactly the available 2 is fine
        Assert.Equal((8m, 8m), (await QohAsync(s), await ReservedAsync(s)));
        Assert.Equal(HttpStatusCode.BadRequest, (await SaleAtAsync(s, 1m)).StatusCode);
        await AssertReservationInvariantsAsync(s, "after sales");

        var reduced = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/allocations/{lineId}", new ChangeAllocationRequest(5m), s.Src));
        Assert.Equal(5m, LineOfConsignment(ConsignmentTo(reduced, s.AStore)).GetProperty("allocatedBaseQty").GetDecimal());
        Assert.Equal(5m, await ReservedAsync(s));
        Assert.Equal(HttpStatusCode.OK, (await SaleAtAsync(s, 3m)).StatusCode); // 8 on hand - 5 reserved = 3 available
        Assert.Equal((5m, 5m), (await QohAsync(s), await ReservedAsync(s)));
        await AssertReservationInvariantsAsync(s, "after reduce + sale");

        var cancelled = await OkAsync(await CancelShipmentAsync(s, sid, "no longer needed"));
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(0m, await ReservedAsync(s));
        Assert.Equal(5m, await QohAsync(s)); // cancelling moves no stock
        Assert.Equal("Released", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockReservations" WHERE "ConsignmentLineId" = '{lineId}'"""));
        Assert.Equal(0m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AllocatedBaseQty" FROM "StockTransferLines" WHERE "Id" = '{LineOf(req)}'"""));
        await AssertReservationInvariantsAsync(s, "after shipment cancel");
        Assert.Equal(HttpStatusCode.OK, (await SaleAtAsync(s, 5m)).StatusCode);   // everything sellable again
    }

    [Fact]
    public async Task ReleaseEndpoint_ReleasesOnce_RequiresReservationsRelease_AndReasonAndIsIdempotent()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var req = await RequestAsync(s, s.AStore, 10m);
        var sid = IdOf(await NewShipmentAsync(s));
        var allocated = await OkAsync(await AllocateAsync(s, sid, LineOf(req), 6m));
        var reservationId = LineOfConsignment(ConsignmentTo(allocated, s.AStore)).GetProperty("reservationId").GetGuid();

        var report = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/stock-reservations?locationId={s.SrcStore}&pageSize=100", null, s.Src));
        var row = report.GetProperty("items").EnumerateArray().Single(x => IdOf(x) == reservationId);
        Assert.Equal("Active", row.GetProperty("status").GetString());
        Assert.Equal(6m, row.GetProperty("baseQty").GetDecimal());

        // Others: Plan / View / Load holders without Reservations.Release get 403; no reason is a 400; unknown id a 404.
        var planner = await r.CreateAsync("pl", await RoleAsync(s.W, "planner", ["Shipments.View", "Shipments.Plan", "Shipments.Load"]), CompanyAccessScope.CompanyWide);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(planner.Client, HttpMethod.Post, $"/api/v1/stock-reservations/{reservationId}/release", new ReleaseStockReservationRequest("try"), s.Src)).StatusCode);
        Assert.Equal(6m, await ReservedAsync(s));
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-reservations/{reservationId}/release", new { reason = "  " }, s.Src), "reason");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-reservations/{Guid.NewGuid()}/release", new ReleaseStockReservationRequest("x"), s.Src)).StatusCode);

        var released = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-reservations/{reservationId}/release", new ReleaseStockReservationRequest("  customer gone  "), s.Src));
        Assert.Equal("Released", released.GetProperty("status").GetString());
        Assert.Equal("customer gone", released.GetProperty("releaseReason").GetString());
        Assert.Equal(0m, await ReservedAsync(s));
        Assert.Equal(0m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AllocatedBaseQty" FROM "StockTransferLines" WHERE "Id" = '{LineOf(req)}'""")); // request line freed
        Assert.Equal(1L, await AuditCountAsync(s.W, reservationId, "stock_reservation.released"));
        var again = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-reservations/{reservationId}/release", new ReleaseStockReservationRequest("again"), s.Src);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);                                                       // idempotent by state
        Assert.Equal(1L, await AuditCountAsync(s.W, reservationId, "stock_reservation.released"));
        var active = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/stock-reservations?locationId={s.SrcStore}&pageSize=100", null, s.Src));
        Assert.DoesNotContain(reservationId, active.GetProperty("items").EnumerateArray().Select(IdOf));
        var history = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/stock-reservations?locationId={s.SrcStore}&status=Released&pageSize=100", null, s.Src));
        Assert.Contains(reservationId, history.GetProperty("items").EnumerateArray().Select(IdOf));
        await AssertReservationInvariantsAsync(s, "after release");
        // The shipment survives with an empty consignment line set and can be re-allocated.
        await OkAsync(await AllocateAsync(s, sid, LineOf(req), 6m));
        Assert.Equal(6m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "after re-allocation");
    }

    // ---- 3. vehicles, external transport, references, audit --------------------------------------------------------------------------------

    [Fact]
    public async Task OwnFleet_MultipleVehicles_EachConsignmentOnItsOwnLeg_ReaderSeesOnlyItsLeg_AndVehicleScopeIsEnforced()
    {
        var s = await NewSceneAsync(postgres);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var a = await RequestAsync(s, s.AStore, 3m); var b = await RequestAsync(s, s.BStore, 4m);
        var sid = IdOf(await NewShipmentAsync(s));
        await OkAsync(await AllocateAsync(s, sid, LineOf(a), 3m)); await OkAsync(await AllocateAsync(s, sid, LineOf(b), 4m));

        var (_, svA) = await AddOwnVehicleAsync(s, sid);
        var (withTwo, svB) = await AddOwnVehicleAsync(s, sid);
        Assert.Equal(2, withTwo.GetProperty("vehicles").GetArrayLength());
        Assert.Equal([1, 2], withTwo.GetProperty("vehicles").EnumerateArray().Select(v => v.GetProperty("sequence").GetInt32()));

        // Scope: a vehicle assigned to another branch is refused; one assigned to the source branch and a duplicate behave.
        var foreign = await NewVehicleAsync(s.W, s.A);
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(foreign, null, null, null, null), s.Src), "vehicleId");
        var local = await NewVehicleAsync(s.W, s.Src);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(local, null, null, null, null), s.Src)).StatusCode);
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(local, null, null, null, null), s.Src), "vehicleId");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(null, "EXT-1", null, null, null), s.Src), "vehicleId"); // own fleet: no free text
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(Guid.NewGuid(), null, null, null, null), s.Src), "vehicleId");

        var detail = await OkAsync(await GetShipmentAsync(s, sid));
        var (consA, consB) = (ConsignmentTo(detail, s.AStore), ConsignmentTo(detail, s.BStore));
        await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/vehicle", new AssignConsignmentVehicleRequest(svA), s.Src));
        var assigned = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consB)}/vehicle", new AssignConsignmentVehicleRequest(svB), s.Src));
        Assert.Equal(svB, ConsignmentTo(assigned, s.BStore).GetProperty("shipmentVehicleId").GetGuid());
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consB)}/vehicle", new AssignConsignmentVehicleRequest(Guid.NewGuid()), s.Src), "shipmentVehicleId");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Delete, $"/api/v1/shipments/{sid}/vehicles/{svA}", null, s.Src), "shipmentVehicleId"); // still carries a consignment

        // The B reader sees only B's leg (its vehicle number, not A's).
        var numberA = assigned.GetProperty("vehicles").EnumerateArray().Single(v => IdOf(v) == svA).GetProperty("vehicleNumber").GetString()!;
        var numberB = assigned.GetProperty("vehicles").EnumerateArray().Single(v => IdOf(v) == svB).GetProperty("vehicleNumber").GetString()!;
        var reader = await BranchUserAsync(r, s.B, "Shipments.View", "StockTransfers.Receive");
        var body = await (await SendAsync(reader.Client, HttpMethod.Get, $"/api/v1/consignments/{IdOf(consB)}", null, s.B)).Content.ReadAsStringAsync();
        Assert.Contains(numberB, body);
        Assert.DoesNotContain(numberA, body);
        var asSource = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/consignments/{IdOf(consA)}", null, s.Src));
        Assert.Equal(numberA, asSource.GetProperty("leg").GetProperty("vehicleNumber").GetString());

        // Clearing a leg and then removing its vehicle works.
        await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/vehicle", new AssignConsignmentVehicleRequest(null), s.Src));
        var removed = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Delete, $"/api/v1/shipments/{sid}/vehicles/{svA}", null, s.Src));
        Assert.Equal(2, removed.GetProperty("vehicles").GetArrayLength()); // svB + the local one
        Assert.Equal(2L, await AuditCountAsync(s.W, sid, "shipment.vehicle_added") - 1L); // 3 added (two fleet + local), one refused each time: exactly the accepted ones audited
        await AssertReservationInvariantsAsync(s, "vehicles");
    }

    [Fact]
    public async Task ExternalProvider_DetailsAreStored_FreeTextVehicles_AndOwnFleetVehicleIsRefused()
    {
        var s = await NewSceneAsync(postgres);
        await AssertRefusedAsync(await CreateShipmentAsync(s, TransportMode.Road, TransportVia.ExternalProvider), "providerName");   // provider required

        var create = await CreateShipmentAsync(s, TransportMode.Road, TransportVia.ExternalProvider, provider: "  Swift Haulage  ");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var shipment = await JsonAsync(create);
        Assert.Equal("Swift Haulage", shipment.GetProperty("providerName").GetString());
        Assert.False(shipment.GetProperty("transportReferenceValid").GetBoolean());      // Road: needs a vehicle number or a reference
        var sid = IdOf(shipment);

        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(null, null, null, null, null), s.Src), "externalVehicleNumber");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(await NewVehicleAsync(s.W), null, null, null, null), s.Src), "vehicleId");
        var added = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(null, "KA-01-AB-1234", "10 t truck", "Ravi", "9876543210"), s.Src));
        var vehicle = Assert.Single(added.GetProperty("vehicles").EnumerateArray());
        Assert.Equal(("KA-01-AB-1234", "10 t truck", "Ravi", "9876543210"), (vehicle.GetProperty("externalVehicleNumber").GetString(), vehicle.GetProperty("externalDescription").GetString(),
            vehicle.GetProperty("driverName").GetString(), vehicle.GetProperty("driverPhone").GetString()));
        Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("vehicleId").ValueKind);
        Assert.True(added.GetProperty("transportReferenceValid").GetBoolean());          // the vehicle number is the Road reference

        // Switching between own fleet and external while vehicles exist is refused; header edits otherwise work.
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}",
            new UpdateShipmentRequest(TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, null), s.Src), "transportVia");
        var updated = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}",
            new UpdateShipmentRequest(TransportMode.Road, TransportVia.ExternalProvider, "Swift Haulage", "ops@swift.example", "LR-77", null, "updated"), s.Src));
        Assert.Equal(("ops@swift.example", "LR-77", "updated"), (updated.GetProperty("providerContact").GetString(), updated.GetProperty("transportReference").GetString(), updated.GetProperty("notes").GetString()));
        Assert.Equal(1L, await AuditCountAsync(s.W, sid, "shipment.updated"));
    }

    [Theory]
    [InlineData(TransportMode.Air, "AWB-176-123")]
    [InlineData(TransportMode.Sea, "BOL-9981")]
    [InlineData(TransportMode.Rail, "RR-4410")]
    [InlineData(TransportMode.Courier, "TRK0001")]
    [InlineData(TransportMode.HandCarry, "Mr. Rao")]
    public async Task TransportReference_IsRequiredForEveryModeExceptRoad_OverHttp(TransportMode mode, string reference)
    {
        var s = await NewSceneAsync(postgres, 0m);
        var count = await ScalarAsync<long>(s.W.Host.Database, """SELECT COUNT(*) FROM "Shipments" """);
        await AssertRefusedAsync(await CreateShipmentAsync(s, mode, TransportVia.OwnFleet), "transportReference");
        await AssertRefusedAsync(await CreateShipmentAsync(s, mode, TransportVia.OwnFleet, reference: "   "), "transportReference");
        Assert.Equal(count, await ScalarAsync<long>(s.W.Host.Database, """SELECT COUNT(*) FROM "Shipments" """)); // refused creates leave nothing behind

        var ok = await CreateShipmentAsync(s, mode, TransportVia.ExternalProvider, provider: "Carrier", reference: reference);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var shipment = await JsonAsync(ok);
        Assert.True(shipment.GetProperty("transportReferenceValid").GetBoolean());
        Assert.Equal(reference, shipment.GetProperty("transportReference").GetString());
        // Updating a non-Road shipment cannot drop the reference either.
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{IdOf(shipment)}",
            new UpdateShipmentRequest(mode, TransportVia.ExternalProvider, "Carrier", null, null, null, null), s.Src), "transportReference");
    }

    [Fact]
    public async Task TransportHeader_RequestShapeErrors_Map400ToTheFields()
    {
        var s = await NewSceneAsync(postgres, 0m);
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/shipments", new { transportMode = "Road", transportVia = "OwnFleet" }, s.Src), "sourceLocationId");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/shipments", new { sourceLocationId = s.SrcStore, transportVia = "OwnFleet" }, s.Src), "transportMode");
        await AssertRefusedAsync(await CreateShipmentAsync(s, sourceStore: Guid.NewGuid()), "sourceLocationId");
        var tooLong = await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/shipments",
            new CreateShipmentRequest(s.SrcStore, TransportMode.Road, TransportVia.OwnFleet, null, null, new string('r', 101), null, null), s.Src);
        await AssertRefusedAsync(tooLong, "transportReference");
    }

    [Fact]
    public async Task Lifecycle_WritesAuditEvents_ForCreateVehiclesAllocateLoadAndCancel_AndIdempotentRepeatsAddNone()
    {
        var s = await NewSceneAsync(postgres);
        var req = await RequestAsync(s, s.AStore, 6m);
        var created = await CreateShipmentAsync(s, key: "ship-" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sid = IdOf(await JsonAsync(created));
        var (_, sv) = await AddOwnVehicleAsync(s, sid);
        var allocated = await OkAsync(await AllocateAsync(s, sid, LineOf(req), 6m));
        var cons = ConsignmentTo(allocated, s.AStore);
        var lineId = IdOf(LineOfConsignment(cons));
        await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/vehicle", new AssignConsignmentVehicleRequest(sv), s.Src));
        await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/allocations/{lineId}", new ChangeAllocationRequest(5m), s.Src, "chg-" + Guid.NewGuid()));
        var loadKey = "load-" + Guid.NewGuid();
        var load = new LoadConsignmentRequest([new LoadConsignmentLineRequest(lineId, 5m)]);
        var loaded = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/load", load, s.Src, loadKey));
        await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/load", load, s.Src, loadKey)); // replay
        Assert.Equal("ReadyToDispatch", loaded.GetProperty("status").GetString());
        await OkAsync(await CancelShipmentAsync(s, sid, "audit me", "cancel-" + Guid.NewGuid()));

        foreach (var (action, count) in new[] { ("shipment.created", 1L), ("shipment.vehicle_added", 1L), ("shipment.allocated", 1L), ("shipment.vehicle_assigned", 1L),
                     ("shipment.allocation_changed", 1L), ("shipment.loaded", 1L), ("shipment.cancelled", 1L) })
        {
            Assert.True(count == await AuditCountAsync(s.W, sid, action), $"audit '{action}' expected {count}");
        }
        var cancelEvent = await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Reason" FROM "AuditEvents" WHERE "TargetId" = '{sid}' AND "Action" = 'shipment.cancelled'""");
        Assert.Equal("audit me", cancelEvent);
        Assert.Equal(s.W.AdminId, await ScalarAsync<Guid>(s.W.Host.Database, $"""SELECT "ActorUserId" FROM "AuditEvents" WHERE "TargetId" = '{sid}' AND "Action" = 'shipment.created'"""));
        Assert.NotNull(await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "CorrelationId" FROM "AuditEvents" WHERE "TargetId" = '{sid}' AND "Action" = 'shipment.allocated'"""));
        await AssertReservationInvariantsAsync(s, "lifecycle");
    }

    // ---- 4. limits and status transitions --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Allocation_OverAvailable_OverRequestRemaining_AndMultiPickup_AreRefusedOnTheField_AndChangeNothing()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var big = await RequestAsync(s, s.AStore, 20m);        // more requested than the source holds
        var small = await RequestAsync(s, s.BStore, 5m);
        var sid = IdOf(await NewShipmentAsync(s));

        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(big), 11m), "quantity");             // > available (10)
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(small), 6m), "quantity");            // > the request's remaining (5), although 10 are available
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(big), 1.5m), "quantity");            // Piece has no decimals
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(big), 0m), "quantity");
        await AssertRefusedAsync(await AllocateAsync(s, sid, Guid.NewGuid(), 1m), "stockTransferLineId");
        Assert.Equal(0m, await ReservedAsync(s));

        // Multi-pickup: a request out of another source cannot ride on this shipment.
        var (other, _) = await s.W.NewBranchAsync();
        await s.W.EnsureBranchStockAsync(other, 5m);
        var otherStore = await ScalarAsync<Guid>(s.W.Host.Database, $"""SELECT "Id" FROM "StockLocations" WHERE "BranchId" = '{other}' AND "LocationType" = 'BranchStore' LIMIT 1""");
        var foreignSource = await RequestAsync(s, s.BStore, 2m, sourceBranch: other, sourceStore: otherStore);
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(foreignSource), 1m), "stockTransferLineId");
        // A request that has not been approved cannot be allocated.
        var draft = await RequestAsync(s, s.AStore, 2m, approve: false);
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(draft), 1m), "stockTransferLineId");

        // The exact capacity is accepted; one more of anything is not; the unit of a line cannot change.
        await OkAsync(await AllocateAsync(s, sid, LineOf(big), 10m));
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(big), 1m), "quantity");
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(small), 1m), "quantity");
        Assert.Equal(10m, await ReservedAsync(s));
        var inBoxes = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(LineOf(big), s.Box, 1m), s.Src, null);
        Assert.Equal(HttpStatusCode.BadRequest, inBoxes.StatusCode); // either "no stock left" or "already allocated in pc": never accepted
        Assert.Equal(10m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "limits");
    }

    [Fact]
    public async Task Allocation_EnteredInBoxes_KeepsItsUnit_AndReservesTheBaseQuantity()
    {
        var s = await NewSceneAsync(postgres, 30m);
        var req = await RequestAsync(s, s.AStore, 30m);
        var sid = IdOf(await NewShipmentAsync(s));
        var response = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(LineOf(req), s.Box, 2m), s.Src);
        var line = LineOfConsignment(ConsignmentTo(await OkAsync(response), s.AStore));
        Assert.Equal(("box", 2m, 20m, 10m), (line.GetProperty("unitCode").GetString(), line.GetProperty("enteredQty").GetDecimal(), line.GetProperty("allocatedBaseQty").GetDecimal(), line.GetProperty("conversionFactorToBase").GetDecimal()));
        Assert.Equal(20m, await ReservedAsync(s));

        // Adding more in the line's own unit increases it; in another unit is refused.
        var more = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(LineOf(req), s.Box, 1m), s.Src);
        Assert.Equal(30m, LineOfConsignment(ConsignmentTo(await OkAsync(more), s.AStore)).GetProperty("allocatedBaseQty").GetDecimal());
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(req), 1m), "productUnitId");
        // 4 boxes loaded is more than the 3 allocated.
        var consignment = ConsignmentTo(await OkAsync(await GetShipmentAsync(s, sid)), s.AStore);
        var lineId = IdOf(LineOfConsignment(consignment));
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consignment)}/load",
            new LoadConsignmentRequest([new LoadConsignmentLineRequest(lineId, 4m)]), s.Src), "lines[0].loadedQuantity");
        Assert.Equal(30m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "boxes");
    }

    [Fact]
    public async Task Loading_MoreThanAllocated_ShortLoadsAndUnloads_DriveDraftLoadingReadyToDispatch()
    {
        var s = await NewSceneAsync(postgres);
        var a = await RequestAsync(s, s.AStore, 3m); var b = await RequestAsync(s, s.BStore, 4m);
        var shipment = await NewShipmentAsync(s);
        var sid = IdOf(shipment);
        Assert.Equal("Draft", shipment.GetProperty("status").GetString());
        await OkAsync(await AllocateAsync(s, sid, LineOf(a), 3m)); await OkAsync(await AllocateAsync(s, sid, LineOf(b), 4m));
        var (_, svA) = await AddOwnVehicleAsync(s, sid);
        var (full, svB) = await AddOwnVehicleAsync(s, sid);
        var (consA, consB) = (ConsignmentTo(full, s.AStore), ConsignmentTo(full, s.BStore));
        var (lineA, lineB) = (IdOf(LineOfConsignment(consA)), IdOf(LineOfConsignment(consB)));

        async Task<JsonElement> LoadAsync(JsonElement cons, Guid line, decimal qty) => await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post,
            $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/load", new LoadConsignmentRequest([new LoadConsignmentLineRequest(line, qty)]), s.Src));
        string Status(JsonElement sh) => sh.GetProperty("status").GetString()!;
        string ConsStatus(JsonElement sh, Guid store) => ConsignmentTo(sh, store).GetProperty("status").GetString()!;

        // Loading more than allocated (and a line of another consignment) is refused.
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/load",
            new LoadConsignmentRequest([new LoadConsignmentLineRequest(lineA, 4m)]), s.Src), "lines[0].loadedQuantity");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/load",
            new LoadConsignmentRequest([new LoadConsignmentLineRequest(lineB, 1m)]), s.Src), "lines[0].consignmentLineId");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/load",
            new { lines = Array.Empty<object>() }, s.Src), "lines");
        Assert.Equal("Draft", Status(await OkAsync(await GetShipmentAsync(s, sid))));

        // No leg yet: a fully loaded consignment is not "Loaded", the shipment is Loading.
        var afterFull = await LoadAsync(consA, lineA, 3m);
        Assert.Equal(("Loading", "Planned"), (Status(afterFull), ConsStatus(afterFull, s.AStore)));
        await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/vehicle", new AssignConsignmentVehicleRequest(svA), s.Src));
        await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consB)}/vehicle", new AssignConsignmentVehicleRequest(svB), s.Src));
        var legged = await OkAsync(await GetShipmentAsync(s, sid));
        Assert.Equal(("Loading", "Loaded", "Planned"), (Status(legged), ConsStatus(legged, s.AStore), ConsStatus(legged, s.BStore)));

        var partial = await LoadAsync(consB, lineB, 2m);                                 // short load
        Assert.Equal(("Loading", "Planned"), (Status(partial), ConsStatus(partial, s.BStore)));
        var ready = await LoadAsync(consB, lineB, 4m);
        Assert.Equal(("ReadyToDispatch", "Loaded", "Loaded"), (Status(ready), ConsStatus(ready, s.AStore), ConsStatus(ready, s.BStore)));
        Assert.Equal(JsonValueKind.String, ConsignmentTo(ready, s.BStore).GetProperty("loadedAt").ValueKind);

        var unloaded = await LoadAsync(consA, lineA, 1m);                                // unload one consignment: the shipment steps back
        Assert.Equal(("Loading", "Planned", "Loaded"), (Status(unloaded), ConsStatus(unloaded, s.AStore), ConsStatus(unloaded, s.BStore)));
        var zero = await LoadAsync(consA, lineA, 0m);
        var zeroB = await LoadAsync(consB, lineB, 0m);
        Assert.Equal("Draft", Status(zeroB));                                            // nothing loaded any more
        _ = zero;

        // Reducing the allocation below what is loaded is refused until it is unloaded.
        await LoadAsync(consA, lineA, 3m);
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/allocations/{lineA}", new ChangeAllocationRequest(2m), s.Src), "quantity");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Delete, $"/api/v1/shipments/{sid}/allocations/{lineA}", null, s.Src), "quantity");
        Assert.Equal(7m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "loading");

        // A cancelled shipment accepts no more planning or loading, and a repeat cancel is a no-op.
        await OkAsync(await CancelShipmentAsync(s, sid, "done"));
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/load",
            new LoadConsignmentRequest([new LoadConsignmentLineRequest(lineA, 1m)]), s.Src), "status");
        await AssertRefusedAsync(await AllocateAsync(s, sid, LineOf(a), 1m), "status");
        Assert.Equal(HttpStatusCode.OK, (await CancelShipmentAsync(s, sid, "again")).StatusCode);
        Assert.Equal(1L, await AuditCountAsync(s.W, sid, "shipment.cancelled"));
        Assert.Equal(0m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "cancelled after loading");
    }

    [Fact]
    public async Task CancelConsignment_ReleasesOnlyItsOwnReservations_AndTheShipmentKeepsTheOther()
    {
        var s = await NewSceneAsync(postgres);
        var a = await RequestAsync(s, s.AStore, 3m); var b = await RequestAsync(s, s.BStore, 4m);
        var sid = IdOf(await NewShipmentAsync(s));
        var allocated = await OkAsync(await AllocateAsync(s, sid, LineOf(a), 3m));
        allocated = await OkAsync(await AllocateAsync(s, sid, LineOf(b), 4m));
        var consA = ConsignmentTo(allocated, s.AStore);

        var after = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/cancel", new ShipmentReasonRequest("A not ready"), s.Src));
        Assert.Equal(4m, await ReservedAsync(s));
        Assert.Single(after.GetProperty("consignments").EnumerateArray(), c => c.GetProperty("status").GetString() != "Cancelled");
        Assert.Equal(0m, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AllocatedBaseQty" FROM "StockTransferLines" WHERE "Id" = '{LineOf(a)}'"""));
        Assert.Equal(1L, await AuditCountAsync(s.W, sid, "consignment.cancelled"));
        // The cancelled consignment's request can be allocated again (a new consignment is created).
        var again = await OkAsync(await AllocateAsync(s, sid, LineOf(a), 3m));
        Assert.Equal(2, again.GetProperty("consignments").EnumerateArray().Count(c => c.GetProperty("status").GetString() != "Cancelled"));
        Assert.Equal(7m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "consignment cancel");
    }

    // ---- 5. legacy interplay -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyDispatch_IsAlwaysRefused_AndCancelOfTheRequest_IsRefusedWhileAnAllocationIsOutstanding()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var req = await RequestAsync(s, s.AStore, 10m);
        var transferId = IdOf(req);
        var sid = IdOf(await NewShipmentAsync(s));
        await OkAsync(await AllocateAsync(s, sid, LineOf(req), 4m));

        var dispatch = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/dispatch",
            new DispatchStockTransferRequest([new DispatchStockTransferLineRequest(LineOf(req), s.Pc, 10m)]), s.Src);
        await AssertRefusedAsync(dispatch, "status");
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/cancel", new StockTransferReasonRequest("no"), s.Src), "status");
        Assert.Equal("Approved", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{transferId}'"""));
        Assert.Equal((10m, 4m), (await QohAsync(s), await ReservedAsync(s)));
        await AssertReservationInvariantsAsync(s, "legacy refused");

        await OkAsync(await CancelShipmentAsync(s, sid));
        // FI-D3 (Phase 05): even with nothing allocated any more, dispatch happens only through a shipment.
        var ok = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/dispatch",
            new DispatchStockTransferRequest([new DispatchStockTransferLineRequest(LineOf(req), s.Pc, 10m)]), s.Src);
        await AssertRefusedAsync(ok, "status");
        Assert.Equal((10m, 0m), (await QohAsync(s), await ReservedAsync(s)));
        Assert.Equal("Approved", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{transferId}'"""));
        await AssertReservationInvariantsAsync(s, "legacy dispatch refused after release");

        var second = await RequestAsync(s, s.BStore, 2m);                                   // cancelling an unallocated approved request still works
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(second)}/cancel", new StockTransferReasonRequest("fine"), s.Src)).StatusCode);
    }

    // ---- 7. purchase cancellation of reserved stock ----------------------------------------------------------------------------------------

    [Fact]
    public async Task PurchaseCancellation_ThatWouldStrandAReservation_IsACleanRefusal_NotA500()
    {
        var s = await NewSceneAsync(postgres, 0m);
        var purchase = await s.W.PurchaseAsync(s.Src, qty: 10m);
        var purchaseId = IdOf(await OkAsync(purchase));
        Assert.Equal(10m, await QohAsync(s)); // the purchase landed in the source branch's store
        var req = await RequestAsync(s, s.AStore, 10m);
        var sid = IdOf(await NewShipmentAsync(s));
        await OkAsync(await AllocateAsync(s, sid, LineOf(req), 8m));

        var cancel = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/purchases/{purchaseId}/cancel", null, s.Src);

        Assert.True((int)cancel.StatusCode < 500, $"cancelling reserved stock answered {(int)cancel.StatusCode}: {await cancel.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.BadRequest, cancel.StatusCode);
        Assert.Equal((10m, 8m), (await QohAsync(s), await ReservedAsync(s)));              // nothing changed
        Assert.NotEqual("Cancelled", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "PurchaseOrders" WHERE "Id" = '{purchaseId}'"""));
        await AssertReservationInvariantsAsync(s, "refused purchase cancel");

        // After the reservation is released the purchase cancels normally.
        await OkAsync(await CancelShipmentAsync(s, sid));
        Assert.True((await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/purchases/{purchaseId}/cancel", null, s.Src)).IsSuccessStatusCode);
        Assert.Equal(0m, await QohAsync(s));
        await AssertReservationInvariantsAsync(s, "purchase cancelled after release");
    }

    [Fact]
    public async Task PurchaseCancellation_LeavingEnoughStockForTheReservations_Succeeds()
    {
        var s = await NewSceneAsync(postgres, 10m);             // 10 seeded + 10 purchased
        var purchaseId = IdOf(await OkAsync(await s.W.PurchaseAsync(s.Src, qty: 10m)));
        var req = await RequestAsync(s, s.AStore, 10m);
        var sid = IdOf(await NewShipmentAsync(s));
        await OkAsync(await AllocateAsync(s, sid, LineOf(req), 8m));

        var cancel = await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/purchases/{purchaseId}/cancel", null, s.Src);
        Assert.True((int)cancel.StatusCode < 500, $"{(int)cancel.StatusCode}: {await cancel.Content.ReadAsStringAsync()}");
        Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync()); // 10 left on hand >= 8 reserved
        Assert.Equal((10m, 8m), (await QohAsync(s), await ReservedAsync(s)));
        await AssertReservationInvariantsAsync(s, "purchase cancel with enough stock");
    }

    // ---- review fixes: source visibility (FI-D32 amendment) and incoming union --------------------------------------------------------------

    [Fact]
    public async Task CompanyLevelSourceShipment_IsHiddenFromBranchAndNarrowedUsers_ListAndDetail_WithRealBranchHeaders()
    {
        var s = await NewSceneAsync(postgres, 0m);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var warehouse = await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/warehouses", new CreateWarehouseRequest("SC" + Guid.NewGuid().ToString("N")[..6], "Central shipping warehouse", null));
        var centralLocation = (await OkAsync(warehouse)).GetProperty("stockLocationId").GetGuid();
        var branchShipment = IdOf(await NewShipmentAsync(s));
        var centralShipment = IdOf(await JsonAsync(await CreateShipmentAsync(s, sourceStore: centralLocation)));

        var branchUser = await BranchUserAsync(r, s.Src, "Shipments.View");
        var narrowed = await r.CreateAsync("nr", await RoleAsync(s.W, "viewer", ["Shipments.View"]), CompanyAccessScope.AssignedBranches, [s.Src], s.Src);
        var allBranches = await r.CreateAsync("ab", await RoleAsync(s.W, "viewer", ["Shipments.View"]), CompanyAccessScope.CompanyWide);

        async Task<List<Guid>> ListedAsync(HttpClient client) =>
            (await OkAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/shipments?pageSize=100", null, s.Src))).GetProperty("items").EnumerateArray().Select(IdOf).ToList();

        foreach (var client in new[] { branchUser.Client, narrowed.Client })
        {
            Assert.Equal(HttpStatusCode.OK, (await GetShipmentAsync(s, branchShipment, client)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetShipmentAsync(s, centralShipment, client)).StatusCode);
            var listed = await ListedAsync(client);
            Assert.Contains(branchShipment, listed);
            Assert.DoesNotContain(centralShipment, listed);
        }
        foreach (var client in new[] { allBranches.Client, s.W.Admin })
        {
            Assert.Equal(HttpStatusCode.OK, (await GetShipmentAsync(s, centralShipment, client)).StatusCode);
            Assert.Contains(centralShipment, await ListedAsync(client));
        }
        // Planning from a company-level source needs the same all-branch membership: the narrowed planner is refused, the all-branch one is not.
        var narrowedPlanner = await r.CreateAsync("np", await RoleAsync(s.W, "planner", ["Shipments.View", "Shipments.Plan"]), CompanyAccessScope.AssignedBranches, [s.Src], s.Src);
        var refused = await CreateShipmentAsync(s, client: narrowedPlanner.Client, sourceStore: centralLocation);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task IncomingConsignments_UseTheUnionOfEveryHeldCodesVisibility()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var req = await RequestAsync(s, s.BStore, 5m);
        var sid = IdOf(await NewShipmentAsync(s));
        var cons = ConsignmentTo(await OkAsync(await AllocateAsync(s, sid, LineOf(req), 5m)), s.BStore);

        // Shipments.View only at A, StockTransfers.Receive only at B: the first held code alone (Shipments.View) never showed B's consignment.
        var user = await r.CreateAsync("un", await RoleAsync(r.World, "harmless", ["Sales.View"]), CompanyAccessScope.AssignedBranches, [s.A, s.B], s.B);
        foreach (var (name, codes, branch) in new[] { ("sv", new[] { "Shipments.View" }, s.A), ("rc", new[] { "StockTransfers.Receive" }, s.B) })
        {
            var role = await RoleAsync(r.World, "u" + name + branch, codes, RoleScope.Branch, branch);
            Assert.Equal(HttpStatusCode.Created, (await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles", new AssignRoleRequest(role, null))).StatusCode);
        }

        var incoming = await OkAsync(await SendAsync(user.Client, HttpMethod.Get, "/api/v1/consignments?direction=incoming&pageSize=100", null, s.B));
        Assert.Equal([IdOf(cons)], incoming.GetProperty("items").EnumerateArray().Select(IdOf).ToList());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(user.Client, HttpMethod.Get, $"/api/v1/consignments/{IdOf(cons)}", null, s.B)).StatusCode);
    }

    [Fact]
    public async Task StockDetail_ExposesReservedAndAvailableQuantityPerLocation()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var req = await RequestAsync(s, s.AStore, 8m);
        var sid = IdOf(await NewShipmentAsync(s));
        await OkAsync(await AllocateAsync(s, sid, LineOf(req), 8m));

        var detail = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/stock/{s.W.ProductId}", null, s.Src));
        var row = detail.GetProperty("locations").EnumerateArray().Single(l => l.GetProperty("locationId").GetGuid() == s.SrcStore);
        Assert.Equal((10m, 8m, 2m), (row.GetProperty("qty").GetDecimal(), row.GetProperty("reservedQuantity").GetDecimal(), row.GetProperty("availableQuantity").GetDecimal()));
    }

    // ---- idempotency + permissions --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdempotentReplays_OfCreateAllocateAndCancel_ReturnTheSameResult_AndChangeNothingTwice()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var req = await RequestAsync(s, s.AStore, 10m);
        var createKey = "c-" + Guid.NewGuid();
        var first = await JsonAsync(await CreateShipmentAsync(s, key: createKey));
        var replay = await CreateShipmentAsync(s, key: createKey);
        Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
        Assert.Equal(IdOf(first), IdOf(await JsonAsync(replay)));
        Assert.Equal(1L, await AuditCountAsync(s.W, IdOf(first), "shipment.created"));
        var sid = IdOf(first);

        var key = "a-" + Guid.NewGuid();
        await OkAsync(await AllocateAsync(s, sid, LineOf(req), 4m, key));
        var again = await OkAsync(await AllocateAsync(s, sid, LineOf(req), 4m, key));
        Assert.Equal(4m, LineOfConsignment(ConsignmentTo(again, s.AStore)).GetProperty("allocatedBaseQty").GetDecimal());
        Assert.Equal(4m, await ReservedAsync(s));
        await AssertRefusedAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{Guid.NewGuid()}/allocations", new AllocateShipmentRequest(LineOf(req), s.Pc, 4m), s.Src, key), "IdempotencyKey"); // same key, another shipment

        var cancelKey = "x-" + Guid.NewGuid();
        await OkAsync(await CancelShipmentAsync(s, sid, "once", cancelKey));
        await OkAsync(await CancelShipmentAsync(s, sid, "twice", cancelKey));
        Assert.Equal(1L, await AuditCountAsync(s.W, sid, "shipment.cancelled"));
        Assert.Equal(0m, await ReservedAsync(s));
        await AssertReservationInvariantsAsync(s, "replays");
    }

    [Fact]
    public async Task Permissions_ViewPlanLoad_AreSeparate_AndUnknownIdsAre404()
    {
        var s = await NewSceneAsync(postgres, 10m);
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var req = await RequestAsync(s, s.AStore, 5m);
        var sid = IdOf(await NewShipmentAsync(s));
        var allocated = await OkAsync(await AllocateAsync(s, sid, LineOf(req), 4m)); // 1 of the request's 5 stays unallocated for the planner below
        var cons = ConsignmentTo(allocated, s.AStore);
        var lineId = IdOf(LineOfConsignment(cons));
        var viewer = await r.CreateAsync("vw", await RoleAsync(s.W, "viewer", ["Shipments.View"]), CompanyAccessScope.CompanyWide);
        var loader = await r.CreateAsync("ld", await RoleAsync(s.W, "loader", ["Shipments.View", "Shipments.Load"]), CompanyAccessScope.CompanyWide);
        var planner = await r.CreateAsync("pn", await RoleAsync(s.W, "plannerOnly", ["Shipments.View", "Shipments.Plan"]), CompanyAccessScope.CompanyWide);
        var none = await r.CreateAsync("nn", await RoleAsync(s.W, "nothing", ["Sales.View"]), CompanyAccessScope.CompanyWide);
        var load = new LoadConsignmentRequest([new LoadConsignmentLineRequest(lineId, 1m)]);

        Assert.Equal(HttpStatusCode.OK, (await GetShipmentAsync(s, sid, viewer.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetShipmentAsync(s, sid, none.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(none.Client, HttpMethod.Get, "/api/v1/shipments", null, s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(none.Client, HttpMethod.Get, "/api/v1/stock-reservations", null, s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(none.Client, HttpMethod.Get, "/api/v1/consignments?direction=outgoing", null, s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateShipmentAsync(s, client: viewer.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AllocateAsync(s, sid, LineOf(req), 1m, client: loader.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(viewer.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/load", load, s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(planner.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/load", load, s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(loader.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/cancel", new ShipmentReasonRequest("x"), s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(loader.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/load", load, s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AllocateAsync(s, sid, LineOf(req), 1m, client: planner.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetShipmentAsync(s, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/consignments/{Guid.NewGuid()}", null, s.Src)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s.W.Admin, HttpMethod.Get, "/api/v1/consignments", null, s.Src)).StatusCode); // direction required
    }

    // ---- 8. company isolation ----------------------------------------------------------------------------------------------------------------

    private static readonly SemaphoreSlim IsolationGate = new(1, 1);
    private static IsolationWorld? _isolation;

    [Fact]
    public async Task OtherCompanysShipmentConsignmentAndReservation_Are404_ForReadsAndEveryWrite_AndNothingChanges()
    {
        await IsolationGate.WaitAsync();
        IsolationWorld iso;
        try { iso = _isolation ??= await IsolationWorld.BuildAsync(postgres); }
        finally { IsolationGate.Release(); }
        using var a = iso.Host.NewClient(iso.A.Token);
        using var b = iso.Host.NewClient(iso.B.Token);
        Guid WarehouseLocation(Ids ids)
        {
            using var db = iso.Host.Database.CreateDbContext(ids.Company);
            return db.Warehouses.Where(x => x.Id == ids.WarehouseId).Select(x => x.StockLocationId).Single();
        }
        var db0 = iso.Host.Database;

        // Company A: an approved request, a shipment with one own-fleet vehicle and one allocation.
        var transfer = await OkAsync(await SendAsync(a, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.A.LocationId, WarehouseLocation(iso.A), "isolation", true, [new StockTransferLineRequest(iso.A.ProductId, iso.A.ProductUnitId, 4m)])));
        await OkAsync(await SendAsync(a, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(transfer)}/approve"));
        var lineRequest = transfer.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var created = await SendAsync(a, HttpMethod.Post, "/api/v1/shipments", new CreateShipmentRequest(iso.A.LocationId, TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, "iso"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sid = IdOf(await JsonAsync(created));
        var withVehicle = await OkAsync(await SendAsync(a, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(iso.A.VehicleId, null, null, null, null)));
        var svId = withVehicle.GetProperty("vehicles")[0].GetProperty("id").GetGuid();
        var allocated = await OkAsync(await SendAsync(a, HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(lineRequest, iso.A.ProductUnitId, 3m)));
        var cons = allocated.GetProperty("consignments")[0];
        var (cid, lid, rid) = (IdOf(cons), cons.GetProperty("lines")[0].GetProperty("id").GetGuid(), cons.GetProperty("lines")[0].GetProperty("reservationId").GetGuid());

        // Reads from B: never listed, detail 404; A still sees its own.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(b, HttpMethod.Get, $"/api/v1/shipments/{sid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(b, HttpMethod.Get, $"/api/v1/consignments/{cid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(a, HttpMethod.Get, $"/api/v1/shipments/{sid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(a, HttpMethod.Get, $"/api/v1/consignments/{cid}")).StatusCode);
        Assert.DoesNotContain(sid, (await OkAsync(await SendAsync(b, HttpMethod.Get, "/api/v1/shipments?pageSize=100&scope=company"))).GetProperty("items").EnumerateArray().Select(IdOf));
        Assert.Contains(sid, (await OkAsync(await SendAsync(a, HttpMethod.Get, "/api/v1/shipments?pageSize=100&scope=company"))).GetProperty("items").EnumerateArray().Select(IdOf));
        foreach (var direction in new[] { "incoming", "outgoing" })
        {
            Assert.DoesNotContain(cid, (await OkAsync(await SendAsync(b, HttpMethod.Get, $"/api/v1/consignments?direction={direction}&pageSize=100&scope=company"))).GetProperty("items").EnumerateArray().Select(IdOf));
        }
        foreach (var view in new[] { "current", "incoming", "outgoing", "completed", "discrepancies" })
        {
            Assert.DoesNotContain(cid, (await OkAsync(await SendAsync(b, HttpMethod.Get, $"/api/v1/transit?view={view}&pageSize=100&scope=company"))).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("consignmentId").GetGuid()));
        }
        Assert.DoesNotContain(rid, (await OkAsync(await SendAsync(b, HttpMethod.Get, "/api/v1/stock-reservations?pageSize=100&scope=company"))).GetProperty("items").EnumerateArray().Select(IdOf));
        Assert.Contains(rid, (await OkAsync(await SendAsync(a, HttpMethod.Get, "/api/v1/stock-reservations?pageSize=100&scope=company"))).GetProperty("items").EnumerateArray().Select(IdOf));

        var counts = await CountsAsync(db0);
        var state = await StateAsync(db0, sid, rid);

        // Writes from B naming A's ids: every one is a 404.
        var load = new LoadConsignmentRequest([new LoadConsignmentLineRequest(lid, 1m)]);
        foreach (var (method, route, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Put, $"/api/v1/shipments/{sid}", new UpdateShipmentRequest(TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, "hijack")),
            (HttpMethod.Post, $"/api/v1/shipments/{sid}/cancel", new ShipmentReasonRequest("hijack")),
            (HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(iso.B.VehicleId, null, null, null, null)),
            (HttpMethod.Delete, $"/api/v1/shipments/{sid}/vehicles/{svId}", null),
            (HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(lineRequest, iso.B.ProductUnitId, 1m)),
            (HttpMethod.Put, $"/api/v1/shipments/{sid}/allocations/{lid}", new ChangeAllocationRequest(1m)),
            (HttpMethod.Delete, $"/api/v1/shipments/{sid}/allocations/{lid}", null),
            (HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{cid}/vehicle", new AssignConsignmentVehicleRequest(null)),
            (HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{cid}/cancel", new ShipmentReasonRequest("hijack")),
            (HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{cid}/load", load),
            (HttpMethod.Post, $"/api/v1/stock-reservations/{rid}/release", new ReleaseStockReservationRequest("hijack")),
            // Phase 05: dispatch, receipts (record, post, list, get) naming A's shipment / consignment / line.
            (HttpMethod.Post, $"/api/v1/shipments/{sid}/dispatch", null),
            (HttpMethod.Post, $"/api/v1/consignments/{cid}/receipts", new RecordReceiptRequest([new ReceiptLineRequest(lid, 1m, 0m, 0m, 0m)], "hijack")),
            (HttpMethod.Post, $"/api/v1/consignments/{cid}/receipts/{Guid.NewGuid()}/post", null),
            (HttpMethod.Get, $"/api/v1/consignments/{cid}/receipts", null),
            (HttpMethod.Get, $"/api/v1/consignments/{cid}/receipts/{Guid.NewGuid()}", null),
            // Phase 06: approve, reject, dispositions (record, list) naming A's consignment / line.
            (HttpMethod.Post, $"/api/v1/consignments/{cid}/receipts/{Guid.NewGuid()}/approve", null),
            (HttpMethod.Post, $"/api/v1/consignments/{cid}/receipts/{Guid.NewGuid()}/reject", new RejectReceiptRequest("hijack")),
            (HttpMethod.Post, $"/api/v1/consignments/{cid}/dispositions", new RecordDispositionRequest("hijack", [new DispositionLineRequest(lid, DispositionBucket.Damaged, DispositionAction.WriteOff, 1m)])),
            (HttpMethod.Get, $"/api/v1/consignments/{cid}/dispositions", null),
        })
        {
            var response = await SendAsync(b, method, route, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        Assert.Equal(counts, await CountsAsync(db0));
        Assert.Equal(state, await StateAsync(db0, sid, rid));

        // B's own shipment naming A's masters: 400 on the field, nothing created.
        await AssertRefusedAsync(await SendAsync(b, HttpMethod.Post, "/api/v1/shipments", new CreateShipmentRequest(iso.A.LocationId, TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, null)), "sourceLocationId");
        var own = await SendAsync(b, HttpMethod.Post, "/api/v1/shipments", new CreateShipmentRequest(iso.B.LocationId, TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var ownId = IdOf(await JsonAsync(own));
        await AssertRefusedAsync(await SendAsync(b, HttpMethod.Post, $"/api/v1/shipments/{ownId}/vehicles", new AddShipmentVehicleRequest(iso.A.VehicleId, null, null, null, null)), "vehicleId");
        await AssertRefusedAsync(await SendAsync(b, HttpMethod.Post, $"/api/v1/shipments/{ownId}/allocations", new AllocateShipmentRequest(lineRequest, iso.B.ProductUnitId, 1m)), "stockTransferLineId");
        Assert.Equal(await StateAsync(db0, sid, rid), state);
        Assert.Equal(0L, await ScalarAsync<long>(db0, $"""SELECT COUNT(*) FROM "ConsignmentLines" WHERE "StockTransferLineId" = '{lineRequest}' AND "Id" <> '{lid}'"""));
        await AssertReconciliationScriptCleanAsync(db0, "isolation");
    }

    private static async Task<(long, long, long)> CountsAsync(TestDatabase db) =>
        (await ScalarAsync<long>(db, """SELECT COUNT(*) FROM "Shipments" """), await ScalarAsync<long>(db, """SELECT COUNT(*) FROM "ConsignmentLines" """),
         await ScalarAsync<long>(db, """SELECT COUNT(*) FROM "StockReservations" """));

    private static async Task<(string, string, decimal)> StateAsync(TestDatabase db, Guid shipment, Guid reservation) =>
        (await ScalarAsync<string>(db, $"""SELECT "Status" FROM "Shipments" WHERE "Id" = '{shipment}'"""),
         await ScalarAsync<string>(db, $"""SELECT "Status" FROM "StockReservations" WHERE "Id" = '{reservation}'"""),
         await ScalarAsync<decimal>(db, $"""SELECT "BaseQty" FROM "StockReservations" WHERE "Id" = '{reservation}'"""));
}
