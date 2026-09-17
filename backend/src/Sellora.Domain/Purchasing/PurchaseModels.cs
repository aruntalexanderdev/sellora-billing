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
