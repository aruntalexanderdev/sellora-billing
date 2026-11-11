using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Phase 8b: a location is in use while any StockItems row at it has a non-zero quantity. Open transfers stay false
/// until Phase 9. The location row is locked FOR UPDATE first (D5), so the check waits for in-flight postings (which hold KEY
/// SHARE on it) and later postings wait for the surrounding admin transaction; callers must be inside a transaction.</summary>
public sealed class StockLocationUsageService(SelloraDbContext db) : IStockLocationUsage
{
    public async Task<StockLocationUsage> GetAsync(Guid stockLocationId, CancellationToken ct = default)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        if (db.Database.IsRelational())
        {
            await db.Database
                .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"StockLocations\" WHERE \"CompanyId\" = {companyId} AND \"Id\" = {stockLocationId} FOR UPDATE")
                .ToListAsync(ct);
        }

        var hasStock = await db.StockItems.AsNoTracking()
            .AnyAsync(s => s.CompanyId == companyId && s.StockLocationId == stockLocationId && s.QuantityOnHand != 0, ct);
        return new StockLocationUsage(hasStock, HasOpenTransfers: false);
    }
}
