using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;

namespace Sellora.Domain.Abstractions;

public interface IPurchaseService
{
    /// <summary>Creates and immediately completes a purchase: writes lines, a batch per line, stock
    /// movements, and updates each product's StockItem, all as one transaction. Throws
    /// <see cref="DomainValidationException"/> for business-rule violations.</summary>
    Task<PurchaseOrder> CreatePurchaseAsync(CreatePurchaseInput input, Guid createdByUserId, CancellationToken ct = default);

    /// <summary>Updates a purchase's non-structural header fields, including the amount paid. The paid amount
    /// is entered in the purchase's transaction currency; the service records it as entered and converts it to
    /// the base currency using the exchange rate captured on the purchase. Returns null if the purchase does
    /// not exist; throws <see cref="DomainValidationException"/> if it is cancelled or over-paid.</summary>
    Task<PurchaseOrder?> UpdateHeaderAsync(Guid purchaseOrderId, UpdatePurchaseHeaderInput input, CancellationToken ct = default);

    /// <summary>Reverses a purchase's stock effects. Throws <see cref="DomainValidationException"/> if
    /// already cancelled or if any of its batches have already been consumed by a sale.</summary>
    Task CancelPurchaseAsync(Guid purchaseOrderId, CancellationToken ct = default);
}
