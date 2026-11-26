using System.Security.Cryptography;
using System.Text;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Data.Migrations;

namespace Sellora.Tests.TaxProfiles;

public class TaxMigrationSqlTests
{
    // Frozen embedded migration SQL: an edit to an applied migration's SQL must fail CI (add a new migration instead).
    [Theory]
    [InlineData("20261124120429_AddTaxProfiles.seed.sql", "3a1db339ef88d97dffabc20bad10aed5fc0e5218360885aee7543befd9ec6a3a")]
    [InlineData("20261124120429_AddTaxProfiles.backfill.sql", "d51c26b2ef5a7392d0765fd7386652e5b3a5808357e7b5e08d79b4cb3633dee5")]
    [InlineData("20261124120429_AddTaxProfiles.legacy-rows.sql", "284ec03227940bea0c54bb137d0f4a85e21c2c2118233ed3a5a9643967068afe")]
    public void Embedded_migration_sql_is_frozen_and_lf_only(string resource, string sha256)
    {
        var text = MigrationSql.Read(resource);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain("$EF$", text);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    }

    [Fact]
    public void Seed_data_has_the_designed_shape()
    {
        Assert.Equal(TaxSeedData.SeededCountryCodes, TaxSeedData.Profiles.Select(p => p.CountryCode).Distinct());
        Assert.Equal(5, TaxSeedData.ProfilesFor("IN").Count());
        Assert.Equal(3, TaxSeedData.ProfilesFor("AE").Count());
        foreach (var cc in TaxSeedData.SeededCountryCodes) Assert.NotNull(TaxSeedData.DefaultFor(cc));
        Assert.Equal("GST-18", TaxSeedData.DefaultFor("IN")!.Code);

        // One company: no duplicate (country, code) and every component line points at a seeded component of the same country.
        Assert.Equal(TaxSeedData.Profiles.Count, TaxSeedData.Profiles.Select(p => (p.CountryCode, p.Code)).Distinct().Count());
        Assert.Equal(TaxSeedData.Profiles.Count, TaxSeedData.Profiles.Select(p => (p.CountryCode, p.Name.ToLowerInvariant())).Distinct().Count());
        foreach (var profile in TaxSeedData.Profiles)
        {
            foreach (var line in profile.Components)
            {
                Assert.Contains(TaxSeedData.Components, c => c.CountryCode == profile.CountryCode && c.Code == line.ComponentCode);
            }
            Assert.Equal(profile.Treatment == TaxTreatment.Taxable, profile.Components.Count > 0);
        }
    }

    [Fact]
    public void Gst_profiles_have_equal_cgst_sgst_halves_and_an_igst_of_the_sum()
    {
        foreach (var profile in TaxSeedData.ProfilesFor("IN").Where(p => p.Treatment == TaxTreatment.Taxable))
        {
            var cgst = profile.Components.Single(c => c.ComponentCode == "CGST").RatePercent;
            var sgst = profile.Components.Single(c => c.ComponentCode == "SGST").RatePercent;
            var igst = profile.Components.Single(c => c.ComponentCode == "IGST").RatePercent;
            Assert.Equal(cgst, sgst);
            Assert.Equal(igst, cgst + sgst);
            Assert.Equal(profile.TotalPercent, cgst + sgst);
        }
    }

    [Fact]
    public void Legacy_code_fits_the_code_format_and_is_stable()
    {
        var code = TaxSeedData.LegacyCode(Guid.Parse("10000000-0000-4000-8000-0000000000ab"));
        Assert.Equal("LEGACY-0000000000AB", code);
        Assert.Matches("^[A-Z0-9][A-Z0-9-]{0,19}$", code);
    }
}
