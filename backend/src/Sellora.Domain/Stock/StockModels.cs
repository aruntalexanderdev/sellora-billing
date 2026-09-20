using Sellora.Domain.Entities;

namespace Sellora.Domain.Stock;

public sealed record CreateAdjustmentInput(
    Guid ProductId,
    AdjustmentType Type,
    AdjustmentDirection Direction,
    decimal QuantityBaseUnits,
    decimal? UnitCost,
    string? Reason,
    string? IdempotencyKey = null
);
