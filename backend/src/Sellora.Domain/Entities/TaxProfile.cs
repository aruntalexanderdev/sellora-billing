using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>A named tax setup such as "GST 18%" for one country (BR2). Its rates live in dated <see cref="TaxProfileVersion"/> rows.</summary>
public class TaxProfile : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    /// <summary>The jurisdiction. Fixed after creation: assignments and components are checked against it (BR10).</summary>
    public Guid CountryId { get; set; }

    public required string Code { get; set; }
    public required string Name { get; set; }
    public TaxRegime Regime { get; set; }
    public TaxTreatment Treatment { get; set; }

    /// <summary>The mirror <see cref="Tax"/> row (D3): every profile has one until the destructive release M3 retires <c>Taxes</c>.
    /// Legacy profiles point at the original tax; seeded and new profiles at a mirror created with them.</summary>
    public Guid LegacyTaxId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<TaxProfileVersion> Versions { get; set; } = new List<TaxProfileVersion>();
}
