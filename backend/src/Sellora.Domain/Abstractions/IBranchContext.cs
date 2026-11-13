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

    /// <summary>The primary branch of an AssignedBranches membership (working branch when no header is sent); null for company-wide members.</summary>
    Guid? PrimaryBranchId { get; }

    /// <summary>
    /// Phase 01 (FI-D11): the branch the request works in: X-Branch-Id, else the primary branch, else the company default branch
    /// (else a deterministic pick among the allowed branches; null only when nothing is accessible). Also the default READ scope.
    /// </summary>
    Guid? ActiveBranchId { get; }

    /// <summary>
    /// Branches whose documents a READ may return (query filters, StockVisibility, dashboard): by default only <see cref="ActiveBranchId"/>;
    /// widened to the allowed (and permitted) branches by an honoured <c>?scope=company</c>. null = every branch of the company.
    /// <see cref="AllowedBranchIds"/> stays the WRITE scope.
    /// </summary>
    IReadOnlySet<Guid>? ReadBranchIds { get; }

    /// <summary>True when the read scope was widened to the whole allowed company scope (or the context is unrestricted).</summary>
    bool IsCompanyScope { get; }

    bool CanAccess(Guid branchId);

    /// <summary>True when a read may return rows of <paramref name="branchId"/> (read scope, not the write scope).</summary>
    bool CanRead(Guid branchId);
}
