using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <summary>An active, sellable unit of a catalog product. <c>IsDefault</c> mirrors IsDefaultSalesUnit;
/// <c>CurrentPrice</c> is null when the unit is not priced (POS refuses it).</summary>
public record SaleCatalogUnitDto(Guid ProductUnitId, Guid UnitId, string UnitCode, string DisplayName,
    decimal ConversionFactorToBase, short DecimalPlaces, bool IsBaseUnit, bool IsDefault, decimal? CurrentPrice, string? Barcode);

public record SaleBarcodeMatchDto(SaleCatalogProductDto Product, Guid ProductUnitId);

public record SaleCatalogProductDto(
    Guid ProductId, string Sku, string Name, string? Category, string BaseUnitLabel,
    decimal QuantityOnHand, decimal TaxRatePercent, IReadOnlyList<SaleCatalogUnitDto> Units,
    Guid? StockLocationId = null,
    Guid? CategoryId = null, string? SubcategoryName = null
);

public record SalesInvoiceListItemDto(
    Guid Id, string InvoiceNo, string CustomerName, DateOnly InvoiceDate,
    decimal GrandTotal, decimal PaidAmount, decimal DueAmount, PaymentStatus PaymentStatus, SalesStatus Status,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);

/// <summary>How much of one purchase batch a sale line drew, at the batch's cost — the sale → batch → purchase trail.</summary>
public record SaleBatchConsumptionDto(
    Guid PurchaseBatchId, string InternalBatchNo, string? SupplierBatchNumber,
    string SourceType, string SourceRefNo, Guid? PurchaseOrderId,
    decimal QtyBaseUnits, decimal ReturnedQtyBaseUnits, decimal UnitCost
);

/// <summary>A unit a sold line can be returned in. <c>MaxQty</c> is the largest entered quantity that satisfies both the unit's
/// and the base unit's decimal rules without exceeding the returnable base quantity.</summary>
public record SaleReturnUnitDto(Guid ProductUnitId, string UnitCode, string DisplayName, short DecimalPlaces,
    decimal ConversionFactorToBase, bool IsLineUnit, decimal MaxQty);

public record SalesInvoiceLineDto(
    Guid Id, Guid ProductId, string ProductName, Guid ProductUnitId, string UnitLabel, string UnitCode,
    decimal ConversionFactorToBase, short UnitDecimalPlaces,
    decimal Qty, decimal BaseQty, string BaseUnitLabel, short BaseUnitDecimalPlaces,
    decimal UnitPrice, decimal? ListUnitPrice, bool PriceOverridden,
    decimal TaxRatePercent, decimal Discount, decimal TaxableValue, decimal TaxAmount, decimal LineTotal, decimal CostOfGoodsSold,
    decimal ReturnedBaseQty, decimal ReturnableBaseQty, IReadOnlyList<SaleReturnUnitDto> ReturnUnits,
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
    IReadOnlyList<SalesReturnSummaryDto> Returns,
    Guid BranchId, string? BranchCode, string? BranchName,
    string? SellerGstin,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);

public record CreateSaleLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? ProductUnitId,
    [Range(0.001, 999999999)] decimal Qty,
    [Range(0, 999999999)] decimal UnitPrice,
    [Range(0, 999999999)] decimal Discount,
    bool OverridePrice = false
);

public record CreateSaleRequest(
    [Required] Guid? CustomerId,
    [Required] DateOnly InvoiceDate,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal PaidAmount,
    [MinLength(1), MaxLength(200)] CreateSaleLineRequest[] Lines,
    Guid? AccountId = null,
    Guid? StockLocationId = null
);

public record AddSalePaymentRequest(
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [Range(0, 999999999)] decimal ExpectedDue,
    Guid? AccountId = null
);

public record SaleReturnLineRequest([Required] Guid? SalesInvoiceLineId, Guid? ProductUnitId, [Range(0.001, 999999999)] decimal Qty);

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
    decimal Total, decimal RefundedAmount,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);

public record SalesReturnLineDto(
    Guid Id, string ProductName, string UnitLabel, string UnitCode, decimal Qty, decimal BaseQty, string BaseUnitLabel, short BaseUnitDecimalPlaces,
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal
);

public record SalesReturnDetailDto(
    Guid Id, string ReturnNo, DateOnly ReturnDate, Guid SalesInvoiceId, string InvoiceNo, string CustomerName,
    string? Reason, decimal TaxableTotal, decimal TaxTotal, decimal Total, decimal RefundedAmount,
    string CreatedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<SalesReturnLineDto> Lines, IReadOnlyList<SalesPaymentDto> Refunds,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);
