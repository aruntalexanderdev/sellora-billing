namespace Sellora.Domain.Entities;

/// <summary>What a <see cref="StockTransferReceipt"/> records (phase-9 D1). Receipt = accepted/damaged quantities at the destination;
/// Resolution = in-transit remainder closed as written-off or returned to the source. Persisted as its name (string enum).</summary>
public enum StockTransferReceiptKind
{
    Receipt,
    Resolution,
}
