using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// The share of a <see cref="StockTransferLine"/> dispatched out of one <see cref="PurchaseBatch"/> (phase-9 D1/D5). Surrogate PK Id (Phase 05); unique per (LineId, PurchaseBatchId) for legacy rows and per (ConsignmentLineId, PurchaseBatchId) for shipment rows. Per-transfer batch identity: receipts and returns draw from these rows, never from the shared Transit balance.
/// UnitCost is the batch's per-base-unit cost at dispatch (snapshot).
/// </summary>
public class StockTransferLineBatch : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid LineId { get; set; }
    public Guid PurchaseBatchId { get; set; }

    /// <summary>FI-D34: the shipment consignment line that dispatched this batch share (NULL for legacy direct dispatch). Traceability only.</summary>
    public Guid? ConsignmentLineId { get; set; }

    public decimal DispatchedBaseQty { get; set; }
    public decimal UnitCost { get; set; }

    public decimal ReceivedBaseQty { get; set; }
    public decimal DamagedBaseQty { get; set; }
    public decimal WrittenOffBaseQty { get; set; }
    public decimal ReturnedBaseQty { get; set; }

    /// <summary>FI-D40: quantity of a shipment batch row accepted into the destination by a disposition (AcceptToDestination). Distinct from ReceivedBaseQty, which stays equal to posted-receipt accepted quantity.</summary>
    public decimal AcceptedCorrectionBaseQty { get; set; }
}
