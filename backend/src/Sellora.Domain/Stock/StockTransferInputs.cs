namespace Sellora.Domain.Stock;

/// <summary>A requested transfer line: <paramref name="Quantity"/> is in the product unit <paramref name="ProductUnitId"/>
/// (any active unit of the product). The service converts to base units once and snapshots the unit.</summary>
public sealed record StockTransferLineInput(Guid ProductId, Guid ProductUnitId, decimal Quantity);

/// <summary>Creates a transfer (Draft, or Requested when <paramref name="Submit"/>). The branch is the working branch (X-Branch-Id).</summary>
public sealed record CreateStockTransferInput(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    string? Notes,
    bool Submit,
    IReadOnlyList<StockTransferLineInput> Lines,
    string? IdempotencyKey = null);

/// <summary>Replaces a Draft transfer's endpoints, notes and lines.</summary>
public sealed record UpdateStockTransferInput(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    string? Notes,
    IReadOnlyList<StockTransferLineInput> Lines);

/// <summary>What leaves for one existing line: 0 &lt;= <paramref name="Quantity"/> &lt;= requested, in any active unit of the product.
/// Every line of the transfer must appear exactly once; the unshipped remainder is closed (no backorder).</summary>
/// <summary>Approved quantity for one request line, in the line's requested unit: 0 ≤ Quantity ≤ requested. Lines left out are approved as requested.</summary>
public sealed record ApproveStockTransferLineInput(Guid LineId, decimal Quantity);

/// <summary>Approve, optionally approving less than requested on some lines; Reason is required when any line is reduced.</summary>
public sealed record ApproveStockTransferInput(IReadOnlyList<ApproveStockTransferLineInput>? Lines = null, string? Reason = null);

public sealed record DispatchStockTransferLineInput(Guid LineId, Guid ProductUnitId, decimal Quantity);

public sealed record DispatchStockTransferInput(
    IReadOnlyList<DispatchStockTransferLineInput> Lines,
    string? IdempotencyKey = null);

/// <summary>Goods arriving for one line, in any active unit of the product: accepted go to the destination, damaged are
/// written off from Transit. Lines with both quantities 0 (or omitted) record nothing.</summary>
public sealed record ReceiveStockTransferLineInput(Guid LineId, Guid ProductUnitId, decimal AcceptedQuantity, decimal DamagedQuantity);

public sealed record ReceiveStockTransferInput(
    string? Notes,
    IReadOnlyList<ReceiveStockTransferLineInput> Lines,
    string? IdempotencyKey = null);

/// <summary>How the remaining outstanding quantity of a line is closed.</summary>
public enum StockTransferResolution
{
    WriteOff,
    ReturnToSource,
}

/// <summary>Every line that still has outstanding quantity needs an action; lines with nothing outstanding may be omitted.</summary>
public sealed record ResolveStockTransferLineInput(Guid LineId, StockTransferResolution Action);

public sealed record ResolveStockTransferInput(
    string Reason,
    IReadOnlyList<ResolveStockTransferLineInput> Lines,
    string? IdempotencyKey = null);
