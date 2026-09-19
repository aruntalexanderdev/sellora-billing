using Sellora.Domain.Entities;
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

    /// <summary>Reverses a sale's stock effects, restoring exactly the batches it consumed. Throws
    /// <see cref="DomainValidationException"/> if already cancelled.</summary>
    Task CancelSaleAsync(Guid salesInvoiceId, CancellationToken ct = default);
}
