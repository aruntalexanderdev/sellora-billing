using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

public interface IPricingService
{
    /// <summary>Closes the currently-open price for this selling mode (if any) and opens a new one.
    /// Throws <see cref="DomainValidationException"/> for business-rule violations.</summary>
    Task<SellingPrice> SetPriceAsync(Guid sellingModeId, decimal price, Guid userId, CancellationToken ct = default);
}
