using Sellora.Domain.Abstractions;

namespace Sellora.Tests.TestData;

/// <summary>Fixed effective-permission set for controller unit tests that do not exercise the real evaluation (Phase 7 replaced token claims).</summary>
public sealed class FakeEffectivePermissions(params string[] companyCodes) : IEffectivePermissionService
{
    private readonly EffectivePermissionSet _set = new(false, companyCodes.ToHashSet(StringComparer.Ordinal), new Dictionary<Guid, IReadOnlySet<string>>());

    public Task<EffectivePermissionSet> GetAsync(Guid membershipId, long accessVersion, CancellationToken ct = default) => Task.FromResult(_set);

    public Task<EffectivePermissionSet> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_set);
}
