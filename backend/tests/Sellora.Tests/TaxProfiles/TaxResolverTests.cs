using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Tests.TaxProfiles;

/// <summary>BR5, BR8, BR9, BR11: product, subcategory, category, company default; HSN and its warning (T11, AC20); AC12, AC13, AC14.</summary>
public class TaxResolverTests
{
    private sealed record Setup(TaxWorld W, TaxProfile Gst5, TaxProfile Gst18, TaxProfile Gst40, ProductCategory Beverages, ProductSubcategory SoftDrinks);

    private static async Task<Setup> SeedAsync(string? subHsn = null)
    {
        var w = await TaxWorld.CreateAsync("IN");
        var beverages = await w.AddCategoryAsync("BEV", "Beverages");
        var soft = await w.AddSubcategoryAsync(beverages, "SOFT", "Soft Drinks", subHsn);
        return new Setup(w, await w.ProfileAsync("GST-5"), await w.ProfileAsync("GST-18"), await w.ProfileAsync("GST-40"), beverages, soft);
    }

    [Fact]
    public async Task Resolve_Ac12_SubcategoryBeatsCategory_AndReportsTheSource()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, null));
        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));
        var cola = await s.W.AddProductAsync("COLA", s.Beverages.Id, s.SoftDrinks.Id);
        var juice = await s.W.AddProductAsync("JUICE", s.Beverages.Id, null);

        var r = await s.W.Resolver.ResolveAsync(cola.Id, s.W.India.Id);
        var j = await s.W.Resolver.ResolveAsync(juice.Id, s.W.India.Id);

        Assert.Equal(TaxScope.Subcategory, r.Source);
        Assert.Equal("Soft Drinks", r.SourceName);
        Assert.Equal(s.Gst40.Id, r.SalesProfile.Id);
        Assert.Equal(TaxScope.Category, j.Source);
        Assert.Equal("Beverages", j.SourceName);
        Assert.Equal(s.Gst5.Id, j.SalesProfile.Id);
    }

    [Fact]
    public async Task Resolve_ProductOverrideBeatsEverything_AndCompanyDefaultIsTheFallback()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));
        var cola = await s.W.AddProductAsync("COLA", s.Beverages.Id, s.SoftDrinks.Id);
        var loose = await s.W.AddProductAsync("LOOSE");
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, cola.Id, s.W.India.Id, s.Gst5.Id, null));

        var r = await s.W.Resolver.ResolveAsync(cola.Id, s.W.India.Id);
        var fallback = await s.W.Resolver.ResolveAsync(loose.Id, s.W.India.Id);

        Assert.Equal(TaxScope.Product, r.Source);
        Assert.Equal(cola.Name, r.SourceName);
        Assert.Equal(s.Gst5.Id, r.SalesProfile.Id);
        Assert.Equal(TaxScope.Company, fallback.Source);
        Assert.Equal("Company default", fallback.SourceName);
        Assert.Equal(s.Gst18.Id, fallback.SalesProfile.Id);
        Assert.Equal(s.Gst18.LegacyTaxId, fallback.SalesProfile.MirrorTaxId);
    }

    [Fact]
    public async Task Resolve_Ac13_PurchaseProfileEmpty_MeansSameAsSales_ElseItsOwn()
    {
        var s = await SeedAsync();
        var a = await s.W.AddProductAsync("A");
        var b = await s.W.AddProductAsync("B");
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, a.Id, s.W.India.Id, s.Gst40.Id, null));
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, b.Id, s.W.India.Id, s.Gst40.Id, s.Gst5.Id));

        var ra = await s.W.Resolver.ResolveAsync(a.Id, s.W.India.Id);
        var rb = await s.W.Resolver.ResolveAsync(b.Id, s.W.India.Id);

        Assert.Equal(s.Gst40.Id, ra.PurchaseProfile.Id);
        Assert.True(ra.PurchaseSameAsSales);
        Assert.Equal(s.Gst5.Id, rb.PurchaseProfile.Id);
        Assert.Equal(s.Gst40.Id, rb.SalesProfile.Id);
        Assert.False(rb.PurchaseSameAsSales);
    }

    [Fact]
    public async Task Resolve_Ac14_NoAssignmentForTheDocumentCountry_RefusedNamingTheProductAndTheCountry()
    {
        var s = await SeedAsync();
        var cola = await s.W.AddProductAsync("COLA", s.Beverages.Id, s.SoftDrinks.Id); // assigned only through the Indian company default

        var ex = await TaxWorld.FailsAsync(() => s.W.Resolver.ResolveAsync(cola.Id, s.W.Uae.Id));

        Assert.Equal("taxProfile", ex.Field);
        Assert.Contains(cola.Name, ex.Message);
        Assert.Contains("United Arab Emirates", ex.Message);
        Assert.Equal("Lines", (await TaxWorld.FailsAsync(() => s.W.Resolver.ResolveAsync(cola.Id, s.W.Uae.Id, "Lines"))).Field);
    }

    [Fact]
    public async Task ResolveMany_ResolvesEveryProductInOneCall_AndFailsOnTheFirstUnresolved()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));
        var cola = await s.W.AddProductAsync("COLA", s.Beverages.Id, s.SoftDrinks.Id);
        var loose = await s.W.AddProductAsync("LOOSE");

        var all = await s.W.Resolver.ResolveManyAsync([cola.Id, loose.Id, cola.Id], s.W.India.Id);

        Assert.Equal(2, all.Count);
        Assert.Equal(s.Gst40.Id, all[cola.Id].SalesProfile.Id);
        Assert.Equal(s.Gst18.Id, all[loose.Id].SalesProfile.Id);
        await TaxWorld.FailsAsync(() => s.W.Resolver.ResolveManyAsync([cola.Id, loose.Id], s.W.Uae.Id));
        Assert.Equal("taxProfile", await TaxWorld.FieldOfAsync(() => s.W.Resolver.ResolveManyAsync([Guid.NewGuid()], s.W.India.Id)));
    }

    // ------------------------------------------------------------------ HSN (BR9, T11, AC20)

    [Fact]
    public async Task Resolve_HsnComesFromTheProductThenTheSubcategory()
    {
        var s = await SeedAsync(subHsn: "2202");
        var own = await s.W.AddProductAsync("OWN", s.Beverages.Id, s.SoftDrinks.Id, hsn: "220299");
        var inherited = await s.W.AddProductAsync("INH", s.Beverages.Id, s.SoftDrinks.Id);
        var none = await s.W.AddProductAsync("NONE");

        var a = await s.W.Resolver.ResolveAsync(own.Id, s.W.India.Id);
        var b = await s.W.Resolver.ResolveAsync(inherited.Id, s.W.India.Id);
        var c = await s.W.Resolver.ResolveAsync(none.Id, s.W.India.Id);

        Assert.Equal(("220299", TaxHsnSource.Product, false), (a.HsnCode, a.HsnSource, a.HsnWarning));
        Assert.Equal(("2202", TaxHsnSource.Subcategory, false), (b.HsnCode, b.HsnSource, b.HsnWarning));
        Assert.Equal((null, TaxHsnSource.None, true), (c.HsnCode, c.HsnSource, c.HsnWarning));
    }

    [Fact]
    public async Task Resolve_Ac20_GstTaxableProductWithoutHsn_Warns_ButNonTaxableAndVatDoNot()
    {
        var s = await SeedAsync();
        var taxable = await s.W.AddProductAsync("TAX");
        var nil = await s.W.AddProductAsync("NIL");
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, nil.Id, s.W.India.Id, (await s.W.ProfileAsync("GST-NIL")).Id, null));

        var t = await s.W.Resolver.ResolveAsync(taxable.Id, s.W.India.Id);
        var n = await s.W.Resolver.ResolveAsync(nil.Id, s.W.India.Id);

        Assert.True(t.HsnWarning);
        Assert.Contains(taxable.Name, t.HsnWarningMessage);
        Assert.False(n.HsnWarning);
        Assert.Null(n.HsnWarningMessage);

        var uae = await TaxWorld.CreateAsync("AE");
        var item = await uae.AddProductAsync("VATITEM", taxId: (await uae.MirrorAsync(await uae.ProfileAsync("VAT-5"))).Id);
        Assert.False((await uae.Resolver.ResolveAsync(item.Id, uae.Uae.Id)).HsnWarning);
    }

    [Fact]
    public async Task Resolve_WarnsWhenOnlyThePurchaseProfileIsGstTaxable()
    {
        var s = await SeedAsync();
        var p = await s.W.AddProductAsync("P");
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, p.Id, s.W.India.Id, (await s.W.ProfileAsync("GST-EXEMPT")).Id, s.Gst5.Id));

        Assert.True((await s.W.Resolver.ResolveAsync(p.Id, s.W.India.Id)).HsnWarning);
    }

    // ------------------------------------------------------------------ preview (product form)

    [Fact]
    public async Task Preview_UsesTheGivenOverrides_NotTheStoredAssignment()
    {
        var s = await SeedAsync();
        var p = await s.W.AddProductAsync("P", s.Beverages.Id, s.SoftDrinks.Id);
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, p.Id, s.W.India.Id, s.Gst40.Id, null));

        var inherited = await s.W.Resolver.PreviewAsync(new(s.W.India.Id, p.Id, p.Name, s.Beverages.Id, s.SoftDrinks.Id, null));
        var overridden = await s.W.Resolver.PreviewAsync(new(s.W.India.Id, null, "New", s.Beverages.Id, null, "2202", s.Gst5.Id, s.Gst18.Id));

        Assert.Equal(TaxScope.Company, inherited!.Source);
        Assert.Equal(s.Gst18.Id, inherited.SalesProfile.Id);
        Assert.Equal(TaxScope.Product, overridden!.Source);
        Assert.Equal(s.Gst5.Id, overridden.SalesProfile.Id);
        Assert.Equal(s.Gst18.Id, overridden.PurchaseProfile.Id);
        Assert.Equal(("2202", TaxHsnSource.Product, false), (overridden.HsnCode, overridden.HsnSource, overridden.HsnWarning));
    }

    [Fact]
    public async Task Preview_UnresolvedIsNull_PurchaseOverrideNeedsASalesOverride_BadHsnRefused()
    {
        var s = await SeedAsync();

        Assert.Null(await s.W.Resolver.PreviewAsync(new(s.W.Uae.Id, null, "X", null, null, null)));
        Assert.Equal("salesTaxProfileId", await TaxWorld.FieldOfAsync(() => s.W.Resolver.PreviewAsync(new(s.W.India.Id, null, "X", null, null, null, null, s.Gst5.Id))));
        Assert.Equal("hsnCode", await TaxWorld.FieldOfAsync(() => s.W.Resolver.PreviewAsync(new(s.W.India.Id, null, "X", null, null, "12"))));
    }

    // ------------------------------------------------------------------ document country (BR8, BR11)

    [Fact]
    public async Task ResolveDocumentCountry_BranchCountryThenCompanyCountryThenRefused()
    {
        var s = await SeedAsync();
        var branch = await s.W.Db.Branches.SingleAsync();

        Assert.Equal(s.W.India.Id, await s.W.Resolver.ResolveDocumentCountryAsync(branch.Id)); // branch has none: the company's
        Assert.Equal(s.W.India.Id, await s.W.Resolver.ResolveDocumentCountryAsync(null));
        branch.CountryId = s.W.Uae.Id;
        await s.W.Db.SaveChangesAsync();
        Assert.Equal(s.W.Uae.Id, await s.W.Resolver.ResolveDocumentCountryAsync(branch.Id));

        s.W.Settings.CountryId = null;
        branch.CountryId = null;
        await s.W.Db.SaveChangesAsync();
        Assert.Equal("taxProfile", await TaxWorld.FieldOfAsync(() => s.W.Resolver.ResolveDocumentCountryAsync(branch.Id)));
        Assert.Equal("Lines", await TaxWorld.FieldOfAsync(() => s.W.Resolver.ResolveDocumentCountryAsync(null, "Lines")));
        Assert.Equal("taxProfile", await TaxWorld.FieldOfAsync(() => s.W.Resolver.ResolveDocumentCountryAsync(Guid.NewGuid())));
    }

    // ------------------------------------------------------------------ version by document date (BR5, E4)

    [Fact]
    public async Task ResolveVersion_ReturnsTheVersionValidOnTheDate_WithItsComponents()
    {
        var s = await SeedAsync();

        var r = await s.W.Resolver.ResolveVersionAsync(s.Gst18.Id, new DateOnly(2026, 1, 1));

        Assert.Equal(s.Gst18.Id, r.Profile.Id);
        Assert.Equal(18m, r.Version.TotalPercent);
        Assert.Equal(3, r.Version.Components.Count);
        Assert.All(r.Version.Components, c => Assert.NotNull(c.TaxComponent));
    }

    [Fact]
    public async Task ResolveVersion_DateBeforeTheFirstVersion_AndInactiveProfile_Refused()
    {
        var s = await SeedAsync();

        var early = await TaxWorld.FailsAsync(() => s.W.Resolver.ResolveVersionAsync(s.Gst40.Id, new DateOnly(2025, 9, 21))); // GST 40% starts 2025-09-22 (E4)
        Assert.Equal("taxProfile", early.Field);
        Assert.Contains("2025-09-21", early.Message);
        await s.W.Profiles.SetActiveAsync(s.Gst5.Id, false);
        Assert.Contains("inactive", (await TaxWorld.FailsAsync(() => s.W.Resolver.ResolveVersionAsync(s.Gst5.Id, new DateOnly(2026, 1, 1), "Lines"))).Message);
        Assert.Equal("taxProfile", await TaxWorld.FieldOfAsync(() => s.W.Resolver.ResolveVersionAsync(Guid.NewGuid(), new DateOnly(2026, 1, 1))));
    }
}
