using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Phase 05 (FI-D35/D36) destination-side receiving. <see cref="RecordAsync"/> verifies quantities (a Verified receipt, no stock moves);
/// <see cref="PostAsync"/> moves the ACCEPTED quantity Transit to the destination exactly once. Business-rule violations throw
/// <see cref="DomainValidationException"/>; a consignment/receipt that does not exist or is not visible to the destination caller yields null (404).
/// Returned receipts carry Lines.
/// </summary>
public interface IConsignmentReceivingService
{
    Task<ConsignmentReceipt?> RecordAsync(Guid consignmentId, RecordReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Posting an already Posted receipt is a no-op that returns it (idempotent by state).</summary>
    Task<ConsignmentReceipt?> PostAsync(Guid consignmentId, Guid receiptId, PostReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Phase 06 (FI-D39): approves (= posts) a PendingApproval receipt. Needs StockTransfers.ApproveReceipt at the destination; otherwise null (404). Idempotent by state.</summary>
    Task<ConsignmentReceipt?> ApproveAsync(Guid consignmentId, Guid receiptId, ApproveReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Phase 06 (FI-D39): rejects an unposted receipt for correction and frees its quantities. Rejecting a rejected receipt is a no-op.</summary>
    Task<ConsignmentReceipt?> RejectAsync(Guid consignmentId, Guid receiptId, RejectReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Phase 06 (FI-D40): resolves damaged / rejected / missing quantities of POSTED receipts (write-off, return to source, accept to destination). Company-level StockTransfers.Resolve. Returns the disposition with Lines.</summary>
    Task<ConsignmentDisposition?> DispositionAsync(Guid consignmentId, RecordDispositionInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);
}
