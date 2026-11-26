using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// GET /shipments/{id}/eligible-vehicles: the own-fleet picker for branch planners, who hold Shipments.Plan but not the company-only
/// Company.Vehicles.View. Returns what AddVehicleAsync accepts (active, central or the shipment's branch, not already on it); hidden = 404.
/// </summary>
public class ShipmentEligibleVehiclesEndpointTests
{
    private static async Task<Guid> VehicleIdAsync(ShipmentWorld w, Guid stockLocationId)
    {
        await using var db = w.T.Db();
        return await db.Vehicles.Where(v => v.StockLocationId == stockLocationId).Select(v => v.Id).SingleAsync();
    }

    private static async Task<ActionResult<IReadOnlyList<ShipmentEligibleVehicleDto>>> GetAsync(ShipmentWorld w, Guid shipmentId, Guid branch, IEffectivePermissionService permissions)
    {
        await using var db = w.T.Db(branch, new HashSet<Guid> { branch });
        var controller = new ShipmentsController(new ShipmentService(db, permissions), db, permissions);
        return await controller.EligibleVehicles(shipmentId, default);
    }

    [Fact]
    public async Task BranchPlanner_SeesCentralAndOwnBranchVehicles_NotOtherBranches_AndNotOnesAlreadyOnTheShipment()
    {
        var w = await ShipmentWorld.CreateAsync();
        var s = await w.CreateShipmentAsync(); // Main source, own fleet
        var central = await VehicleIdAsync(w, w.L.CentralVan);
        var planner = FakeScopedPermissions.AtBranch(w.Main, "Shipments.Plan");

        var list = Assert.IsType<OkObjectResult>((await GetAsync(w, s.Id, w.Main, planner)).Result).Value as IReadOnlyList<ShipmentEligibleVehicleDto>;
        Assert.Equal([central], list!.Select(v => v.Id));                // BLR's van is not offered to a Main shipment

        await w.AddOwnVehicleAsync(s.Id, central);
        list = Assert.IsType<OkObjectResult>((await GetAsync(w, s.Id, w.Main, planner)).Result).Value as IReadOnlyList<ShipmentEligibleVehicleDto>;
        Assert.Empty(list!);                                              // already on the shipment
    }

    [Fact]
    public async Task OtherBranch_GetsNotFound_AndExternalProviderShipments_HaveNoOwnFleetChoices()
    {
        var w = await ShipmentWorld.CreateAsync();
        var s = await w.CreateShipmentAsync();
        Assert.IsType<NotFoundResult>((await GetAsync(w, s.Id, w.Blr, FakeScopedPermissions.AtBranch(w.Blr, "Shipments.Plan"))).Result);

        var external = await w.CreateShipmentAsync(via: TransportVia.ExternalProvider, provider: "Blue Dart");
        var list = Assert.IsType<OkObjectResult>((await GetAsync(w, external.Id, w.Main, FakeScopedPermissions.AtBranch(w.Main, "Shipments.Plan"))).Result).Value as IReadOnlyList<ShipmentEligibleVehicleDto>;
        Assert.Empty(list!);
    }
}
