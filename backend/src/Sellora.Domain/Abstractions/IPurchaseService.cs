using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;

namespace Sellora.Domain.Abstractions;

public interface IPurchaseService
{
    /// <summary>Creates and immediately completes a purchase: writes lines, a batch per line, stock
    /// movements, and updates each product's StockItem, all as one transaction. Throws
    /// <see cref="DomainValidationException"/> for business-rule violations.</summary>
    Task<PurchaseOrder> CreatePurchaseAsync(CreatePurchaseInput input, Guid createdByUserId, CancellationToken ct = default);

    /// <summary>Updates a purchase's non-structural header fields (supplier invoice number and notes). Payments
    /// are never changed here; they go through <see cref="AddPaymentAsync"/>. Returns null if the purchase does
    /// not exist; throws <see cref="DomainValidationException"/> if it is cancelled.</summary>
    Task<PurchaseOrder?> UpdateHeaderAsync(Guid purchaseOrderId, UpdatePurchaseHeaderInput input, CancellationToken ct = default);

    /// <summary>Appends a payment to the purchase's ledger against its outstanding due, in the purchase's
    /// transaction currency, converting it at the purchase's captured rate. Runs under a row lock so
    /// concurrent payments cannot together exceed the due. Returns null if the purchase does not exist; throws
    /// <see cref="DomainValidationException"/> if it is cancelled or fully paid, the amount is not positive or
    /// exceeds the due, or the due differs from <see cref="AddPurchasePaymentInput.ExpectedDue"/>.</summary>
    Task<PurchaseOrder?> AddPaymentAsync(Guid purchaseOrderId, AddPurchasePaymentInput input, Guid paidByUserId, CancellationToken ct = default);

    /// <summary>Reverses a purchase's stock effects. Throws <see cref="DomainValidationException"/> if
    /// already cancelled or if any of its batches have already been consumed by a sale.</summary>
    Task CancelPurchaseAsync(Guid purchaseOrderId, CancellationToken ct = default);
}
