using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Api.Contracts;

public record CurrentStockReportRowDto(
    Guid ProductId, string Sku, string Name, string? Category, string BaseUnitLabel,
    decimal QuantityOnHand, decimal AverageCost, decimal StockValue,
    decimal? SellingPricePerBaseUnit, decimal? SellingValue, StockStatus Status,
    IReadOnlyList<StockLocationBalanceDto>? Locations = null,
    int BaseDecimalPlaces = 0, IReadOnlyList<BreakdownUnitDto>? BreakdownUnits = null,
    Guid? CategoryId = null, string? SubcategoryName = null
);

public record StockMovementReportRowDto(
    Guid Id, DateTimeOffset OccurredAt, string ProductName, string Sku, string BaseUnitLabel,
    StockMovementType Type, decimal QuantityBaseUnits, string ReferenceType, string ReferenceNo, string UserName,
    AdjustmentType? ReasonCode, decimal? EnteredQuantity, Guid? EnteredProductUnitId, string? EnteredUnitCode,
    decimal? ConversionFactorSnapshot, int? EnteredDecimalPlaces, int BaseDecimalPlaces,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null,
    Guid? ReferenceId = null
);

public record PurchaseReportRowDto(
    Guid Id, string PurchaseNo, DateOnly PurchaseDate, string SupplierName,
    decimal Subtotal, decimal TaxTotal, decimal OtherChargesTotal, decimal GrandTotal,
    decimal PaidAmount, decimal DueAmount, PurchaseStatus Status,
    string CurrencyCode, decimal OriginalGrandTotal,
    Guid BranchId, string? BranchCode, string? BranchName,
    decimal TaxableTotal = 0m, IReadOnlyList<PurchaseReportTaxDto>? TaxByKind = null
);

/// <summary>Tax of one purchase by component kind (TP-3). Rows saved before the calculator (no component) are counted as <c>Other</c>.</summary>
public record PurchaseReportTaxDto(TaxComponentKind Kind, decimal TaxAmount);

public record SalesReportRowDto(
    Guid Id, string InvoiceNo, DateOnly InvoiceDate, string CustomerName,
    decimal Subtotal, decimal TaxTotal, decimal DiscountTotal, decimal GrandTotal,
    decimal PaidAmount, decimal DueAmount, SalesStatus Status,
    Guid BranchId, string? BranchCode, string? BranchName
);

/// <summary>
/// Gross margin basis (docx §12.5): SalesRevenue is the taxable value actually charged (net of
/// discount, excluding tax collected on the government's behalf), CostOfGoodsSold is the real
/// FIFO-consumed landed cost recorded on each sale line — never the latest purchase price. Only
/// Completed invoices are counted.
/// </summary>
public record ProfitSummaryDto(
    DateOnly? DateFrom, DateOnly? DateTo, int InvoiceCount,
    decimal SalesRevenue, decimal CostOfGoodsSold, decimal GrossProfit, decimal? GrossMarginPercent,
    Guid? BranchId = null
);
