using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Api.Contracts;

public record StockOverviewItemDto(
    Guid ProductId, string Sku, string Name, string? Category, string BaseUnitLabel,
    decimal QuantityOnHand, decimal AverageCost, decimal StockValue, StockStatus Status
);

public record StockMovementDto(
    Guid Id, DateTimeOffset OccurredAt, StockMovementType Type, decimal QuantityBaseUnits,
    string ReferenceType, string ReferenceNo, string UserName, string? Notes,
    Guid? PurchaseBatchId, string? InternalBatchNo,
    AdjustmentType? ReasonCode, decimal? EnteredQuantity, Guid? EnteredProductUnitId, string? EnteredUnitCode,
    decimal? ConversionFactorSnapshot, int? EnteredDecimalPlaces, int BaseDecimalPlaces
);

public record StockBatchDto(
    Guid Id, DateTimeOffset CreatedAt, string SourceType, string SourceRefNo,
    decimal PurchasedBaseQty, decimal RemainingBaseQty, decimal UnitCost,
    string InternalBatchNo, string? SupplierBatchNumber, DateOnly ReceivedDate,
    Guid? PurchaseOrderId, Guid? PurchaseOrderLineId, Guid? StockAdjustmentId = null
);

/// <summary>One sale line's draw on a batch — the batch → sale trail shown from Stock Details.</summary>
public record BatchConsumptionDto(
    Guid SalesInvoiceId, string InvoiceNo, DateOnly InvoiceDate, SalesStatus SaleStatus,
    string ProductName, decimal QtyBaseUnits, decimal ReturnedQtyBaseUnits, decimal UnitCost
);

public record StockDetailDto(
    Guid ProductId, string Sku, string Name, string? Category, string BaseUnitLabel, string TaxLabel,
    decimal QuantityOnHand, decimal AverageCost, decimal StockValue, decimal ReorderLevel, StockStatus Status,
    IReadOnlyList<StockMovementDto> Movements, IReadOnlyList<StockBatchDto> Batches
);
