using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

public class Supplier : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? City { get; set; }
    public string? Gstin { get; set; }
    public string? PaymentTerms { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Default currency for new purchases from this supplier. Saved purchases keep their own snapshot.</summary>
    public Guid CurrencyId { get; set; }
    public Currency Currency { get; set; } = null!;

    /// <summary>Optional country (BR8). StateId, when set, must belong to it (composite FK).</summary>
    public Guid? CountryId { get; set; }
    public Guid? StateId { get; set; }
}
