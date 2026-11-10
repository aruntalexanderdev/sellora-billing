using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Writes of the platform-shared Currencies table (Phase 7 D11/Q3). Company callers are refused while
/// <c>Platform:ReferenceDataEditableByCompanies</c> is false; platform callers are allowed and every platform write appends a
/// <see cref="PlatformAuditEvent"/> in the same SaveChanges. A null options value (unit tests) applies no flag guard.
/// </summary>
public class CurrencyService(SelloraDbContext db, IOptions<PlatformOptions>? platformOptions = null) : ICurrencyService
{
    private const string DuplicateMessage = "A currency with this code already exists.";

    public async Task<Currency> CreateAsync(UpsertCurrencyInput input, ReferenceDataActor actor, CancellationToken ct = default)
    {
        EnsureMayWrite(actor);
        var code = input.Code.Trim().ToUpperInvariant();
        if (await db.Currencies.AnyAsync(c => c.Code == code, ct))
        {
            throw new DomainValidationException(nameof(input.Code), DuplicateMessage);
        }

        var currency = new Currency
        {
            Id = Guid.NewGuid(), Code = code, Name = input.Name.Trim(), Symbol = input.Symbol.Trim(), DecimalPrecision = input.DecimalPrecision,
        };
        db.Currencies.Add(currency);
        Audit(actor, PlatformAuditActions.CurrencyCreated, currency.Id, null, Snapshot(currency));
        await db.SaveChangesAsync(ct);
        return currency;
    }

    public async Task<Currency?> UpdateAsync(Guid id, UpsertCurrencyInput input, ReferenceDataActor actor, CancellationToken ct = default)
    {
        EnsureMayWrite(actor);
        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (currency is null) return null;

        var code = input.Code.Trim().ToUpperInvariant();
        if (await db.Currencies.AnyAsync(c => c.Id != id && c.Code == code, ct))
        {
            throw new DomainValidationException(nameof(input.Code), DuplicateMessage);
        }

        var before = Snapshot(currency);
        currency.Code = code;
        currency.Name = input.Name.Trim();
        currency.Symbol = input.Symbol.Trim();
        currency.DecimalPrecision = input.DecimalPrecision;
        Audit(actor, PlatformAuditActions.CurrencyUpdated, currency.Id, before, Snapshot(currency));
        await db.SaveChangesAsync(ct);
        return currency;
    }

    public async Task<CurrencyStatusOutcome> SetActiveAsync(Guid id, bool isActive, ReferenceDataActor actor, CancellationToken ct = default)
    {
        EnsureMayWrite(actor);
        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (currency is null) return CurrencyStatusOutcome.NotFound;

        // Documented IgnoreQueryFilters bypass: a currency that is ANY company's base currency cannot be deactivated,
        // so this must see every company's settings, not just the current one.
        if (!isActive && await db.BusinessSettings.IgnoreQueryFilters([CompanyQueryFilter.Name]).AnyAsync(s => s.BaseCurrencyId == id, ct))
        {
            return CurrencyStatusOutcome.IsBaseCurrency;
        }

        var wasActive = currency.IsActive;
        currency.IsActive = isActive;
        if (wasActive != isActive) Audit(actor, PlatformAuditActions.CurrencyStatusChanged, currency.Id, new { IsActive = wasActive }, new { currency.IsActive });
        await db.SaveChangesAsync(ct);
        return CurrencyStatusOutcome.Changed;
    }

    private void EnsureMayWrite(ReferenceDataActor actor)
    {
        if (actor.IsPlatform) return;
        if (platformOptions is { Value.ReferenceDataEditableByCompanies: false }) throw new ReferenceDataPlatformManagedException();
    }

    private void Audit(ReferenceDataActor actor, string action, Guid currencyId, object? before, object? after)
    {
        if (!actor.IsPlatform) return;
        PlatformAuditLog.Append(db, action, PlatformAuditTargets.Currency, currencyId, actor.UserId, null, null, before, after, actor.CorrelationId);
    }

    private static object Snapshot(Currency c) => new { c.Code, c.Name, c.Symbol, c.DecimalPrecision, c.IsActive };
}
