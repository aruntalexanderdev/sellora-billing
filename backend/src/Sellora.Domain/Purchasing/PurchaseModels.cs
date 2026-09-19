using Sellora.Domain.Entities;

namespace Sellora.Domain.Purchasing;

public sealed record PurchaseLineInput(
    Guid ProductId,
    Guid SellingModeId,
    decimal Qty,
    decimal Rate,
    decimal OtherCharges,
    decimal Discount
);

public sealed record CreatePurchaseInput(
    Guid SupplierId,
    DateOnly PurchaseDate,
    string? SupplierInvoiceNumber,
    string? Notes,
    PaymentMethod PaymentMethod,
    decimal PaidAmount,
    IReadOnlyList<PurchaseLineInput> Lines
);

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
    decimal ExpectedDue
);
