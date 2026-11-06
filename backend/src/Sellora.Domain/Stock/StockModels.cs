using Sellora.Domain.Entities;

namespace Sellora.Domain.Stock;

/// <summary>A stock adjustment as entered: <paramref name="Quantity"/> is in the product unit
/// <paramref name="ProductUnitId"/> (any active unit of the product); <paramref name="UnitCost"/> is per entered unit and
/// required for increases only (ignored for decreases). The service converts both to base units once, at posting.</summary>
public sealed record CreateAdjustmentInput(
    Guid ProductId,
    Guid ProductUnitId,
    AdjustmentType Type,
    AdjustmentDirection Direction,
    decimal Quantity,
    decimal? UnitCost,
    string? Reason,
    string? IdempotencyKey = null
);
