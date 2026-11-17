using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TestData;

/// <summary>
/// Phase 04 InMemory fixture on top of <see cref="TransferWorld"/>: 100 pc of the Pen at the Main store, approved requests Main store -> BLR store,
/// and one helper per shipment operation (each on a fresh context like a separate request). <see cref="AssertReconciledAsync"/> is the FI-D5 invariant:
/// ReservedQuantity = sum of Active reservations, 0 &lt;= reserved &lt;= on hand, request AllocatedBaseQty = sum of Active consignment lines.
/// </summary>
public sealed class ShipmentWorld
{
    public static readonly string[] Codes = [.. TransferWorld.AllCodes, "Shipments.View", "Shipments.Plan", "Shipments.Load", "Reservations.Release"];

    public required TransferWorld T { get; init; }
    public LocationWorld L => T.L;
    public Guid Main => T.Main;
    public Guid Blr => T.Blr;
    public Guid UserId => T.W.UserId;

    public static IEffectivePermissionService Everything() => LocationWorld.Company(Codes);

    public static async Task<ShipmentWorld> CreateAsync(decimal penStockAtMain = 100m)
    {
        var t = await TransferWorld.CreateAsync();
        if (penStockAtMain > 0) await t.StockPenAsync(t.L.MainStore, penStockAtMain);
        return new ShipmentWorld { T = t };
    }

    /// <summary>An Approved request Main store (or <paramref name="source"/>) to BLR store (or <paramref name="destination"/>) for <paramref name="boxes"/> Box of the Pen.</summary>
    public async Task<StockTransfer> ApprovedPenAsync(decimal boxes, Guid? source = null, Guid? destination = null)
    {
        var t = await T.ApprovedAsync(source ?? L.MainStore, destination ?? L.BlrStore, T.Pen(T.Box, boxes));
        return await T.ReloadAsync(t.Id);
    }

    public async Task<Guid> LineIdAsync(Guid transferId) => (await T.ReloadAsync(transferId)).Lines.Single().Id;

    private async Task<TResult> RunAsync<TResult>(Func<ShipmentService, Task<TResult>> op, Guid? branch, IEffectivePermissionService? permissions)
    {
        await using var db = T.Db(branch ?? Main);
        return await op(new ShipmentService(db, permissions ?? Everything()));
    }

    public Task<Shipment> CreateShipmentAsync(
        Guid? source = null, TransportMode mode = TransportMode.Road, TransportVia via = TransportVia.OwnFleet, string? reference = null, string? provider = null,
        Guid? branch = null, IEffectivePermissionService? permissions = null, string? key = null) =>
        RunAsync(s => s.CreateAsync(new CreateShipmentInput(source ?? L.MainStore, mode, via, provider, null, reference, null, "n", key), UserId), branch, permissions);

    public Task<Shipment?> UpdateAsync(Guid id, TransportMode mode, TransportVia via, string? reference = null, string? provider = null, Guid? branch = null) =>
        RunAsync(s => s.UpdateAsync(id, new UpdateShipmentInput(mode, via, provider, null, reference, null, "n2"), UserId), branch, null);

    public Task<Shipment?> AddOwnVehicleAsync(Guid id, Guid vehicleId, Guid? branch = null) =>
        RunAsync(s => s.AddVehicleAsync(id, new ShipmentVehicleInput(vehicleId, null, null, "Driver", "999"), UserId), branch, null);

    public Task<Shipment?> AddExternalVehicleAsync(Guid id, string number = "KA01AB1234", Guid? branch = null) =>
        RunAsync(s => s.AddVehicleAsync(id, new ShipmentVehicleInput(null, number, "Truck", "Ext driver", null), UserId), branch, null);

    public Task<Shipment?> RemoveVehicleAsync(Guid id, Guid shipmentVehicleId) => RunAsync(s => s.RemoveVehicleAsync(id, shipmentVehicleId, UserId), null, null);

    public Task<Shipment?> AllocateAsync(Guid shipmentId, Guid requestLineId, Guid productUnit, decimal qty, string? key = null, Guid? branch = null, IEffectivePermissionService? permissions = null) =>
        RunAsync(s => s.AllocateAsync(shipmentId, new AllocateShipmentInput(requestLineId, productUnit, qty, key), UserId), branch, permissions);

    public Task<Shipment?> ChangeAsync(Guid shipmentId, Guid consignmentLineId, decimal qty) =>
        RunAsync(s => s.ChangeAllocationAsync(shipmentId, consignmentLineId, new ChangeAllocationInput(qty), UserId), null, null);

    public Task<Shipment?> CancelAllocationAsync(Guid shipmentId, Guid consignmentLineId) =>
        RunAsync(s => s.CancelAllocationAsync(shipmentId, consignmentLineId, UserId), null, null);

    public Task<Shipment?> AssignAsync(Guid shipmentId, Guid consignmentId, Guid? shipmentVehicleId) =>
        RunAsync(s => s.AssignVehicleAsync(shipmentId, consignmentId, shipmentVehicleId, UserId), null, null);

    public Task<Shipment?> LoadAsync(Guid shipmentId, Guid consignmentId, Guid consignmentLineId, decimal loadedQty, string? key = null, Guid? branch = null, IEffectivePermissionService? permissions = null) =>
        RunAsync(s => s.LoadAsync(shipmentId, consignmentId, new LoadConsignmentInput([new LoadConsignmentLineInput(consignmentLineId, loadedQty)], key), UserId), branch, permissions);

    public Task<Shipment?> CancelAsync(Guid shipmentId, string? key = null) => RunAsync(s => s.CancelAsync(shipmentId, "changed plan", UserId, key), null, null);

    public Task<Shipment?> CancelConsignmentAsync(Guid shipmentId, Guid consignmentId) => RunAsync(s => s.CancelConsignmentAsync(shipmentId, consignmentId, "no longer needed", UserId), null, null);

    public Task<Shipment?> DispatchAsync(Guid shipmentId, string? key = null, Guid? branch = null, IEffectivePermissionService? permissions = null) =>
        RunAsync(s => s.DispatchAsync(shipmentId, new DispatchShipmentInput(key), UserId), branch, permissions);

    /// <summary>A ReadyToDispatch shipment: an approved request of <paramref name="requestedBoxes"/> (default: <paramref name="boxes"/>) Box, <paramref name="boxes"/> of it allocated and loaded in full, own-fleet leg on the central van.</summary>
    public async Task<(Shipment S, StockTransfer T)> ReadyAsync(Guid? source = null, Guid? destination = null, decimal boxes = 5m, decimal? requestedBoxes = null)
    {
        var t = await ApprovedPenAsync(requestedBoxes ?? boxes, source, destination);
        var s = await CreateShipmentAsync(source);
        await AllocateAsync(s.Id, t.Lines.Single().Id, T.Box, boxes);
        Guid vehicleId;
        await using (var db = T.Db()) vehicleId = await db.Vehicles.Where(v => v.StockLocationId == L.CentralVan).Select(v => v.Id).SingleAsync();
        var leg = (await AddOwnVehicleAsync(s.Id, vehicleId))!.Vehicles.Single().Id;
        var planned = await ReloadAsync(s.Id);
        var consignment = planned.Consignments.Single();
        await AssignAsync(s.Id, consignment.Id, leg);
        await LoadAsync(s.Id, consignment.Id, consignment.Lines.Single().Id, boxes);
        var ready = await ReloadAsync(s.Id);
        Assert.Equal(ShipmentStatus.ReadyToDispatch, ready.Status);
        return (ready, await T.ReloadAsync(t.Id));
    }

    // ---- receiving (Phase 05): the destination's calls, on the BLR branch by default ----------------------------------------------------

    private async Task<TResult> ReceiveRunAsync<TResult>(Func<ConsignmentReceivingService, Task<TResult>> op, Guid? branch, IEffectivePermissionService? permissions)
    {
        await using var db = T.Db(branch ?? Blr);
        return await op(new ConsignmentReceivingService(db, permissions ?? Everything()));
    }

    public Task<ConsignmentReceipt?> RecordAsync(Guid consignmentId, Guid consignmentLineId, decimal accepted, decimal damaged = 0m, decimal rejected = 0m, decimal missing = 0m,
        string? key = null, Guid? branch = null, IEffectivePermissionService? permissions = null) =>
        ReceiveRunAsync(r => r.RecordAsync(consignmentId, new RecordReceiptInput([new ReceiptLineInput(consignmentLineId, accepted, damaged, rejected, missing)], "n", key), UserId), branch, permissions);

    public Task<ConsignmentReceipt?> PostAsync(Guid consignmentId, Guid receiptId, string? key = null, Guid? branch = null, IEffectivePermissionService? permissions = null) =>
        ReceiveRunAsync(r => r.PostAsync(consignmentId, receiptId, new PostReceiptInput(key), UserId), branch, permissions);

    public async Task<List<ConsignmentReceipt>> ReceiptsAsync(Guid consignmentId)
    {
        await using var db = T.Db();
        return await db.ConsignmentReceipts.AsNoTracking().Include(r => r.Lines).Where(r => r.ConsignmentId == consignmentId).OrderBy(r => r.Sequence).ToListAsync();
    }

    public async Task<ConsignmentLine> LineAsync(Guid consignmentLineId)
    {
        await using var db = T.Db();
        return await db.ConsignmentLines.AsNoTracking().SingleAsync(l => l.Id == consignmentLineId);
    }

    public async Task<List<StockTransferLineBatch>> BatchRowsAsync()
    {
        await using var db = T.Db();
        return await db.StockTransferLineBatches.AsNoTracking().ToListAsync();
    }

    public async Task<List<StockMovement>> MovementsAsync(Guid transferId)
    {
        await using var db = T.Db();
        return await db.StockMovements.AsNoTracking().Where(m => m.ReferenceType == "StockTransfer" && m.ReferenceId == transferId).OrderBy(m => m.OccurredAt).ToListAsync();
    }

    public Task<StockReservation?> ReleaseAsync(Guid reservationId, Guid? branch = null) =>
        RunAsync(s => s.ReleaseReservationAsync(reservationId, "stale plan", UserId), branch, null);

    public async Task<Shipment> ReloadAsync(Guid id)
    {
        await using var db = T.Db();
        return await db.Shipments.AsNoTracking().Include(s => s.Vehicles).Include(s => s.Consignments).ThenInclude(c => c.Lines).AsSplitQuery().SingleAsync(s => s.Id == id);
    }

    public async Task<List<StockReservation>> ReservationsAsync()
    {
        await using var db = T.Db();
        return await db.StockReservations.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    public async Task<decimal> ReservedAsync(Guid location, Guid? productId = null)
    {
        await using var db = T.Db();
        var id = productId ?? T.PenId;
        return await db.StockItems.AsNoTracking().Where(i => i.StockLocationId == location && i.ProductId == id).Select(i => i.ReservedQuantity).SingleOrDefaultAsync();
    }

    public async Task<decimal> RequestAllocatedAsync(Guid transferId) => (await T.ReloadAsync(transferId)).Lines.Single().AllocatedBaseQty;

    public static async Task<string?> FieldAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        return ex.Field;
    }

    /// <summary>FI-D5 reconciliation after every operation.</summary>
    public async Task AssertReconciledAsync(string step)
    {
        await using var db = T.Db();
        var items = await db.StockItems.AsNoTracking().ToListAsync();
        var reservations = await db.StockReservations.AsNoTracking().ToListAsync();
        var lines = await db.ConsignmentLines.AsNoTracking().ToListAsync();
        var requestLines = await db.StockTransferLines.AsNoTracking().ToListAsync();

        foreach (var i in items)
        {
            var active = reservations.Where(r => r.Status == StockReservationStatus.Active && r.SourceLocationId == i.StockLocationId && r.ProductId == i.ProductId).Sum(r => r.BaseQty);
            Assert.True(i.ReservedQuantity == active, $"{step}: ReservedQuantity {i.ReservedQuantity} <> active reservations {active} at {i.StockLocationId}");
            Assert.True(i.ReservedQuantity >= 0 && i.ReservedQuantity <= i.QuantityOnHand, $"{step}: reserved {i.ReservedQuantity} outside 0..{i.QuantityOnHand}");
        }
        foreach (var l in lines)
        {
            // A dispatched line's reservation is Consumed (its BaseQty = the dispatched quantity); only Active reservations count towards ReservedQuantity.
            var active = reservations.Where(r => r.ConsignmentLineId == l.Id && r.Status is StockReservationStatus.Active or StockReservationStatus.Consumed).Sum(r => r.BaseQty);
            Assert.True(l.Status == ConsignmentLineStatus.Active ? l.AllocatedBaseQty == active : active == 0 && l.AllocatedBaseQty == 0 && l.LoadedBaseQty == 0, $"{step}: line {l.Id} vs reservation");
            Assert.True(l.LoadedBaseQty <= l.AllocatedBaseQty, $"{step}: loaded above allocated");
            Assert.True(l.EnteredQty * l.ConversionFactorToBase == l.AllocatedBaseQty, $"{step}: line arithmetic");
        }
        foreach (var rl in requestLines)
        {
            var allocated = lines.Where(l => l.StockTransferLineId == rl.Id && l.Status == ConsignmentLineStatus.Active).Sum(l => l.AllocatedBaseQty);
            Assert.True(rl.AllocatedBaseQty == allocated, $"{step}: request line {rl.Id} allocated {rl.AllocatedBaseQty} <> lines {allocated}");
        }
        Assert.Equal(reservations.Count(r => r.Status == StockReservationStatus.Active), reservations.Where(r => r.Status == StockReservationStatus.Active).Select(r => r.ConsignmentLineId).Distinct().Count());
    }
}
