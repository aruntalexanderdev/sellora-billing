using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The only place (besides the seeder and SettingsController) that reads <c>BusinessSettings</c>. Each company has exactly one
/// settings row; reads carry an explicit CompanyId predicate in addition to the global query filter (ADR-07).
/// </summary>
public static class CompanySettings
{
    public static async Task<CompanySettingsSnapshot> GetCurrentCompanySettingsAsync(this SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return await db.BusinessSettings
            .Where(s => s.CompanyId == companyId)
            .Select(s => new CompanySettingsSnapshot(
                s.CompanyId, s.BusinessName, s.InvoicePrefix,
                s.BaseCurrencyId, s.BaseCurrency.Code, s.BaseCurrency.Symbol, s.BaseCurrency.DecimalPrecision))
            .SingleAsync(ct);
    }

    /// <summary>The current company's base currency (tracked entity, like the previous <c>Select(s =&gt; s.BaseCurrency)</c>).</summary>
    public static async Task<Currency> GetBaseCurrencyAsync(this SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return await db.BusinessSettings
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.BaseCurrency)
            .SingleAsync(ct);
    }
}
