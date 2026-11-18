using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Api.Contracts;

// ---- Requests (phase-9 design section 6) ----

/// <summary><see cref="Quantity"/> is entered in <see cref="ProductUnitId"/> (any active unit of the product).</summary>
public record StockTransferLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? ProductUnitId,
    [Range(0.001, 999999999)] decimal Quantity);

/// <summary>Creates a Draft transfer, or a Requested one when <see cref="Submit"/>. Header key: Idempotency-Key.</summary>
public record CreateStockTransferRequest(
    [Required] Guid? SourceLocationId,
    [Required] Guid? DestinationLocationId,
    [MaxLength(500)] string? Notes,
    bool Submit,
    [Required, MinLength(1)] List<StockTransferLineRequest> Lines);

/// <summary>Replaces a Draft transfer's endpoints, notes and lines.</summary>
public record UpdateStockTransferRequest(
    [Required] Guid? SourceLocationId,
    [Required] Guid? DestinationLocationId,
    [MaxLength(500)] string? Notes,
    [Required, MinLength(1)] List<StockTransferLineRequest> Lines);

/// <summary>Reject / cancel.</summary>
public record StockTransferReasonRequest([Required, MaxLength(300)] string Reason);

/// <summary>Close the remainder of an approved request (FI-D26).</summary>
public record CloseStockTransferRequest([Required, MaxLength(500)] string Reason);

/// <summary>0 &lt;= <see cref="Quantity"/> &lt;= requested; every line of the transfer must appear once.</summary>
public record DispatchStockTransferLineRequest(
    [Required] Guid? LineId,
    [Required] Guid? ProductUnitId,
    [Range(0, 999999999)] decimal Quantity);

public record DispatchStockTransferRequest([Required, MinLength(1)] List<DispatchStockTransferLineRequest> Lines);

public record ReceiveStockTransferLineRequest(
    [Required] Guid? LineId,
    [Required] Guid? ProductUnitId,
    [Range(0, 999999999)] decimal AcceptedQuantity,
    [Range(0, 999999999)] decimal DamagedQuantity);

public record ReceiveStockTransferRequest(
    [MaxLength(500)] string? Notes,
    [Required, MinLength(1)] List<ReceiveStockTransferLineRequest> Lines);

public record ResolveStockTransferLineRequest([Required] Guid? LineId, StockTransferResolution Action);

public record ResolveStockTransferRequest(
    [Required, MaxLength(300)] string Reason,
    [Required, MinLength(1)] List<ResolveStockTransferLineRequest> Lines);

// ---- Responses ----

/// <summary>One endpoint of a transfer (from / to / transit): location id, code, name and type (type is the StockLocationType name).</summary>
public record StockTransferLocationDto(Guid Id, string? Code, string? Name, string? Type);

/// <summary>A branch reference with its label (null branch = company-level endpoint).</summary>
public record StockTransferBranchDto(Guid? Id, string? Code, string? Name);

/// <summary>List row. <see cref="OutstandingLineCount"/> = lines with goods still in transit (quantities are never summed across products).</summary>
public record StockTransferListItemDto(
    Guid Id, string TransferNo, StockTransferStatus Status, DateTimeOffset CreatedAt,
    StockTransferBranchDto Branch,
    StockTransferLocationDto From, StockTransferLocationDto To,
    StockTransferBranchDto SourceBranch, StockTransferBranchDto DestinationBranch,
    int LineCount, int OutstandingLineCount,
    DateTimeOffset? DispatchedAt, DateTimeOffset? CompletedAt);

/// <summary>A user reference; the name is null when the user cannot be resolved.</summary>
public record StockTransferActorDto(Guid? UserId, string? UserName, DateTimeOffset? At);

/// <summary>Where one line's dispatched quantity came from: the per-transfer batch identity with its cost and settled counters (base units).</summary>
public record StockTransferLineBatchDto(
    Guid PurchaseBatchId, string InternalBatchNo, decimal? UnitCost,
    decimal DispatchedBaseQty, decimal ReceivedBaseQty, decimal DamagedBaseQty, decimal WrittenOffBaseQty, decimal ReturnedBaseQty, decimal AcceptedCorrectionBaseQty,
    decimal OutstandingBaseQty);

/// <summary>Request snapshot, dispatch snapshot (null until dispatch) and settlement counters of one line. Quantities per line are in the
/// line's product base unit. <see cref="RemainingBaseQty"/> = Requested - Closed - Dispatched (FI-D27).</summary>
public record StockTransferLineDto(
    Guid Id, int LineNo, Guid ProductId, string? ProductName, string? Sku, string? BaseUnitCode, short? BaseDecimalPlaces,
    Guid ProductUnitId, string UnitCode, decimal ConversionFactorToBase, decimal RequestedQty, decimal RequestedBaseQty,
    Guid? DispatchProductUnitId, string? DispatchUnitCode, decimal? DispatchFactor, decimal? DispatchedQty, decimal DispatchedBaseQty,
    decimal ReceivedBaseQty, decimal DamagedBaseQty, decimal WrittenOffBaseQty, decimal ReturnedBaseQty, decimal AcceptedCorrectionBaseQty,
    decimal OutstandingBaseQty,
    decimal AllocatedBaseQty, decimal ClosedBaseQty, decimal RemainingBaseQty,
    IReadOnlyList<StockTransferLineBatchDto> Batches);

public record StockTransferReceiptLineDto(
    Guid TransferLineId, Guid ProductId, Guid ProductUnitId, string UnitCode, decimal ConversionFactorToBase,
    decimal AcceptedQty, decimal AcceptedBaseQty, decimal DamagedQty, decimal DamagedBaseQty,
    decimal WrittenOffBaseQty, decimal ReturnedBaseQty);

/// <summary>A receipt (<see cref="Kind"/> Receipt) or resolution (Resolution) event; <see cref="Notes"/> is the receiver's notes or the resolution reason.</summary>
public record StockTransferReceiptDto(
    Guid Id, int Sequence, StockTransferReceiptKind Kind, DateTimeOffset At, Guid ByUserId, string? ByUserName, string? Notes,
    IReadOnlyList<StockTransferReceiptLineDto> Lines);

/// <summary>Transfer detail: returned by GET {id} and by every write endpoint (including an idempotent replay).</summary>
public record StockTransferDto(
    Guid Id, string TransferNo, StockTransferStatus Status, string? Notes,
    StockTransferBranchDto Branch,
    StockTransferLocationDto From, StockTransferLocationDto To, StockTransferLocationDto Transit,
    StockTransferBranchDto SourceBranch, StockTransferBranchDto DestinationBranch,
    StockTransferActorDto Created, StockTransferActorDto? Submitted, StockTransferActorDto? Approved,
    StockTransferActorDto? Rejected, string? RejectionReason,
    StockTransferActorDto? Dispatched, StockTransferActorDto? Completed,
    StockTransferActorDto? Cancelled, string? CancellationReason,
    DateTimeOffset? ClosedAt, string? ClosedByName, string? ClosureReason,
    IReadOnlyList<StockTransferLineDto> Lines,
    IReadOnlyList<StockTransferReceiptDto> Receipts);

/// <summary>Ready-for-shipment queue row (FI-D28): an Approved / PartiallyFulfilled request with quantity still to ship.</summary>
public record ReadyForShipmentItemDto(
    Guid Id, string TransferNo, StockTransferStatus Status,
    Guid SourceLocationId, string? SourceLocationName, Guid DestinationLocationId, string? DestinationLocationName,
    string? DestinationBranchName, DateTimeOffset? ApprovedAt, int LineCount, int RemainingLineCount);

/// <summary>One status transition of a transfer, read from the audit trail (Action is the audit action code).</summary>
public record StockTransferHistoryEntryDto(
    DateTimeOffset At, string Action, StockTransferStatus? FromStatus, StockTransferStatus? ToStatus, string? ActorName, string? Reason);

/// <summary>Response of POST receipts / resolve: the id of the receipt it created, and the transfer after posting.</summary>
public record StockTransferReceiptResultDto(Guid ReceiptId, StockTransferDto Transfer);
