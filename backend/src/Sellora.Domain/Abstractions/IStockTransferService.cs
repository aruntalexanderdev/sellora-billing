using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Domain.Abstractions;

/// <summary>A receipt or resolution posting: the transfer (lines, batches, receipts loaded) and the receipt it created.</summary>
public sealed record StockTransferReceiptResult(StockTransfer Transfer, Guid ReceiptId);

/// <summary>
/// Stock transfer lifecycle (Phase 9). Every operation is its own transaction. Business-rule violations throw
/// <see cref="DomainValidationException"/>; a transfer that does not exist or is hidden by the branch filter yields null
/// (the controller answers 404). Returned transfers carry Lines (with Batches) and Receipts (with Lines).
/// <paramref name="correlationId"/> is stamped on the audit event.
/// </summary>
public interface IStockTransferService
{
    Task<StockTransfer> CreateAsync(CreateStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Draft only: replaces endpoints, notes and lines.</summary>
    Task<StockTransfer?> UpdateDraftAsync(Guid id, UpdateStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    Task<StockTransfer?> SubmitAsync(Guid id, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<StockTransfer?> ApproveAsync(Guid id, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<StockTransfer?> RejectAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<StockTransfer?> CancelAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Approved only; exactly once: source to Transit, FIFO at the source, batch identity and cost preserved.</summary>
    Task<StockTransfer?> DispatchAsync(Guid id, DispatchStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Dispatched / PartiallyReceived: accepted quantities Transit to destination, damaged written off from Transit.</summary>
    Task<StockTransferReceiptResult?> ReceiveAsync(Guid id, ReceiveStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Dispatched / PartiallyReceived, company-level StockTransfers.Resolve: closes all outstanding quantity (write-off or return to source) and completes the transfer.</summary>
    Task<StockTransferReceiptResult?> ResolveAsync(Guid id, ResolveStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);
}
