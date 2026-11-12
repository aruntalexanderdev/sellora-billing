using Sellora.Domain.Abstractions;

namespace Sellora.Infrastructure.Data;

/// <summary>
/// Scoped holder of the branch scope (Phase 2, ADR-04). The HTTP pipeline sets it once per request after validating the
/// X-Branch-Id header and the membership scope; non-HTTP callers (seeder, tests) use <see cref="Unrestricted"/>. A fresh,
/// never-set instance grants nothing (fail closed): <see cref="AllowedBranchIds"/> is empty.
/// </summary>
public sealed class BranchContext : IBranchContext
{
    private static readonly IReadOnlySet<Guid> NoBranches = new HashSet<Guid>();

    private bool _isSet;
    private Guid? _requestedBranchId;
    private IReadOnlySet<Guid>? _allowedBranchIds = NoBranches;
    private Guid? _primaryBranchId;
    private Guid? _activeBranchId;
    private bool _companyScope;

    public Guid? RequestedBranchId => _requestedBranchId;
    public IReadOnlySet<Guid>? AllowedBranchIds => _allowedBranchIds;
    public Guid? PrimaryBranchId => _primaryBranchId;

    public Guid? ActiveBranchId => _activeBranchId;
    public bool IsCompanyScope => _companyScope;

    /// <summary>Computed lazily so <see cref="Narrow"/> and <see cref="WidenToCompanyScope"/> are reflected. Default {active}; company scope = allowed.</summary>
    public IReadOnlySet<Guid>? ReadBranchIds
    {
        get
        {
            if (_companyScope || _activeBranchId is not { } active) return _allowedBranchIds;
            return _allowedBranchIds is null || _allowedBranchIds.Contains(active) ? new HashSet<Guid> { active } : NoBranches;
        }
    }

    public bool CanAccess(Guid branchId) => _allowedBranchIds is null || _allowedBranchIds.Contains(branchId);

    public bool CanRead(Guid branchId) => ReadBranchIds is not { } read || read.Contains(branchId);

    /// <summary>FI-D11: widens the read scope from the active branch to every allowed branch. Called only after the endpoint's permission was proven company-level.</summary>
    public void WidenToCompanyScope() => _companyScope = true;

    /// <summary>Sets the scope once; a second call throws (no mid-request branch switch). <paramref name="allowedBranchIds"/> null = every branch.</summary>
    public void Set(Guid? requestedBranchId, IReadOnlySet<Guid>? allowedBranchIds, Guid? primaryBranchId = null, Guid? activeBranchId = null)
    {
        if (requestedBranchId == Guid.Empty) throw new ArgumentException("A branch id must not be empty.", nameof(requestedBranchId));
        if (_isSet) throw new InvalidOperationException("The branch context cannot change within a scope.");
        _isSet = true;
        _requestedBranchId = requestedBranchId;
        _allowedBranchIds = allowedBranchIds;
        _primaryBranchId = primaryBranchId;
        // Active = explicit resolution by the middleware, else header, else primary; null keeps the read scope = allowed (non-HTTP callers).
        _activeBranchId = activeBranchId ?? requestedBranchId ?? primaryBranchId;
    }

    /// <summary>
    /// Phase 7 (E5): narrows the allowed branches to <paramref name="permitted"/> (intersection; repeated calls intersect again), so documents of branches
    /// where the required permission is not held vanish from lists/details. Only ever shrinks the scope; ignored before <see cref="Set"/> ran.
    /// </summary>
    public void Narrow(IReadOnlySet<Guid> permitted)
    {
        _allowedBranchIds = _allowedBranchIds is null
            ? new HashSet<Guid>(permitted)
            : _allowedBranchIds.Where(permitted.Contains).ToHashSet();
    }

    /// <summary>Shared context that grants nothing (design-time, tests that must not resolve a branch).</summary>
    public static IBranchContext None { get; } = new FixedBranchContext(null, new HashSet<Guid>());

    /// <summary>A context with access to every branch of the company (seeder, tests, company-wide users), optionally with a requested branch.</summary>
    public static IBranchContext Unrestricted(Guid? requested = null) => new FixedBranchContext(requested, null);

    private sealed class FixedBranchContext(Guid? requested, IReadOnlySet<Guid>? allowed) : IBranchContext
    {
        public Guid? RequestedBranchId => requested;
        public IReadOnlySet<Guid>? AllowedBranchIds => allowed;
        public Guid? PrimaryBranchId => null;
        public Guid? ActiveBranchId => requested;
        public IReadOnlySet<Guid>? ReadBranchIds => allowed;
        public bool IsCompanyScope => allowed is null;
        public bool CanRead(Guid branchId) => CanAccess(branchId);
        public bool CanAccess(Guid branchId) => allowed is null || allowed.Contains(branchId);
    }
}
