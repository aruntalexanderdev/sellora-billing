using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-1 review: T25 (assignable only with a version valid today; a new version must start after the last document), T24 (country change), T21/T25 (default restore).</summary>
public class TaxReviewFixesTests
{
    // ---- T25: a profile with no version valid today cannot be assigned ----------------------------------------------------------

    private static async Task<TaxProfile> ProfileWithoutVersionAsync(TaxWorld w) =>
        await w.Profiles.CreateAsync(new(w.India.Id, "NOVER", "No version yet", TaxRegime.Gst, TaxTreatment.Taxable), null);

    private static async Task<TaxProfile> ProfileEndedYesterdayAsync(TaxWorld w)
    {
        var p = await w.Profiles.CreateAsync(new(w.India.Id, "ENDED", "Ended", TaxRegime.Gst, TaxTreatment.NilRated, TaxWorld.Version(new DateOnly(2020, 1, 1))), null);
        var v = await w.Db.TaxProfileVersions.SingleAsync(x => x.TaxProfileId == p.Id);
        v.EffectiveTo = TaxWorld.Today.AddDays(-1);
        await w.Db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Upsert_ProfileWithoutAVersionValidToday_RefusedOnTheProfileField_ForEveryScope()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var category = await w.AddCategoryAsync("C", "Cat");
        var product = await w.AddProductAsync("P");
        var none = await ProfileWithoutVersionAsync(w);
        var ended = await ProfileEndedYesterdayAsync(w);
        var gst5 = await w.ProfileAsync("GST-5");

        foreach (var bad in new[] { none, ended })
        {
            Assert.Equal("salesProfileId", await TaxWorld.FieldOfAsync(() => w.Assignments.UpsertAsync(new(TaxScope.Company, null, w.India.Id, bad.Id, null))));
            Assert.Equal("salesProfileId", await TaxWorld.FieldOfAsync(() => w.Assignments.UpsertAsync(new(TaxScope.Category, category.Id, w.India.Id, bad.Id, null))));
            Assert.Equal("salesProfileId", await TaxWorld.FieldOfAsync(() => w.Assignments.UpsertAsync(new(TaxScope.Product, product.Id, w.India.Id, bad.Id, null))));
            Assert.Equal("purchaseProfileId", await TaxWorld.FieldOfAsync(() => w.Assignments.UpsertAsync(new(TaxScope.Category, category.Id, w.India.Id, gst5.Id, bad.Id))));
        }
        Assert.Single(await w.Db.TaxAssignments.ToListAsync()); // only the seeded company default
    }

    [Fact]
    public async Task Upsert_AfterAVersionIsAdded_TheProfileCanBeAssigned()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var none = await ProfileWithoutVersionAsync(w);
        await w.Profiles.AddVersionAsync(none.Id, TaxWorld.Version(TaxWorld.Today, await w.GstSetAsync(12m)), null);

        var a = await w.Assignments.UpsertAsync(new(TaxScope.Company, null, w.India.Id, none.Id, null));

        Assert.Equal(none.Id, a.SalesTaxProfileId);
    }

    [Fact]
    public async Task ProductOverride_ProfileWithoutAVersionValidToday_RefusedOnTheProductFields()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var none = await ProfileWithoutVersionAsync(w);
        var gst5 = await w.ProfileAsync("GST-5");
        var service = new ProductService(w.Db);
        Sellora.Domain.Inventory.UpsertProductInput Input(Guid? sales, Guid? purchase) => new("X", "X", null, null, w.Piece.Id, 0m,
            [new(null, w.Piece.Id, null, 1m, "Each", null, true, true, true, true, true)], null, null, sales, purchase);

        Assert.Equal("salesTaxProfileId", await TaxWorld.FieldOfAsync(() => service.CreateProductAsync(Input(none.Id, null))));
        Assert.Equal("purchaseTaxProfileId", await TaxWorld.FieldOfAsync(() => service.CreateProductAsync(Input(gst5.Id, none.Id))));
        Assert.Empty(await w.Db.Products.ToListAsync());
    }

    [Fact]
    public async Task SeedStandard_DoesNotPickAnExistingProfileOfTheRightNameButAnotherRateAsTheDefault()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);
        var legacy = new Tax { Id = Guid.NewGuid(), Name = "Mine", RatePercent = 12m };
        w.Db.Taxes.Add(legacy);
        var p = new TaxProfile { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "MINE", Name = "GST 18%", Regime = TaxRegime.Gst, Treatment = TaxTreatment.Taxable, LegacyTaxId = legacy.Id, CreatedAt = DateTime.UtcNow };
        p.Versions.Add(new TaxProfileVersion { Id = Guid.NewGuid(), TaxProfileId = p.Id, EffectiveFrom = new DateOnly(2000, 1, 1), TotalPercent = 12m, CreatedAt = DateTime.UtcNow });
        w.Db.TaxProfiles.Add(p);
        await w.Db.SaveChangesAsync();

        var result = await w.Profiles.SeedStandardAsync();

        Assert.False(result.CompanyDefaultSet); // "GST 18%" by name, but 12%: not the standard rate
        Assert.Empty(await w.Db.TaxAssignments.ToListAsync());
    }

    [Fact]
    public async Task RestoreCompanyDefault_AddsTheStandardRateWhenMissing_AndNothingWhenPresentOrUnsuitable()
    {
        var w = await TaxWorld.CreateAsync("IN");
        w.Db.TaxAssignments.RemoveRange(await w.Db.TaxAssignments.ToListAsync());
        await w.Db.SaveChangesAsync();

        Assert.True(await TaxSeeder.RestoreCompanyDefaultAsync(w.Db, w.CompanyId, w.India.Id, w.Clock));
        await w.Db.SaveChangesAsync();
        Assert.Equal((await w.ProfileAsync("GST-18")).Id, (await w.Db.TaxAssignments.SingleAsync()).SalesTaxProfileId);
        Assert.False(await TaxSeeder.RestoreCompanyDefaultAsync(w.Db, w.CompanyId, w.India.Id, w.Clock)); // present

        w.Db.TaxAssignments.RemoveRange(await w.Db.TaxAssignments.ToListAsync());
        (await w.ProfileAsync("GST-18")).IsActive = false;
        await w.Db.SaveChangesAsync();
        Assert.False(await TaxSeeder.RestoreCompanyDefaultAsync(w.Db, w.CompanyId, w.India.Id, w.Clock)); // standard profile inactive
        Assert.False(await TaxSeeder.RestoreCompanyDefaultAsync(w.Db, w.CompanyId, w.Qatar.Id, w.Clock)); // no standard seed
    }

    // ---- T25 / BR4: a new version starts after the last document that used the profile ----------------------------------------------

    [Fact]
    public async Task AddVersion_OnOrBeforeTheLastDocumentDateOfTheProfile_RefusedNamingThatDate_TheDayAfterIsFine()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await w.Profiles.CreateAsync(new(w.India.Id, "G12", "G12", TaxRegime.Gst, TaxTreatment.Taxable, TaxWorld.Version(new DateOnly(2026, 1, 1), await w.GstSetAsync(12m))), null);
        await w.UseVersionOnSaleAsync(p, new DateOnly(2026, 6, 10));
        await w.UseVersionOnSaleAsync(p, new DateOnly(2026, 6, 3));

        foreach (var date in new[] { new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 1) })
        {
            var ex = await TaxWorld.FailsAsync(async () => await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(date, await w.GstSetAsync(18m)), null));
            Assert.Equal("effectiveFrom", ex.Field);
            Assert.Contains("2026-06-10", ex.Message);
        }
        var ok = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 6, 11), await w.GstSetAsync(18m)), null);
        Assert.Equal(new DateOnly(2026, 6, 10), (await w.Db.TaxProfileVersions.SingleAsync(v => v.TaxProfileId == p.Id && v.EffectiveFrom == new DateOnly(2026, 1, 1))).EffectiveTo);
        Assert.Equal(18m, ok!.TotalPercent);
    }

    [Fact]
    public async Task AddVersion_PurchaseDocumentsCountToo()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await w.Profiles.CreateAsync(new(w.India.Id, "G12", "G12", TaxRegime.Gst, TaxTreatment.Taxable, TaxWorld.Version(new DateOnly(2026, 1, 1), await w.GstSetAsync(12m))), null);
        var product = await w.AddProductAsync("PU", taxId: p.LegacyTaxId);
        var order = new PurchaseOrder { Id = Guid.NewGuid(), PurchaseNo = "PUR-1", PurchaseDate = new DateOnly(2026, 7, 7) };
        w.Db.PurchaseOrderLines.Add(new PurchaseOrderLine { Id = Guid.NewGuid(), PurchaseOrderId = order.Id, PurchaseOrder = order, ProductId = product.Id, TaxId = p.LegacyTaxId });
        await w.Db.SaveChangesAsync();

        var ex = await TaxWorld.FailsAsync(async () => await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 7, 7), await w.GstSetAsync(18m)), null));

        Assert.Contains("2026-07-07", ex.Message);
    }

    // ---- T24: changing the company country ------------------------------------------------------------------------------------------

    private static async Task<SettingsController> SettingsAsync(TaxWorld w)
    {
        w.Db.Companies.Add(new Company { Id = w.CompanyId, Code = "DEF", Name = "Sellora Mart" });
        await w.Db.SaveChangesAsync();
        return new SettingsController(w.Db, new BranchService(w.Db, new StockLocationUsageService(w.Db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")) } },
        };
    }

    private static UpdateBusinessSettingsRequest Request(TaxWorld w, Guid? country) =>
        new("Sellora Mart", null, null, null, null, null, null, w.Settings.InvoicePrefix, w.Settings.BaseCurrencyId, CountryId: country);

    [Fact]
    public async Task SettingsPut_CountryChange_RefusedWhileProductsOrDocumentsExist()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var controller = await SettingsAsync(w);
        await w.AddProductAsync("P");

        var result = await controller.Update(Request(w, w.Uae.Id), default);

        Assert.True(controller.ModelState.ContainsKey("countryId"));
        Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(w.India.Id, (await w.Db.BusinessSettings.AsNoTracking().SingleAsync()).CountryId);
        Assert.Equal(5, await w.Db.TaxProfiles.CountAsync()); // nothing seeded
    }

    [Fact]
    public async Task SettingsPut_CountryChange_RefusedWhileASaleOrPurchaseExists()
    {
        foreach (var kind in new[] { "sale", "purchase" })
        {
            var w = await TaxWorld.CreateAsync("IN");
            var controller = await SettingsAsync(w);
            if (kind == "sale") w.Db.SalesInvoices.Add(new SalesInvoice { Id = Guid.NewGuid(), InvoiceNo = "INV-1", InvoiceDate = TaxWorld.Today });
            else w.Db.PurchaseOrders.Add(new PurchaseOrder { Id = Guid.NewGuid(), PurchaseNo = "PUR-1", PurchaseDate = TaxWorld.Today });
            await w.Db.SaveChangesAsync();

            await controller.Update(Request(w, w.Uae.Id), default);

            Assert.True(controller.ModelState.ContainsKey("countryId"), kind);
        }
    }

    [Fact]
    public async Task SettingsPut_CountryChange_OnAnEmptyCompany_SeedsTheNewCountryAndKeepsTheOldProfiles()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var controller = await SettingsAsync(w);

        var result = await controller.Update(Request(w, w.Uae.Id), default);

        var dto = Assert.IsType<BusinessSettingsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(w.Uae.Id, dto.CountryId);
        Assert.Equal(8, await w.Db.TaxProfiles.CountAsync()); // 5 Indian (kept, unused) + 3 UAE
        Assert.Equal(["VAT-5", "VAT-EXEMPT", "VAT-ZERO"], await w.Db.TaxProfiles.Where(p => p.CountryId == w.Uae.Id).OrderBy(p => p.Code).Select(p => p.Code).ToListAsync());
        var uaeDefault = await w.Db.TaxAssignments.SingleAsync(a => a.CountryId == w.Uae.Id);
        Assert.Equal((await w.ProfileAsync("VAT-5", w.Uae)).Id, uaeDefault.SalesTaxProfileId);
        Assert.Equal(2, await w.Db.TaxAssignments.CountAsync());
    }

    [Fact]
    public async Task SettingsPut_OmittedCountry_WhileProductsExist_KeepsCountryAndState_InsteadOfClearingThem()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var controller = await SettingsAsync(w);
        await w.AddProductAsync("P");

        var result = await controller.Update(Request(w, null), default);

        var dto = Assert.IsType<BusinessSettingsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(w.India.Id, dto.CountryId);
        Assert.Equal(5, await w.Db.TaxProfiles.CountAsync());
    }

    [Fact]
    public async Task SettingsPut_SameCountryOrFirstCountry_IsNotACountryChangeCheckedAgainstProducts()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var controller = await SettingsAsync(w);
        await w.AddProductAsync("P");

        var result = await controller.Update(Request(w, w.India.Id), default); // unchanged

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(controller.ModelState.IsValid);
    }
}
