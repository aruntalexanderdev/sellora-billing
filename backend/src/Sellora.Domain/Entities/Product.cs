using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

public class Product : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    /// <summary>Legacy free-text category, mirrored with the category master name for this release (dropped in Phase 09).</summary>
    public string? Category { get; set; }

    /// <summary>Category master link (same company). Navigation-less by convention (filtered principals).</summary>
    public Guid? CategoryId { get; set; }
    public Guid? SubcategoryId { get; set; }

    public Guid BaseUnitId { get; set; }
    public Unit BaseUnit { get; set; } = null!;

    public Guid TaxId { get; set; }
    public Tax Tax { get; set; } = null!;

    /// <summary>HSN/SAC code (4, 6 or 8 digits); NULL inherits the subcategory's code (BR9).</summary>
    public string? HsnCode { get; set; }

    /// <summary>Price basis override (BR14): NULL follows <c>BusinessSettings.SellingPricesIncludeTax</c>.</summary>
    public bool? PriceIncludesTax { get; set; }

    /// <summary>Reorder threshold in the base unit — drives the low-stock badge once inventory exists.</summary>
    public decimal ReorderLevel { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<ProductUnit> Units { get; set; } = new List<ProductUnit>();
}
