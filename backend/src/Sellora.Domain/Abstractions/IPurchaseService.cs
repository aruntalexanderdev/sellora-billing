using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
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

    /// <summary>Sends goods back to a supplier: takes them out of the batches they came in on (only stock not yet
    /// sold can go back), credits the purchase by their value and, if a refund is given, records the supplier's
    /// refund in the same transaction. Throws <see cref="DomainValidationException"/> for an unknown or cancelled
    /// purchase, no lines, a quantity above what was bought or is still in stock, a date before the purchase or in
    /// the future, or a refund above what is refundable.</summary>
    Task<PurchaseReturn> CreateReturnAsync(CreatePurchaseReturnInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Records money a supplier paid back, in the purchase's transaction currency, up to what has been paid
    /// beyond what is owed (after returns, or all of it for a cancelled purchase). Returns null if the purchase
    /// does not exist.</summary>
    Task<PurchaseOrder?> AddRefundAsync(Guid purchaseOrderId, AddPurchaseRefundInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Undoes one payment entered in error, exactly as recorded (its amount and rate are copied). A payment can
    /// be reversed once. Returns null if the purchase does not exist.</summary>
    Task<PurchaseOrder?> ReversePaymentAsync(Guid purchaseOrderId, Guid paymentId, ReversePaymentInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Reverses a purchase's stock effects. Throws <see cref="DomainValidationException"/> if
    /// already cancelled or if any of its batches have already been consumed by a sale.</summary>
    Task CancelPurchaseAsync(Guid purchaseOrderId, Guid cancelledByUserId, CancellationToken ct = default);
}
