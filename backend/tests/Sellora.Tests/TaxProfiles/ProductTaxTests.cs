using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-1: ProductService writes the product-level tax assignment and the mirror Product.TaxId, validates HSN and overrides; the detail DTO reports the resolution and T11 warning.</summary>
public class ProductTaxTests
{
    private sealed record Setup(TaxWorld W, ProductService Service, TaxProfile Gst5, TaxProfile Gst18, TaxProfile Gst40, ProductCategory Beverages, ProductSubcategory SoftDrinks);

    private static async Task<Setup> SeedAsync(string country = "IN")
    {
        var w = await TaxWorld.CreateAsync(country);
        var beverages = await w.AddCategoryAsync("BEV", "Beverages");
        var soft = await w.AddSubcategoryAsync(beverages, "SOFT", "Soft Drinks");
        return country == "IN"
            ? new Setup(w, new ProductService(w.Db), await w.ProfileAsync("GST-5"), await w.ProfileAsync("GST-18"), await w.ProfileAsync("GST-40"), beverages, soft)
            : new Setup(w, new ProductService(w.Db), null!, null!, null!, beverages, soft);
    }

    private static UpsertProductInput Input(Setup s, string sku = "COLA", Guid? categoryId = null, Guid? subcategoryId = null, string? hsn = null, bool? includesTax = null,
        Guid? sales = null, Guid? purchase = null) =>
        new(sku, "Cola " + sku, categoryId, subcategoryId, s.W.Piece.Id, 0m, [new ProductUnitInputItem(null, s.W.Piece.Id, null, 1m, "Each", null, true, true, true, true, true)],
            hsn, includesTax, sales, purchase);

    private static async Task<TaxAssignment?> OwnAsync(Setup s, Guid productId) =>
        await s.W.Db.TaxAssignments.SingleOrDefaultAsync(a => a.Scope == TaxScope.Product && a.ProductId == productId);

    [Fact]
    public async Task Create_NoOverride_InheritsTheCompanyDefault_AndWritesItsMirrorTax()
    {
        var s = await SeedAsync();

        var product = await s.Service.CreateProductAsync(Input(s));

        Assert.Equal(s.Gst18.LegacyTaxId, product.TaxId);
        Assert.Equal(18m, product.Tax.RatePercent);
        Assert.Null(await OwnAsync(s, product.Id));
    }

    [Fact]
    public async Task Create_InheritsTheSubcategoryProfile_WhenOneIsAssigned()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));

        var product = await s.Service.CreateProductAsync(Input(s, categoryId: s.Beverages.Id, subcategoryId: s.SoftDrinks.Id));

        Assert.Equal(s.Gst40.LegacyTaxId, product.TaxId);
    }

    [Fact]
    public async Task Create_WithSalesOverride_StoresAProductAssignment_AndTheMirrorFollowsIt()
    {
        var s = await SeedAsync();

        var product = await s.Service.CreateProductAsync(Input(s, sales: s.Gst5.Id));

        var own = await OwnAsync(s, product.Id);
        Assert.Equal(s.Gst5.Id, own!.SalesTaxProfileId);
        Assert.Null(own.PurchaseTaxProfileId); // purchase = sales stored as NULL (BR7)
        Assert.Equal(s.W.India.Id, own.CountryId);
        Assert.Equal(s.Gst5.LegacyTaxId, product.TaxId);
    }

    [Fact]
    public async Task Create_PurchaseOverrideEqualToSales_IsStoredAsNull_ADifferentOneIsKept()
    {
        var s = await SeedAsync();

        var same = await s.Service.CreateProductAsync(Input(s, "A", sales: s.Gst5.Id, purchase: s.Gst5.Id));
        var different = await s.Service.CreateProductAsync(Input(s, "B", sales: s.Gst5.Id, purchase: s.Gst40.Id));

        Assert.Null((await OwnAsync(s, same.Id))!.PurchaseTaxProfileId);
        Assert.Equal(s.Gst40.Id, (await OwnAsync(s, different.Id))!.PurchaseTaxProfileId);
        Assert.Equal(s.Gst5.LegacyTaxId, different.TaxId); // the mirror is the SALES profile's
    }

    [Fact]
    public async Task Create_InvalidOverrides_RefusedOnTheirFields_AndNothingIsSaved()
    {
        var s = await SeedAsync();
        var uae = await s.W.Profiles.CreateAsync(new(s.W.Uae.Id, "VAT-5", "VAT 5%", TaxRegime.Vat, TaxTreatment.Exempt), null);
        await s.W.Profiles.SetActiveAsync(s.Gst40.Id, false);

        Assert.Equal("salesTaxProfileId", await TaxWorld.FieldOfAsync(() => s.Service.CreateProductAsync(Input(s, purchase: s.Gst5.Id)))); // purchase needs a sales override
        Assert.Equal("salesTaxProfileId", await TaxWorld.FieldOfAsync(() => s.Service.CreateProductAsync(Input(s, sales: Guid.NewGuid()))));
        Assert.Equal("salesTaxProfileId", await TaxWorld.FieldOfAsync(() => s.Service.CreateProductAsync(Input(s, sales: uae.Id)))); // BR10: other country
        Assert.Equal("salesTaxProfileId", await TaxWorld.FieldOfAsync(() => s.Service.CreateProductAsync(Input(s, sales: s.Gst40.Id)))); // inactive
        Assert.Equal("purchaseTaxProfileId", await TaxWorld.FieldOfAsync(() => s.Service.CreateProductAsync(Input(s, sales: s.Gst5.Id, purchase: uae.Id))));
        Assert.Empty(await s.W.Db.Products.ToListAsync());
        Assert.Equal(1, await s.W.Db.TaxAssignments.CountAsync()); // just the company default
    }

    [Fact]
    public async Task Create_CompanyWithoutACountry_Refused()
    {
        var s = await SeedAsync();
        s.W.Settings.CountryId = null;
        await s.W.Db.SaveChangesAsync();

        var ex = await TaxWorld.FailsAsync(() => s.Service.CreateProductAsync(Input(s)));

        Assert.Equal("taxProfile", ex.Field);
        Assert.Contains("country", ex.Message);
        Assert.Empty(await s.W.Db.Products.ToListAsync());
    }

    [Fact]
    public async Task Create_NothingAppliesForTheCountry_RefusedNamingTheProduct()
    {
        var s = await SeedAsync();
        s.W.Db.TaxAssignments.RemoveRange(await s.W.Db.TaxAssignments.ToListAsync()); // no company default
        await s.W.Db.SaveChangesAsync();

        var ex = await TaxWorld.FailsAsync(() => s.Service.CreateProductAsync(Input(s)));

        Assert.Equal("taxProfile", ex.Field);
        Assert.Contains("Cola COLA", ex.Message);
        var ok = await s.Service.CreateProductAsync(Input(s, sales: s.Gst5.Id)); // an override is enough
        Assert.Equal(s.Gst5.LegacyTaxId, ok.TaxId);
    }

    [Fact]
    public async Task Update_ChangingTheOverride_UpdatesTheRowAndTheMirror_RemovingItFallsBackToTheInheritedProfile()
    {
        var s = await SeedAsync();
        var product = await s.Service.CreateProductAsync(Input(s, sales: s.Gst5.Id));

        var changed = await s.Service.UpdateProductAsync(product.Id, Input(s, sales: s.Gst40.Id, purchase: s.Gst5.Id));
        Assert.Equal(s.Gst40.LegacyTaxId, changed!.TaxId);
        var own = await OwnAsync(s, product.Id);
        Assert.Equal((s.Gst40.Id, (Guid?)s.Gst5.Id), (own!.SalesTaxProfileId, own.PurchaseTaxProfileId));
        Assert.NotNull(own.UpdatedAt);
        Assert.Equal(1, await s.W.Db.TaxAssignments.CountAsync(a => a.Scope == TaxScope.Product));

        var cleared = await s.Service.UpdateProductAsync(product.Id, Input(s));
        Assert.Null(await OwnAsync(s, product.Id));
        Assert.Equal(s.Gst18.LegacyTaxId, cleared!.TaxId);
    }

    [Fact]
    public async Task Update_ChangingTheCategory_ReResolvesTheMirror()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Category, s.Beverages.Id, s.W.India.Id, s.Gst5.Id, null));
        var product = await s.Service.CreateProductAsync(Input(s));
        Assert.Equal(s.Gst18.LegacyTaxId, product.TaxId);

        var moved = await s.Service.UpdateProductAsync(product.Id, Input(s, categoryId: s.Beverages.Id));

        Assert.Equal(s.Gst5.LegacyTaxId, moved!.TaxId);
    }

    [Fact]
    public async Task Update_AnExistingOverrideOnAnInactiveProfile_MayBeKept_ButNotNewlyChosen()
    {
        var s = await SeedAsync();
        var product = await s.Service.CreateProductAsync(Input(s, sales: s.Gst5.Id));
        (await s.W.Db.TaxProfiles.SingleAsync(p => p.Id == s.Gst5.Id)).IsActive = false; // deactivated behind the service's guard (e.g. data fix)
        await s.W.Db.SaveChangesAsync();

        var kept = await s.Service.UpdateProductAsync(product.Id, Input(s, sales: s.Gst5.Id) with { Name = "Renamed" });
        Assert.Equal("Renamed", kept!.Name);
        var other = await s.Service.CreateProductAsync(Input(s, "OTHER"));
        Assert.Equal("salesTaxProfileId", await TaxWorld.FieldOfAsync(() => s.Service.UpdateProductAsync(other.Id, Input(s, "OTHER", sales: s.Gst5.Id))));
    }

    [Fact]
    public async Task Update_UnknownProduct_ReturnsNull()
    {
        var s = await SeedAsync();

        Assert.Null(await s.Service.UpdateProductAsync(Guid.NewGuid(), Input(s)));
    }

    // ------------------------------------------------------------------ HSN and price basis

    [Fact]
    public async Task Create_StoresHsnAndPriceBasis_EmptyHsnIsNull()
    {
        var s = await SeedAsync();

        var a = await s.Service.CreateProductAsync(Input(s, "A", hsn: " 220299 ", includesTax: false));
        var b = await s.Service.CreateProductAsync(Input(s, "B", hsn: "  "));

        Assert.Equal("220299", a.HsnCode);
        Assert.False(a.PriceIncludesTax);
        Assert.Null(b.HsnCode);
        Assert.Null(b.PriceIncludesTax); // follows the company setting
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("ABCD")]
    [InlineData("123456789")]
    public async Task Create_InvalidHsn_RefusedOnHsnCode(string hsn)
    {
        var s = await SeedAsync();

        Assert.Equal("hsnCode", await TaxWorld.FieldOfAsync(() => s.Service.CreateProductAsync(Input(s, hsn: hsn))));
    }

    [Theory]
    [InlineData("2202")]
    [InlineData("220299")]
    [InlineData("22029990")]
    public async Task Create_HsnOfFourSixOrEightDigits_Accepted(string hsn)
    {
        var s = await SeedAsync();

        Assert.Equal(hsn, (await s.Service.CreateProductAsync(Input(s, hsn: hsn))).HsnCode);
    }

    // ------------------------------------------------------------------ detail DTO (resolution + warning)

    private static async Task<ProductDetailDto> DetailAsync(Setup s, Guid id)
    {
        var result = await new ProductsController(s.W.Db, s.Service, s.W.Resolver).Get(id, default);
        return (ProductDetailDto)((OkObjectResult)result.Result!).Value!;
    }

    [Fact]
    public async Task Detail_Ac20_GstTaxableProductWithoutHsn_CarriesAWarningOnHsnCode_AndTheResolution()
    {
        var s = await SeedAsync();
        await s.W.Assignments.UpsertAsync(new(TaxScope.Subcategory, s.SoftDrinks.Id, s.W.India.Id, s.Gst40.Id, null));
        var product = await s.Service.CreateProductAsync(Input(s, categoryId: s.Beverages.Id, subcategoryId: s.SoftDrinks.Id));

        var d = await DetailAsync(s, product.Id);

        Assert.Equal(s.Gst40.Id, d.SalesTaxProfile!.Id);
        Assert.Equal(s.Gst40.Id, d.PurchaseTaxProfile!.Id);
        Assert.Equal(TaxScope.Subcategory, d.TaxSource);
        Assert.Equal("Soft Drinks", d.TaxSourceName);
        Assert.Null(d.SalesOverrideProfileId);
        Assert.Null(d.EffectiveHsnCode);
        Assert.Equal("None", d.HsnSource);
        var warning = Assert.Single(d.Warnings!);
        Assert.Equal("hsnCode", warning.Field);
        Assert.Equal(s.Gst40.LegacyTaxId, d.TaxId); // the mirror stays in the contract
    }

    [Fact]
    public async Task Detail_WithOwnHsnAndOverride_HasNoWarning_AndReportsTheOverrideIds()
    {
        var s = await SeedAsync();
        var product = await s.Service.CreateProductAsync(Input(s, hsn: "220299", sales: s.Gst5.Id, purchase: s.Gst18.Id));

        var d = await DetailAsync(s, product.Id);

        Assert.Empty(d.Warnings!);
        Assert.Equal("220299", d.HsnCode);
        Assert.Equal("220299", d.EffectiveHsnCode);
        Assert.Equal("Product", d.HsnSource);
        Assert.Equal(TaxScope.Product, d.TaxSource);
        Assert.Equal(s.Gst5.Id, d.SalesOverrideProfileId);
        Assert.Equal(s.Gst18.Id, d.PurchaseOverrideProfileId);
        Assert.Equal(s.Gst18.Id, d.PurchaseTaxProfile!.Id);
    }

    [Fact]
    public async Task Detail_InheritsTheSubcategoryHsn()
    {
        var s = await SeedAsync();
        s.SoftDrinks.HsnCode = "2202";
        await s.W.Db.SaveChangesAsync();
        var product = await s.Service.CreateProductAsync(Input(s, categoryId: s.Beverages.Id, subcategoryId: s.SoftDrinks.Id));

        var d = await DetailAsync(s, product.Id);

        Assert.Null(d.HsnCode);
        Assert.Equal("2202", d.EffectiveHsnCode);
        Assert.Equal("Subcategory", d.HsnSource);
        Assert.Empty(d.Warnings!);
    }

    // ------------------------------------------------------------------ subcategory HSN (ProductCategoryService)

    [Fact]
    public async Task Subcategory_HsnCode_IsStoredValidatedAndClearable()
    {
        var s = await SeedAsync();
        var service = new ProductCategoryService(s.W.Db);

        var created = await service.CreateSubcategoryAsync(s.Beverages.Id, new("JUICE", "Juice", 1, " 2009 "));
        Assert.Equal("2009", created!.HsnCode);
        var updated = await service.UpdateSubcategoryAsync(s.Beverages.Id, created.Id, new("JUICE", "Juice", 1, "200989"));
        Assert.Equal("200989", updated!.HsnCode);
        var cleared = await service.UpdateSubcategoryAsync(s.Beverages.Id, created.Id, new("JUICE", "Juice", 1, ""));
        Assert.Null(cleared!.HsnCode);
        Assert.Equal("hsnCode", await TaxWorld.FieldOfAsync(() => service.CreateSubcategoryAsync(s.Beverages.Id, new("BAD", "Bad", 2, "12"))));
        Assert.Equal("hsnCode", await TaxWorld.FieldOfAsync(() => service.UpdateSubcategoryAsync(s.Beverages.Id, created.Id, new("JUICE", "Juice", 1, "abcd"))));
        Assert.Equal(2, await s.W.Db.ProductSubcategories.CountAsync()); // SOFT + JUICE: the invalid create saved nothing
    }
}
