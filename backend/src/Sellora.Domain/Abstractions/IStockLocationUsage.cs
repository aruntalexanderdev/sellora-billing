namespace Sellora.Domain.Abstractions;

/// <summary>Whether a stock location still carries stock or open transfers (Phase 4 D8). Consulted before warehouse/vehicle
/// deactivation and vehicle reassignment. Phase 4 ships an inert implementation; Phase 8 (balances) and Phase 9 (open transfers) fill it in.</summary>
public interface IStockLocationUsage
{
    Task<StockLocationUsage> GetAsync(Guid stockLocationId, CancellationToken ct = default);
}

public sealed record StockLocationUsage(bool HasStock, bool HasOpenTransfers)
{
    public bool InUse => HasStock || HasOpenTransfers;
}
