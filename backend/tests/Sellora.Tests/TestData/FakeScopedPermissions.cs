using Sellora.Domain.Abstractions;

namespace Sellora.Tests.TestData;

/// <summary>Fixed effective-permission set with company-level codes AND per-branch codes (branch-scoped role holders), for read-narrowing tests.</summary>
public sealed class FakeScopedPermissions(IEnumerable<string> companyCodes, IReadOnlyDictionary<Guid, string[]> branchCodes) : IEffectivePermissionService
{
    private readonly EffectivePermissionSet _set = new(
        false, companyCodes.ToHashSet(StringComparer.Ordinal),
        branchCodes.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value.ToHashSet(StringComparer.Ordinal)));

    /// <summary>Holds <paramref name="codes"/> at exactly one branch (no company-level grant).</summary>
    public static FakeScopedPermissions AtBranch(Guid branchId, params string[] codes) =>
        new([], new Dictionary<Guid, string[]> { [branchId] = codes });

    public Task<EffectivePermissionSet> GetAsync(Guid membershipId, long accessVersion, CancellationToken ct = default) => Task.FromResult(_set);
    public Task<EffectivePermissionSet> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_set);
}
