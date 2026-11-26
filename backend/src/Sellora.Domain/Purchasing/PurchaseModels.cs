using Sellora.Domain.Entities;
using Sellora.Domain.Payments;

namespace Sellora.Domain.Purchasing;

public sealed record PurchaseLineInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal Qty,
    decimal Rate,
    decimal OtherCharges,
    decimal Discount,
    string? SupplierBatchNumber = null
);

public sealed record CreatePurchaseInput(
    Guid SupplierId,
    DateOnly PurchaseDate,
    string? SupplierInvoiceNumber,
    string? Notes,
    PaymentMethod PaymentMethod,
    decimal PaidAmount,
    IReadOnlyList<PurchaseLineInput> Lines,
    Guid? AccountId = null,
    string? IdempotencyKey = null,
    Guid? StockLocationId = null,
    bool RatesIncludeTax = false,
    decimal? BillDiscountPercent = null,
    decimal? BillDiscountAmount = null,
    decimal BillCharges = 0m
);

/// <summary>A soft finding of a purchase save (TP-3): a supplier with no state (<c>supplierId</c>) or a GST line without HSN (<c>lines[i].hsnCode</c>).</summary>
public sealed record PurchaseWarning(string Field, string Message);

/// <summary>Editable header fields of a saved purchase. Payments are not editable here.</summary>
public sealed record UpdatePurchaseHeaderInput(
    string? SupplierInvoiceNumber,
    string? Notes
);

/// <summary>An additional payment against a saved purchase's outstanding due. <see cref="Amount"/> and
/// <see cref="ExpectedDue"/> are in the purchase's transaction currency; ExpectedDue is the due amount the
/// caller saw, so a duplicate or stale submission is rejected instead of being applied twice.</summary>
public sealed record AddPurchasePaymentInput(
    decimal Amount,
    PaymentMethod PaymentMethod,
    decimal ExpectedDue,
    Guid? AccountId = null
);

/// <summary><see cref="Qty"/> is in <see cref="ProductUnitId"/>: the line's own unit (even if since deactivated) or any
/// active unit of the product. Null means the line's own unit.</summary>
public sealed record PurchaseReturnLineInput(Guid PurchaseOrderLineId, Guid? ProductUnitId, decimal Qty);

/// <summary>Goods sent back to a supplier. An optional <see cref="Refund"/> records money the supplier paid
/// back in the same step, in the purchase's transaction currency.</summary>
public sealed record CreatePurchaseReturnInput(
    Guid PurchaseOrderId,
    DateOnly ReturnDate,
    string? Reason,
    IReadOnlyList<PurchaseReturnLineInput> Lines,
    RefundDetails? Refund = null,
    Guid? StockLocationId = null,
    string? IdempotencyKey = null
);

/// <summary>Money a supplier paid back, in the purchase's transaction currency. <see cref="ExpectedRefundable"/>
/// is the refundable amount the caller saw, so a stale or duplicate submission is rejected.</summary>
public sealed record AddPurchaseRefundInput(
    decimal Amount,
    PaymentMethod PaymentMethod,
    decimal ExpectedRefundable,
    Guid? AccountId = null,
    string? Reason = null,
    Guid? PurchaseReturnId = null
);
