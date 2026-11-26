using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data;

/// <summary>
/// Standard tax components and profiles per country (docs/features/tax-profiles.md T12) and the deterministic id formulas shared with
/// the migration. Data only: the seeder service (WP2) writes these rows for new companies; migration AddTaxProfiles seeds the same rows for
/// existing companies from the frozen embedded SQL (Data/Migrations/Sql/*_AddTaxProfiles.seed.sql, backfill.sql, legacy-rows.sql) and a
/// Postgres drift test compares the two. Ids are md5-derived (as <c>DbSeeder.Md5Guid</c> and <see cref="GeoSeedData"/>); every formula below
/// has a character-for-character twin in the SQL. Rates are examples: confirm against current notifications before production.
/// </summary>
public static class TaxSeedData
{
    public sealed record ComponentSeed(string CountryCode, string Code, string Name, TaxComponentKind Kind, TaxCalculationMethod Method);

    /// <summary>One component line of a seeded version. Exactly one of <paramref name="RatePercent"/> / <paramref name="AmountPerBaseUnit"/> is set.</summary>
    public sealed record VersionComponentSeed(string ComponentCode, TaxApplicability Applicability, decimal? RatePercent, decimal? AmountPerBaseUnit, int SortOrder);

    /// <summary>A standard profile with its single version. <paramref name="IsCompanyDefault"/> marks the standard rate (T12/T21): the company-default assignment.</summary>
    public sealed record ProfileSeed(
        string CountryCode, string Code, string Name, TaxRegime Regime, TaxTreatment Treatment,
        DateOnly EffectiveFrom, decimal TotalPercent, bool IsCompanyDefault, IReadOnlyList<VersionComponentSeed> Components);

    /// <summary>First day of the version the migration gives every legacy tax (a rate that has always applied).</summary>
    public static readonly DateOnly LegacyEffectiveFrom = new(2000, 1, 1);

    /// <summary>Prefix of the code of a profile migrated from a legacy tax: <c>LEGACY-</c> + the last 12 hex digits of the tax id, upper case (the random part: leading digits of time-ordered ids repeat).</summary>
    public const string LegacyCodePrefix = "LEGACY-";

    // Ids ---------------------------------------------------------------------------------------------------------------------------------

    private static string D(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static Guid ComponentId(Guid companyId, string countryCode, string code) => Md5Guid($"sellora:taxcomponent:{D(companyId)}:{countryCode}:{code}");
    public static Guid ProfileId(Guid companyId, string countryCode, string code) => Md5Guid($"sellora:taxprofile:{D(companyId)}:{countryCode}:{code}");
    /// <summary>Id of the mirror <c>Taxes</c> row of a seeded profile (D3).</summary>
    public static Guid MirrorTaxId(Guid companyId, string countryCode, string code) => Md5Guid($"sellora:taxmirror:{D(companyId)}:{countryCode}:{code}");
    public static Guid VersionId(Guid companyId, string countryCode, string profileCode, DateOnly effectiveFrom) =>
        Md5Guid($"sellora:taxversion:{D(companyId)}:{countryCode}:{profileCode}:{Day(effectiveFrom)}");
    public static Guid VersionComponentId(Guid versionId, string componentCode, TaxApplicability applicability) =>
        Md5Guid($"sellora:taxvc:{D(versionId)}:{componentCode}:{applicability}");
    public static Guid CompanyDefaultAssignmentId(Guid companyId, string countryCode) => Md5Guid($"sellora:taxassignment:{D(companyId)}:{countryCode}:company");
    public static Guid ProductAssignmentId(Guid companyId, string countryCode, Guid productId) =>
        Md5Guid($"sellora:taxassignment:{D(companyId)}:{countryCode}:product:{D(productId)}");

    /// <summary>Legacy profile / version of an existing <c>Taxes</c> row (the profile's <c>LegacyTaxId</c> is the tax itself).</summary>
    public static Guid LegacyProfileId(Guid taxId) => Md5Guid($"sellora:taxprofile:legacy:{D(taxId)}");
    public static Guid LegacyVersionId(Guid taxId) => Md5Guid($"sellora:taxversion:legacy:{D(taxId)}");
    public static string LegacyCode(Guid taxId) => LegacyCodePrefix + D(taxId).Replace("-", "", StringComparison.Ordinal)[^12..].ToUpperInvariant();

    /// <summary>Id of the single legacy tax row of a line (the four write paths and <c>legacy-rows.sql</c> use the same id, so the SQL catch-up never duplicates a row).</summary>
    public static Guid LegacyLineTaxId(Guid lineId) => Md5Guid($"sellora:linetax:legacy:{D(lineId)}");

    private static Guid Md5Guid(string text) => Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))));

    // Data ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Countries with a standard seed (T12). Qatar and Kuwait have none (BR11: no profile until the company creates one).</summary>
    public static IReadOnlyList<string> SeededCountryCodes { get; } = ["IN", "AE", "SA", "BH", "OM"];

    public static IReadOnlyList<ComponentSeed> Components { get; } =
    [
        new("IN", "CGST", "Central GST", TaxComponentKind.Cgst, TaxCalculationMethod.Percent),
        new("IN", "SGST", "State GST", TaxComponentKind.Sgst, TaxCalculationMethod.Percent),
        new("IN", "UTGST", "Union territory GST", TaxComponentKind.Utgst, TaxCalculationMethod.Percent),
        new("IN", "IGST", "Integrated GST", TaxComponentKind.Igst, TaxCalculationMethod.Percent),
        new("IN", "CESS", "Cess", TaxComponentKind.Cess, TaxCalculationMethod.Percent),
        new("IN", "CESS-UNIT", "Cess per unit", TaxComponentKind.Cess, TaxCalculationMethod.AmountPerBaseUnit),
        new("AE", "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent),
        new("SA", "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent),
        new("BH", "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent),
        new("OM", "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent),
    ];

    /// <summary>The VAT component of a country that has no standard seed but a company with legacy taxes there (migration only; code VAT, name as the seeded ones).</summary>
    public static ComponentSeed GenericVatComponent(string countryCode) =>
        new(countryCode, "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent);

    private static readonly DateOnly GstStart = new(2017, 7, 1);

    public static IReadOnlyList<ProfileSeed> Profiles { get; } = BuildProfiles();

    public static IEnumerable<ComponentSeed> ComponentsFor(string countryCode) => Components.Where(c => c.CountryCode == countryCode);
    public static IEnumerable<ProfileSeed> ProfilesFor(string countryCode) => Profiles.Where(p => p.CountryCode == countryCode);
    public static ProfileSeed? DefaultFor(string countryCode) => Profiles.SingleOrDefault(p => p.CountryCode == countryCode && p.IsCompanyDefault);

    private static List<ProfileSeed> BuildProfiles()
    {
        var list = new List<ProfileSeed>
        {
            new("IN", "GST-NIL", "GST Nil rated", TaxRegime.Gst, TaxTreatment.NilRated, GstStart, 0m, false, []),
            new("IN", "GST-EXEMPT", "GST Exempt", TaxRegime.Gst, TaxTreatment.Exempt, GstStart, 0m, false, []),
            Gst("GST-5", "GST 5%", 5m, GstStart, false),
            Gst("GST-18", "GST 18%", 18m, GstStart, true),
            Gst("GST-40", "GST 40%", 40m, new DateOnly(2025, 9, 22), false),
        };
        foreach (var (cc, rate, from) in new[] { ("AE", 5m, new DateOnly(2018, 1, 1)), ("SA", 15m, new DateOnly(2020, 7, 1)), ("BH", 10m, new DateOnly(2022, 1, 1)), ("OM", 5m, new DateOnly(2021, 4, 16)) })
        {
            var label = rate.ToString("0", CultureInfo.InvariantCulture);
            list.Add(new(cc, "VAT-" + label, $"VAT {label}%", TaxRegime.Vat, TaxTreatment.Taxable, from, rate, true, [new("VAT", TaxApplicability.Always, rate, null, 1)]));
            list.Add(new(cc, "VAT-ZERO", "VAT Zero-rated", TaxRegime.Vat, TaxTreatment.ZeroRated, from, 0m, false, []));
            list.Add(new(cc, "VAT-EXEMPT", "VAT Exempt", TaxRegime.Vat, TaxTreatment.Exempt, from, 0m, false, []));
        }
        return list;
    }

    // CGST and SGST are the halves of the rate (intra-state), IGST the whole (inter-state): BR3, D6.
    private static ProfileSeed Gst(string code, string name, decimal rate, DateOnly from, bool isDefault) =>
        new("IN", code, name, TaxRegime.Gst, TaxTreatment.Taxable, from, rate, isDefault,
        [
            new("CGST", TaxApplicability.IntraState, rate / 2, null, 1),
            new("SGST", TaxApplicability.IntraState, rate / 2, null, 2),
            new("IGST", TaxApplicability.InterState, rate, null, 3),
        ]);
}
