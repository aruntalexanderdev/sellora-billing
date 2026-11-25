using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Standard tax components and profiles for a company's country (T12): GST for India, VAT for AE/SA/BH/OM. Same rows and ids as the
/// frozen migration SQL (<see cref="TaxSeedData"/> is the single C# source). A profile is skipped when the company already has a profile
/// of that country with the same code or the same name (case-insensitive); skipped seeds are counted. The mirror tax of a profile gets the profile
/// name, or the name with the country code when another tax of the company already has it (deviation from the SQL: multi-country companies).
/// Adds entities to the context and does not save: the caller saves (company creation, API, DbSeeder).
/// </summary>
public static class TaxSeeder
{
    public static async Task<TaxSeedResult> SeedCompanyAsync(SelloraDbContext db, Guid companyId, Guid? countryId, TimeProvider? time = null, CancellationToken ct = default)
    {
        if (countryId is not { } cid) return TaxSeedResult.None;
        var country = await db.Countries.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid, ct);
        if (country is null || !TaxSeedData.SeededCountryCodes.Contains(country.Code)) return TaxSeedResult.None;

        var cc = country.Code;
        var today = TaxRules.Today(time ?? TimeProvider.System);
        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        // Components: by code (or name) per company and country; an existing one is reused.
        var components = await db.TaxComponents.Where(c => c.CountryId == cid).ToListAsync(ct);
        var byCode = components.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var componentsAdded = 0;
        foreach (var seed in TaxSeedData.ComponentsFor(cc))
        {
            if (byCode.ContainsKey(seed.Code) || components.Any(c => string.Equals(c.Name, seed.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var component = new TaxComponent
            {
                Id = TaxSeedData.ComponentId(companyId, cc, seed.Code), CompanyId = companyId, CountryId = cid, Code = seed.Code, Name = seed.Name,
                Kind = seed.Kind, Method = seed.Method, IsActive = true, CreatedAt = now,
            };
            db.TaxComponents.Add(component);
            components.Add(component);
            byCode[seed.Code] = component;
            componentsAdded++;
        }

        var profiles = await db.TaxProfiles.Where(p => p.CountryId == cid).ToListAsync(ct);
        var takenTaxNames = (await db.Taxes.AsNoTracking().Select(t => t.Name).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        var skipped = 0;
        foreach (var seed in TaxSeedData.ProfilesFor(cc))
        {
            if (profiles.Any(p => p.Code == seed.Code || string.Equals(p.Name, seed.Name, StringComparison.OrdinalIgnoreCase))
                || !seed.Components.All(vc => byCode.TryGetValue(vc.ComponentCode, out var c) && c.Method == TaxCalculationMethod.Percent && c.IsActive))
            {
                skipped++;
                continue;
            }

            var mirror = new Tax
            {
                Id = TaxSeedData.MirrorTaxId(companyId, cc, seed.Code), CompanyId = companyId,
                Name = TaxRules.MirrorName(seed.Name, cc, takenTaxNames), RatePercent = seed.EffectiveFrom <= today ? seed.TotalPercent : 0m, IsActive = true,
            };
            var profile = new TaxProfile
            {
                Id = TaxSeedData.ProfileId(companyId, cc, seed.Code), CompanyId = companyId, CountryId = cid, Code = seed.Code, Name = seed.Name,
                Regime = seed.Regime, Treatment = seed.Treatment, LegacyTaxId = mirror.Id, IsActive = true, CreatedAt = now,
            };
            var version = new TaxProfileVersion
            {
                Id = TaxSeedData.VersionId(companyId, cc, seed.Code, seed.EffectiveFrom), CompanyId = companyId, TaxProfileId = profile.Id,
                EffectiveFrom = seed.EffectiveFrom, EffectiveTo = null, TotalPercent = seed.TotalPercent, CreatedAt = now, CreatedByUserId = null,
            };
            foreach (var vc in seed.Components)
            {
                var component = byCode[vc.ComponentCode];
                version.Components.Add(new TaxProfileVersionComponent
                {
                    Id = TaxSeedData.VersionComponentId(version.Id, vc.ComponentCode, vc.Applicability), CompanyId = companyId, TaxProfileVersionId = version.Id,
                    TaxComponentId = component.Id, Method = component.Method, Applicability = vc.Applicability,
                    RatePercent = vc.RatePercent, AmountPerBaseUnit = vc.AmountPerBaseUnit, SortOrder = vc.SortOrder,
                });
            }
            profile.Versions.Add(version);
            db.Taxes.Add(mirror);
            db.TaxProfiles.Add(profile);
            profiles.Add(profile);
            added++;
        }

        // T12/T21: the standard rate becomes the company default, unless the company already has one for the country.
        var defaultSet = false;
        var standard = TaxSeedData.DefaultFor(cc);
        if (standard is not null && !await db.TaxAssignments.AnyAsync(a => a.Scope == TaxScope.Company && a.CountryId == cid, ct)
            && !db.TaxAssignments.Local.Any(a => a.Scope == TaxScope.Company && a.CountryId == cid))
        {
            var chosen = profiles.Where(p => p.Code == standard.Code || string.Equals(p.Name, standard.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Code == standard.Code).ThenBy(p => p.Id).FirstOrDefault();
            if (chosen is not null)
            {
                db.TaxAssignments.Add(new TaxAssignment
                {
                    Id = TaxSeedData.CompanyDefaultAssignmentId(companyId, cc), CompanyId = companyId, Scope = TaxScope.Company, CountryId = cid,
                    SalesTaxProfileId = chosen.Id, PurchaseTaxProfileId = null, CreatedAt = now,
                });
                defaultSet = true;
            }
        }

        return new TaxSeedResult(true, componentsAdded, added, skipped, defaultSet);
    }
}
