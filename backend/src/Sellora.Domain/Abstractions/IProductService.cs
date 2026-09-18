using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Abstractions;

public interface IProductService
{
    /// <summary>Creates a product with its selling modes. Throws <see cref="DomainValidationException"/>
    /// for business-rule violations.</summary>
    Task<Product> CreateProductAsync(UpsertProductInput input, CancellationToken ct = default);

    /// <summary>Updates a product's fields and reconciles its selling modes against the submitted list:
    /// modes matched by id are updated in place, new entries (no id) are created, and modes dropped from
    /// the list are deactivated (if referenced by purchase or sales history) or deleted (if not). Never
    /// deletes a selling mode that still has transactional references. Returns null if the product does
    /// not exist. Throws <see cref="DomainValidationException"/> for business-rule violations.</summary>
    Task<Product?> UpdateProductAsync(Guid productId, UpsertProductInput input, CancellationToken ct = default);
}
