using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Phase 8a: every stock posting happens at the company's default store (the BranchStore of its default branch), so
/// behaviour matches the single shared pool of earlier phases. Phase 8b replaces this by per-document resolution.
/// </summary>
internal static class StockLocationDefaults
{
    public static async Task<Guid> GetDefaultStoreIdAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var branch = await BranchRules.GetDefaultBranchAsync(db, ct);
        var id = await db.StockLocations
            .Where(l => l.CompanyId == companyId && l.BranchId == branch.Id && l.LocationType == StockLocationType.BranchStore)
            .Select(l => (Guid?)l.Id)
            .SingleOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException("The company's default branch has no store location.");
    }
}
