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
    Guid? CategoryId = null, string? SubcategoryName = null,
    // ---- TP-4: the price basis the unit prices are in (product setting, else the company's) and the effective HSN/SAC (product, else subcategory).
    bool PriceIncludesTax = false, string? HsnCode = null,
    // ---- TP-5a (F7): set when the product has no sales profile or valid version today, so TaxRatePercent (0) is not a real rate.
    string? TaxWarning = null
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

/// <summary>One tax row of a sales or sales-return line (BR23). For a per-base-unit row <see cref="BaseAmount"/> is the base quantity, not money.
/// <see cref="IsLegacy"/>: a row of a document saved before the calculator (no component, no treatment).</summary>
public record SalesLineTaxDto(
    Guid? ComponentId, string Code, TaxComponentKind Kind, TaxCalculationMethod Method, decimal? RatePercent, decimal? AmountPerBaseUnit,
    decimal BaseAmount, decimal TaxAmount, TaxTreatment? Treatment, bool IsLegacy);

/// <summary>A soft finding of the save: field <c>lines[i].hsnCode</c>. Only the create response carries them.</summary>
public record SalesWarningDto(string Field, string Message);

public record SalesInvoiceLineDto(
    Guid Id, Guid ProductId, string ProductName, Guid ProductUnitId, string UnitLabel, string UnitCode,
    decimal ConversionFactorToBase, short UnitDecimalPlaces,
    decimal Qty, decimal BaseQty, string BaseUnitLabel, short BaseUnitDecimalPlaces,
    decimal UnitPrice, decimal? ListUnitPrice, bool PriceOverridden,
    decimal TaxRatePercent, decimal Discount, decimal TaxableValue, decimal TaxAmount, decimal LineTotal, decimal CostOfGoodsSold,
    decimal ReturnedBaseQty, decimal ReturnableBaseQty, IReadOnlyList<SaleReturnUnitDto> ReturnUnits,
    IReadOnlyList<SaleBatchConsumptionDto> BatchConsumptions,
    // ---- TP-4 tax snapshot (null / empty / false / 0 on history). PriceIncludesTax: UnitPrice (and Discount) include tax.
    IReadOnlyList<SalesLineTaxDto>? Taxes = null,
    Guid? TaxProfileId = null, string? TaxProfileName = null, Guid? TaxProfileVersionId = null,
    PlaceOfSupply? PlaceOfSupply = null, bool PriceIncludesTax = false, string? HsnCode = null,
    decimal BillDiscountShare = 0m, decimal BillChargeShare = 0m
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
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null,
    // ---- TP-4 tax snapshot. TaxableTotal falls back to Subtotal - DiscountTotal for history; Regime is null for history (no place of supply).
    decimal TaxableTotal = 0m,
    decimal? BillDiscountPercent = null, decimal BillDiscountAmount = 0m, decimal BillCharges = 0m,
    PlaceOfSupply? PlaceOfSupply = null, TaxRegime? Regime = null,
    Guid? PlaceOfSupplyStateId = null, string? PlaceOfSupplyStateName = null,
    Guid? SellerStateId = null, string? SellerStateName = null, PartyTaxStatus PartyTaxStatus = PartyTaxStatus.Unspecified,
    IReadOnlyList<SalesWarningDto>? Warnings = null,
    // ---- T31b: the seller as the printed tax invoice shows it, set by the server from the invoice's branch (company settings as fallback), so it does not depend
    // on the reader's permissions. SellerTaxIdLabel is "TRN" for VAT invoices, else "GSTIN".
    string? SellerName = null, string? SellerAddress = null, string SellerTaxIdLabel = "GSTIN",
    string? SellerStateGstCode = null, string? PlaceOfSupplyStateGstCode = null
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
    Guid? StockLocationId = null,
    // ---- TP-4 (all optional: an old client keeps today's behaviour). At most one of the bill discount percent and amount.
    [Range(0, 100)] decimal? BillDiscountPercent = null,
    [Range(0, 999999999)] decimal? BillDiscountAmount = null,
    [Range(0, 999999999)] decimal BillCharges = 0m,
    // The total the cashier saw; a different saved total is a 400 on expectedGrandTotal, so cash is never taken on a stale total.
    [Range(0, 999999999)] decimal? ExpectedGrandTotal = null
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
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal,
    // ---- TP-4: the reversed tax rows of the return line (empty on history that missed the catch-up) and the original line's place of supply
    IReadOnlyList<SalesLineTaxDto>? Taxes = null, PlaceOfSupply? PlaceOfSupply = null
);

public record SalesReturnDetailDto(
    Guid Id, string ReturnNo, DateOnly ReturnDate, Guid SalesInvoiceId, string InvoiceNo, string CustomerName,
    string? Reason, decimal TaxableTotal, decimal TaxTotal, decimal Total, decimal RefundedAmount,
    string CreatedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<SalesReturnLineDto> Lines, IReadOnlyList<SalesPaymentDto> Refunds,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);
