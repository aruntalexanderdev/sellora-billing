using Sellora.Domain.Entities;

namespace Sellora.Domain.Sales;

public sealed record SaleLineInput(
    Guid ProductId,
    Guid SellingModeId,
    decimal Qty,
    decimal UnitPrice,
    decimal Discount
);

public sealed record CreateSaleInput(
    Guid CustomerId,
    DateOnly InvoiceDate,
    PaymentMethod PaymentMethod,
    decimal PaidAmount,
    IReadOnlyList<SaleLineInput> Lines
);
