using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Api.Contracts;

public record StockOverviewItemDto(
    Guid ProductId, string Sku, string Name, string? Category, string BaseUnitLabel,
    decimal QuantityOnHand, decimal AverageCost, decimal StockValue, StockStatus Status,
    IReadOnlyList<StockLocationBalanceDto>? Locations = null
);

public record StockMovementDto(
    Guid Id, DateTimeOffset OccurredAt, StockMovementType Type, decimal QuantityBaseUnits,
    string ReferenceType, string ReferenceNo, string UserName, string? Notes,
    Guid? PurchaseBatchId, string? InternalBatchNo,
    AdjustmentType? ReasonCode, decimal? EnteredQuantity, Guid? EnteredProductUnitId, string? EnteredUnitCode,
    decimal? ConversionFactorSnapshot, int? EnteredDecimalPlaces, int BaseDecimalPlaces,
    Guid? StockLocationId = null, string? StockLocationCode = null, string? StockLocationName = null
);

public record StockBatchDto(
    Guid Id, DateTimeOffset CreatedAt, string SourceType, string SourceRefNo,
    decimal PurchasedBaseQty, decimal RemainingBaseQty, decimal UnitCost,
    string InternalBatchNo, string? SupplierBatchNumber, DateOnly ReceivedDate,
    Guid? PurchaseOrderId, Guid? PurchaseOrderLineId, Guid? StockAdjustmentId = null,
    IReadOnlyList<StockBatchLocationDto>? Locations = null
);

/// <summary>One sale line's draw on a batch — the batch → sale trail shown from Stock Details.</summary>
public record BatchConsumptionDto(
    Guid SalesInvoiceId, string InvoiceNo, DateOnly InvoiceDate, SalesStatus SaleStatus,
    string ProductName, decimal QtyBaseUnits, decimal ReturnedQtyBaseUnits, decimal UnitCost
);

public record StockDetailDto(
    Guid ProductId, string Sku, string Name, string? Category, string BaseUnitLabel, string TaxLabel,
    decimal QuantityOnHand, decimal AverageCost, decimal StockValue, decimal ReorderLevel, StockStatus Status,
    IReadOnlyList<StockMovementDto> Movements, IReadOnlyList<StockBatchDto> Batches,
    IReadOnlyList<StockLocationBalanceDto>? Locations = null
);

/// <summary>Phase 8: one product's stock at one location (base units; never summed across products).</summary>
public record StockLocationBalanceDto(
    Guid LocationId, string Code, string Name, string Type, decimal Qty, decimal AvgCost, decimal Value
);

/// <summary>Phase 8: remaining quantity of one batch at one location.</summary>
public record StockBatchLocationDto(Guid LocationId, string Code, string Name, decimal Qty);
