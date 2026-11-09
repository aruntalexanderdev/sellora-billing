using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Writes the entered-unit snapshot on a ledger row (Phase 6a). The unit context is written on every movement;
/// <paramref name="enteredQty"/> only when this single row carries the whole document line, so that
/// EnteredQuantity x factor = |QuantityBaseUnits| (DB CHECK). Split rows pass null.
/// </summary>
internal static class MovementSnapshot
{
    public static StockMovement Apply(StockMovement movement, Guid productUnitId, string unitCode, decimal factor, decimal? enteredQty)
    {
        movement.EnteredProductUnitId = productUnitId;
        movement.EnteredUnitCode = unitCode;
        movement.ConversionFactorSnapshot = factor;
        movement.EnteredQuantity = enteredQty;
        return movement;
    }
}
