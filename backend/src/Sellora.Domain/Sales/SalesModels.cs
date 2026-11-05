using Sellora.Domain.Entities;
using Sellora.Domain.Payments;

namespace Sellora.Domain.Sales;

public sealed record SaleLineInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal Qty,
    decimal UnitPrice,
    decimal Discount
);

/// <summary>A payment received against a saved invoice's outstanding due. <see cref="ExpectedDue"/> is the due
/// the caller saw, so a duplicate or stale submission is rejected instead of being applied twice.</summary>
public sealed record AddSalePaymentInput(
    decimal Amount,
    PaymentMethod PaymentMethod,
    decimal ExpectedDue,
    Guid? AccountId = null
);

public sealed record CreateSaleInput(
    Guid CustomerId,
    DateOnly InvoiceDate,
    PaymentMethod PaymentMethod,
    decimal PaidAmount,
    IReadOnlyList<SaleLineInput> Lines,
    Guid? AccountId = null,
    string? IdempotencyKey = null
);

/// <summary><see cref="Qty"/> is in the selling unit the invoice line was sold in.</summary>
public sealed record SaleReturnLineInput(Guid SalesInvoiceLineId, decimal Qty);

/// <summary>Goods a customer brings back. An optional <see cref="Refund"/> pays money back in the same step.</summary>
public sealed record CreateSaleReturnInput(
    Guid SalesInvoiceId,
    DateOnly ReturnDate,
    string? Reason,
    IReadOnlyList<SaleReturnLineInput> Lines,
    RefundDetails? Refund = null
);

/// <summary>Money paid back to a customer. <see cref="ExpectedRefundable"/> is the refundable amount the caller
/// saw, so a stale or duplicate submission is rejected instead of being applied twice.</summary>
public sealed record AddSaleRefundInput(
    decimal Amount,
    PaymentMethod PaymentMethod,
    decimal ExpectedRefundable,
    Guid? AccountId = null,
    string? Reason = null,
    Guid? SalesReturnId = null
);
