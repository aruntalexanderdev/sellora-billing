using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Tax profiles and their dated versions (BR2–BR6, TP-1 design). Version writes lock the profile row <c>FOR UPDATE</c> (D5, no exclusion constraint),
/// are append-only, and keep the mirror <see cref="Tax"/> row (D3) at the total of the version valid today. All writes run in one transaction.
/// </summary>
public class TaxProfileService(SelloraDbContext db, TimeProvider? time = null) : ITaxProfileService
{
    private const string CodeIndex = "IX_TaxProfiles_CompanyId_CountryId_Code";
    private const string NameIndex = "UX_TaxProfiles_Country_Name_Lower";
    private const string MirrorNameIndex = "IX_Taxes_CompanyId_Name";
    private const decimal MaxTotalPercent = 100m;

    private TimeProvider Clock => time ?? TimeProvider.System;

    // ------------------------------------------------------------------ profile

    public async Task<TaxProfile> CreateAsync(CreateTaxProfileInput input, Guid? userId, CancellationToken ct = default)
    {
        var code = TaxRules.NormaliseCode(input.Code);
        var name = TaxRules.NormaliseName(input.Name);
        if (!Enum.IsDefined(input.Regime)) throw new DomainValidationException("regime", "Select a valid regime.");
        if (!Enum.IsDefined(input.Treatment)) throw new DomainValidationException("treatment", "Select a valid treatment.");
        var country = await db.Countries.AsNoTracking().SingleOrDefaultAsync(c => c.Id == input.CountryId, ct)
            ?? throw new DomainValidationException("countryId", "Select a valid country.");
        var expected = TaxRules.RegimeFor(country.Code);
        if (input.Regime != expected)
        {
            throw new DomainValidationException("regime", $"{country.Name} uses {(expected == TaxRegime.Gst ? "GST" : "VAT")}; a profile there must have that regime.");
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await EnsureUniqueAsync(null, input.CountryId, code, name, ct);

        var mirror = new Tax { Id = Guid.NewGuid(), Name = await MirrorNameAsync(name, country.Code, null, ct), RatePercent = 0m, IsActive = true };
        var profile = new TaxProfile
        {
            Id = Guid.NewGuid(), CountryId = input.CountryId, Code = code, Name = name, Regime = input.Regime, Treatment = input.Treatment,
            LegacyTaxId = mirror.Id, IsActive = true, CreatedAt = Clock.GetUtcNow().UtcDateTime,
        };
        db.Taxes.Add(mirror);
        db.TaxProfiles.Add(profile);

        if (input.InitialVersion is { } initial)
        {
            await AddVersionCoreAsync(profile, initial, userId, ct);
        }
        else
        {
            await SaveAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return profile;
    }

    public async Task<TaxProfile?> UpdateAsync(Guid id, UpdateTaxProfileInput input, CancellationToken ct = default)
    {
        var profile = await db.TaxProfiles.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return null;

        var code = TaxRules.NormaliseCode(input.Code);
        var name = TaxRules.NormaliseName(input.Name);
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await EnsureUniqueAsync(id, profile.CountryId, code, name, ct);

        profile.Code = code;
        if (!string.Equals(profile.Name, name, StringComparison.Ordinal))
        {
            profile.Name = name;
            var countryCode = await db.Countries.AsNoTracking().Where(c => c.Id == profile.CountryId).Select(c => c.Code).SingleAsync(ct);
            var mirror = await db.Taxes.SingleAsync(t => t.Id == profile.LegacyTaxId, ct);
            mirror.Name = await MirrorNameAsync(name, countryCode, mirror.Id, ct);
        }
        await SaveAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return profile;
    }

    public async Task<TaxProfile?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await TaxLocks.LockAssignmentsAsync(db, ct);
        var profile = await db.TaxProfiles.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return null;

        if (profile.IsActive != isActive)
        {
            if (!isActive && await db.TaxAssignments.AnyAsync(a => a.SalesTaxProfileId == id || a.PurchaseTaxProfileId == id, ct))
            {
                throw new DomainValidationException("isActive", $"{profile.Name} is still assigned to the company, a category, a subcategory or a product. Assign another profile first.");
            }
            profile.IsActive = isActive;
            var mirror = await db.Taxes.SingleAsync(t => t.Id == profile.LegacyTaxId, ct);
            mirror.IsActive = isActive;
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return profile;
    }

    public async Task<TaxSeedResult> SeedStandardAsync(CancellationToken ct = default)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var countryId = await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct);
        if (countryId is null) throw new DomainValidationException("taxProfile", "Set the company's country in the business settings first.");

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await TaxLocks.LockAssignmentsAsync(db, ct);
        var result = await TaxSeeder.SeedCompanyAsync(db, companyId, countryId, Clock, ct);
        await SaveAsync(ct);
        if (result.CompanyDefaultSet)
        {
            // Products without an own assignment now inherit the new default.
            if (await TaxMirror.SyncAsync(db, null, ct) > 0) await db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return result;
    }

    // ------------------------------------------------------------------ versions

    public async Task<TaxProfileVersion?> AddVersionAsync(Guid profileId, AddTaxVersionInput input, Guid? userId, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await TaxLocks.LockProfileAsync(db, profileId, ct);
        var profile = await db.TaxProfiles.SingleOrDefaultAsync(p => p.Id == profileId, ct);
        if (profile is null) return null;

        var version = await AddVersionCoreAsync(profile, input, userId, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return version;
    }

    private async Task<TaxProfileVersion> AddVersionCoreAsync(TaxProfile profile, AddTaxVersionInput input, Guid? userId, CancellationToken ct)
    {
        var today = TaxRules.Today(Clock);
        var now = Clock.GetUtcNow().UtcDateTime;
        var errors = new List<FieldError>();

        // T19 is lifted (TP-4): sales and purchases both resolve the version valid on the document date, so a version may start in the future.

        var (lines, total) = await ValidateComponentsAsync(profile, input.Components, errors, ct);

        var existing = await db.TaxProfileVersions.Include(v => v.Components).Where(v => v.TaxProfileId == profile.Id).ToListAsync(ct);
        var latest = existing.OrderByDescending(v => v.EffectiveFrom).FirstOrDefault();
        var replace = false;
        if (latest is not null)
        {
            if (input.EffectiveFrom < latest.EffectiveFrom)
            {
                errors.Add(new FieldError("effectiveFrom",
                    $"The version must start on or after {latest.EffectiveFrom:yyyy-MM-dd}, the start of the latest version. Earlier rates cannot be rewritten."));
            }
            else if (input.EffectiveFrom == latest.EffectiveFrom)
            {
                if (await IsVersionUsedAsync(profile, latest, ct))
                {
                    errors.Add(new FieldError("effectiveFrom",
                        $"The version from {latest.EffectiveFrom:yyyy-MM-dd} is already used by documents and cannot be changed. Add a new version from a later date."));
                }
                replace = true;
            }
        }
        if (latest is not null && !errors.Any(e => e.Field == "effectiveFrom")
            && await TaxVersionUsage.LatestDocumentDateAsync(db, profile.LegacyTaxId, ct) is { } lastUsed && input.EffectiveFrom <= lastUsed)
        {
            // T25 / BR4: a used version's dates are immutable, so the one that replaces it must start after the last document that used the profile.
            errors.Add(new FieldError("effectiveFrom", $"Documents dated up to {lastUsed:yyyy-MM-dd} already use this profile. The new version must start after {lastUsed:yyyy-MM-dd}."));
        }
        if (errors.Count > 0) throw new DomainValidationException(errors);

        TaxProfileVersion version;
        if (replace)
        {
            // BR4: same date and nothing uses the old version: replace its components. Two saves because the unique index on (version, component, applicability)
            // would otherwise see the new rows before the old ones are gone.
            version = latest!;
            db.TaxProfileVersionComponents.RemoveRange(version.Components.ToList());
            await SaveAsync(ct);
            version.TotalPercent = total;
            version.CreatedAt = now;
            version.CreatedByUserId = userId;
        }
        else
        {
            if (latest is not null)
            {
                // BR4: the previous version ends the day before. Saved first: at most one open version per profile.
                latest.EffectiveTo = input.EffectiveFrom.AddDays(-1);
                await SaveAsync(ct);
            }
            version = new TaxProfileVersion
            {
                Id = Guid.NewGuid(), TaxProfileId = profile.Id, EffectiveFrom = input.EffectiveFrom, EffectiveTo = null,
                TotalPercent = total, CreatedAt = now, CreatedByUserId = userId,
            };
            db.TaxProfileVersions.Add(version);
        }

        var sort = 0;
        foreach (var (line, component) in lines)
        {
            db.TaxProfileVersionComponents.Add(new TaxProfileVersionComponent
            {
                Id = Guid.NewGuid(), TaxProfileVersionId = version.Id, TaxComponentId = component.Id, Method = component.Method,
                Applicability = line.Applicability, RatePercent = line.RatePercent, AmountPerBaseUnit = line.AmountPerBaseUnit, SortOrder = ++sort,
            });
        }
        await SaveAsync(ct);

        await SyncMirrorAsync(profile, today, ct);
        await db.Entry(version).Collection(v => v.Components).Query().Include(c => c.TaxComponent).LoadAsync(ct);
        return version;
    }

    /// <summary>The mirror tax follows the version valid today (D3): rate = its total, active = the profile's.</summary>
    private async Task SyncMirrorAsync(TaxProfile profile, DateOnly today, CancellationToken ct)
    {
        var current = await db.TaxProfileVersions.AsNoTracking()
            .Where(v => v.TaxProfileId == profile.Id && v.EffectiveFrom <= today && (v.EffectiveTo == null || v.EffectiveTo >= today))
            .Select(v => (decimal?)v.TotalPercent).FirstOrDefaultAsync(ct);
        var mirror = await db.Taxes.SingleAsync(t => t.Id == profile.LegacyTaxId, ct);
        mirror.RatePercent = current ?? 0m;
        mirror.IsActive = profile.IsActive;
        await db.SaveChangesAsync(ct);
    }

    internal Task<bool> IsVersionUsedAsync(TaxProfile profile, TaxProfileVersion version, CancellationToken ct) =>
        TaxVersionUsage.IsUsedAsync(db, profile.LegacyTaxId, version.EffectiveFrom, version.EffectiveTo, ct);

    // ------------------------------------------------------------------ validation of a version's component set (BR3)

    private async Task<(List<(TaxVersionComponentInput Line, TaxComponent Component)> Lines, decimal Total)> ValidateComponentsAsync(
        TaxProfile profile, IReadOnlyList<TaxVersionComponentInput> inputs, List<FieldError> errors, CancellationToken ct)
    {
        var lines = new List<(TaxVersionComponentInput, TaxComponent)>();
        var firstError = errors.Count;
        var ids = inputs.Select(i => i.TaxComponentId).Distinct().ToArray();
        var components = await db.TaxComponents.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var seen = new HashSet<(Guid, TaxApplicability)>();
        var regime = profile.Regime;

        for (var i = 0; i < inputs.Count; i++)
        {
            var line = inputs[i];
            var at = $"components[{i}]";
            if (!components.TryGetValue(line.TaxComponentId, out var component))
            {
                errors.Add(new FieldError($"{at}.taxComponentId", "Select a valid component."));
                continue;
            }
            if (component.CountryId != profile.CountryId) errors.Add(new FieldError($"{at}.taxComponentId", $"{component.Code} belongs to another country than the profile."));
            if (!component.IsActive) errors.Add(new FieldError($"{at}.taxComponentId", $"{component.Code} is inactive."));
            if (!Enum.IsDefined(line.Applicability)) errors.Add(new FieldError($"{at}.applicability", "Select when the component applies."));
            if (!seen.Add((component.Id, line.Applicability))) errors.Add(new FieldError($"{at}.taxComponentId", $"{component.Code} is listed twice with the same applicability."));

            if (component.Method == TaxCalculationMethod.Percent)
            {
                if (line.RatePercent is not { } rate) errors.Add(new FieldError($"{at}.ratePercent", $"Enter the rate of {component.Code}."));
                else if (rate < 0 || rate > 100) errors.Add(new FieldError($"{at}.ratePercent", "The rate must be between 0 and 100."));
                else if (TaxRules.DecimalPlaces(rate) > 4) errors.Add(new FieldError($"{at}.ratePercent", "The rate allows at most 4 decimal places."));
                if (line.AmountPerBaseUnit is not null) errors.Add(new FieldError($"{at}.amountPerBaseUnit", $"{component.Code} is a percentage; it has no amount per unit."));
            }
            else
            {
                if (line.AmountPerBaseUnit is not { } amount) errors.Add(new FieldError($"{at}.amountPerBaseUnit", $"Enter the amount per unit of {component.Code}."));
                else if (amount < 0 || amount >= 1_000_000_000_000m) errors.Add(new FieldError($"{at}.amountPerBaseUnit", "The amount per unit must be zero or more."));
                else if (TaxRules.DecimalPlaces(amount) > 6) errors.Add(new FieldError($"{at}.amountPerBaseUnit", "The amount per unit allows at most 6 decimal places."));
                if (line.RatePercent is not null) errors.Add(new FieldError($"{at}.ratePercent", $"{component.Code} is an amount per unit; it has no percentage."));
            }

            var kindError = KindError(regime, component, line.Applicability);
            if (kindError is not null) errors.Add(new FieldError($"{at}.applicability", kindError));
            lines.Add((line, component));
        }

        var total = lines.Where(l => l.Item2.Method == TaxCalculationMethod.Percent && l.Item1.Applicability is TaxApplicability.IntraState or TaxApplicability.Always)
            .Sum(l => l.Item1.RatePercent ?? 0m);

        if (profile.Treatment != TaxTreatment.Taxable)
        {
            if (inputs.Count > 0) errors.Add(new FieldError("components", "A nil rated, exempt, zero-rated or non-taxable profile carries no tax components."));
        }
        else if (inputs.Count == 0)
        {
            errors.Add(new FieldError("components", "A taxable profile needs at least one tax component."));
        }
        else if (errors.Count == firstError)
        {
            ValidateSet(profile, lines, total, errors);
        }
        return (lines, total);
    }

    private static string? KindError(TaxRegime regime, TaxComponent component, TaxApplicability applicability)
    {
        switch (component.Kind)
        {
            case TaxComponentKind.Cgst or TaxComponentKind.Sgst or TaxComponentKind.Utgst:
                if (regime != TaxRegime.Gst) return $"{component.Code} belongs to a GST profile.";
                if (applicability != TaxApplicability.IntraState) return $"{component.Code} applies to intra-state supplies only.";
                if (component.Method != TaxCalculationMethod.Percent) return $"{component.Code} must be a percentage component.";
                break;
            case TaxComponentKind.Igst:
                if (regime != TaxRegime.Gst) return $"{component.Code} belongs to a GST profile.";
                if (applicability != TaxApplicability.InterState) return $"{component.Code} applies to inter-state supplies only.";
                if (component.Method != TaxCalculationMethod.Percent) return $"{component.Code} must be a percentage component.";
                break;
            case TaxComponentKind.Vat:
                if (regime != TaxRegime.Vat) return $"{component.Code} belongs to a VAT profile.";
                if (applicability != TaxApplicability.Always) return $"{component.Code} applies to every supply.";
                if (component.Method != TaxCalculationMethod.Percent) return $"{component.Code} must be a percentage component.";
                break;
            default: // Cess, Other
                if (applicability != TaxApplicability.Always) return $"{component.Code} applies to every supply.";
                break;
        }
        return null;
    }

    /// <summary>BR3 / D6: GST taxable = one CGST + one SGST (or UTGST) of equal rate and one IGST equal to their sum; VAT taxable = one VAT line; total above zero (never "0% taxable", BR2) and at most 3 decimals.</summary>
    private static void ValidateSet(TaxProfile profile, List<(TaxVersionComponentInput Line, TaxComponent Component)> lines, decimal total, List<FieldError> errors)
    {
        int Count(Func<TaxComponent, bool> kind) => lines.Count(l => kind(l.Component));
        decimal Rate(Func<TaxComponent, bool> kind) => lines.Where(l => kind(l.Component)).Sum(l => l.Line.RatePercent ?? 0m);

        if (profile.Regime == TaxRegime.Gst)
        {
            bool Cgst(TaxComponent c) => c.Kind == TaxComponentKind.Cgst;
            bool State(TaxComponent c) => c.Kind is TaxComponentKind.Sgst or TaxComponentKind.Utgst;
            bool Igst(TaxComponent c) => c.Kind == TaxComponentKind.Igst;

            if (Count(Cgst) != 1 || Count(State) != 1)
            {
                errors.Add(new FieldError("components", "An intra-state set needs exactly one CGST and one SGST (or UTGST) component."));
            }
            else if (Rate(Cgst) != Rate(State))
            {
                errors.Add(new FieldError("components", "CGST and SGST (or UTGST) must have the same rate."));
            }
            if (Count(Igst) != 1)
            {
                errors.Add(new FieldError("components", "An inter-state set needs exactly one IGST component."));
            }
            else if (Count(Cgst) == 1 && Count(State) == 1 && Rate(Igst) != Rate(Cgst) + Rate(State))
            {
                errors.Add(new FieldError("components", "IGST must equal CGST plus SGST (or UTGST)."));
            }
        }
        else if (Count(c => c.Kind == TaxComponentKind.Vat) != 1)
        {
            errors.Add(new FieldError("components", "A taxable VAT profile needs exactly one VAT component."));
        }

        if (total <= 0) errors.Add(new FieldError("components", "A taxable profile needs a rate above zero. Use a nil rated or zero-rated profile for no tax."));
        else if (total > MaxTotalPercent) errors.Add(new FieldError("components", $"The total rate cannot exceed {MaxTotalPercent:0}%."));
        else if (TaxRules.DecimalPlaces(total) > 3) errors.Add(new FieldError("components", "The total rate allows at most 3 decimal places."));
    }

    // ------------------------------------------------------------------ uniqueness

    private async Task EnsureUniqueAsync(Guid? selfId, Guid countryId, string code, string name, CancellationToken ct)
    {
        if (await db.TaxProfiles.AnyAsync(p => p.Id != selfId && p.CountryId == countryId && p.Code == code, ct)) throw DuplicateCode();
        var lower = name.ToLower();
        if (await db.TaxProfiles.AnyAsync(p => p.Id != selfId && p.CountryId == countryId && p.Name.ToLower() == lower, ct)) throw DuplicateName();
    }

    private async Task<string> MirrorNameAsync(string profileName, string countryCode, Guid? selfTaxId, CancellationToken ct)
    {
        var taken = (await db.Taxes.AsNoTracking().Where(t => t.Id != selfTaxId).Select(t => t.Name).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return TaxRules.MirrorName(profileName, countryCode, taken);
    }

    private static DomainValidationException DuplicateCode() => new("code", "A profile with this code already exists for the country.");

    private static DomainValidationException DuplicateName() => new("name", "A profile with this name already exists for the country.");

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } c }
            && (c is CodeIndex or NameIndex or MirrorNameIndex))
        {
            throw c == CodeIndex ? DuplicateCode() : DuplicateName();
        }
    }
}
