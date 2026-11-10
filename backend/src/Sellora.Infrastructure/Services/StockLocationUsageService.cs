using Sellora.Domain.Abstractions;

namespace Sellora.Infrastructure.Services;

/// <summary>Phase 4 default: no balances or transfers exist yet, so no location is ever in use. Phase 8 (stock balances) and
/// Phase 9 (open transfers) replace this with real checks.</summary>
public sealed class StockLocationUsageService : IStockLocationUsage
{
    public Task<StockLocationUsage> GetAsync(Guid stockLocationId, CancellationToken ct = default) =>
        Task.FromResult(new StockLocationUsage(HasStock: false, HasOpenTransfers: false));
}
