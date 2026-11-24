using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>The rates of a profile from <see cref="EffectiveFrom"/> (BR3, BR4). Append-only; a version used by a document line is immutable.
/// At most one version per profile is open (<see cref="EffectiveTo"/> NULL).</summary>
public class TaxProfileVersion : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public Guid TaxProfileId { get; set; }
    public TaxProfile TaxProfile { get; set; } = null!;

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>The last valid day; NULL while the version is open.</summary>
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>Display total of the intra-state percent components (service rule; blocking V__ check).</summary>
    public decimal TotalPercent { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>NULL for versions created by the migration or the seeder.</summary>
    public Guid? CreatedByUserId { get; set; }

    public ICollection<TaxProfileVersionComponent> Components { get; set; } = new List<TaxProfileVersionComponent>();
}
