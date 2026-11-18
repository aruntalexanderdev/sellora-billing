using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D40: the quantity of one consignment line's bucket resolved by one action. Unit snapshot as on the consignment line; EnteredQty x factor = BaseQty (DB CHECK), BaseQty &gt; 0.
/// Append-only. One row per (disposition, consignment line, bucket, action).
/// </summary>
public class ConsignmentDispositionLine : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid DispositionId { get; set; }
    public Guid ConsignmentLineId { get; set; }

    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public Guid UnitId { get; set; }
    public required string UnitCode { get; set; }
    public decimal ConversionFactorToBase { get; set; }

    public DispositionBucket Bucket { get; set; }
    public DispositionAction Action { get; set; }
    public decimal EnteredQty { get; set; }
    public decimal BaseQty { get; set; }
}
