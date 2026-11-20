using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;

namespace Sellora.Infrastructure.Services;

/// <summary>Guard and audit shared by <see cref="CountryService"/> and <see cref="StateService"/> (D8, D29). Unlike CurrencyService, company writes
/// are audited too, and an actor without a real identity is rejected so no audit row is ever anonymous.</summary>
internal static class GeoWriteRules
{
    public static void EnsureMayWrite(ReferenceDataActor actor, IOptions<PlatformOptions>? options)
    {
        if (!actor.IsPlatform && options is { Value.ReferenceDataEditableByCompanies: false }) throw new ReferenceDataPlatformManagedException();
        if (actor.UserId is null) throw new InvalidOperationException("A reference-data write requires the acting user id.");
        if (!actor.IsPlatform && actor.CompanyId is null) throw new InvalidOperationException("A company reference-data write requires the company id.");
    }

    public static void Audit(SelloraDbContext db, ReferenceDataActor actor, string action, string target, Guid id, object? before, object? after) =>
        PlatformAuditLog.Append(db, action, target, id, actor.UserId, actor.IsPlatform ? null : actor.CompanyId, null, before, after, actor.CorrelationId);

    public static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed partial class CountryService(SelloraDbContext db, IOptions<PlatformOptions>? platformOptions = null) : ICountryService
{
    public async Task<Country> CreateAsync(CreateCountryInput input, ReferenceDataActor actor, CancellationToken ct = default)
    {
        GeoWriteRules.EnsureMayWrite(actor, platformOptions);
        var (code, iso3, name) = Normalise(input.Code, input.Iso3Code, input.Name);
        await EnsureUniqueAsync(null, code, iso3, name, ct);

        var country = new Country { Id = Guid.NewGuid(), Code = code, Iso3Code = iso3, Name = name, IsActive = input.IsActive, IsSystem = false };
        db.Countries.Add(country);
        GeoWriteRules.Audit(db, actor, PlatformAuditActions.CountryCreated, PlatformAuditTargets.Country, country.Id, null, Snapshot(country));
        await db.SaveChangesAsync(ct);
        return country;
    }

    public async Task<Country?> UpdateAsync(Guid id, UpdateCountryInput input, ReferenceDataActor actor, CancellationToken ct = default)
    {
        GeoWriteRules.EnsureMayWrite(actor, platformOptions);
        var country = await db.Countries.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (country is null) return null;

        var (code, iso3, name) = Normalise(input.Code, input.Iso3Code, input.Name);
        if (country.IsSystem)
        {
            if (code != country.Code) throw new DomainValidationException("code", "The code of a system country cannot be changed.");
            if (!string.Equals(iso3, country.Iso3Code, StringComparison.Ordinal)) throw new DomainValidationException("iso3Code", "The ISO3 code of a system country cannot be changed.");
        }
        await EnsureUniqueAsync(id, code, iso3, name, ct);

        var before = Snapshot(country);
        country.Code = code;
        country.Iso3Code = iso3;
        country.Name = name;
        GeoWriteRules.Audit(db, actor, PlatformAuditActions.CountryUpdated, PlatformAuditTargets.Country, country.Id, before, Snapshot(country));
        await db.SaveChangesAsync(ct);
        return country;
    }

    public async Task<GeoStatusOutcome> SetActiveAsync(Guid id, bool isActive, ReferenceDataActor actor, CancellationToken ct = default)
    {
        GeoWriteRules.EnsureMayWrite(actor, platformOptions);
        var country = await db.Countries.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (country is null) return GeoStatusOutcome.NotFound;
        if (country.IsActive == isActive) return GeoStatusOutcome.Changed; // no-op: no write, no audit

        var was = country.IsActive;
        country.IsActive = isActive; // BR6: never cascades to states or to existing address links
        GeoWriteRules.Audit(db, actor, isActive ? PlatformAuditActions.CountryActivated : PlatformAuditActions.CountryDeactivated,
            PlatformAuditTargets.Country, country.Id, new { IsActive = was }, new { country.IsActive });
        await db.SaveChangesAsync(ct);
        return GeoStatusOutcome.Changed;
    }

    private static (string Code, string? Iso3, string Name) Normalise(string? code, string? iso3, string? name)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!Alpha2().IsMatch(c)) throw new DomainValidationException("code", "Code must be 2 letters (ISO 3166-1 alpha-2).");
        var i = GeoWriteRules.Blank(iso3)?.ToUpperInvariant();
        if (i is not null && !Alpha3().IsMatch(i)) throw new DomainValidationException("iso3Code", "ISO3 code must be 3 letters.");
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > 100) throw new DomainValidationException("name", "Enter a name of up to 100 characters.");
        return (c, i, n);
    }

    private async Task EnsureUniqueAsync(Guid? selfId, string code, string? iso3, string name, CancellationToken ct)
    {
        if (await db.Countries.AnyAsync(c => c.Id != selfId && c.Code == code, ct))
            throw new DomainValidationException("code", "A country with this code already exists.");
        if (iso3 is not null && await db.Countries.AnyAsync(c => c.Id != selfId && c.Iso3Code != null && c.Iso3Code.ToUpper() == iso3, ct))
            throw new DomainValidationException("iso3Code", "A country with this ISO3 code already exists.");
        var lower = name.ToLower();
        if (await db.Countries.AnyAsync(c => c.Id != selfId && c.Name.ToLower() == lower, ct))
            throw new DomainValidationException("name", "A country with this name already exists.");
    }

    private static object Snapshot(Country c) => new { c.Code, c.Iso3Code, c.Name, c.IsActive };

    [GeneratedRegex("^[A-Z]{2}$")] private static partial Regex Alpha2();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex Alpha3();
}

public sealed partial class StateService(SelloraDbContext db, IOptions<PlatformOptions>? platformOptions = null) : IStateService
{
    private const string IndiaCode = "IN";

    public async Task<State> CreateAsync(CreateStateInput input, ReferenceDataActor actor, CancellationToken ct = default)
    {
        GeoWriteRules.EnsureMayWrite(actor, platformOptions);
        var country = await db.Countries.AsNoTracking().SingleOrDefaultAsync(c => c.Id == input.CountryId, ct)
            ?? throw new DomainValidationException("countryId", "Select a valid country.");
        var (code, name, gst) = Normalise(input.Code, input.Name, input.Kind, input.GstStateCode, country);
        // BR6: AC11 wins over the contract table: an inactive country is accepted only for an inactive state; an active one is 400 isActive.
        if (input.IsActive && !country.IsActive) throw new DomainValidationException("isActive", "A state cannot be active under an inactive country.");
        await EnsureUniqueAsync(null, country.Id, code, name, gst, ct);

        var state = new State { Id = Guid.NewGuid(), CountryId = country.Id, Code = code, Name = name, Kind = input.Kind, GstStateCode = gst, IsActive = input.IsActive, IsSystem = false };
        db.States.Add(state);
        GeoWriteRules.Audit(db, actor, PlatformAuditActions.StateCreated, PlatformAuditTargets.State, state.Id, null, Snapshot(state));
        await db.SaveChangesAsync(ct);
        return state;
    }

    public async Task<State?> UpdateAsync(Guid id, UpdateStateInput input, ReferenceDataActor actor, CancellationToken ct = default)
    {
        GeoWriteRules.EnsureMayWrite(actor, platformOptions);
        var state = await db.States.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (state is null) return null;
        var country = await db.Countries.AsNoTracking().SingleAsync(c => c.Id == state.CountryId, ct);

        var (code, name, gst) = Normalise(input.Code, input.Name, input.Kind, input.GstStateCode, country);
        if (state.IsSystem)
        {
            if (code != state.Code) throw new DomainValidationException("code", "The code of a system state cannot be changed.");
            if (!string.Equals(gst, state.GstStateCode, StringComparison.Ordinal)) throw new DomainValidationException("gstStateCode", "The GST state code of a system state cannot be changed.");
        }
        await EnsureUniqueAsync(id, state.CountryId, code, name, gst, ct);

        var before = Snapshot(state);
        state.Code = code;
        state.Name = name;
        state.Kind = input.Kind;
        state.GstStateCode = gst;
        GeoWriteRules.Audit(db, actor, PlatformAuditActions.StateUpdated, PlatformAuditTargets.State, state.Id, before, Snapshot(state));
        await db.SaveChangesAsync(ct);
        return state;
    }

    public async Task<GeoStatusOutcome> SetActiveAsync(Guid id, bool isActive, ReferenceDataActor actor, CancellationToken ct = default)
    {
        GeoWriteRules.EnsureMayWrite(actor, platformOptions);
        var state = await db.States.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (state is null) return GeoStatusOutcome.NotFound;
        if (state.IsActive == isActive) return GeoStatusOutcome.Changed; // no-op: no write, no audit

        if (isActive && !await db.Countries.AnyAsync(c => c.Id == state.CountryId && c.IsActive, ct))
        {
            throw new DomainValidationException("isActive", "A state cannot be activated while its country is inactive.");
        }
        var was = state.IsActive;
        state.IsActive = isActive;
        GeoWriteRules.Audit(db, actor, isActive ? PlatformAuditActions.StateActivated : PlatformAuditActions.StateDeactivated,
            PlatformAuditTargets.State, state.Id, new { IsActive = was }, new { state.IsActive });
        await db.SaveChangesAsync(ct);
        return GeoStatusOutcome.Changed;
    }

    private static (string Code, string Name, string? Gst) Normalise(string? code, string? name, StateKind kind, string? gstStateCode, Country country)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!StateCode().IsMatch(c)) throw new DomainValidationException("code", "Code must be 1 to 3 letters or digits.");
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > 100) throw new DomainValidationException("name", "Enter a name of up to 100 characters.");
        if (!Enum.IsDefined(kind)) throw new DomainValidationException("kind", "Select a valid kind.");
        var gst = GeoWriteRules.Blank(gstStateCode);
        if (gst is not null)
        {
            if (country.Code != IndiaCode) throw new DomainValidationException("gstStateCode", "A GST state code applies to Indian states only.");
            if (!Gst().IsMatch(gst)) throw new DomainValidationException("gstStateCode", "GST state code must be 2 digits.");
        }
        return (c, n, gst);
    }

    private async Task EnsureUniqueAsync(Guid? selfId, Guid countryId, string code, string name, string? gst, CancellationToken ct)
    {
        if (await db.States.AnyAsync(s => s.Id != selfId && s.CountryId == countryId && s.Code == code, ct))
            throw new DomainValidationException("code", "A state with this code already exists in this country.");
        var lower = name.ToLower();
        if (await db.States.AnyAsync(s => s.Id != selfId && s.CountryId == countryId && s.Name.ToLower() == lower, ct))
            throw new DomainValidationException("name", "A state with this name already exists in this country.");
        // Only Indian states carry a code (BR4), so a global check equals "unique within India".
        if (gst is not null && await db.States.AnyAsync(s => s.Id != selfId && s.GstStateCode == gst, ct))
            throw new DomainValidationException("gstStateCode", "This GST state code is already used by another state.");
    }

    private static object Snapshot(State s) => new { s.CountryId, s.Code, s.Name, Kind = s.Kind.ToString(), s.GstStateCode, s.IsActive };

    [GeneratedRegex("^[A-Z0-9]{1,3}$")] private static partial Regex StateCode();
    [GeneratedRegex("^[0-9]{2}$")] private static partial Regex Gst();
}
