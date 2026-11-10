using Sellora.Domain.Abstractions;

namespace Sellora.Tests.TestData;

/// <summary>Test double for <see cref="IStockLocationUsage"/>: reports the locations in <see cref="InUse"/> as holding stock (the real hook is inert until Phases 8/9).</summary>
public sealed class FakeStockLocationUsage : IStockLocationUsage
{
    public HashSet<Guid> InUse { get; } = [];
    public bool Everything { get; set; }
    public List<Guid> Asked { get; } = [];

    public Task<StockLocationUsage> GetAsync(Guid stockLocationId, CancellationToken ct = default)
    {
        Asked.Add(stockLocationId);
        var used = Everything || InUse.Contains(stockLocationId);
        return Task.FromResult(new StockLocationUsage(used, false));
    }
}
