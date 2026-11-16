using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D29: the allocation of one transfer-request line to a consignment. Unit snapshot (product unit, unit, code, factor) and entered qty are
/// frozen at allocation; EnteredQty x factor = AllocatedBaseQty exactly (DB CHECK). Loaded never exceeds allocated. One Active row per
/// (consignment, request line); a cancelled row stays as history.
/// </summary>
public class ConsignmentLine : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ConsignmentId { get; set; }

    public Guid StockTransferId { get; set; }
    public Guid StockTransferLineId { get; set; }

    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public Guid UnitId { get; set; }
    public required string UnitCode { get; set; }
    public decimal ConversionFactorToBase { get; set; }
    public decimal EnteredQty { get; set; }

    public decimal AllocatedBaseQty { get; set; }
    public decimal LoadedBaseQty { get; set; }

    /// <summary>FI-D34: moved source to Transit at dispatch (&lt;= Allocated; the unloaded remainder is released and Allocated reduced to this).</summary>
    public decimal DispatchedBaseQty { get; set; }

    // FI-D35 cumulative verified counters (Accepted + Damaged + Rejected + Missing <= Dispatched); Posted = accepted quantity already moved to the destination (<= Accepted).
    public decimal AcceptedBaseQty { get; set; }
    public decimal DamagedBaseQty { get; set; }
    public decimal RejectedBaseQty { get; set; }
    public decimal MissingBaseQty { get; set; }
    public decimal PostedBaseQty { get; set; }

    public ConsignmentLineStatus Status { get; set; } = ConsignmentLineStatus.Active;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}
