using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record SaleCatalogModeDto(Guid Id, string Label, decimal ConversionFactorToBase, decimal? CurrentPrice);

public record SaleCatalogProductDto(
    Guid ProductId, string Sku, string Name, string? Category, string BaseUnitLabel,
    decimal QuantityOnHand, decimal TaxRatePercent, IReadOnlyList<SaleCatalogModeDto> SellingModes
);

public record SalesInvoiceListItemDto(
    Guid Id, string InvoiceNo, string CustomerName, DateOnly InvoiceDate,
    decimal GrandTotal, decimal PaidAmount, decimal DueAmount, PaymentStatus PaymentStatus, SalesStatus Status
);

public record SalesInvoiceLineDto(
    Guid Id, Guid ProductId, string ProductName, Guid SellingModeId, string SellingModeLabel,
    decimal Qty, decimal BaseQty, string BaseUnitLabel, decimal UnitPrice,
    decimal TaxRatePercent, decimal Discount, decimal TaxableValue, decimal TaxAmount, decimal LineTotal, decimal CostOfGoodsSold
);

public record SalesPaymentDto(
    Guid Id, DateOnly PaymentDate, decimal Amount, PaymentMethod PaymentMethod, string CreatedByName, DateTimeOffset CreatedAt
);

public record SalesInvoiceDetailDto(
    Guid Id, string InvoiceNo, Guid CustomerId, string CustomerName, DateOnly InvoiceDate,
    PaymentMethod PaymentMethod, decimal PaidAmount, decimal DueAmount, PaymentStatus PaymentStatus,
    decimal Subtotal, decimal TaxTotal, decimal DiscountTotal, decimal GrandTotal,
    SalesStatus Status, string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt,
    IReadOnlyList<SalesInvoiceLineDto> Lines,
    IReadOnlyList<SalesPaymentDto> Payments
);

public record CreateSaleLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? SellingModeId,
    [Range(0.001, double.MaxValue)] decimal Qty,
    [Range(0, double.MaxValue)] decimal UnitPrice,
    [Range(0, double.MaxValue)] decimal Discount
);

public record CreateSaleRequest(
    [Required] Guid? CustomerId,
    [Required] DateOnly InvoiceDate,
    PaymentMethod PaymentMethod,
    [Range(0, double.MaxValue)] decimal PaidAmount,
    [MinLength(1)] CreateSaleLineRequest[] Lines,
    Guid? AccountId = null
);

public record AddSalePaymentRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal ExpectedDue,
    Guid? AccountId = null
);
