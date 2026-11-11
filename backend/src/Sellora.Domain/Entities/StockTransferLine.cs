using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// One product on a <see cref="StockTransfer"/>. Request snapshot (unit, code, factor, qty, base qty) is written at create; the
/// dispatch snapshot (all-or-none, DB CHECK) at dispatch, in any active unit of the product. Base quantities are never rounded:
/// entered qty x factor = base qty exactly (DB CHECK). The four counters are base quantities settled out of the dispatched quantity;
/// outstanding = DispatchedBaseQty - Received - Damaged - WrittenOff - Returned.
/// </summary>
public class StockTransferLine : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
    public int LineNo { get; set; }

    public Guid ProductId { get; set; }

    // Request snapshot.
    public Guid ProductUnitId { get; set; }
    public Guid UnitId { get; set; }
    public required string UnitCode { get; set; }
    public decimal ConversionFactorToBase { get; set; }
    public decimal RequestedQty { get; set; }
    public decimal RequestedBaseQty { get; set; }

    // Dispatch snapshot: null until the transfer is dispatched (a dispatched-as-zero line has the snapshot with DispatchedBaseQty 0).
    public Guid? DispatchProductUnitId { get; set; }
    public Guid? DispatchUnitId { get; set; }
    public string? DispatchUnitCode { get; set; }
    public decimal? DispatchFactor { get; set; }
    public decimal? DispatchedQty { get; set; }

    /// <summary>Base quantity dispatched (0 until dispatch).</summary>
    public decimal DispatchedBaseQty { get; set; }

    public decimal ReceivedBaseQty { get; set; }
    public decimal DamagedBaseQty { get; set; }
    public decimal WrittenOffBaseQty { get; set; }
    public decimal ReturnedBaseQty { get; set; }

    public List<StockTransferLineBatch> Batches { get; set; } = [];
}
