using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Shared FOR UPDATE lock on the current company row (Phase 4 D5): serialises branch, warehouse and vehicle writes and the settings PUT.</summary>
internal static class CompanyLocks
{
    // No-op on the in-memory provider.
    public static async Task<Guid> LockCurrentCompanyAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        if (db.Database.IsRelational())
        {
            await db.Database
                .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Companies\" WHERE \"Id\" = {companyId} FOR UPDATE")
                .ToListAsync(ct);
        }
        return companyId;
    }
}
