using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Branch validation shared by every service that stamps or checks a branch (Phase 2, ADR-04). The client never supplies a
/// branch id for a header document: the branch is the validated X-Branch-Id (<see cref="IBranchContext.RequestedBranchId"/>)
/// or, with no header, the company's default branch. Every lookup carries an explicit CompanyId predicate in addition to
/// the global filter, and finds the branch by id (branch foreign keys are navigation-less by design).
/// </summary>
internal static class BranchRules
{
    public const string Field = "branchId";

    public static async Task<Branch> GetDefaultBranchAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == companyId && b.IsDefault, ct)
            ?? throw new InvalidOperationException("The company has no default branch.");
    }

    /// <summary>The branch a new purchase, sale or adjustment is recorded at: the requested branch, else the primary one (AssignedBranches), else the default one.
    /// It must exist in the company, be active and be accessible to the caller (a context that grants nothing always fails).</summary>
    public static async Task<Branch> ResolveWorkingBranchAsync(SelloraDbContext db, CancellationToken ct)
    {
        var requested = db.BranchContext.RequestedBranchId;
        if (requested is { } id)
        {
            return await EnsureCanUseAsync(db, id, requireActive: true, Field, ct);
        }

        // No header: an AssignedBranches member works in the primary branch (D4); a company-wide member in the default one.
        if (db.BranchContext.PrimaryBranchId is { } primary)
        {
            return await EnsureCanUseAsync(db, primary, requireActive: true, Field, ct);
        }

        var branch = await GetDefaultBranchAsync(db, ct);
        if (!db.BranchContext.CanAccess(branch.Id) || !branch.IsActive)
        {
            throw new DomainValidationException(Field, "Select a branch you can work in.");
        }
        return branch;
    }

    /// <summary>A branch an explicit id points at (document tag, return, account tag): must exist in the company and be accessible;
    /// optionally active. Unknown, foreign, inaccessible and (when required) inactive branches share one message (no existence leak).</summary>
    public static async Task<Branch> EnsureCanUseAsync(SelloraDbContext db, Guid branchId, bool requireActive, string field, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == companyId && b.Id == branchId, ct);
        if (branch is null || !db.BranchContext.CanAccess(branch.Id) || (requireActive && !branch.IsActive))
        {
            throw new DomainValidationException(field, "Select a valid, active branch.");
        }
        return branch;
    }

    /// <summary>A return is recorded at the branch of the original document (goods go back where they were sold/received).
    /// A requested branch that differs is refused; the original's branch must be active and accessible.</summary>
    public static async Task<Branch> ResolveReturnBranchAsync(SelloraDbContext db, Guid originalBranchId, CancellationToken ct)
    {
        if (db.BranchContext.RequestedBranchId is { } requested && requested != originalBranchId)
        {
            throw new DomainValidationException(Field, "Returns must be recorded at the branch of the original document.");
        }
        return await EnsureCanUseAsync(db, originalBranchId, requireActive: true, Field, ct);
    }

    /// <summary>Payments, refunds, reversals and cancellations act on a document of its own branch (no active check:
    /// dues of a closed branch can still be collected). A no-op while every member is company-wide (Phase 5 tightens it).</summary>
    public static void EnsureCanAccessDocument(SelloraDbContext db, Guid documentBranchId)
    {
        if (!db.BranchContext.CanAccess(documentBranchId))
        {
            throw new DomainValidationException(Field, "You do not have access to this document's branch.");
        }
    }
}
