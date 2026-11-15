using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>A subcategory of exactly one <see cref="ProductCategory"/> of the same company.</summary>
public class ProductSubcategory : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public ProductCategory Category { get; set; } = null!;
    public required string Code { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
