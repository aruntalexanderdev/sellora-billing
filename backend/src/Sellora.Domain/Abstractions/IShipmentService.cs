using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Phase 04 (FI-D29..D33) shipment planning, allocation (with stock reservations) and loading. Every operation is its own transaction.
/// Business-rule violations throw <see cref="DomainValidationException"/>; a shipment that does not exist or is hidden by the branch filter
/// yields null (the controller answers 404). Returned shipments carry Vehicles and Consignments (with Lines).
/// Nothing here moves stock: allocation only reserves (StockItem.ReservedQuantity + StockReservations); dispatch consumes in Phase 05.
/// </summary>
public interface IShipmentService
{
    Task<Shipment> CreateAsync(CreateShipmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Draft / Loading only.</summary>
    Task<Shipment?> UpdateAsync(Guid id, UpdateShipmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    Task<Shipment?> AddVehicleAsync(Guid id, ShipmentVehicleInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<Shipment?> RemoveVehicleAsync(Guid id, Guid shipmentVehicleId, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Cancels the shipment: every consignment and line is cancelled, reservations released, request allocations reduced. Idempotent by state.</summary>
    Task<Shipment?> CancelAsync(Guid id, string reason, Guid userId, string? idempotencyKey = null, string? correlationId = null, CancellationToken ct = default);

    Task<Shipment?> CancelConsignmentAsync(Guid id, Guid consignmentId, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default);

    Task<Shipment?> AllocateAsync(Guid id, AllocateShipmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<Shipment?> ChangeAllocationAsync(Guid id, Guid consignmentLineId, ChangeAllocationInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<Shipment?> CancelAllocationAsync(Guid id, Guid consignmentLineId, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Assigns (or clears, with null) the leg that carries a consignment.</summary>
    Task<Shipment?> AssignVehicleAsync(Guid id, Guid consignmentId, Guid? shipmentVehicleId, Guid userId, string? correlationId = null, CancellationToken ct = default);

    Task<Shipment?> LoadAsync(Guid id, Guid consignmentId, LoadConsignmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Reservations.Release: releases an Active reservation and reduces its consignment line and the request allocation to zero. Idempotent by state. Null = not found.</summary>
    Task<StockReservation?> ReleaseReservationAsync(Guid reservationId, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default);
}
