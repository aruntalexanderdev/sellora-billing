using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Phase 8b: a location is in use while any StockItems row at it has a non-zero quantity. An open transfer (Source or Destination = the location, Status not Completed/Cancelled/Rejected; Phase 9 D9) also counts, read
/// with the Branch filter bypassed (company-wide integrity). The location row is locked FOR UPDATE first (D5), so the check waits for in-flight postings (which hold KEY
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
        var hasOpenTransfers = await db.StockTransfers.IgnoreQueryFilters([BranchQueryFilter.Name]).AsNoTracking()
            .AnyAsync(t => t.CompanyId == companyId
                && (t.SourceLocationId == stockLocationId || t.DestinationLocationId == stockLocationId)
                && t.Status != StockTransferStatus.Completed && t.Status != StockTransferStatus.Cancelled && t.Status != StockTransferStatus.Rejected, ct);
        return new StockLocationUsage(hasStock, hasOpenTransfers);
    }
}
