using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>Sets the sales and purchase profile for a scope and country (BR7). Exactly one row per (scope target, country).
/// Shape by <see cref="Scope"/>: Company: no target; Category: <see cref="CategoryId"/>; Subcategory: <see cref="CategoryId"/> and
/// <see cref="SubcategoryId"/> (the composite FK proves the subcategory belongs to the category); Product: <see cref="ProductId"/>.</summary>
public class TaxAssignment : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public TaxScope Scope { get; set; }
    public Guid CountryId { get; set; }

    public Guid? CategoryId { get; set; }
    public Guid? SubcategoryId { get; set; }
    public Guid? ProductId { get; set; }

    public Guid SalesTaxProfileId { get; set; }

    /// <summary>NULL = same as <see cref="SalesTaxProfileId"/> (BR7).</summary>
    public Guid? PurchaseTaxProfileId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
