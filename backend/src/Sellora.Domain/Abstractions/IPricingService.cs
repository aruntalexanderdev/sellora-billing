using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

public interface IPricingService
{
    /// <summary>Closes the currently-open price for this product unit (if any) and opens a new one. The unit must be
    /// active. Runs under the product's row lock so it serialises with product-unit edits and with concurrent price
    /// changes for the same product. Throws <see cref="DomainValidationException"/> for business-rule violations.</summary>
    Task<SellingPrice> SetPriceAsync(Guid productUnitId, decimal price, Guid userId, CancellationToken ct = default);
}
