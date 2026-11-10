namespace Sellora.Domain.Abstractions;

/// <summary>
/// Server-side effective permissions per membership (Phase 7, D6/D7). Implemented in Infrastructure with one query and an IMemoryCache keyed
/// ("perm", membershipId, accessVersion). Callable before the company context exists (login/refresh/switch): the implementation loads by
/// membership id with the Company filter ignored and explicit company predicates.
/// </summary>
public interface IEffectivePermissionService
{
    /// <summary>The set of <paramref name="membershipId"/> at <paramref name="accessVersion"/> (callers read the version in the same round-trip that validated the membership).</summary>
    Task<EffectivePermissionSet> GetAsync(Guid membershipId, long accessVersion, CancellationToken ct = default);

    /// <summary>The set of the current request's membership (<see cref="IMembershipContext"/>); <see cref="EffectivePermissionSet.None"/> when no membership is established.</summary>
    Task<EffectivePermissionSet> GetCurrentAsync(CancellationToken ct = default);
}
