using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record PurchaseListItemDto(
    Guid Id, string PurchaseNo, string SupplierName, DateOnly PurchaseDate,
    decimal GrandTotal, PaymentStatus PaymentStatus, PurchaseStatus Status,
    string CurrencyCode, decimal OriginalGrandTotal
);

public record PurchaseLineDto(
    Guid Id, Guid ProductId, string ProductName, Guid SellingModeId, string SellingModeLabel,
    decimal Qty, decimal BaseQty, string BaseUnitLabel, decimal Rate,
    decimal TaxRatePercent, decimal OtherCharges, decimal Discount,
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal, decimal UnitCost
);

public record PurchasePaymentDto(
    Guid Id, DateOnly PaymentDate, decimal Amount, decimal ExchangeRateToBase, decimal BaseAmount,
    PaymentMethod PaymentMethod, string CreatedByName, DateTimeOffset CreatedAt
);

public record PurchaseDetailDto(
    Guid Id, string PurchaseNo, Guid SupplierId, string SupplierName, DateOnly PurchaseDate,
    string? SupplierInvoiceNumber, string? Notes, PaymentMethod PaymentMethod,
    decimal PaidAmount, decimal DueAmount, PaymentStatus PaymentStatus,
    decimal Subtotal, decimal TaxTotal, decimal OtherChargesTotal, decimal DiscountTotal, decimal GrandTotal,
    PurchaseStatus Status, string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt,
    Guid CurrencyId, string CurrencyCode, string CurrencySymbol, decimal ExchangeRateToBase, decimal OriginalGrandTotal,
    decimal OriginalPaidAmount, decimal OriginalDueAmount,
    string BaseCurrencyCode, string BaseCurrencySymbol, int CurrencyDecimalPrecision, int BaseCurrencyDecimalPrecision,
    IReadOnlyList<PurchaseLineDto> Lines,
    IReadOnlyList<PurchasePaymentDto> Payments
);

public record CreatePurchaseLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? SellingModeId,
    [Range(0.001, double.MaxValue)] decimal Qty,
    [Range(0, double.MaxValue)] decimal Rate,
    [Range(0, double.MaxValue)] decimal OtherCharges,
    [Range(0, double.MaxValue)] decimal Discount
);

public record CreatePurchaseRequest(
    [Required] Guid? SupplierId,
    [Required] DateOnly PurchaseDate,
    [MaxLength(60)] string? SupplierInvoiceNumber,
    [MaxLength(500)] string? Notes,
    PaymentMethod PaymentMethod,
    [Range(0, double.MaxValue)] decimal PaidAmount,
    [MinLength(1)] CreatePurchaseLineRequest[] Lines
);

public record UpdatePurchaseHeaderRequest(
    [MaxLength(60)] string? SupplierInvoiceNumber,
    [MaxLength(500)] string? Notes
);

public record AddPurchasePaymentRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal ExpectedDue
);
