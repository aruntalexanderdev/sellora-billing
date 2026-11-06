using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <summary><see cref="Quantity"/> is entered in <see cref="ProductUnitId"/> (any active unit of the product);
/// <see cref="UnitCost"/> is per entered unit and required for increases only.</summary>
public record CreateStockAdjustmentRequest(
    [Required] Guid? ProductId,
    [Required] Guid? ProductUnitId,
    AdjustmentType Type,
    AdjustmentDirection Direction,
    [Range(0.001, 999999999)] decimal Quantity,
    [Range(0, 999999999)] decimal? UnitCost,
    [MaxLength(300)] string? Reason
);

/// <summary>One row of the adjustment list. Entered quantity/unit/cost are the posting-time snapshots;
/// <see cref="UnitCost"/> is per base unit. Decimal places are read live (frozen once a unit is in use).</summary>
public record StockAdjustmentListItemDto(
    Guid Id, string AdjustmentNo, DateTimeOffset CreatedAt, Guid ProductId, string ProductName, string Sku,
    AdjustmentType Type, AdjustmentDirection Direction, decimal EnteredQuantity, string UnitCode, short EnteredDecimalPlaces,
    decimal QuantityBaseUnits, string BaseUnitCode, short BaseDecimalPlaces, decimal? EnteredUnitCost, decimal? UnitCost,
    decimal BeforeQty, decimal AfterQty, string UserName
);

/// <summary>A batch the adjustment created (+) or drew from (−), from its stock movements.</summary>
public record StockAdjustmentBatchDto(Guid PurchaseBatchId, string InternalBatchNo, decimal QuantityBaseUnits, decimal UnitCost);

/// <summary>Adjustment detail — returned by GET {id} and by POST (including an idempotent replay).</summary>
public record StockAdjustmentDto(
    Guid Id, string AdjustmentNo, DateTimeOffset CreatedAt, Guid ProductId, string ProductName, string Sku,
    AdjustmentType Type, AdjustmentDirection Direction, decimal EnteredQuantity, Guid ProductUnitId, string UnitCode, string UnitDisplayName,
    decimal ConversionFactorToBase, short EnteredDecimalPlaces, decimal QuantityBaseUnits, string BaseUnitCode, short BaseDecimalPlaces,
    decimal? EnteredUnitCost, decimal? UnitCost, string? Reason, decimal BeforeQty, decimal AfterQty, string UserName,
    IReadOnlyList<StockAdjustmentBatchDto> Batches
);
