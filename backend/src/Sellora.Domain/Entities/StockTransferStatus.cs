namespace Sellora.Domain.Entities;

/// <summary>Lifecycle of a <see cref="StockTransfer"/> (phase-9 D2). Persisted as its name (string enum).</summary>
public enum StockTransferStatus
{
    Draft,
    Requested,
    Approved,
    Rejected,
    Dispatched,
    PartiallyReceived,
    Completed,
    Cancelled,
    // FI-D2/FI-D26 (Phase 03): request-level fulfilment states, appended so existing string values are unchanged.
    PartiallyFulfilled,
    Fulfilled,
    Closed,
    // FI-D48: level-1 (requester / destination branch) approval of a high-value request, before the source branch decides (Requested).
    AwaitingRequesterApproval,
}
