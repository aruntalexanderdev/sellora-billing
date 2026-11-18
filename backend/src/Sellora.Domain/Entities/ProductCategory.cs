using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>Company-owned product category master. Deactivated, never deleted, so products keep their category.</summary>
public class ProductCategory : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<ProductSubcategory> Subcategories { get; set; } = new List<ProductSubcategory>();
}
