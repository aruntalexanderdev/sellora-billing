using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Category and subcategory masters (Phase 08, FI-D16). Deactivate only, never delete. Business-rule violations throw
/// <see cref="DomainValidationException"/> keyed <c>code</c>, <c>name</c>, <c>sortOrder</c> or <c>isActive</c>.
/// Methods return null when the category (or the subcategory under that category) does not exist.
/// </summary>
public interface IProductCategoryService
{
    Task<ProductCategory> CreateCategoryAsync(UpsertProductClassificationInput input, CancellationToken ct = default);

    /// <summary>Updates a category; a rename also updates the mirrored legacy <c>Product.Category</c> of its products.</summary>
    Task<ProductCategory?> UpdateCategoryAsync(Guid categoryId, UpsertProductClassificationInput input, CancellationToken ct = default);

    Task<ProductCategory?> SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken ct = default);

    Task<ProductSubcategory?> CreateSubcategoryAsync(Guid categoryId, UpsertProductClassificationInput input, CancellationToken ct = default);

    Task<ProductSubcategory?> UpdateSubcategoryAsync(Guid categoryId, Guid subcategoryId, UpsertProductClassificationInput input, CancellationToken ct = default);

    /// <summary>Activating a subcategory of an inactive category is rejected (<c>isActive</c>).</summary>
    Task<ProductSubcategory?> SetSubcategoryActiveAsync(Guid categoryId, Guid subcategoryId, bool isActive, CancellationToken ct = default);
}
