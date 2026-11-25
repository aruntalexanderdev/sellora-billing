using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TaxProfiles;

/// <summary>T12 / AC21: the standard components, profiles and company default of a new company, the same rows and ids as <see cref="TaxSeedData"/> (and the migration SQL).</summary>
public class TaxSeederTests
{
    [Fact]
    public async Task SeedCompany_India_CreatesStandardComponentsProfilesAndGst18Default()
    {
        var w = await TaxWorld.CreateAsync("IN");

        Assert.Equal(["CESS", "CESS-UNIT", "CGST", "IGST", "SGST", "UTGST"], await w.Db.TaxComponents.OrderBy(c => c.Code).Select(c => c.Code).ToListAsync());
        Assert.Equal(["GST-18", "GST-40", "GST-5", "GST-EXEMPT", "GST-NIL"], await w.Db.TaxProfiles.OrderBy(p => p.Code).Select(p => p.Code).ToListAsync());

        var gst18 = await w.ProfileAsync("GST-18");
        var version = Assert.Single(gst18.Versions);
        Assert.Equal(new DateOnly(2017, 7, 1), version.EffectiveFrom);
        Assert.Null(version.EffectiveTo);
        Assert.Equal(18m, version.TotalPercent);
        Assert.Equal(["CGST:IntraState:9", "SGST:IntraState:9", "IGST:InterState:18"],
            version.Components.OrderBy(c => c.SortOrder).Select(c => $"{w.Db.TaxComponents.Single(k => k.Id == c.TaxComponentId).Code}:{c.Applicability}:{c.RatePercent:0.####}").ToList());
        Assert.Equal(TaxTreatment.NilRated, (await w.ProfileAsync("GST-NIL")).Treatment);
        Assert.Equal(TaxTreatment.Exempt, (await w.ProfileAsync("GST-EXEMPT")).Treatment);
        Assert.Empty((await w.ProfileAsync("GST-NIL")).Versions.Single().Components);

        // The standard rate is the company default (T12, T21); purchase empty = same as sales.
        var company = await w.Db.TaxAssignments.SingleAsync();
        Assert.Equal(TaxScope.Company, company.Scope);
        Assert.Equal(gst18.Id, company.SalesTaxProfileId);
        Assert.Null(company.PurchaseTaxProfileId);

        // Every profile has a mirror tax at the rate valid today (D3).
        Assert.Equal(18m, (await w.MirrorAsync(gst18)).RatePercent);
        Assert.Equal(0m, (await w.MirrorAsync(await w.ProfileAsync("GST-NIL"))).RatePercent);
        Assert.Equal(5, await w.Db.Taxes.CountAsync());
    }

    [Fact]
    public async Task SeedCompany_Uae_CreatesVatFiveZeroRatedAndExemptWithVat5Default()
    {
        var w = await TaxWorld.CreateAsync("AE");

        Assert.Equal(["VAT"], await w.Db.TaxComponents.Select(c => c.Code).ToListAsync());
        Assert.Equal(["VAT-5", "VAT-EXEMPT", "VAT-ZERO"], await w.Db.TaxProfiles.OrderBy(p => p.Code).Select(p => p.Code).ToListAsync());
        Assert.Equal(TaxTreatment.ZeroRated, (await w.ProfileAsync("VAT-ZERO")).Treatment); // deviation e: not NilRated
        var vat5 = await w.ProfileAsync("VAT-5");
        Assert.Equal(TaxRegime.Vat, vat5.Regime);
        Assert.Equal(TaxApplicability.Always, vat5.Versions.Single().Components.Single().Applicability);
        Assert.Equal(vat5.Id, (await w.Db.TaxAssignments.SingleAsync()).SalesTaxProfileId);
    }

    [Theory]
    [InlineData("IN", 5, "GST-18")]
    [InlineData("AE", 3, "VAT-5")]
    [InlineData("OM", 3, "VAT-5")]
    public async Task SeedCompany_UsesTheIdFormulasOfTaxSeedData(string country, int profiles, string defaultCode)
    {
        var w = await TaxWorld.CreateAsync(country);
        var cc = country;

        Assert.Equal(profiles, await w.Db.TaxProfiles.CountAsync());
        foreach (var seed in TaxSeedData.ProfilesFor(cc))
        {
            var profile = await w.Db.TaxProfiles.FindAsync(TaxSeedData.ProfileId(w.CompanyId, cc, seed.Code));
            Assert.NotNull(profile);
            Assert.Equal(TaxSeedData.MirrorTaxId(w.CompanyId, cc, seed.Code), profile!.LegacyTaxId);
            Assert.NotNull(await w.Db.TaxProfileVersions.FindAsync(TaxSeedData.VersionId(w.CompanyId, cc, seed.Code, seed.EffectiveFrom)));
        }
        foreach (var seed in TaxSeedData.ComponentsFor(cc))
        {
            Assert.NotNull(await w.Db.TaxComponents.FindAsync(TaxSeedData.ComponentId(w.CompanyId, cc, seed.Code)));
        }
        Assert.Equal(TaxSeedData.CompanyDefaultAssignmentId(w.CompanyId, cc), (await w.Db.TaxAssignments.SingleAsync()).Id);
        Assert.Equal(TaxSeedData.ProfileId(w.CompanyId, cc, defaultCode), (await w.Db.TaxAssignments.SingleAsync()).SalesTaxProfileId);
    }

    [Fact]
    public async Task SeedCompany_SecondRun_IsIdempotent()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var before = (await w.Db.TaxProfiles.CountAsync(), await w.Db.TaxComponents.CountAsync(), await w.Db.Taxes.CountAsync(), await w.Db.TaxProfileVersions.CountAsync(), await w.Db.TaxAssignments.CountAsync());

        var again = await TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, w.India.Id, w.Clock);
        await w.Db.SaveChangesAsync();

        Assert.True(again.CountrySupported);
        Assert.Equal(0, again.ComponentsAdded);
        Assert.Equal(0, again.ProfilesAdded);
        Assert.Equal(5, again.ProfilesSkipped);
        Assert.False(again.CompanyDefaultSet);
        Assert.Equal(before, (await w.Db.TaxProfiles.CountAsync(), await w.Db.TaxComponents.CountAsync(), await w.Db.Taxes.CountAsync(), await w.Db.TaxProfileVersions.CountAsync(), await w.Db.TaxAssignments.CountAsync()));
    }

    [Fact]
    public async Task SeedCompany_ExistingProfileWithTheSameNameOrCode_IsSkippedAndNotRenamed()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);
        var legacy = new Tax { Id = Guid.NewGuid(), Name = "Standard GST", RatePercent = 18m };
        w.Db.Taxes.Add(legacy);
        w.Db.TaxProfiles.Add(new TaxProfile
        {
            Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "MY-18", Name = "gst 18%", Regime = TaxRegime.Gst, Treatment = TaxTreatment.Taxable,
            LegacyTaxId = legacy.Id, CreatedAt = DateTime.UtcNow,
        });
        await w.Db.SaveChangesAsync();

        var result = await TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, w.India.Id, w.Clock);
        await w.Db.SaveChangesAsync();

        Assert.Equal(4, result.ProfilesAdded);
        Assert.Equal(1, result.ProfilesSkipped);
        Assert.Equal("gst 18%", (await w.Db.TaxProfiles.SingleAsync(p => p.Code == "MY-18")).Name);
        Assert.DoesNotContain(await w.Db.TaxProfiles.Select(p => p.Code).ToListAsync(), c => c == "GST-18");
        // The default points at the existing profile that made the seed skip (matched by name), as the migration does.
        Assert.Equal((await w.Db.TaxProfiles.SingleAsync(p => p.Code == "MY-18")).Id, (await w.Db.TaxAssignments.SingleAsync()).SalesTaxProfileId);
    }

    [Fact]
    public async Task SeedCompany_ExistingCompanyDefault_IsKept()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);
        var first = await TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, w.India.Id, w.Clock);
        await w.Db.SaveChangesAsync();
        var gst5 = await w.ProfileAsync("GST-5");
        var assignment = await w.Db.TaxAssignments.SingleAsync();
        assignment.SalesTaxProfileId = gst5.Id;
        await w.Db.SaveChangesAsync();

        var again = await TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, w.India.Id, w.Clock);

        Assert.True(first.CompanyDefaultSet);
        Assert.False(again.CompanyDefaultSet);
        Assert.Equal(gst5.Id, (await w.Db.TaxAssignments.SingleAsync()).SalesTaxProfileId);
    }

    [Fact]
    public async Task SeedCompany_NoCountry_SeedsNothing()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);

        var result = await TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, null, w.Clock);
        await w.Db.SaveChangesAsync();

        Assert.False(result.CountrySupported);
        Assert.Empty(await w.Db.TaxProfiles.ToListAsync());
        Assert.Empty(await w.Db.TaxComponents.ToListAsync());
        Assert.Empty(await w.Db.Taxes.ToListAsync());
    }

    [Fact]
    public async Task SeedCompany_CountryWithoutStandardSeed_SeedsNothing()
    {
        var w = await TaxWorld.CreateAsync("QA", seed: false);

        var result = await TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, w.Qatar.Id, w.Clock);
        await w.Db.SaveChangesAsync();

        Assert.False(result.CountrySupported);
        Assert.Empty(await w.Db.TaxProfiles.ToListAsync());
    }

    [Fact]
    public async Task SeedCompany_SecondGccCountry_GetsDistinctMirrorTaxNames()
    {
        var w = await TaxWorld.CreateAsync("AE");

        var oman = await TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, w.Oman.Id, w.Clock);
        await w.Db.SaveChangesAsync();

        Assert.Equal(3, oman.ProfilesAdded);
        var names = await w.Db.Taxes.Select(t => t.Name).ToListAsync();
        Assert.Equal(names.Count, names.Distinct().Count()); // Taxes.Name is unique per company
        Assert.Equal(6, names.Count);
        Assert.Contains("VAT 5% (OM)", names);
        Assert.Equal(2, await w.Db.TaxAssignments.CountAsync()); // one company default per country
    }
}
