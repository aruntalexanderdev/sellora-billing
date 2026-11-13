using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Abstractions;

public interface IProductService
{
    /// <summary>Creates a product with its unit hierarchy (all rows new). Throws <see cref="DomainValidationException"/>
    /// for business-rule violations; hierarchy errors are raised together, keyed <c>Units[i].Field</c> / <c>Units</c>.</summary>
    Task<Product> CreateProductAsync(UpsertProductInput input, CancellationToken ct = default);

    /// <summary>Updates a product's fields and upserts its unit hierarchy under a <c>FOR UPDATE</c> lock on the product:
    /// rows matched by id are updated in place, rows without id are added, and omitted rows are kept inactive when
    /// they are locked (referenced by lines or prices, or contained in such a row) or still contained in a kept row,
    /// otherwise deleted. A locked row's unit, contained unit and quantity cannot change (deactivate it and add a new
    /// one). Returns null if the product does not exist. Throws <see cref="DomainValidationException"/> for
    /// business-rule violations.</summary>
    Task<Product?> UpdateProductAsync(Guid productId, UpsertProductInput input, CancellationToken ct = default);

    /// <summary>Hard-deletes a product that has no history: its selling prices, unit rows, zero-quantity stock rows and
    /// the product itself, under a <c>FOR UPDATE</c> lock on the product, and writes a <c>product.deleted</c> audit event.
    /// Throws <see cref="DomainValidationException"/> (field <c>Product</c>) when any document line, adjustment, transfer,
    /// stock movement, batch or non-zero stock references it: history is never deleted, deactivate the product instead.
    /// Returns false if the product does not exist.</summary>
    Task<bool> DeleteProductAsync(Guid productId, Guid userId, string? correlationId, CancellationToken ct = default);

    /// <summary>Validates a unit hierarchy exactly as save would (with <paramref name="productId"/>: against the stored
    /// rows and their lock state) without taking locks or persisting anything. Business-rule problems are returned in
    /// the result, not thrown. Returns null if <paramref name="productId"/> is given but the product does not exist.</summary>
    Task<ProductUnitPreview?> PreviewUnitsAsync(Guid? productId, Guid baseUnitId, IReadOnlyList<ProductUnitInputItem> units, CancellationToken ct = default);
}
