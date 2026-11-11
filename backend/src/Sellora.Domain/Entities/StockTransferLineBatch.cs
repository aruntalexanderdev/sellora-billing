using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// The share of a <see cref="StockTransferLine"/> dispatched out of one <see cref="PurchaseBatch"/> (phase-9 D1/D5). PK (LineId,
/// PurchaseBatchId). Per-transfer batch identity: receipts and returns draw from these rows, never from the shared Transit balance.
/// UnitCost is the batch's per-base-unit cost at dispatch (snapshot).
/// </summary>
public class StockTransferLineBatch : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid LineId { get; set; }
    public Guid PurchaseBatchId { get; set; }

    public decimal DispatchedBaseQty { get; set; }
    public decimal UnitCost { get; set; }

    public decimal ReceivedBaseQty { get; set; }
    public decimal DamagedBaseQty { get; set; }
    public decimal WrittenOffBaseQty { get; set; }
    public decimal ReturnedBaseQty { get; set; }
}
