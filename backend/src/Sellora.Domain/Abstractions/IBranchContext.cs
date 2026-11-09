namespace Sellora.Domain.Abstractions;

/// <summary>
/// The branch scope of the current scope (HTTP request, seeder, test), resolved from the validated X-Branch-Id header and the
/// membership's access scope (Phase 2, ADR-04). It never grants anything by itself: services still validate the branch.
/// </summary>
public interface IBranchContext
{
    /// <summary>The validated X-Branch-Id header value, or null when the client sent none.</summary>
    Guid? RequestedBranchId { get; }

    /// <summary>Branches the caller may work in; null = every branch of the company.</summary>
    IReadOnlySet<Guid>? AllowedBranchIds { get; }

    bool CanAccess(Guid branchId);
}
