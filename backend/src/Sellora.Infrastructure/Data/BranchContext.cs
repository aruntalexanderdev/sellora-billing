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

    public Guid? RequestedBranchId => _requestedBranchId;
    public IReadOnlySet<Guid>? AllowedBranchIds => _allowedBranchIds;

    public bool CanAccess(Guid branchId) => _allowedBranchIds is null || _allowedBranchIds.Contains(branchId);

    /// <summary>Sets the scope once; a second call throws (no mid-request branch switch). <paramref name="allowedBranchIds"/> null = every branch.</summary>
    public void Set(Guid? requestedBranchId, IReadOnlySet<Guid>? allowedBranchIds)
    {
        if (requestedBranchId == Guid.Empty) throw new ArgumentException("A branch id must not be empty.", nameof(requestedBranchId));
        if (_isSet) throw new InvalidOperationException("The branch context cannot change within a scope.");
        _isSet = true;
        _requestedBranchId = requestedBranchId;
        _allowedBranchIds = allowedBranchIds;
    }

    /// <summary>Shared context that grants nothing (design-time, tests that must not resolve a branch).</summary>
    public static IBranchContext None { get; } = new FixedBranchContext(null, new HashSet<Guid>());

    /// <summary>A context with access to every branch of the company (seeder, tests, company-wide users), optionally with a requested branch.</summary>
    public static IBranchContext Unrestricted(Guid? requested = null) => new FixedBranchContext(requested, null);

    private sealed class FixedBranchContext(Guid? requested, IReadOnlySet<Guid>? allowed) : IBranchContext
    {
        public Guid? RequestedBranchId => requested;
        public IReadOnlySet<Guid>? AllowedBranchIds => allowed;
        public bool CanAccess(Guid branchId) => allowed is null || allowed.Contains(branchId);
    }
}
