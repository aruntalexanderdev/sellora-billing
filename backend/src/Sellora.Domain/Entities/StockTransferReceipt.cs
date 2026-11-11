using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// One receiving (<see cref="StockTransferReceiptKind.Receipt"/>) or resolving (<see cref="StockTransferReceiptKind.Resolution"/>)
/// event of a transfer (phase-9 D1). Sequence is 1-based per transfer. <see cref="Notes"/> holds the receiver's notes or the resolution reason.
/// </summary>
public class StockTransferReceipt : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public int Sequence { get; set; }
    public StockTransferReceiptKind Kind { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid ByUserId { get; set; }
    public string? Notes { get; set; }

    public List<StockTransferReceiptLine> Lines { get; set; } = [];
}
