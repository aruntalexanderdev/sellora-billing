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
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal, decimal UnitCost,
    decimal ReturnedQty, decimal ReturnableQty,
    string? InternalBatchNo, string? SupplierBatchNumber, DateOnly? ReceivedDate
);

public record PurchasePaymentDto(
    Guid Id, PaymentEntryKind Kind, DateOnly PaymentDate, decimal Amount, decimal ExchangeRateToBase, decimal BaseAmount,
    PaymentMethod PaymentMethod, string CreatedByName, DateTimeOffset CreatedAt, bool IsReversed, string? Reason, string? ReturnNo
);

public record PurchaseReturnSummaryDto(Guid Id, string ReturnNo, DateOnly ReturnDate, decimal Total, decimal OriginalTotal, decimal RefundedAmount);

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
    IReadOnlyList<PurchasePaymentDto> Payments,
    decimal ReturnedAmount, decimal OriginalReturnedAmount, decimal NetTotal, decimal OriginalNetTotal,
    decimal OriginalRefundableAmount, IReadOnlyList<PurchaseReturnSummaryDto> Returns
);

public record CreatePurchaseLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? SellingModeId,
    [Range(0.001, 999999999)] decimal Qty,
    [Range(0, 999999999)] decimal Rate,
    [Range(0, 999999999)] decimal OtherCharges,
    [Range(0, 999999999)] decimal Discount,
    [MaxLength(60)] string? SupplierBatchNumber = null
);

public record CreatePurchaseRequest(
    [Required] Guid? SupplierId,
    [Required] DateOnly PurchaseDate,
    [MaxLength(60)] string? SupplierInvoiceNumber,
    [MaxLength(500)] string? Notes,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal PaidAmount,
    [MinLength(1), MaxLength(200)] CreatePurchaseLineRequest[] Lines,
    Guid? AccountId = null
);

public record UpdatePurchaseHeaderRequest(
    [MaxLength(60)] string? SupplierInvoiceNumber,
    [MaxLength(500)] string? Notes
);

public record AddPurchasePaymentRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal ExpectedDue,
    Guid? AccountId = null
);

public record PurchaseReturnLineRequest([Required] Guid? PurchaseOrderLineId, [Range(0.001, 999999999)] decimal Qty);

public record PurchaseRefundRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    Guid? AccountId = null,
    [MaxLength(200)] string? Reason = null
);

public record CreatePurchaseReturnRequest(
    [Required] Guid? PurchaseOrderId,
    [Required] DateOnly ReturnDate,
    [MaxLength(300)] string? Reason,
    [MinLength(1)] PurchaseReturnLineRequest[] Lines,
    PurchaseRefundRequest? Refund = null
);

public record AddPurchaseRefundRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal ExpectedRefundable,
    Guid? AccountId = null,
    [MaxLength(200)] string? Reason = null,
    Guid? PurchaseReturnId = null
);

public record PurchaseReturnListItemDto(
    Guid Id, string ReturnNo, DateOnly ReturnDate, Guid PurchaseOrderId, string PurchaseNo, string SupplierName,
    decimal Total, decimal OriginalTotal, string CurrencyCode, decimal RefundedAmount
);

public record PurchaseReturnLineDto(
    Guid Id, string ProductName, string SellingModeLabel, decimal Qty, decimal BaseQty, string BaseUnitLabel,
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal
);

public record PurchaseReturnDetailDto(
    Guid Id, string ReturnNo, DateOnly ReturnDate, Guid PurchaseOrderId, string PurchaseNo, string SupplierName,
    string? Reason, decimal TaxableTotal, decimal TaxTotal, decimal Total, decimal OriginalTotal, decimal RefundedAmount,
    string CurrencyCode, string CurrencySymbol, int CurrencyDecimalPrecision,
    string BaseCurrencyCode, string BaseCurrencySymbol, int BaseCurrencyDecimalPrecision,
    string CreatedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseReturnLineDto> Lines, IReadOnlyList<PurchasePaymentDto> Refunds
);
