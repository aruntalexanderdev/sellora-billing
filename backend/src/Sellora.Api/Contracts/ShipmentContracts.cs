using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

// ---- Requests (Phase 04, FI-D29..D33) ----

/// <summary>Creates a Draft shipment out of one source location. TransportReference is mandatory for every mode except Road (vehicles carry it). Header key: Idempotency-Key.</summary>
public record CreateShipmentRequest(
    [Required] Guid? SourceLocationId,
    [Required] TransportMode? TransportMode,
    [Required] TransportVia? TransportVia,
    [MaxLength(120)] string? ProviderName,
    [MaxLength(120)] string? ProviderContact,
    [MaxLength(100)] string? TransportReference,
    DateOnly? PlannedDispatchDate,
    [MaxLength(500)] string? Notes);

/// <summary>Replaces the header (Draft / Loading only); the source location never changes.</summary>
public record UpdateShipmentRequest(
    [Required] TransportMode? TransportMode,
    [Required] TransportVia? TransportVia,
    [MaxLength(120)] string? ProviderName,
    [MaxLength(120)] string? ProviderContact,
    [MaxLength(100)] string? TransportReference,
    DateOnly? PlannedDispatchDate,
    [MaxLength(500)] string? Notes);

/// <summary>Own fleet: VehicleId. External provider: ExternalVehicleNumber (+ description). Driver optional in both.</summary>
public record AddShipmentVehicleRequest(
    Guid? VehicleId,
    [MaxLength(40)] string? ExternalVehicleNumber,
    [MaxLength(120)] string? ExternalDescription,
    [MaxLength(120)] string? DriverName,
    [MaxLength(30)] string? DriverPhone);

public record ShipmentReasonRequest([Required, MaxLength(300)] string Reason);

/// <summary>Quantity to ADD to the allocation of the request line, entered in ProductUnitId (the allocation keeps that unit). Header key: Idempotency-Key.</summary>
public record AllocateShipmentRequest(
    [Required] Guid? StockTransferLineId,
    [Required] Guid? ProductUnitId,
    [Range(0.001, 999999999)] decimal Quantity);

/// <summary>New TOTAL quantity in the allocation's unit; 0 cancels the line.</summary>
public record ChangeAllocationRequest([Range(0, 999999999)] decimal Quantity);

/// <summary>Null clears the leg.</summary>
public record AssignConsignmentVehicleRequest(Guid? ShipmentVehicleId);

/// <summary>Absolute loaded quantity of the line in its own unit (lower than before = unload).</summary>
public record LoadConsignmentLineRequest([Required] Guid? ConsignmentLineId, [Range(0, 999999999)] decimal LoadedQuantity);

public record LoadConsignmentRequest([Required, MinLength(1)] List<LoadConsignmentLineRequest> Lines);

public record ReleaseStockReservationRequest([Required, MaxLength(300)] string Reason);

// ---- Responses ----

public record ShipmentListItemDto(
    Guid Id, string ShipmentNo, ShipmentStatus Status, TransportMode TransportMode, TransportVia TransportVia,
    string? ProviderName, string? TransportReference, DateOnly? PlannedDispatchDate,
    StockTransferBranchDto Branch, StockTransferLocationDto Source,
    int ConsignmentCount, int LineCount, int VehicleCount, DateTimeOffset CreatedAt);

public record ShipmentVehicleDto(
    Guid Id, int Sequence, Guid? VehicleId, string? VehicleNumber, string? VehicleName,
    string? ExternalVehicleNumber, string? ExternalDescription, string? DriverName, string? DriverPhone);

/// <summary>What the request line looked like when the response was built: allocation against request. Remaining = Requested - Closed - Allocated.</summary>
public record ConsignmentLineRequestDto(
    decimal RequestedBaseQty, decimal ClosedBaseQty, decimal DispatchedBaseQty, decimal AllocatedBaseQty, decimal RemainingUnallocatedBaseQty);

public record ConsignmentLineDto(
    Guid Id, Guid StockTransferId, string? TransferNo, Guid StockTransferLineId,
    Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode,
    Guid ProductUnitId, string UnitCode, decimal ConversionFactorToBase,
    decimal EnteredQty, decimal AllocatedBaseQty, decimal LoadedBaseQty, decimal LoadedQty,
    ConsignmentLineStatus Status, ConsignmentLineRequestDto Request, Guid? ReservationId, decimal ReservedBaseQty);

public record ConsignmentDto(
    Guid Id, string ConsignmentNo, ConsignmentStatus Status,
    StockTransferLocationDto Destination, StockTransferBranchDto DestinationBranch, Guid? ShipmentVehicleId,
    DateTimeOffset? LoadedAt, StockTransferActorDto? Cancelled, string? CancellationReason,
    IReadOnlyList<ConsignmentLineDto> Lines);

public record ShipmentDto(
    Guid Id, string ShipmentNo, ShipmentStatus Status, TransportMode TransportMode, TransportVia TransportVia,
    string? ProviderName, string? ProviderContact, string? TransportReference, bool TransportReferenceValid,
    DateOnly? PlannedDispatchDate, string? Notes,
    StockTransferBranchDto Branch, StockTransferLocationDto Source,
    IReadOnlyList<ShipmentVehicleDto> Vehicles, IReadOnlyList<ConsignmentDto> Consignments,
    StockTransferActorDto Created, StockTransferActorDto? Updated, StockTransferActorDto? Cancelled, string? CancellationReason);

/// <summary>The leg of a consignment: the one vehicle that carries it (never the other legs).</summary>
public record ConsignmentLegDto(string? VehicleNumber, string? VehicleName, string? DriverName, string? DriverPhone);

public record ConsignmentListItemDto(
    Guid Id, string ConsignmentNo, ConsignmentStatus Status, string Direction,
    Guid? ShipmentId, string ShipmentNo, ShipmentStatus ShipmentStatus, TransportMode TransportMode, DateOnly? PlannedDispatchDate,
    StockTransferLocationDto Source, StockTransferLocationDto Destination, StockTransferBranchDto DestinationBranch,
    int LineCount, DateTimeOffset? LoadedAt);

public record ConsignmentDetailLineDto(
    Guid Id, string? TransferNo, Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode,
    Guid ProductUnitId, string UnitCode, decimal EnteredQty, decimal AllocatedBaseQty, decimal LoadedBaseQty, decimal LoadedQty);

/// <summary>One consignment as its reader sees it. Incoming readers never get ShipmentId, other destinations, vehicles other than this leg, or costs.</summary>
public record ConsignmentDetailDto(
    Guid Id, string ConsignmentNo, ConsignmentStatus Status, string Direction,
    Guid? ShipmentId, string ShipmentNo, ShipmentStatus ShipmentStatus,
    TransportMode TransportMode, TransportVia TransportVia, string? ProviderName, string? TransportReference, DateOnly? PlannedDispatchDate,
    StockTransferLocationDto Source, StockTransferLocationDto Destination, StockTransferBranchDto DestinationBranch,
    ConsignmentLegDto? Leg, DateTimeOffset? LoadedAt, IReadOnlyList<ConsignmentDetailLineDto> Lines);

public record StockReservationDto(
    Guid Id, StockReservationStatus Status, StockTransferLocationDto Source,
    Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode, decimal BaseQty,
    Guid ShipmentId, string ShipmentNo, Guid ConsignmentId, string ConsignmentNo, Guid ConsignmentLineId,
    StockTransferLocationDto Destination, string? TransferNo,
    DateTimeOffset CreatedAt, string? CreatedBy, int AgeDays,
    DateTimeOffset? ReleasedAt, string? ReleasedBy, string? ReleaseReason, DateTimeOffset? ConsumedAt);
