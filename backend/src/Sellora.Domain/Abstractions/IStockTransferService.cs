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

    /// <summary>
    /// Requested only. Lines may be approved at less than requested (down to 0, in the requested unit; the difference is closed); a reason is
    /// required when any line is reduced and at least one line must stay above 0 (otherwise reject). Under the request's approval
    /// separation snapshot, neither its creator nor its submitter may approve it.
    /// </summary>
    Task<StockTransfer?> ApproveAsync(Guid id, ApproveStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<StockTransfer?> RejectAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default);
    Task<StockTransfer?> CancelAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// FI-D26: Approved / PartiallyFulfilled with something already dispatched and no outstanding allocation: closes the remainder
    /// (ClosedBaseQty = Requested - Dispatched per line) and sets Closed. Reason required (field "reason", at most 500 characters).
    /// Idempotent by state like the other transitions.
    /// </summary>
    Task<StockTransfer?> CloseAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Approved only; exactly once: source to Transit, FIFO at the source, batch identity and cost preserved.</summary>
    Task<StockTransfer?> DispatchAsync(Guid id, DispatchStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Dispatched / PartiallyReceived: accepted quantities Transit to destination, damaged written off from Transit.</summary>
    Task<StockTransferReceiptResult?> ReceiveAsync(Guid id, ReceiveStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Dispatched / PartiallyReceived, company-level StockTransfers.Resolve: closes all outstanding quantity (write-off or return to source) and completes the transfer.</summary>
    Task<StockTransferReceiptResult?> ResolveAsync(Guid id, ResolveStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// True when the caller's active branch (or company-level grant) fits the transfer's source for StockTransfers.Approve — the same check
    /// approve / reject / close run. Does not check that the permission is held (the policy does that) nor the status. Read-only.
    /// </summary>
    Task<bool> FitsApproverAsync(StockTransfer transfer, CancellationToken ct = default);
}
