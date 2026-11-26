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

/// <summary>Quantities of one consignment line in the LINE's own unit (ConsignmentLineDto.UnitCode). Lines with all four at 0 are ignored.</summary>
public record ReceiptLineRequest(
    [Required] Guid? ConsignmentLineId,
    [Range(0, 999999999)] decimal AcceptedQuantity,
    [Range(0, 999999999)] decimal DamagedQuantity,
    [Range(0, 999999999)] decimal RejectedQuantity,
    [Range(0, 999999999)] decimal MissingQuantity);

/// <summary>Verifies (records) a receipt; moves no stock. Header key: Idempotency-Key.</summary>
public record RecordReceiptRequest([Required, MinLength(1)] List<ReceiptLineRequest> Lines, [MaxLength(500)] string? Notes);

/// <summary>Phase 06 (FI-D39): rejects an unposted receipt for correction. Header key: Idempotency-Key.</summary>
public record RejectReceiptRequest([Required, MaxLength(500)] string Reason);

/// <summary>Phase 06 (FI-D40): Quantity is in the consignment line's own unit (ConsignmentLineDto.UnitCode).</summary>
public record DispositionLineRequest(
    [Required] Guid? ConsignmentLineId,
    [Required] DispositionBucket? Bucket,
    [Required] DispositionAction? Action,
    [Range(0.000001, 999999999)] decimal Quantity);

/// <summary>Resolves damaged / rejected / missing quantities of POSTED receipts. Header key: Idempotency-Key.</summary>
public record RecordDispositionRequest([Required, MaxLength(500)] string Reason, [Required, MinLength(1)] List<DispositionLineRequest> Lines);

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
    ConsignmentLineStatus Status, ConsignmentLineRequestDto Request, Guid? ReservationId, decimal ReservedBaseQty,
    decimal DispatchedBaseQty, decimal AcceptedBaseQty, decimal DamagedBaseQty, decimal RejectedBaseQty, decimal MissingBaseQty, decimal PostedBaseQty,
    decimal DispositionedDamagedBaseQty = 0, decimal DispositionedRejectedBaseQty = 0, decimal DispositionedMissingBaseQty = 0, decimal OpenDiscrepancyBaseQty = 0);

public record ConsignmentDto(
    Guid Id, string ConsignmentNo, ConsignmentStatus Status,
    StockTransferLocationDto Destination, StockTransferBranchDto DestinationBranch, Guid? ShipmentVehicleId,
    DateTimeOffset? LoadedAt, StockTransferActorDto? Cancelled, string? CancellationReason,
    IReadOnlyList<ConsignmentLineDto> Lines, DateTimeOffset? DispatchedAt);

public record ShipmentDto(
    Guid Id, string ShipmentNo, ShipmentStatus Status, TransportMode TransportMode, TransportVia TransportVia,
    string? ProviderName, string? ProviderContact, string? TransportReference, bool TransportReferenceValid,
    DateOnly? PlannedDispatchDate, string? Notes,
    StockTransferBranchDto Branch, StockTransferLocationDto Source,
    IReadOnlyList<ShipmentVehicleDto> Vehicles, IReadOnlyList<ConsignmentDto> Consignments,
    StockTransferActorDto Created, StockTransferActorDto? Updated, StockTransferActorDto? Cancelled, string? CancellationReason,
    StockTransferActorDto? Dispatched);

/// <summary>The leg of a consignment: the one vehicle that carries it (never the other legs).</summary>
public record ConsignmentLegDto(string? VehicleNumber, string? VehicleName, string? DriverName, string? DriverPhone);

public record ConsignmentListItemDto(
    Guid Id, string ConsignmentNo, ConsignmentStatus Status, string Direction,
    Guid? ShipmentId, string ShipmentNo, ShipmentStatus ShipmentStatus, TransportMode TransportMode, DateOnly? PlannedDispatchDate,
    StockTransferLocationDto Source, StockTransferLocationDto Destination, StockTransferBranchDto DestinationBranch,
    int LineCount, DateTimeOffset? LoadedAt, DateTimeOffset? DispatchedAt);

public record ConsignmentDetailLineDto(
    Guid Id, string? TransferNo, Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode,
    Guid ProductUnitId, string UnitCode, decimal EnteredQty, decimal AllocatedBaseQty, decimal LoadedBaseQty, decimal LoadedQty,
    decimal DispatchedBaseQty, decimal AcceptedBaseQty, decimal DamagedBaseQty, decimal RejectedBaseQty, decimal MissingBaseQty, decimal PostedBaseQty,
    decimal ConversionFactorToBase,
    decimal DispositionedDamagedBaseQty = 0, decimal DispositionedRejectedBaseQty = 0, decimal DispositionedMissingBaseQty = 0, decimal OpenDiscrepancyBaseQty = 0);

/// <summary>One consignment as its reader sees it. Incoming readers never get ShipmentId, other destinations, vehicles other than this leg, or costs.</summary>
public record ConsignmentDetailDto(
    Guid Id, string ConsignmentNo, ConsignmentStatus Status, string Direction,
    Guid? ShipmentId, string ShipmentNo, ShipmentStatus ShipmentStatus,
    TransportMode TransportMode, TransportVia TransportVia, string? ProviderName, string? TransportReference, DateOnly? PlannedDispatchDate,
    StockTransferLocationDto Source, StockTransferLocationDto Destination, StockTransferBranchDto DestinationBranch,
    ConsignmentLegDto? Leg, DateTimeOffset? LoadedAt, IReadOnlyList<ConsignmentDetailLineDto> Lines, DateTimeOffset? DispatchedAt, int ReceiptCount,
    int DispositionCount = 0, bool HasOpenDiscrepancy = false);

public record StockReservationDto(
    Guid Id, StockReservationStatus Status, StockTransferLocationDto Source,
    Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode, decimal BaseQty,
    Guid ShipmentId, string ShipmentNo, Guid ConsignmentId, string ConsignmentNo, Guid ConsignmentLineId,
    StockTransferLocationDto Destination, string? TransferNo,
    DateTimeOffset CreatedAt, string? CreatedBy, int AgeDays,
    DateTimeOffset? ReleasedAt, string? ReleasedBy, string? ReleaseReason, DateTimeOffset? ConsumedAt);

// ---- Phase 05: receipts and transit views (FI-D35..D37) ----

public record ConsignmentReceiptLineDto(
    Guid ConsignmentLineId, Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode,
    Guid ProductUnitId, string UnitCode, decimal ConversionFactorToBase,
    decimal AcceptedQty, decimal AcceptedBaseQty, decimal DamagedQty, decimal DamagedBaseQty,
    decimal RejectedQty, decimal RejectedBaseQty, decimal MissingQty, decimal MissingBaseQty);

/// <summary>One receipt event. Verified = recorded, no stock moved; Posted = the accepted quantity was moved Transit to the destination. ConsignmentStatus / ShipmentStatus are the statuses right now.</summary>
public record ConsignmentReceiptDto(
    Guid Id, Guid ConsignmentId, string ConsignmentNo, ConsignmentStatus ConsignmentStatus, ShipmentStatus ShipmentStatus,
    int Sequence, ConsignmentReceiptStatus Status, StockTransferActorDto Verified, StockTransferActorDto? Posted, string? Notes,
    IReadOnlyList<ConsignmentReceiptLineDto> Lines,
    ReceiptPostingMode PolicyMode = ReceiptPostingMode.Direct, bool RequireSeparation = false,
    StockTransferActorDto? Rejected = null, string? RejectionReason = null, StockTransferActorDto? ApprovedBy = null);

/// <summary>Per product and consignment line (never summed across products). OutstandingBaseQty = dispatched - accounted (nothing verified yet); InTransitBaseQty = dispatched - posted - dispositioned (still physically in Transit). OpenDiscrepancyBaseQty = damaged + rejected + missing - dispositioned (FI-D41).</summary>
public record TransitLineDto(
    Guid ConsignmentLineId, Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode, string UnitCode,
    decimal DispatchedBaseQty, decimal AcceptedBaseQty, decimal DamagedBaseQty, decimal RejectedBaseQty, decimal MissingBaseQty, decimal PostedBaseQty,
    decimal OutstandingBaseQty, decimal InTransitBaseQty,
    decimal DispositionedDamagedBaseQty = 0, decimal DispositionedRejectedBaseQty = 0, decimal DispositionedMissingBaseQty = 0, decimal OpenDiscrepancyBaseQty = 0);

/// <summary>A consignment in (or through) transit. Direction = "outgoing" when the caller reads the shipment's source (ShipmentId present), otherwise "incoming" (no ShipmentId, no other consignments).</summary>
public record TransitItemDto(
    Guid ConsignmentId, string ConsignmentNo, ConsignmentStatus Status, string Direction,
    Guid? ShipmentId, string ShipmentNo, ShipmentStatus ShipmentStatus, TransportMode TransportMode, string? TransportReference,
    StockTransferLocationDto Source, StockTransferLocationDto Destination, StockTransferBranchDto DestinationBranch,
    DateTimeOffset? DispatchedAt, int ReceiptCount, int UnpostedReceiptCount, bool HasDiscrepancy, IReadOnlyList<TransitLineDto> Lines);

// ---- Phase 06: dispositions (FI-D40) ----

public record ConsignmentDispositionLineDto(
    Guid ConsignmentLineId, Guid ProductId, string? ProductSku, string? ProductName, string? BaseUnitCode,
    Guid ProductUnitId, string UnitCode, decimal ConversionFactorToBase,
    DispositionBucket Bucket, DispositionAction Action, decimal Quantity, decimal BaseQty);

/// <summary>One append-only disposition event. ConsignmentStatus / ShipmentStatus are the statuses right now.</summary>
public record ConsignmentDispositionDto(
    Guid Id, Guid ConsignmentId, string ConsignmentNo, ConsignmentStatus ConsignmentStatus, ShipmentStatus ShipmentStatus,
    int Sequence, string Reason, StockTransferActorDto By, IReadOnlyList<ConsignmentDispositionLineDto> Lines);

/// <summary>Own-fleet vehicle a shipment planner may add (GET /shipments/{id}/eligible-vehicles); AssignedBranchId null = central vehicle.</summary>
public record ShipmentEligibleVehicleDto(Guid Id, string VehicleNumber, string VehicleName, string VehicleType, Guid? AssignedBranchId, bool IsActive);
