using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>One component line of a version. <see cref="Method"/> is a copy of the component's method: the composite FK
/// (CompanyId, TaxComponentId, Method) makes "the rate matches the component's method" a database rule.</summary>
public class TaxProfileVersionComponent : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public Guid TaxProfileVersionId { get; set; }
    public TaxProfileVersion TaxProfileVersion { get; set; } = null!;

    public Guid TaxComponentId { get; set; }
    public TaxComponent TaxComponent { get; set; } = null!;

    /// <summary>Copy of <see cref="TaxComponent.Method"/>; Percent requires <see cref="RatePercent"/>, AmountPerBaseUnit requires <see cref="AmountPerBaseUnit"/>.</summary>
    public TaxCalculationMethod Method { get; set; }

    public TaxApplicability Applicability { get; set; }
    public decimal? RatePercent { get; set; }
    public decimal? AmountPerBaseUnit { get; set; }
    public int SortOrder { get; set; }
}
