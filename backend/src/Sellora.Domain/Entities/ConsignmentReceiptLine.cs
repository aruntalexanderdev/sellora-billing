using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D35: per consignment line, the accepted / damaged / rejected / missing quantities of one receipt. Unit snapshot as on the consignment line;
/// entered x factor = base for each of the four (DB CHECK); at least one base quantity is positive. One row per (receipt, consignment line).
/// </summary>
public class ConsignmentReceiptLine : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ReceiptId { get; set; }
    public Guid ConsignmentLineId { get; set; }

    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public Guid UnitId { get; set; }
    public required string UnitCode { get; set; }
    public decimal ConversionFactorToBase { get; set; }

    public decimal AcceptedEnteredQty { get; set; }
    public decimal AcceptedBaseQty { get; set; }
    public decimal DamagedEnteredQty { get; set; }
    public decimal DamagedBaseQty { get; set; }
    public decimal RejectedEnteredQty { get; set; }
    public decimal RejectedBaseQty { get; set; }
    public decimal MissingEnteredQty { get; set; }
    public decimal MissingBaseQty { get; set; }
}
