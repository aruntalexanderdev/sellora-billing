using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class ExchangeRateService(SelloraDbContext db) : IExchangeRateService
{
    public async Task<decimal> ResolveRateToBaseAsync(Guid currencyId, DateOnly asOfDate, CancellationToken ct = default)
    {
        var baseCurrencyId = await db.BusinessSettings.Select(s => s.BaseCurrencyId).SingleAsync(ct);
        if (currencyId == baseCurrencyId) return 1m;

        var rate = await db.ExchangeRates
            .Where(r => r.CurrencyId == currencyId && r.EffectiveDate <= asOfDate)
            .OrderByDescending(r => r.EffectiveDate)
            .Select(r => (decimal?)r.RateToBase)
            .FirstOrDefaultAsync(ct);

        return rate ?? throw new DomainValidationException(
            "CurrencyId", "No exchange rate is configured for this currency on or before the selected date.");
    }
}
