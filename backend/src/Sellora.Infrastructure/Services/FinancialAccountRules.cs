using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

internal static class FinancialAccountRules
{
    /// <summary>A payment or expense may name an account, but only one that exists and is active.
    /// Null means the money is not tracked against an account. An account tagged with a branch may only be used for documents of
    /// that branch; company-level accounts are usable everywhere, and a document without a branch (company-level expense) may use any.</summary>
    public static async Task EnsureUsableAsync(SelloraDbContext db, Guid? accountId, Guid? branchId, CancellationToken ct)
    {
        if (accountId is null) return;

        var account = await db.FinancialAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new DomainValidationException("AccountId", "Select a valid account.");
        if (!account.IsActive)
        {
            throw new DomainValidationException("AccountId", $"{account.Name} is inactive.");
        }
        if (account.BranchId is { } accountBranch && branchId is { } documentBranch && accountBranch != documentBranch)
        {
            throw new DomainValidationException("AccountId", $"{account.Name} belongs to another branch.");
        }
    }
}
