using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Exclusive lock on the current company row (Phase 4 D5): serialises branch, warehouse and vehicle writes and the settings PUT.
/// FOR NO KEY UPDATE (not FOR UPDATE) so it does not conflict with the KEY SHARE that every insert's CompanyId foreign key takes:
/// a posting holding a location KEY SHARE would otherwise deadlock against location admin waiting for that location (Phase 8b).
/// </summary>
internal static class CompanyLocks
{
    // No-op on the in-memory provider.
    public static async Task<Guid> LockCurrentCompanyAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        if (db.Database.IsRelational())
        {
            await db.Database
                .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Companies\" WHERE \"Id\" = {companyId} FOR NO KEY UPDATE")
                .ToListAsync(ct);
        }
        return companyId;
    }
}
