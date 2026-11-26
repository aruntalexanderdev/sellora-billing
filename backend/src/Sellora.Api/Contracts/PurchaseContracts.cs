using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record PurchaseListItemDto(
    Guid Id, string PurchaseNo, string SupplierName, DateOnly PurchaseDate,
    decimal GrandTotal, PaymentStatus PaymentStatus, PurchaseStatus Status,
    string CurrencyCode, decimal OriginalGrandTotal,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);

public record PurchaseLineDto(
    Guid Id, Guid ProductId, string ProductName, Guid ProductUnitId, string UnitLabel,
    string UnitCode, decimal ConversionFactorToBase, short UnitDecimalPlaces,
    decimal Qty, decimal BaseQty, string BaseUnitLabel, short BaseUnitDecimalPlaces, decimal Rate,
    decimal TaxRatePercent, decimal OtherCharges, decimal Discount,
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal, decimal UnitCost,
    decimal ReturnedBaseQty, decimal ReturnableBaseQty, IReadOnlyList<PurchaseReturnUnitDto> ReturnUnits,
    string? InternalBatchNo, string? SupplierBatchNumber, DateOnly? ReceivedDate,
    IReadOnlyList<PurchaseLineLocationBalanceDto>? LocationBalances = null,
    // ---- TP-3 tax snapshot (null / empty / false on history: IsLegacy rows only, no profile)
    IReadOnlyList<PurchaseLineTaxDto>? Taxes = null,
    Guid? TaxProfileId = null, string? TaxProfileName = null, Guid? TaxProfileVersionId = null,
    PlaceOfSupply? PlaceOfSupply = null, bool PriceIncludesTax = false, string? HsnCode = null,
    decimal BillDiscountShare = 0m, decimal BillChargeShare = 0m,
    decimal? OriginalRate = null, decimal? OriginalDiscount = null, decimal? OriginalOtherCharges = null,
    decimal? OriginalBillDiscountShare = null, decimal? OriginalBillChargeShare = null,
    decimal? OriginalTaxableValue = null, decimal? OriginalTaxAmount = null, decimal? OriginalLineTotal = null
);

/// <summary>One tax row of a purchase or purchase-return line (BR23). For a per-base-unit row <see cref="BaseAmount"/> is the base quantity, not money.
/// <see cref="IsLegacy"/>: a row of a document saved before the calculator (no component, no treatment); the original twins are null on legacy rows.</summary>
public record PurchaseLineTaxDto(
    Guid? ComponentId, string Code, TaxComponentKind Kind, TaxCalculationMethod Method, decimal? RatePercent, decimal? AmountPerBaseUnit,
    decimal BaseAmount, decimal TaxAmount, decimal? OriginalBaseAmount, decimal? OriginalTaxAmount, TaxTreatment? Treatment, bool IsLegacy);

/// <summary>A soft finding of the save: field <c>supplierId</c> (supplier without state) or <c>lines[i].hsnCode</c>. Only the create response carries them.</summary>
public record PurchaseWarningDto(string Field, string Message);

/// <summary>Phase 10 (D3): base quantity of a purchase line's batch at one visible stock location (balance > 0 only).</summary>
public record PurchaseLineLocationBalanceDto(Guid StockLocationId, string Code, string Name, decimal BaseQty);

/// <summary>A unit a purchase line can be returned in. <see cref="MaxQty"/> is the returnable base quantity expressed in
/// this unit, truncated to its decimal places (guidance only; the server re-validates).</summary>
public record PurchaseReturnUnitDto(
    Guid ProductUnitId, string UnitCode, string DisplayName, short DecimalPlaces,
    decimal ConversionFactorToBase, bool IsLineUnit, decimal MaxQty
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
    decimal OriginalRefundableAmount, IReadOnlyList<PurchaseReturnSummaryDto> Returns,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null,
    // ---- TP-3 tax snapshot. TaxableTotal falls back to Subtotal + OtherChargesTotal - DiscountTotal for history; the bill amounts are in the base
    // currency, the Original* twins in the purchase currency.
    decimal TaxableTotal = 0m, bool RatesIncludeTax = false,
    decimal? BillDiscountPercent = null, decimal BillDiscountAmount = 0m, decimal BillCharges = 0m,
    decimal OriginalBillDiscountAmount = 0m, decimal OriginalBillCharges = 0m,
    PlaceOfSupply? PlaceOfSupply = null, Guid? PlaceOfSupplyStateId = null, string? PlaceOfSupplyStateName = null,
    Guid? SupplierStateId = null, string? SupplierStateName = null, PartyTaxStatus PartyTaxStatus = PartyTaxStatus.Unspecified,
    IReadOnlyList<PurchaseWarningDto>? Warnings = null
);

public record CreatePurchaseLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? ProductUnitId,
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
    Guid? AccountId = null,
    Guid? StockLocationId = null,
    // ---- TP-3 (all optional: an old client keeps today's behaviour). At most one of the bill discount percent and amount.
    bool RatesIncludeTax = false,
    [Range(0, 100)] decimal? BillDiscountPercent = null,
    [Range(0, 999999999)] decimal? BillDiscountAmount = null,
    [Range(0, 999999999)] decimal BillCharges = 0m
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

/// <summary><c>ProductUnitId</c> is the unit <c>Qty</c> is entered in; omitted means the purchase line's own unit.</summary>
public record PurchaseReturnLineRequest([Required] Guid? PurchaseOrderLineId, Guid? ProductUnitId, [Range(0.001, 999999999)] decimal Qty);

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
    PurchaseRefundRequest? Refund = null,
    Guid? StockLocationId = null
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
    decimal Total, decimal OriginalTotal, string CurrencyCode, decimal RefundedAmount,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);

public record PurchaseReturnLineDto(
    Guid Id, string ProductName, Guid ProductUnitId, string UnitLabel, string UnitCode, decimal ConversionFactorToBase,
    decimal Qty, decimal BaseQty, string BaseUnitLabel, short BaseUnitDecimalPlaces,
    decimal TaxableValue, decimal TaxAmount, decimal LineTotal,
    // ---- TP-3: the reversed rows of the original line (IsLegacy rows reverse as before), copied profile/version/place, document-currency twins (null on history)
    IReadOnlyList<PurchaseLineTaxDto>? Taxes = null,
    Guid? TaxProfileId = null, Guid? TaxProfileVersionId = null, PlaceOfSupply? PlaceOfSupply = null,
    decimal? OriginalTaxableValue = null, decimal? OriginalTaxAmount = null
);

public record PurchaseReturnDetailDto(
    Guid Id, string ReturnNo, DateOnly ReturnDate, Guid PurchaseOrderId, string PurchaseNo, string SupplierName,
    string? Reason, decimal TaxableTotal, decimal TaxTotal, decimal Total, decimal OriginalTotal, decimal RefundedAmount,
    string CurrencyCode, string CurrencySymbol, int CurrencyDecimalPrecision,
    string BaseCurrencyCode, string BaseCurrencySymbol, int BaseCurrencyDecimalPrecision,
    string CreatedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseReturnLineDto> Lines, IReadOnlyList<PurchasePaymentDto> Refunds,
    Guid BranchId, string? BranchCode, string? BranchName,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);
