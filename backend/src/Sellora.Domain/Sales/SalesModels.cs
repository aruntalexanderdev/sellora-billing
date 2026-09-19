using Sellora.Domain.Entities;

namespace Sellora.Domain.Sales;

public sealed record SaleLineInput(
    Guid ProductId,
    Guid SellingModeId,
    decimal Qty,
    decimal UnitPrice,
    decimal Discount
);

/// <summary>A payment received against a saved invoice's outstanding due. <see cref="ExpectedDue"/> is the due
/// the caller saw, so a duplicate or stale submission is rejected instead of being applied twice.</summary>
public sealed record AddSalePaymentInput(
    decimal Amount,
    PaymentMethod PaymentMethod,
    decimal ExpectedDue
);

public sealed record CreateSaleInput(
    Guid CustomerId,
    DateOnly InvoiceDate,
    PaymentMethod PaymentMethod,
    decimal PaidAmount,
    IReadOnlyList<SaleLineInput> Lines
);
