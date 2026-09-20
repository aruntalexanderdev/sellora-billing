using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Sales;

namespace Sellora.Domain.Abstractions;

public interface ISalesService
{
    /// <summary>Creates and immediately completes a sale: consumes purchase batches FIFO for real COGS,
    /// writes stock movements, and decrements each product's StockItem, all as one transaction. Throws
    /// <see cref="DomainValidationException"/> for business-rule violations, including insufficient stock.</summary>
    Task<SalesInvoice> CreateSaleAsync(CreateSaleInput input, Guid createdByUserId, CancellationToken ct = default);

    /// <summary>Appends a payment to the invoice's ledger against its outstanding due. Runs under a row lock so
    /// concurrent payments cannot together exceed the due. Returns null if the invoice does not exist; throws
    /// <see cref="DomainValidationException"/> if it is cancelled or fully paid, the amount is not positive or
    /// exceeds the due, or the due differs from <see cref="AddSalePaymentInput.ExpectedDue"/>.</summary>
    Task<SalesInvoice?> AddPaymentAsync(Guid salesInvoiceId, AddSalePaymentInput input, Guid receivedByUserId, CancellationToken ct = default);

    /// <summary>Takes goods back from a customer: puts them into the exact batches they were sold from, credits the
    /// invoice by their value (so its net total falls) and, if a refund is given, pays money back in the same
    /// transaction. Returned quantities are capped at what was sold and not yet returned. Throws
    /// <see cref="DomainValidationException"/> for an unknown or cancelled sale, no lines, a quantity above what can
    /// still be returned, a date before the sale or in the future, or a refund above what is refundable.</summary>
    Task<SalesReturn> CreateReturnAsync(CreateSaleReturnInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Pays money back to a customer, up to what they have paid beyond what they owe (after returns, or all
    /// of it for a cancelled sale). Appends a refund entry to the payment ledger and an opposite money transaction;
    /// nothing is overwritten. Returns null if the invoice does not exist.</summary>
    Task<SalesInvoice?> AddRefundAsync(Guid salesInvoiceId, AddSaleRefundInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Undoes one payment entered in error: appends a reversal that copies its amount, and an opposite
    /// money transaction. A payment can be reversed once, and only while the invoice still holds at least that much.
    /// Returns null if the invoice does not exist.</summary>
    Task<SalesInvoice?> ReversePaymentAsync(Guid salesInvoiceId, Guid paymentId, ReversePaymentInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Reverses a sale's stock effects, restoring exactly the batches it consumed. Throws
    /// <see cref="DomainValidationException"/> if already cancelled.</summary>
    Task CancelSaleAsync(Guid salesInvoiceId, Guid cancelledByUserId, CancellationToken ct = default);
}
