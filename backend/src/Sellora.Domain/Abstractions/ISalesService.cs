using Sellora.Domain.Entities;
using Sellora.Domain.Sales;

namespace Sellora.Domain.Abstractions;

public interface ISalesService
{
    /// <summary>Creates and immediately completes a sale: consumes purchase batches FIFO for real COGS,
    /// writes stock movements, and decrements each product's StockItem, all as one transaction. Throws
    /// <see cref="DomainValidationException"/> for business-rule violations, including insufficient stock.</summary>
    Task<SalesInvoice> CreateSaleAsync(CreateSaleInput input, Guid createdByUserId, CancellationToken ct = default);

    /// <summary>Reverses a sale's stock effects, restoring exactly the batches it consumed. Throws
    /// <see cref="DomainValidationException"/> if already cancelled.</summary>
    Task CancelSaleAsync(Guid salesInvoiceId, CancellationToken ct = default);
}
