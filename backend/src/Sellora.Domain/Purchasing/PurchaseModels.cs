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

/// <summary>Editable header fields of a saved purchase. <see cref="PaidAmount"/> is in the purchase's own
/// transaction currency (the supplier currency it was created in).</summary>
public sealed record UpdatePurchaseHeaderInput(
    string? SupplierInvoiceNumber,
    string? Notes,
    PaymentMethod PaymentMethod,
    decimal PaidAmount
);
