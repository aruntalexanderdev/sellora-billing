using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record CreateStockAdjustmentRequest(
    [Required] Guid? ProductId,
    AdjustmentType Type,
    AdjustmentDirection Direction,
    [Range(0.001, 999999999)] decimal QuantityBaseUnits,
    [Range(0, 999999999)] decimal? UnitCost,
    [MaxLength(300)] string? Reason
);

public record StockAdjustmentDto(
    Guid Id, string AdjustmentNo, Guid ProductId, string ProductName,
    AdjustmentType Type, AdjustmentDirection Direction, decimal QuantityBaseUnits, decimal? UnitCost,
    string? Reason, decimal BeforeQty, decimal AfterQty, string UserName, DateTimeOffset CreatedAt
);
