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
}
