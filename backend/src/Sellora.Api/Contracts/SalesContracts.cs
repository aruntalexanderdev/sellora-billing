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

/// <summary>How much of one purchase batch a sale line drew, at the batch's cost — the sale → batch → purchase trail.</summary>
public record SaleBatchConsumptionDto(
    Guid PurchaseBatchId, string InternalBatchNo, string? SupplierBatchNumber,
    string SourceType, string SourceRefNo, Guid? PurchaseOrderId,
    decimal QtyBaseUnits, decimal ReturnedQtyBaseUnits, decimal UnitCost
);

public record SalesInvoiceLineDto(
    Guid Id, Guid ProductId, string ProductName, Guid SellingModeId, string SellingModeLabel,
    decimal Qty, decimal BaseQty, string BaseUnitLabel, decimal UnitPrice,
    decimal TaxRatePercent, decimal Discount, decimal TaxableValue, decimal TaxAmount, decimal LineTotal, decimal CostOfGoodsSold,
    decimal ReturnedQty, decimal ReturnableQty,
    IReadOnlyList<SaleBatchConsumptionDto> BatchConsumptions
);

public record SalesPaymentDto(
    Guid Id, PaymentEntryKind Kind, DateOnly PaymentDate, decimal Amount, PaymentMethod PaymentMethod,
    string CreatedByName, DateTimeOffset CreatedAt, bool IsReversed, string? Reason, string? ReturnNo
);

public record SalesReturnSummaryDto(Guid Id, string ReturnNo, DateOnly ReturnDate, decimal Total, decimal RefundedAmount);

public record SalesInvoiceDetailDto(
    Guid Id, string InvoiceNo, Guid CustomerId, string CustomerName, DateOnly InvoiceDate,
    PaymentMethod PaymentMethod, decimal PaidAmount, decimal DueAmount, PaymentStatus PaymentStatus,
    decimal Subtotal, decimal TaxTotal, decimal DiscountTotal, decimal GrandTotal,
    SalesStatus Status, string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt,
    IReadOnlyList<SalesInvoiceLineDto> Lines,
    IReadOnlyList<SalesPaymentDto> Payments,
    decimal ReturnedAmount, decimal NetTotal, decimal RefundableAmount,
    IReadOnlyList<SalesReturnSummaryDto> Returns
);

public record CreateSaleLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? SellingModeId,
    [Range(0.001, 999999999)] decimal Qty,
    [Range(0, 999999999)] decimal UnitPrice,
    [Range(0, 999999999)] decimal Discount
);

public record CreateSaleRequest(
    [Required] Guid? CustomerId,
    [Required] DateOnly InvoiceDate,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal PaidAmount,
    [MinLength(1), MaxLength(200)] CreateSaleLineRequest[] Lines,
    Guid? AccountId = null
);

public record AddSalePaymentRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal ExpectedDue,
    Guid? AccountId = null
);

public record SaleReturnLineRequest([Required] Guid? SalesInvoiceLineId, [Range(0.001, 999999999)] decimal Qty);

public record RefundRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    Guid? AccountId = null,
    [MaxLength(200)] string? Reason = null
);

public record CreateSaleReturnRequest(
    [Required] Guid? SalesInvoiceId,
    [Required] DateOnly ReturnDate,
    [MaxLength(300)] string? Reason,
    [MinLength(1)] SaleReturnLineRequest[] Lines,
    RefundRequest? Refund = null
);

public record AddSaleRefundRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal ExpectedRefundable,
    Guid? AccountId = null,
    [MaxLength(200)] string? Reason = null,
    Guid? SalesReturnId = null
);

public record ReversePaymentRequest([MaxLength(200)] string? Reason = null);

public record SalesReturnListItemDto(
    Guid Id, string ReturnNo, DateOnly ReturnDate, Guid SalesInvoiceId, string InvoiceNo, string CustomerName,
    decimal Total, decimal RefundedAmount
);

public record SalesReturnLineDto(
    Guid Id, string ProductName, string SellingModeLabel, decimal Qty, decimal BaseQty, string BaseUnitLabel,
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal
);

public record SalesReturnDetailDto(
    Guid Id, string ReturnNo, DateOnly ReturnDate, Guid SalesInvoiceId, string InvoiceNo, string CustomerName,
    string? Reason, decimal TaxableTotal, decimal TaxTotal, decimal Total, decimal RefundedAmount,
    string CreatedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<SalesReturnLineDto> Lines, IReadOnlyList<SalesPaymentDto> Refunds
);
