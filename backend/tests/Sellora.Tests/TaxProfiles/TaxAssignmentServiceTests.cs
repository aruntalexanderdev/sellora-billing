using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Tests.TaxProfiles;

/// <summary>Assignments (BR7, BR10, BR6), mirror re-sync of Products.TaxId (D3) and T20 "clear redundant overrides".</summary>
public class TaxAssignmentServiceTests
{
    private sealed record Setup(TaxWorld W, TaxProfile Gst5, TaxProfile Gst18, TaxProfile Gst40, ProductCategory Beverages, ProductSubcategory SoftDrinks, Product Cola);

    private static async Task<Setup> SeedAsync()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var beverages = await w.AddCategoryAsync("BEV", "Beverages");
        var soft = await w.AddSubcategoryAsync(beverages, "SOFT", "Soft Drinks");
        var cola = await w.AddProductAsync("COLA", beverages.Id, soft.Id);
        return new Setup(w, await w.ProfileAsync("GST-5"), await w.ProfileAsync("GST-18"), await w.ProfileAsync("GST-40"), beverages, soft, cola);
    }

    private static async Task<Guid> TaxIdOfAsync(Setup s, Product p) => (await s.W.Db.Products.AsNoTracking().SingleAsync(x => x.Id == p.Id)).TaxId;

    [Fact]
    public async Task Upsert_CreatesThenUpdatesTheSingleRowOfAScopeAndCountry()
    {
        var s = await SeedAsync();

        var created = await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, null));
        var updated = await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst40.Id, null));

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(s.Gst40.Id, updated.SalesTaxProfileId);
        Assert.NotNull(updated.UpdatedAt);
        Assert.Equal(2, await s.W.Db.TaxAssignments.CountAsync()); // company default + category
        Assert.Equal(s.Beverages.Id, created.CategoryId);
        Assert.Null(created.SubcategoryId);
    }

    [Fact]
    public async Task Upsert_SubcategoryRow_CarriesItsCategory_AndProductRowItsProduct()
    {
        var s = await SeedAsync();

        var sub = await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));
        var product = await s.W.Assignments.UpsertAsync(new(TaxScope.Product, s.Cola.Id, s.W.India.Id, s.Gst5.Id, null));

        Assert.Equal(s.Beverages.Id, sub.CategoryId);
        Assert.Equal(s.SoftDrinks.Id, sub.SubcategoryId);
        Assert.Equal(s.Cola.Id, product.ProductId);
    }

    [Fact]
    public async Task Upsert_PurchaseProfileEqualToSales_IsStoredAsNull_ADifferentOneIsKept()
    {
        var s = await SeedAsync();

        var same = await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst18.Id, s.Gst18.Id));
        Assert.Null(same.PurchaseTaxProfileId);
        var different = await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst18.Id, s.Gst5.Id));
        Assert.Equal(s.Gst5.Id, different.PurchaseTaxProfileId);
    }

    [Fact]
    public async Task Upsert_ProfileOfAnotherCountry_RefusedOnTheProfileField()
    {
        var s = await SeedAsync();
        var uaeVat = await s.W.Profiles.CreateAsync(new(s.W.Uae.Id, "VAT-5", "VAT 5%", TaxRegime.Vat, TaxTreatment.Exempt, TaxWorld.Version(new DateOnly(2020, 1, 1))), null);

        Assert.Equal("salesProfileId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, uaeVat.Id, null))));
        Assert.Equal("purchaseProfileId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, uaeVat.Id))));
        Assert.Equal("salesProfileId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.Uae.Id, s.Gst5.Id, null))));
    }

    [Fact]
    public async Task Upsert_InactiveProfile_RefusedForANewAssignment_ButAnExistingOneMayKeepIt()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, null));
        // GST 5% is assigned, so it cannot be deactivated through the service; the row can still hold an inactive profile after a direct change.
        (await s.W.Db.TaxProfiles.SingleAsync(p => p.Id == s.Gst5.Id)).IsActive = false;
        await s.W.Db.SaveChangesAsync();

        Assert.Equal("salesProfileId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst5.Id, null))));
        Assert.Equal("purchaseProfileId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst18.Id, s.Gst5.Id)))); // not the current purchase profile
        var keep = await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, null)); // unchanged value on the same row
        Assert.Equal(s.Gst5.Id, keep.SalesTaxProfileId);
    }

    [Fact]
    public async Task Upsert_InvalidScopeTargetOrCountry_Refused()
    {
        var s = await SeedAsync();

        Assert.Equal("scopeId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Category, null, s.W.India.Id, s.Gst5.Id, null))));
        Assert.Equal("scopeId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Category, Guid.NewGuid(), s.W.India.Id, s.Gst5.Id, null))));
        Assert.Equal("scopeId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, Guid.NewGuid(), s.W.India.Id, s.Gst5.Id, null))));
        Assert.Equal("scopeId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Product, Guid.NewGuid(), s.W.India.Id, s.Gst5.Id, null))));
        Assert.Equal("countryId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Company, null, Guid.NewGuid(), s.Gst5.Id, null))));
        Assert.Equal("salesProfileId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.UpsertAsync(new(TaxScope.Company, null, s.W.India.Id, Guid.NewGuid(), null))));
    }

    [Fact]
    public async Task Delete_CompanyDefault_Refused_OtherRowsDeleted_UnknownIsFalse()
    {
        var s = await SeedAsync();
        var company = await s.W.Db.TaxAssignments.SingleAsync();
        var category = await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, null));

        Assert.Equal("id", await TaxWorld.FieldOfAsync(() => s.W.Assignments.DeleteAsync(company.Id)));
        Assert.True(await s.W.Assignments.DeleteAsync(category.Id));
        Assert.False(await s.W.Assignments.DeleteAsync(category.Id));
        Assert.Single(await s.W.Db.TaxAssignments.ToListAsync());
    }

    // ------------------------------------------------------------------ mirror

    [Fact]
    public async Task Upsert_ReSyncsTheMirrorTaxOfTheAffectedProductsOnly()
    {
        var s = await SeedAsync();
        var other = await s.W.AddProductAsync("OTHER"); // no category: not affected by the subcategory rule

        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));

        Assert.Equal(s.Gst40.LegacyTaxId, await TaxIdOfAsync(s, s.Cola));
        Assert.Equal(s.Gst18.LegacyTaxId, await TaxIdOfAsync(s, other));
    }

    [Fact]
    public async Task Upsert_Scopes_FollowTheProductSubcategoryCategoryCompanyOrder()
    {
        var s = await SeedAsync();

        await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, null));
        Assert.Equal(s.Gst5.LegacyTaxId, await TaxIdOfAsync(s, s.Cola));
        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));
        Assert.Equal(s.Gst40.LegacyTaxId, await TaxIdOfAsync(s, s.Cola)); // AC12: subcategory beats category
        var product = await s.W.Assignments.UpsertAsync(new(TaxScope.Product, s.Cola.Id, s.W.India.Id, s.Gst18.Id, null));
        Assert.Equal(s.Gst18.LegacyTaxId, await TaxIdOfAsync(s, s.Cola));

        await s.W.Assignments.DeleteAsync(product.Id);
        Assert.Equal(s.Gst40.LegacyTaxId, await TaxIdOfAsync(s, s.Cola));
        await s.W.Assignments.DeleteAsync((await s.W.Db.TaxAssignments.SingleAsync(a => a.Scope == TaxScope.Subcategory)).Id);
        Assert.Equal(s.Gst5.LegacyTaxId, await TaxIdOfAsync(s, s.Cola));
    }

    [Fact]
    public async Task Upsert_CompanyDefaultChange_ReSyncsProductsThatInheritIt()
    {
        var s = await SeedAsync();
        var company = await s.W.Db.TaxAssignments.SingleAsync();

        await s.W.Assignments.UpsertAsync(new(TaxScope.Company, null, s.W.India.Id, s.Gst5.Id, null));

        Assert.Equal(s.Gst5.LegacyTaxId, await TaxIdOfAsync(s, s.Cola));
        Assert.Equal(company.Id, (await s.W.Db.TaxAssignments.SingleAsync()).Id); // updated in place
    }

    [Fact]
    public async Task Upsert_OtherCountryAssignment_DoesNotTouchTheMirror()
    {
        var s = await SeedAsync();
        var uae = await s.W.Profiles.CreateAsync(new(s.W.Uae.Id, "VAT-5", "VAT 5%", TaxRegime.Vat, TaxTreatment.Exempt, TaxWorld.Version(new DateOnly(2020, 1, 1))), null);
        var before = await TaxIdOfAsync(s, s.Cola);

        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, s.Cola.Id, s.W.Uae.Id, uae.Id, null));

        Assert.Equal(before, await TaxIdOfAsync(s, s.Cola));
    }

    // ------------------------------------------------------------------ T20

    [Fact]
    public async Task ClearRedundantOverrides_RemovesOnlyOverridesEqualToTheInheritedProfiles_AndReturnsTheCount()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));
        var same = await s.W.AddProductAsync("SAME", s.Beverages.Id, s.SoftDrinks.Id);   // override equal to the subcategory's: redundant
        var differs = await s.W.AddProductAsync("DIFF", s.Beverages.Id, s.SoftDrinks.Id); // override differs: kept
        var purchaseDiffers = await s.W.AddProductAsync("PUR", s.Beverages.Id, s.SoftDrinks.Id); // same sales, own purchase profile: kept
        var plain = await s.W.AddProductAsync("PLAIN");                                   // equal to the company default: redundant
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, same.Id, s.W.India.Id, s.Gst40.Id, null));
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, differs.Id, s.W.India.Id, s.Gst5.Id, null));
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, purchaseDiffers.Id, s.W.India.Id, s.Gst40.Id, s.Gst5.Id));
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, plain.Id, s.W.India.Id, s.Gst18.Id, s.Gst18.Id));

        var cleared = await s.W.Assignments.ClearRedundantOverridesAsync(s.W.India.Id);

        Assert.Equal(2, cleared);
        var remaining = await s.W.Db.TaxAssignments.Where(a => a.Scope == TaxScope.Product).Select(a => a.ProductId!.Value).ToListAsync();
        Assert.Equal(new[] { differs.Id, purchaseDiffers.Id }.Order(), remaining.Order());
        Assert.Equal(s.Gst40.LegacyTaxId, await TaxIdOfAsync(s, same)); // what it inherits
        Assert.Equal(s.Gst5.LegacyTaxId, await TaxIdOfAsync(s, differs));
        Assert.Equal(s.Gst18.LegacyTaxId, await TaxIdOfAsync(s, plain));
    }

    [Fact]
    public async Task ClearRedundantOverrides_RunTwice_SecondRunClearsNothing_AndAnotherCountryIsUntouched()
    {
        var s = await SeedAsync();
        var p = await s.W.AddProductAsync("P");
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, p.Id, s.W.India.Id, s.Gst18.Id, null));
        var uae = await s.W.Profiles.CreateAsync(new(s.W.Uae.Id, "VAT-5", "VAT 5%", TaxRegime.Vat, TaxTreatment.Exempt, TaxWorld.Version(new DateOnly(2020, 1, 1))), null);
        await s.W.Assignments.UpsertAsync(new(TaxScope.Company, null, s.W.Uae.Id, uae.Id, null));
        await s.W.Assignments.UpsertAsync(new(TaxScope.Product, p.Id, s.W.Uae.Id, uae.Id, null));

        Assert.Equal(1, await s.W.Assignments.ClearRedundantOverridesAsync(s.W.India.Id));
        Assert.Equal(0, await s.W.Assignments.ClearRedundantOverridesAsync(s.W.India.Id));
        Assert.Single(await s.W.Db.TaxAssignments.Where(a => a.Scope == TaxScope.Product).ToListAsync()); // the UAE override stays
        Assert.Equal("countryId", await TaxWorld.FieldOfAsync(() => s.W.Assignments.ClearRedundantOverridesAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task ClearRedundantOverrides_WithoutAnInheritedProfile_KeepsTheOverride()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);
        await TaxSeederHelper.SeedWithoutDefaultAsync(w);
        var gst5 = await w.ProfileAsync("GST-5");
        var p = await w.AddProductAsync("P", taxId: gst5.LegacyTaxId);
        await w.Assignments.UpsertAsync(new(TaxScope.Product, p.Id, w.India.Id, gst5.Id, null));

        Assert.Equal(0, await w.Assignments.ClearRedundantOverridesAsync(w.India.Id)); // nothing to inherit: the override is the only source
        Assert.Single(await w.Db.TaxAssignments.ToListAsync());
    }
}

internal static class TaxSeederHelper
{
    /// <summary>The standard seed with its company default removed (a company that never chose one).</summary>
    public static async Task SeedWithoutDefaultAsync(TaxWorld w)
    {
        await Sellora.Infrastructure.Services.TaxSeeder.SeedCompanyAsync(w.Db, w.CompanyId, w.India.Id, w.Clock);
        await w.Db.SaveChangesAsync();
        w.Db.TaxAssignments.RemoveRange(await w.Db.TaxAssignments.ToListAsync());
        await w.Db.SaveChangesAsync();
    }
}
