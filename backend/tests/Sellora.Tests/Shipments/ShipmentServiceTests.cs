using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Shipments;

/// <summary>
/// Phase 04 (FI-D29..D33) on EF InMemory: shipment header rules, vehicle scope, allocation limits and reservations, release paths, loading and
/// status transitions, legacy dispatch/cancel refusals, idempotent replays. Every test re-checks the reservation invariant (FI-D5). Locks and
/// constraints are not exercised here (integration tests).
/// </summary>
public class ShipmentServiceTests
{
    private static Task<string?> Field(Func<Task> act) => ShipmentWorld.FieldAsync(act);

    private static async Task<Guid> VehicleIdAsync(ShipmentWorld w, Guid stockLocationId)
    {
        await using var db = w.T.Db();
        return await db.Vehicles.Where(v => v.StockLocationId == stockLocationId).Select(v => v.Id).SingleAsync();
    }

    // Approved request of 5 Box (50 pc) Main store -> BLR store, a Draft Road/own-fleet shipment, and the allocation of all of it.
    private static async Task<(ShipmentWorld W, StockTransfer T, Guid LineId, Shipment S)> PlannedAsync(decimal boxes = 5m, decimal stock = 100m, decimal allocateBoxes = 5m)
    {
        var w = await ShipmentWorld.CreateAsync(stock);
        var t = await w.ApprovedPenAsync(boxes);
        var lineId = t.Lines.Single().Id;
        var s = await w.CreateShipmentAsync();
        if (allocateBoxes > 0) await w.AllocateAsync(s.Id, lineId, w.T.Box, allocateBoxes);
        return (w, t, lineId, await w.ReloadAsync(s.Id));
    }

    // ---- header ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_RoadWithoutReference_IsDraftWithAShpNumber()
    {
        var w = await ShipmentWorld.CreateAsync();
        var s = await w.CreateShipmentAsync();

        Assert.Equal(ShipmentStatus.Draft, s.Status);
        Assert.StartsWith("SHP-", s.ShipmentNo);
        Assert.Equal(w.Main, s.BranchId);
        Assert.Equal(w.L.MainStore, s.SourceLocationId);
        await w.AssertReconciledAsync("create");
    }

    [Theory]
    [InlineData(TransportMode.Air)]
    [InlineData(TransportMode.Sea)]
    [InlineData(TransportMode.Rail)]
    [InlineData(TransportMode.Courier)]
    [InlineData(TransportMode.HandCarry)]
    public async Task Create_NonRoadModeWithoutReference_RefusesOnTransportReference(TransportMode mode)
    {
        var w = await ShipmentWorld.CreateAsync();
        Assert.Equal("transportReference", await Field(() => w.CreateShipmentAsync(mode: mode, via: TransportVia.ExternalProvider, provider: "DHL", reference: "  ")));
        Assert.NotNull(await w.CreateShipmentAsync(mode: mode, via: TransportVia.ExternalProvider, provider: "DHL", reference: "REF-1"));
    }

    [Fact]
    public async Task Create_ExternalProviderWithoutName_RefusesOnProviderName()
    {
        var w = await ShipmentWorld.CreateAsync();
        Assert.Equal("providerName", await Field(() => w.CreateShipmentAsync(via: TransportVia.ExternalProvider, provider: " ")));
    }

    [Fact]
    public async Task Update_ChecksTheReferenceAgainstTheNewMode_AndOnlyInDraftOrLoading()
    {
        var w = await ShipmentWorld.CreateAsync();
        var s = await w.CreateShipmentAsync();

        Assert.Equal("transportReference", await Field(() => w.UpdateAsync(s.Id, TransportMode.Air, TransportVia.OwnFleet)));
        var updated = await w.UpdateAsync(s.Id, TransportMode.Air, TransportVia.OwnFleet, reference: "AWB-77");
        Assert.Equal(TransportMode.Air, updated!.TransportMode);

        await w.CancelAsync(s.Id);
        Assert.Equal("status", await Field(() => w.UpdateAsync(s.Id, TransportMode.Road, TransportVia.OwnFleet)));
    }

    [Fact]
    public async Task Create_SourceMustFitTheWorkingBranch_ForABranchOnlyHolder()
    {
        var w = await ShipmentWorld.CreateAsync();
        var mainPlanner = FakeScopedPermissions.AtBranch(w.Main, "Shipments.Plan");

        Assert.NotNull(await w.CreateShipmentAsync(branch: w.Main, permissions: mainPlanner));
        Assert.Equal("stockLocationId", await Field(() => w.CreateShipmentAsync(branch: w.Blr, permissions: mainPlanner)));            // Main's store from BLR
        Assert.Equal("stockLocationId", await Field(() => w.CreateShipmentAsync(source: w.L.CompanyWarehouse, branch: w.Main, permissions: mainPlanner))); // company-level needs the company grant
        Assert.NotNull(await w.CreateShipmentAsync(source: w.L.CompanyWarehouse, branch: w.Main));                                      // company-level holder
    }

    [Fact]
    public async Task Create_WithTheSameIdempotencyKey_ReplaysTheFirstShipment()
    {
        var w = await ShipmentWorld.CreateAsync();
        var first = await w.CreateShipmentAsync(key: "k1");
        var second = await w.CreateShipmentAsync(key: "k1");

        Assert.Equal(first.Id, second.Id);
        await using var db = w.T.Db();
        Assert.Equal(1, await db.Shipments.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == AuditActions.ShipmentCreated));
    }

    // ---- visibility --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BranchFilter_ShowsAShipmentToItsSourceBranchOnly_AndCompanyLevelSourcesToEveryone()
    {
        var w = await ShipmentWorld.CreateAsync();
        var branchShipment = await w.CreateShipmentAsync();                                          // Main store
        var companyShipment = await w.CreateShipmentAsync(source: w.L.CompanyWarehouse);              // company-level source: BranchId null
        Assert.Null(companyShipment.BranchId);

        await using var blr = w.T.Db(w.Blr, new HashSet<Guid> { w.Blr });
        var visible = await blr.Shipments.Select(s => s.Id).ToListAsync();
        Assert.DoesNotContain(branchShipment.Id, visible);
        Assert.Contains(companyShipment.Id, visible);

        await using var main = w.T.Db(w.Main, new HashSet<Guid> { w.Main });
        Assert.Contains(branchShipment.Id, await main.Shipments.Select(s => s.Id).ToListAsync());

        // The service answers null (404) for a hidden shipment.
        await using var hidden = w.T.Db(w.Blr, new HashSet<Guid> { w.Blr });
        Assert.Null(await new ShipmentService(hidden, ShipmentWorld.Everything()).UpdateAsync(branchShipment.Id, new UpdateShipmentInput(TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, null), w.UserId));
    }

    // ---- vehicles ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddVehicle_OwnFleet_AcceptsCentralAndSourceBranchVehicles_NotOtherBranchesOrInactiveOnes()
    {
        var w = await ShipmentWorld.CreateAsync();
        var s = await w.CreateShipmentAsync(); // Main source
        var central = await VehicleIdAsync(w, w.L.CentralVan);
        var blrVan = await VehicleIdAsync(w, w.L.BlrVan);

        Assert.Equal("vehicleId", await Field(() => w.AddOwnVehicleAsync(s.Id, blrVan)));        // assigned to BLR, source is Main
        Assert.Equal("vehicleId", await Field(() => w.AddOwnVehicleAsync(s.Id, Guid.NewGuid())));
        var after = await w.AddOwnVehicleAsync(s.Id, central);
        Assert.Single(after!.Vehicles);
        Assert.Equal("vehicleId", await Field(() => w.AddOwnVehicleAsync(s.Id, central)));       // duplicate

        await using (var db = w.T.Db())
        {
            var van = await db.Vehicles.SingleAsync(v => v.Id == central);
            van.IsActive = false;
            await db.SaveChangesAsync();
        }
        var s2 = await w.CreateShipmentAsync();
        Assert.Equal("vehicleId", await Field(() => w.AddOwnVehicleAsync(s2.Id, central)));      // inactive

        // A BLR-sourced shipment accepts the BLR van.
        var blrShipment = await w.CreateShipmentAsync(source: w.L.BlrStore, branch: w.Blr);
        Assert.Single((await w.AddOwnVehicleAsync(blrShipment.Id, blrVan, branch: w.Blr))!.Vehicles);
    }

    [Fact]
    public async Task AddVehicle_MustMatchTheTransportVia()
    {
        var w = await ShipmentWorld.CreateAsync();
        var own = await w.CreateShipmentAsync();
        var external = await w.CreateShipmentAsync(via: TransportVia.ExternalProvider, provider: "FastTrans");

        var central = await VehicleIdAsync(w, w.L.CentralVan);

        Assert.Equal("vehicleId", await Field(() => w.AddExternalVehicleAsync(own.Id)));
        Assert.Equal("vehicleId", await Field(() => w.AddOwnVehicleAsync(external.Id, central)));
        var ok = await w.AddExternalVehicleAsync(external.Id);
        Assert.Equal("KA01AB1234", ok!.Vehicles.Single().ExternalVehicleNumber);
        Assert.Equal("transportVia", await Field(() => w.UpdateAsync(external.Id, TransportMode.Road, TransportVia.OwnFleet)));
    }

    [Fact]
    public async Task RemoveVehicle_RefusedWhileAConsignmentUsesIt()
    {
        var (w, _, _, s) = await PlannedAsync();
        var central = await VehicleIdAsync(w, w.L.CentralVan);
        var withVehicle = await w.AddOwnVehicleAsync(s.Id, central);
        var legId = withVehicle!.Vehicles.Single().Id;
        await w.AssignAsync(s.Id, s.Consignments.Single().Id, legId);

        Assert.Equal("shipmentVehicleId", await Field(() => w.RemoveVehicleAsync(s.Id, legId)));
        await w.AssignAsync(s.Id, s.Consignments.Single().Id, null);
        Assert.Empty((await w.RemoveVehicleAsync(s.Id, legId))!.Vehicles);
    }

    // ---- allocation --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Allocate_CreatesConsignmentLineAndReservation_AndMaintainsTheCounters()
    {
        var (w, t, lineId, s) = await PlannedAsync(boxes: 5m, allocateBoxes: 3m);

        var consignment = Assert.Single(s.Consignments);
        Assert.StartsWith("CON-", consignment.ConsignmentNo);
        Assert.Equal(w.L.BlrStore, consignment.DestinationLocationId);
        Assert.Equal(w.Blr, consignment.DestinationBranchId);
        var line = Assert.Single(consignment.Lines);
        Assert.Equal((3m, 30m, w.T.Box, "box"), (line.EnteredQty, line.AllocatedBaseQty, line.ProductUnitId, line.UnitCode));
        Assert.Equal(30m, await w.RequestAllocatedAsync(t.Id));
        Assert.Equal(30m, await w.ReservedAsync(w.L.MainStore));
        var reservation = Assert.Single(await w.ReservationsAsync());
        Assert.Equal((StockReservationStatus.Active, 30m, line.Id), (reservation.Status, reservation.BaseQty, reservation.ConsignmentLineId));
        Assert.Equal(ShipmentStatus.Draft, s.Status);
        await w.AssertReconciledAsync("allocate");

        // More of the same request line extends the line and the single reservation.
        await w.AllocateAsync(s.Id, lineId, w.T.Box, 2m);
        var again = await w.ReloadAsync(s.Id);
        Assert.Equal(50m, again.Consignments.Single().Lines.Single().AllocatedBaseQty);
        Assert.Equal(50m, Assert.Single(await w.ReservationsAsync()).BaseQty);
        Assert.Equal(50m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal(100m, await w.T.QohAsync(w.L.MainStore)); // nothing leaves the shelf
        await w.AssertReconciledAsync("allocate more");
    }

    [Fact]
    public async Task Allocate_LimitedByWhatRemainsOnTheRequest()
    {
        var (w, _, lineId, s) = await PlannedAsync(boxes: 5m, allocateBoxes: 3m);

        Assert.Equal("quantity", await Field(() => w.AllocateAsync(s.Id, lineId, w.T.Box, 3m)));  // only 2 Box remain
        await w.AllocateAsync(s.Id, lineId, w.T.Box, 2m);
        Assert.Equal("quantity", await Field(() => w.AllocateAsync(s.Id, lineId, w.T.Box, 1m)));
        await w.AssertReconciledAsync("request limit");
    }

    [Fact]
    public async Task Allocate_LimitedByAvailableStock_OnHandLessReserved()
    {
        var w = await ShipmentWorld.CreateAsync(penStockAtMain: 60m);
        var a = await w.ApprovedPenAsync(5m);   // 50 pc
        var b = await w.ApprovedPenAsync(5m);   // 50 pc
        var s = await w.CreateShipmentAsync();

        await w.AllocateAsync(s.Id, a.Lines.Single().Id, w.T.Box, 5m);                    // 50 of 60
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.AllocateAsync(s.Id, b.Lines.Single().Id, w.T.Box, 2m)); // 20 > 10 left
        Assert.Equal("quantity", ex.Field);
        Assert.Contains("available", ex.Message);
        await w.AllocateAsync(s.Id, b.Lines.Single().Id, w.T.Box, 1m);                    // 10 fits exactly
        Assert.Equal(60m, await w.ReservedAsync(w.L.MainStore));
        await w.AssertReconciledAsync("available limit");
    }

    [Fact]
    public async Task Allocate_RequestFromAnotherSource_IsRefusedAsMultiPickup()
    {
        var w = await ShipmentWorld.CreateAsync();
        var elsewhere = await w.ApprovedPenAsync(2m, source: w.L.MainWarehouse, destination: w.L.BlrStore);
        var s = await w.CreateShipmentAsync(); // Main store

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.AllocateAsync(s.Id, elsewhere.Lines.Single().Id, w.T.Box, 1m));
        Assert.Equal("stockTransferLineId", ex.Field);
        Assert.Contains("Multi-pickup", ex.Message);
    }

    [Fact]
    public async Task Allocate_OnlyApprovedOrPartiallyFulfilledRequests()
    {
        var w = await ShipmentWorld.CreateAsync();
        var requested = await w.T.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.T.Pen(w.T.Box, 2m));
        var s = await w.CreateShipmentAsync();

        Assert.Equal("stockTransferLineId", await Field(() => w.AllocateAsync(s.Id, requested.Lines.Single().Id, w.T.Box, 1m)));
        Assert.Equal("stockTransferLineId", await Field(() => w.AllocateAsync(s.Id, Guid.NewGuid(), w.T.Box, 1m)));
    }

    [Fact]
    public async Task Allocate_SeparatesDestinations_AndSharesAConsignmentPerDestination()
    {
        var w = await ShipmentWorld.CreateAsync();
        var toStore1 = await w.ApprovedPenAsync(2m);
        var toStore2 = await w.ApprovedPenAsync(1m);
        var toWarehouse = await w.ApprovedPenAsync(3m, destination: w.L.BlrWarehouse);
        var s = await w.CreateShipmentAsync();

        await w.AllocateAsync(s.Id, toStore1.Lines.Single().Id, w.T.Box, 2m);
        await w.AllocateAsync(s.Id, toStore2.Lines.Single().Id, w.T.Box, 1m);
        await w.AllocateAsync(s.Id, toWarehouse.Lines.Single().Id, w.T.Box, 3m);

        var reloaded = await w.ReloadAsync(s.Id);
        Assert.Equal(2, reloaded.Consignments.Count);
        var toBlrStore = reloaded.Consignments.Single(c => c.DestinationLocationId == w.L.BlrStore);
        Assert.Equal(2, toBlrStore.Lines.Count);                                         // two requests, one consignment
        Assert.All(reloaded.Consignments, c => Assert.All(c.Lines, l => Assert.Equal(
            c.DestinationLocationId, w.T.ReloadAsync(l.StockTransferId).Result.DestinationLocationId))); // lines share their consignment's destination
        Assert.Equal(60m, await w.ReservedAsync(w.L.MainStore));
        await w.AssertReconciledAsync("destinations");
    }

    [Fact]
    public async Task Allocate_InAnotherUnitThanTheExistingLine_IsRefused_AndDecimalsAreChecked()
    {
        var (w, _, lineId, s) = await PlannedAsync(boxes: 5m, allocateBoxes: 1m);

        Assert.Equal("productUnitId", await Field(() => w.AllocateAsync(s.Id, lineId, w.T.Pc, 10m)));
        Assert.Equal("quantity", await Field(() => w.AllocateAsync(s.Id, lineId, w.T.Box, 1.5m)));   // Box is counted in whole numbers
        Assert.Equal("quantity", await Field(() => w.AllocateAsync(s.Id, lineId, w.T.Box, 0m)));
    }

    [Fact]
    public async Task Allocate_InStatusReadyToDispatchOrCancelled_IsRefused()
    {
        var (w, _, lineId, s) = await PlannedAsync(boxes: 6m, allocateBoxes: 5m);
        await w.AssignAsync(s.Id, s.Consignments.Single().Id, null);
        var withVehicle = await w.AddOwnVehicleAsync(s.Id, await VehicleIdAsync(w, w.L.CentralVan));
        await w.AssignAsync(s.Id, s.Consignments.Single().Id, withVehicle!.Vehicles.Single().Id);
        await w.LoadAsync(s.Id, s.Consignments.Single().Id, s.Consignments.Single().Lines.Single().Id, 5m);
        Assert.Equal(ShipmentStatus.ReadyToDispatch, (await w.ReloadAsync(s.Id)).Status);

        Assert.Equal("status", await Field(() => w.AllocateAsync(s.Id, lineId, w.T.Box, 1m)));
        await w.CancelAsync(s.Id);
        Assert.Equal("status", await Field(() => w.AllocateAsync(s.Id, lineId, w.T.Box, 1m)));
    }

    [Fact]
    public async Task Allocate_WithTheSameIdempotencyKey_ReservesOnce()
    {
        var w = await ShipmentWorld.CreateAsync();
        var t = await w.ApprovedPenAsync(5m);
        var s = await w.CreateShipmentAsync();

        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 2m, key: "a1");
        var replay = await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 2m, key: "a1");

        Assert.Equal(20m, replay!.Consignments.Single().Lines.Single().AllocatedBaseQty);
        Assert.Equal(20m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal("IdempotencyKey", await Field(() => w.AllocateAsync(Guid.NewGuid(), t.Lines.Single().Id, w.T.Box, 2m, key: "a1")));
        await w.AssertReconciledAsync("replay");
    }

    // ---- reservations are a hard block for everything else (FI-D6/D7) ------------------------------------------------------------

    private static async Task<ShipmentWorld> RiceAllocatedAsync(decimal kg)
    {
        var w = await ShipmentWorld.CreateAsync(penStockAtMain: 0m);
        var rice = await w.T.ApprovedAsync(w.L.MainStore, w.L.BlrStore, TransferWorld.Line(w.T.W.ProductId, w.T.W.ProductUnitId, kg));
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, rice.Lines.Single().Id, w.T.W.ProductUnitId, kg);
        return w;
    }

    [Fact]
    public async Task Sale_AfterReservation_CannotUseReservedStock()
    {
        var w = await RiceAllocatedAsync(80m);   // Main store holds 100 kg of Rice

        await Assert.ThrowsAsync<DomainValidationException>(() => w.L.SellAsync(w.Main, w.L.MainStore, qty: 30m));
        var sale = await w.L.SellAsync(w.Main, w.L.MainStore, qty: 20m);
        Assert.NotNull(sale);
        Assert.Equal(80m, await w.L.QohAsync(w.L.MainStore));
        await w.AssertReconciledAsync("sale vs reservation");
    }

    [Fact]
    public async Task AdjustmentDecrease_CannotUseReservedStock()
    {
        var w = await RiceAllocatedAsync(80m);

        await Assert.ThrowsAsync<DomainValidationException>(() => w.L.AdjustAsync(w.Main, w.L.MainStore, AdjustmentDirection.Decrease, 30m));
        await w.L.AdjustAsync(w.Main, w.L.MainStore, AdjustmentDirection.Decrease, 20m);
        await w.AssertReconciledAsync("adjustment vs reservation");
    }

    [Fact]
    public async Task LegacyDispatch_OfAnotherRequest_CannotUseReservedStock()
    {
        var w = await ShipmentWorld.CreateAsync(penStockAtMain: 100m);
        var allocated = await w.ApprovedPenAsync(9m);   // 90 pc reserved
        var other = await w.ApprovedPenAsync(2m);       // 20 pc, nothing allocated on it
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, allocated.Lines.Single().Id, w.T.Box, 9m);

        await Assert.ThrowsAsync<DomainValidationException>(() => w.T.DispatchAsRequestedAsync(other.Id));

        await w.CancelAsync(s.Id);
        Assert.NotNull(await w.T.DispatchAsRequestedAsync(other.Id));
        await w.AssertReconciledAsync("legacy after release");
    }

    // ---- reduce / release --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangeAllocation_Decrease_ReleasesTheDifference_AndZeroCancelsTheLine()
    {
        var (w, t, lineId, s) = await PlannedAsync(boxes: 5m, allocateBoxes: 5m);
        var line = s.Consignments.Single().Lines.Single();

        await w.ChangeAsync(s.Id, line.Id, 2m);
        Assert.Equal(20m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal(20m, await w.RequestAllocatedAsync(t.Id));
        Assert.Equal(20m, Assert.Single(await w.ReservationsAsync()).BaseQty);
        await w.AssertReconciledAsync("reduce");

        await w.ChangeAsync(s.Id, line.Id, 4m);                       // and back up within the limits
        Assert.Equal(40m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal("quantity", await Field(() => w.ChangeAsync(s.Id, line.Id, 6m)));
        await w.AssertReconciledAsync("increase");

        await w.ChangeAsync(s.Id, line.Id, 0m);
        var cancelled = (await w.ReloadAsync(s.Id)).Consignments.Single().Lines.Single();
        Assert.Equal((ConsignmentLineStatus.Cancelled, 0m, 0m), (cancelled.Status, cancelled.EnteredQty, cancelled.AllocatedBaseQty));
        Assert.NotNull(cancelled.CancelledAt);
        var reservation = Assert.Single(await w.ReservationsAsync());
        Assert.Equal(StockReservationStatus.Released, reservation.Status);
        Assert.NotNull(reservation.ReleasedAt);
        Assert.Equal(w.UserId, reservation.ReleasedByUserId);
        Assert.Equal(0m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal(0m, await w.RequestAllocatedAsync(t.Id));
        await w.AssertReconciledAsync("cancel line");

        // The request can be allocated again afterwards (a new active line on the same consignment).
        await w.AllocateAsync(s.Id, lineId, w.T.Box, 5m);
        Assert.Equal(50m, await w.ReservedAsync(w.L.MainStore));
        await w.AssertReconciledAsync("re-allocate");
    }

    [Fact]
    public async Task CancelAllocation_IsTheSameAsZero()
    {
        var (w, t, _, s) = await PlannedAsync();
        await w.CancelAllocationAsync(s.Id, s.Consignments.Single().Lines.Single().Id);

        Assert.Equal(0m, await w.RequestAllocatedAsync(t.Id));
        Assert.Equal(0m, await w.ReservedAsync(w.L.MainStore));
        await w.AssertReconciledAsync("cancel allocation");
    }

    [Fact]
    public async Task CancelShipment_ReleasesEveryReservation_AndCancelsConsignmentsAndLines()
    {
        var w = await ShipmentWorld.CreateAsync();
        var a = await w.ApprovedPenAsync(3m);
        var b = await w.ApprovedPenAsync(2m, destination: w.L.BlrWarehouse);
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, a.Lines.Single().Id, w.T.Box, 3m);
        await w.AllocateAsync(s.Id, b.Lines.Single().Id, w.T.Box, 2m);

        var cancelled = await w.CancelAsync(s.Id);

        Assert.Equal(ShipmentStatus.Cancelled, cancelled!.Status);
        var reloaded = await w.ReloadAsync(s.Id);
        Assert.NotNull(reloaded.CancelledAt);
        Assert.Equal(w.UserId, reloaded.CancelledByUserId);
        Assert.All(reloaded.Consignments, c =>
        {
            Assert.Equal(ConsignmentStatus.Cancelled, c.Status);
            Assert.NotNull(c.CancelledAt);
            Assert.NotNull(c.CancelledByUserId);
            Assert.All(c.Lines, l => Assert.Equal(ConsignmentLineStatus.Cancelled, l.Status));
        });
        Assert.All(await w.ReservationsAsync(), r => Assert.Equal(StockReservationStatus.Released, r.Status));
        Assert.Equal(0m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal(0m, await w.RequestAllocatedAsync(a.Id) + await w.RequestAllocatedAsync(b.Id));
        await w.AssertReconciledAsync("cancel shipment");

        // By state: repeating the cancel changes nothing.
        await w.CancelAsync(s.Id);
        await using var db = w.T.Db();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.ShipmentCancelled));
    }

    [Fact]
    public async Task CancelShipment_WithTheSameKey_ReplaysWithoutASecondEffect()
    {
        var (w, _, _, s) = await PlannedAsync();
        await w.CancelAsync(s.Id, key: "c1");
        var replay = await w.CancelAsync(s.Id, key: "c1");
        Assert.Equal(ShipmentStatus.Cancelled, replay!.Status);
        await w.AssertReconciledAsync("cancel replay");
    }

    [Fact]
    public async Task CancelConsignment_ReleasesItsLinesOnly()
    {
        var w = await ShipmentWorld.CreateAsync();
        var a = await w.ApprovedPenAsync(3m);
        var b = await w.ApprovedPenAsync(2m, destination: w.L.BlrWarehouse);
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, a.Lines.Single().Id, w.T.Box, 3m);
        await w.AllocateAsync(s.Id, b.Lines.Single().Id, w.T.Box, 2m);
        var toWarehouse = (await w.ReloadAsync(s.Id)).Consignments.Single(c => c.DestinationLocationId == w.L.BlrWarehouse);

        await w.CancelConsignmentAsync(s.Id, toWarehouse.Id);

        Assert.Equal(30m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal(0m, await w.RequestAllocatedAsync(b.Id));
        Assert.Equal(30m, await w.RequestAllocatedAsync(a.Id));
        await w.AssertReconciledAsync("cancel consignment");

        // The destination can be planned again: a fresh consignment (the cancelled one stays as history).
        await w.AllocateAsync(s.Id, b.Lines.Single().Id, w.T.Box, 2m);
        Assert.Equal(3, (await w.ReloadAsync(s.Id)).Consignments.Count);
        await w.AssertReconciledAsync("re-plan");
    }

    [Fact]
    public async Task ReleaseReservation_CancelsTheLine_AndReducesTheRequestAllocation()
    {
        var (w, t, _, s) = await PlannedAsync();
        var reservation = Assert.Single(await w.ReservationsAsync());

        var released = await w.ReleaseAsync(reservation.Id);

        Assert.Equal(StockReservationStatus.Released, released!.Status);
        Assert.Equal("stale plan", released.ReleaseReason);
        Assert.Equal(0m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal(0m, await w.RequestAllocatedAsync(t.Id));
        Assert.Equal(ConsignmentLineStatus.Cancelled, (await w.ReloadAsync(s.Id)).Consignments.Single().Lines.Single().Status);
        await w.AssertReconciledAsync("release");

        Assert.Equal(StockReservationStatus.Released, (await w.ReleaseAsync(reservation.Id))!.Status);  // by state
        Assert.Null(await w.ReleaseAsync(Guid.NewGuid()));
        await using var db = w.T.Db();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.StockReservationReleased));
    }

    [Fact]
    public async Task ReleaseReservation_OfAShipmentOutsideTheCallersSourceBranch_IsNotFound()
    {
        var (w, _, _, _) = await PlannedAsync();
        var reservation = Assert.Single(await w.ReservationsAsync());

        await using var db = w.T.Db(w.Blr, new HashSet<Guid> { w.Blr });
        Assert.Null(await new ShipmentService(db, ShipmentWorld.Everything()).ReleaseReservationAsync(reservation.Id, "x", w.UserId));
        Assert.Equal(StockReservationStatus.Active, (await w.ReservationsAsync()).Single().Status);
    }

    // ---- legs and loading --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Load_PartialThenFull_MovesDraftToLoadingToReadyToDispatch_OnceTheLegIsAssigned()
    {
        var (w, _, _, s) = await PlannedAsync();
        var consignment = s.Consignments.Single();
        var line = consignment.Lines.Single();
        var withVehicle = await w.AddOwnVehicleAsync(s.Id, await VehicleIdAsync(w, w.L.CentralVan));
        var legId = withVehicle!.Vehicles.Single().Id;

        await w.LoadAsync(s.Id, consignment.Id, line.Id, 3m);
        var partial = await w.ReloadAsync(s.Id);
        Assert.Equal(ShipmentStatus.Loading, partial.Status);
        Assert.Equal(30m, partial.Consignments.Single().Lines.Single().LoadedBaseQty);
        Assert.Equal(ConsignmentStatus.Planned, partial.Consignments.Single().Status);

        await w.LoadAsync(s.Id, consignment.Id, line.Id, 5m);                      // fully loaded but no leg yet (own fleet)
        var noLeg = await w.ReloadAsync(s.Id);
        Assert.Equal((ShipmentStatus.Loading, ConsignmentStatus.Planned), (noLeg.Status, noLeg.Consignments.Single().Status));

        await w.AssignAsync(s.Id, consignment.Id, legId);
        var ready = await w.ReloadAsync(s.Id);
        Assert.Equal((ShipmentStatus.ReadyToDispatch, ConsignmentStatus.Loaded), (ready.Status, ready.Consignments.Single().Status));
        var loaded = ready.Consignments.Single();
        Assert.NotNull(loaded.LoadedAt);
        Assert.Equal(w.UserId, loaded.LoadedByUserId);
        await w.AssertReconciledAsync("loaded");
    }

    [Fact]
    public async Task Load_Correction_BelowAllocationDemotesTheStatuses_AndZeroGoesBackToDraft()
    {
        var (w, _, _, s) = await PlannedAsync();
        var consignment = s.Consignments.Single();
        var line = consignment.Lines.Single();
        var leg = (await w.AddOwnVehicleAsync(s.Id, await VehicleIdAsync(w, w.L.CentralVan)))!.Vehicles.Single().Id;
        await w.AssignAsync(s.Id, consignment.Id, leg);
        await w.LoadAsync(s.Id, consignment.Id, line.Id, 5m);
        Assert.Equal(ShipmentStatus.ReadyToDispatch, (await w.ReloadAsync(s.Id)).Status);

        await w.LoadAsync(s.Id, consignment.Id, line.Id, 4m);
        var corrected = await w.ReloadAsync(s.Id);
        Assert.Equal((ShipmentStatus.Loading, ConsignmentStatus.Planned), (corrected.Status, corrected.Consignments.Single().Status));
        Assert.Null(corrected.Consignments.Single().LoadedAt);

        await w.LoadAsync(s.Id, consignment.Id, line.Id, 0m);
        Assert.Equal(ShipmentStatus.Draft, (await w.ReloadAsync(s.Id)).Status);
        await w.AssertReconciledAsync("unload");
    }

    [Fact]
    public async Task Load_LimitsAndValidation()
    {
        var (w, _, _, s) = await PlannedAsync();
        var consignment = s.Consignments.Single();
        var line = consignment.Lines.Single();

        Assert.Equal("lines[0].loadedQuantity", await Field(() => w.LoadAsync(s.Id, consignment.Id, line.Id, 6m)));     // above allocated
        Assert.Equal("lines[0].loadedQuantity", await Field(() => w.LoadAsync(s.Id, consignment.Id, line.Id, 2.5m)));   // whole boxes
        Assert.Equal("lines[0].consignmentLineId", await Field(() => w.LoadAsync(s.Id, consignment.Id, Guid.NewGuid(), 1m)));
        Assert.Equal("consignmentId", await Field(() => w.LoadAsync(s.Id, Guid.NewGuid(), line.Id, 1m)));
        Assert.Equal(ShipmentStatus.Draft, (await w.ReloadAsync(s.Id)).Status);
    }

    [Fact]
    public async Task Load_RequiresTheLoadFit_AndAnOpenShipment()
    {
        var (w, _, _, s) = await PlannedAsync();
        var consignment = s.Consignments.Single();
        var line = consignment.Lines.Single();

        var blrLoader = FakeScopedPermissions.AtBranch(w.Blr, "Shipments.Load");
        Assert.Equal("stockLocationId", await Field(() => w.LoadAsync(s.Id, consignment.Id, line.Id, 1m, branch: w.Blr, permissions: blrLoader)));

        await w.CancelAsync(s.Id);
        Assert.Equal("status", await Field(() => w.LoadAsync(s.Id, consignment.Id, line.Id, 1m)));
    }

    [Fact]
    public async Task Load_WithTheSameKey_Replays()
    {
        var (w, _, _, s) = await PlannedAsync();
        var consignment = s.Consignments.Single();
        var line = consignment.Lines.Single();

        await w.LoadAsync(s.Id, consignment.Id, line.Id, 2m, key: "l1");
        await w.LoadAsync(s.Id, consignment.Id, line.Id, 4m, key: "l1");   // the replay ignores the new body

        Assert.Equal(20m, (await w.ReloadAsync(s.Id)).Consignments.Single().Lines.Single().LoadedBaseQty);
    }

    [Fact]
    public async Task ExternalProvider_NeedsNoLeg_ButAnAirShipmentNeedsItsReference()
    {
        var w = await ShipmentWorld.CreateAsync();
        var t = await w.ApprovedPenAsync(2m);
        var s = await w.CreateShipmentAsync(mode: TransportMode.Air, via: TransportVia.ExternalProvider, provider: "SkyCargo", reference: "AWB-1");
        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 2m);
        var shipment = await w.ReloadAsync(s.Id);

        await w.LoadAsync(s.Id, shipment.Consignments.Single().Id, shipment.Consignments.Single().Lines.Single().Id, 2m);

        var ready = await w.ReloadAsync(s.Id);
        Assert.Equal((ShipmentStatus.ReadyToDispatch, ConsignmentStatus.Loaded), (ready.Status, ready.Consignments.Single().Status));
    }

    [Fact]
    public async Task Road_ExternalProvider_ReadyOnlyOnceAVehicleNumberOrReferenceExists()
    {
        var w = await ShipmentWorld.CreateAsync();
        var t = await w.ApprovedPenAsync(2m);
        var s = await w.CreateShipmentAsync(via: TransportVia.ExternalProvider, provider: "FastTrans");   // Road, no reference
        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 2m);
        var shipment = await w.ReloadAsync(s.Id);
        await w.LoadAsync(s.Id, shipment.Consignments.Single().Id, shipment.Consignments.Single().Lines.Single().Id, 2m);

        Assert.Equal(ShipmentStatus.Loading, (await w.ReloadAsync(s.Id)).Status);   // consignment loaded, reference rule not yet met

        await w.AddExternalVehicleAsync(s.Id);
        Assert.Equal(ShipmentStatus.ReadyToDispatch, (await w.ReloadAsync(s.Id)).Status);
    }

    [Fact]
    public async Task Allocate_MoreOnALoadedConsignment_ReopensIt()
    {
        var w = await ShipmentWorld.CreateAsync();
        var t = await w.ApprovedPenAsync(5m);
        var other = await w.ApprovedPenAsync(1m, destination: w.L.BlrWarehouse);
        var s = await w.CreateShipmentAsync(via: TransportVia.ExternalProvider, provider: "FastTrans", reference: "TRK-1");
        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 2m);
        await w.AllocateAsync(s.Id, other.Lines.Single().Id, w.T.Box, 1m);
        var shipment = await w.ReloadAsync(s.Id);
        var first = shipment.Consignments.Single(c => c.DestinationLocationId == w.L.BlrStore);
        await w.LoadAsync(s.Id, first.Id, first.Lines.Single().Id, 2m);
        Assert.Equal(ConsignmentStatus.Loaded, (await w.ReloadAsync(s.Id)).Consignments.Single(c => c.Id == first.Id).Status);

        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 1m);

        var reopened = await w.ReloadAsync(s.Id);
        Assert.Equal(ConsignmentStatus.Planned, reopened.Consignments.Single(c => c.Id == first.Id).Status);
        Assert.Equal(ShipmentStatus.Loading, reopened.Status);
        Assert.Equal("quantity", await Field(() => w.ChangeAsync(s.Id, first.Lines.Single().Id, 1m)));   // 20 pc already loaded: unload first
        Assert.Equal("quantity", await Field(() => w.CancelAllocationAsync(s.Id, first.Lines.Single().Id)));
        await w.AssertReconciledAsync("reopen");
    }

    [Fact]
    public async Task AssignVehicle_MustBeALegOfTheShipment()
    {
        var (w, _, _, s) = await PlannedAsync();
        Assert.Equal("shipmentVehicleId", await Field(() => w.AssignAsync(s.Id, s.Consignments.Single().Id, Guid.NewGuid())));
        Assert.Equal("consignmentId", await Field(() => w.AssignAsync(s.Id, Guid.NewGuid(), null)));
    }

    // ---- legacy path (FI-D33) ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyDispatch_IsRefusedWhileAnythingIsAllocated_AndWorksAfterRelease()
    {
        var (w, t, _, s) = await PlannedAsync(boxes: 5m, allocateBoxes: 1m);

        Assert.Equal("status", await Field(() => w.T.DispatchAsRequestedAsync(t.Id)));

        await w.CancelAsync(s.Id);
        var dispatched = await w.T.DispatchAsRequestedAsync(t.Id);
        Assert.Equal(StockTransferStatus.Dispatched, dispatched!.Status);
        await w.AssertReconciledAsync("legacy dispatch");
    }

    [Fact]
    public async Task CancelApprovedRequest_IsRefusedWhileAllocated()
    {
        var (w, t, _, s) = await PlannedAsync();

        await using (var db = w.T.Db())
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new StockTransferService(db, TransferWorld.Everything()).CancelAsync(t.Id, "x", w.UserId));
            Assert.Equal("status", ex.Field);
        }

        await w.CancelAsync(s.Id);
        await using var db2 = w.T.Db();
        var cancelled = await new StockTransferService(db2, TransferWorld.Everything()).CancelAsync(t.Id, "x", w.UserId);
        Assert.Equal(StockTransferStatus.Cancelled, cancelled!.Status);
    }

    // ---- audit -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryWrite_AppendsAnAuditEvent()
    {
        var (w, _, _, s) = await PlannedAsync();
        var consignment = s.Consignments.Single();
        var line = consignment.Lines.Single();
        var withVehicle = await w.AddOwnVehicleAsync(s.Id, await VehicleIdAsync(w, w.L.CentralVan));
        var legId = withVehicle!.Vehicles.Single().Id;
        await w.UpdateAsync(s.Id, TransportMode.Road, TransportVia.OwnFleet);
        await w.AssignAsync(s.Id, consignment.Id, legId);
        await w.LoadAsync(s.Id, consignment.Id, line.Id, 1m);
        await w.ChangeAsync(s.Id, line.Id, 4m);
        await w.AssignAsync(s.Id, consignment.Id, null);
        await w.RemoveVehicleAsync(s.Id, legId);
        await w.CancelAsync(s.Id);

        await using var db = w.T.Db();
        var actions = (await db.AuditEvents.Select(e => e.Action).ToListAsync()).ToHashSet();
        foreach (var expected in new[]
        {
            AuditActions.ShipmentCreated, AuditActions.ShipmentAllocated, AuditActions.ShipmentVehicleAdded, AuditActions.ShipmentUpdated,
            AuditActions.ShipmentVehicleAssigned, AuditActions.ShipmentLoaded, AuditActions.ShipmentAllocationChanged,
            AuditActions.ShipmentVehicleRemoved, AuditActions.ShipmentCancelled,
        })
        {
            Assert.Contains(expected, actions);
        }
    }
}
