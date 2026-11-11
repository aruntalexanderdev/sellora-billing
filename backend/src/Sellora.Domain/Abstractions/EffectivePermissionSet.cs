using Sellora.Domain.Permissions;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Effective permissions of one company membership (Phase 7, D6), independent of the working branch: the union of the codes of its Active
/// assignments of active roles. <see cref="CompanyCodes"/> come from company-scoped roles (apply to every branch the membership may access, E1);
/// <see cref="BranchCodes"/> map a branch id to the codes of branch-scoped roles at that branch (E2; only while the member holds an active
/// allocation to it). An Administrator assignment sets <see cref="IsAdministrator"/> (every catalogue code, company-wide, E3).
/// Immutable; cached by (membershipId, accessVersion).
/// </summary>
public sealed class EffectivePermissionSet
{
    public static readonly EffectivePermissionSet None = new(false, new HashSet<string>(), new Dictionary<Guid, IReadOnlySet<string>>());

    public EffectivePermissionSet(bool isAdministrator, IReadOnlySet<string> companyCodes, IReadOnlyDictionary<Guid, IReadOnlySet<string>> branchCodes)
    {
        IsAdministrator = isAdministrator;
        CompanyCodes = companyCodes;
        BranchCodes = branchCodes;
    }

    public bool IsAdministrator { get; }

    /// <summary>Codes held through company-scoped roles (does not expand the Administrator: use <see cref="HasCompanyLevel"/>).</summary>
    public IReadOnlySet<string> CompanyCodes { get; }

    /// <summary>Branch id -> codes held through branch-scoped roles at that branch.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlySet<string>> BranchCodes { get; }

    /// <summary>True when <paramref name="code"/> is held company-wide (Administrator or a company role).</summary>
    public bool HasCompanyLevel(string code) => IsAdministrator || CompanyCodes.Contains(code);

    /// <summary>
    /// E5 request rule: the branches at which <paramref name="code"/> is permitted. <c>null</c> = every branch the membership may access (company-level
    /// grant); otherwise exactly the branches whose branch roles hold the code (empty = not permitted anywhere). The caller intersects with the allowed
    /// branches and the working branch.
    /// </summary>
    public IReadOnlySet<Guid>? PermittedBranches(string code)
    {
        if (HasCompanyLevel(code)) return null;
        return BranchCodes.Where(kv => kv.Value.Contains(code)).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>True when <paramref name="code"/> is permitted at <paramref name="branchId"/> (company-level grant, or a branch role at that branch). Does not check branch access.</summary>
    public bool HasAtBranch(string code, Guid branchId) => HasCompanyLevel(code) || (BranchCodes.TryGetValue(branchId, out var codes) && codes.Contains(code));

    /// <summary>Every code effective at <paramref name="branchId"/> (the list returned to the SPA for the working branch); the Administrator gets the whole catalogue.</summary>
    public IReadOnlySet<string> CodesAtBranch(Guid? branchId)
    {
        if (IsAdministrator) return PermissionCatalog.All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(CompanyCodes, StringComparer.Ordinal);
        if (branchId is { } id && BranchCodes.TryGetValue(id, out var branch)) result.UnionWith(branch);
        return result;
    }
}
