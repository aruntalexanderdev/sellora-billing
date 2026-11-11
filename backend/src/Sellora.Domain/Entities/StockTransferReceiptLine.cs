using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// What one receipt did to one transfer line. PK (ReceiptId, TransferLineId). <see cref="Kind"/> is a copy of the receipt's kind, tied
/// to it by a composite FK, so the DB can enforce: Receipt lines carry accepted/damaged only, Resolution lines written-off/returned only.
/// The unit snapshot is required on every line (on Resolution lines the service copies the transfer line's dispatch unit and leaves the
/// entered quantities 0). Entered qty x factor = base qty exactly (DB CHECK) for accepted and damaged.
/// </summary>
public class StockTransferReceiptLine : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid ReceiptId { get; set; }
    public Guid TransferLineId { get; set; }
    public StockTransferReceiptKind Kind { get; set; }

    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public Guid UnitId { get; set; }
    public required string UnitCode { get; set; }
    public decimal ConversionFactorToBase { get; set; }

    public decimal AcceptedQty { get; set; }
    public decimal AcceptedBaseQty { get; set; }
    public decimal DamagedQty { get; set; }
    public decimal DamagedBaseQty { get; set; }
    public decimal WrittenOffBaseQty { get; set; }
    public decimal ReturnedBaseQty { get; set; }
}
