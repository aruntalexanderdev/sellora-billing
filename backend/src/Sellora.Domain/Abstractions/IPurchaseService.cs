using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;

namespace Sellora.Domain.Abstractions;

public interface IPurchaseService
{
    /// <summary>Creates and immediately completes a purchase: writes lines, a batch per line, stock
    /// movements, and updates each product's StockItem, all as one transaction. Throws
    /// <see cref="DomainValidationException"/> for business-rule violations.</summary>
    Task<PurchaseOrder> CreatePurchaseAsync(CreatePurchaseInput input, Guid createdByUserId, CancellationToken ct = default);

    /// <summary>Reverses a purchase's stock effects. Throws <see cref="DomainValidationException"/> if
    /// already cancelled or if any of its batches have already been consumed by a sale.</summary>
    Task CancelPurchaseAsync(Guid purchaseOrderId, CancellationToken ct = default);
}
