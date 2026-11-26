using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.TaxProfiles;

/// <summary>
/// TP-5a (F7): the product, pricing, stock and POS-catalog screens read the product's tax name and rate from its resolved sales profile (company country, version valid today),
/// never from the legacy <c>Product.Tax</c> mirror. Every product here points at a <c>TaxId</c> with no <c>Taxes</c> row at all, so a read that still joined the mirror would drop it.
/// </summary>
public class TaxReadPathTests
{
    private sealed record Fx(TaxWorld W, Product Default, ProductUnit DefaultUnit, Product Reduced, ProductUnit ReducedUnit);

    private static async Task<Fx> SetupAsync(bool defaultAssignment = true, string country = "IN")
    {
        var w = await TaxWorld.CreateAsync(country);
        if (!defaultAssignment) w.Db.TaxAssignments.RemoveRange(w.Db.TaxAssignments.Where(a => a.Scope == TaxScope.Company));
        await w.Db.SaveChangesAsync();

        async Task<(Product, ProductUnit)> AddAsync(string sku, string? profile)
        {
            var product = new Product { Id = Guid.NewGuid(), Sku = sku, Name = "Item " + sku, BaseUnitId = w.Piece.Id, TaxId = Guid.NewGuid() /* no Taxes row */ };
            w.Db.Products.Add(product);
            if (profile is not null)
            {
                var p = await w.ProfileAsync(profile);
                w.Db.TaxAssignments.Add(new TaxAssignment { Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = p.CountryId, SalesTaxProfileId = p.Id, CreatedAt = DateTime.UtcNow });
            }
            var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = w.Piece.Id, IsBaseUnit = true, ConversionFactorToBase = 1m, IsSellable = true, IsPurchasable = true, IsActive = true };
            w.Db.ProductUnits.Add(unit);
            w.Db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = unit.Id, Price = 10m, CreatedByUserId = Guid.NewGuid() });
            await w.Db.SaveChangesAsync();
            return (product, unit);
        }
        var (d, du) = await AddAsync("DEF", null);        // company default GST-18
        var (r, ru) = await AddAsync("RED", "GST-5");     // product override
        w.Db.ChangeTracker.Clear();
        return new Fx(w, d, du, r, ru);
    }

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static T Ok<T>(ActionResult<T> r) => (T)Assert.IsType<OkObjectResult>(r.Result).Value!;

    [Fact]
    public async Task ProductList_ShowsTheProfileName_AndKeepsProductsWhoseMirrorTaxIsGone()
    {
        var f = await SetupAsync();

        var page = Ok(await WithContext(new ProductsController(f.W.Db, new ProductService(f.W.Db), f.W.Resolver)).List(null, 1, 20, default));

        Assert.Equal(2, page.Total);
        Assert.Equal("GST 18%", page.Items.Single(i => i.Sku == "DEF").TaxLabel);
        Assert.Equal("GST 5%", page.Items.Single(i => i.Sku == "RED").TaxLabel);
    }

    [Fact]
    public async Task ProductDetail_ShowsTheProfileNameAndTheRateOfItsVersionValidToday_TaxIdIsStillTheDeprecatedMirrorValue()
    {
        var f = await SetupAsync();

        var detail = Ok(await WithContext(new ProductsController(f.W.Db, new ProductService(f.W.Db), f.W.Resolver)).Get(f.Reduced.Id, default));

        Assert.Equal(("GST 5%", 5m), (detail.TaxLabel, detail.TaxRatePercent));
        Assert.Equal(f.Reduced.TaxId, detail.TaxId); // deprecated, still the stored mirror id (TP-5b drops it)
        Assert.Equal("GST-5", detail.SalesTaxProfile!.Code);
    }

    [Fact]
    public async Task ProductDetail_ARateChangeShowsAtOnce_NotTheMirrorsStaleRate()
    {
        var f = await SetupAsync();
        var profile = await f.W.ProfileAsync("GST-5");
        await f.W.Profiles.AddVersionAsync(profile.Id, TaxWorld.Version(TaxWorld.Today, await f.W.GstSetAsync(6m)), null); // new version from today
        f.W.Db.ChangeTracker.Clear();

        var detail = Ok(await WithContext(new ProductsController(f.W.Db, new ProductService(f.W.Db), f.W.Resolver)).Get(f.Reduced.Id, default));

        Assert.Equal(6m, detail.TaxRatePercent);
    }

    [Fact]
    public async Task ProductDetailAndList_WithoutACompanyCountry_DoNotFail_AndShowNoTax()
    {
        var f = await SetupAsync();
        (await f.W.Db.BusinessSettings.SingleAsync()).CountryId = null;
        await f.W.Db.SaveChangesAsync();
        var controller = WithContext(new ProductsController(f.W.Db, new ProductService(f.W.Db), f.W.Resolver));

        var detail = Ok(await controller.Get(f.Default.Id, default));
        var list = Ok(await controller.List(null, 1, 20, default));

        Assert.Equal(("", 0m), (detail.TaxLabel, detail.TaxRatePercent));
        Assert.All(list.Items, i => Assert.Equal("", i.TaxLabel));
        Assert.Equal(2, list.Total);
    }

    [Fact]
    public async Task Pricing_ShowsTheProfileName()
    {
        var f = await SetupAsync();
        var controller = WithContext(new PricingController(f.W.Db, new PricingService(f.W.Db), new FakeEffectivePermissions("Inventory.Price", "Inventory.View"), f.W.Resolver));

        var rows = Ok(await controller.List(null, default));

        Assert.Equal(2, rows.Count);
        Assert.Equal("GST 18%", rows.Single(r => r.ProductSku == "DEF").TaxLabel);
        Assert.Equal("GST 5%", rows.Single(r => r.ProductSku == "RED").TaxLabel);
    }

    [Fact]
    public async Task StockDetail_ShowsTheProfileName()
    {
        var f = await SetupAsync();
        var controller = WithContext(new StockController(f.W.Db, new FakeEffectivePermissions("Inventory.View", "Reports.Stock", "Reports.Movement"), f.W.Resolver));

        var detail = Ok(await controller.Detail(f.Reduced.Id, default));

        Assert.Equal("GST 5%", detail.TaxLabel);
    }

    [Fact]
    public async Task Catalog_UsesTheProfilesRate_AndNeedsNoMirrorTax()
    {
        var f = await SetupAsync();
        var controller = WithContext(new SalesController(f.W.Db, new SalesService(f.W.Db), new FakeEffectivePermissions("Sales.Create"), f.W.Resolver));

        var items = Ok(await controller.Catalog(null, null, default));

        Assert.Equal(2, items.Count);
        var def = items.Single(i => i.Sku == "DEF");
        var red = items.Single(i => i.Sku == "RED");
        Assert.Equal((18m, 5m), (def.TaxRatePercent, red.TaxRatePercent));
        Assert.Null(def.TaxWarning);
        Assert.Null(red.TaxWarning);
    }

    [Fact]
    public async Task Catalog_WithNoApplicableProfile_ShowsZeroWithAWarning_NeverTheMirrorRate()
    {
        var f = await SetupAsync(defaultAssignment: false); // DEF resolves to nothing; RED still has its own override
        var controller = WithContext(new SalesController(f.W.Db, new SalesService(f.W.Db), new FakeEffectivePermissions("Sales.Create"), f.W.Resolver));

        var items = Ok(await controller.Catalog(null, null, default));

        var def = items.Single(i => i.Sku == "DEF");
        Assert.Equal(0m, def.TaxRatePercent);
        Assert.Contains("No tax rate", def.TaxWarning);
        Assert.Equal((5m, (string?)null), (items.Single(i => i.Sku == "RED").TaxRatePercent, items.Single(i => i.Sku == "RED").TaxWarning));
    }

    [Fact]
    public async Task Catalog_AProfileWithoutAVersionToday_ShowsZeroWithAWarning()
    {
        var f = await SetupAsync();
        var versions = f.W.Db.TaxProfileVersions.Where(v => v.TaxProfile.Code == "GST-5");
        foreach (var v in versions) v.EffectiveFrom = TaxWorld.Today.AddDays(400); // starts far in the future
        await f.W.Db.SaveChangesAsync();
        f.W.Db.ChangeTracker.Clear();
        var controller = WithContext(new SalesController(f.W.Db, new SalesService(f.W.Db), new FakeEffectivePermissions("Sales.Create"), f.W.Resolver));

        var red = Ok(await controller.Catalog(null, null, default)).Single(i => i.Sku == "RED");

        Assert.Equal(0m, red.TaxRatePercent);
        Assert.NotNull(red.TaxWarning);
    }

    [Fact]
    public async Task ResolveCurrentSalesProfiles_IsBatched_NamesTheProfile_AndOmitsProductsWithoutOne()
    {
        var f = await SetupAsync();
        var ids = new List<Guid> { f.Default.Id, f.Reduced.Id };
        for (var i = 0; i < 40; i++)
        {
            var p = new Product { Id = Guid.NewGuid(), Sku = "B" + i, Name = "Bulk " + i, BaseUnitId = f.W.Piece.Id, TaxId = Guid.NewGuid() };
            f.W.Db.Products.Add(p);
            ids.Add(p.Id);
        }
        await f.W.Db.SaveChangesAsync();

        var result = await f.W.Resolver.ResolveCurrentSalesProfilesAsync(f.W.India.Id, TaxWorld.Today, ids);

        Assert.Equal(42, result.Count);
        Assert.Equal(("GST 5%", 5m), (result[f.Reduced.Id].ProfileName, result[f.Reduced.Id].Percent));
        Assert.Equal(("GST 18%", 18m), (result[f.Default.Id].ProfileName, result[f.Default.Id].Percent));
        Assert.Empty(await f.W.Resolver.ResolveCurrentSalesProfilesAsync(f.W.India.Id, TaxWorld.Today, []));

        // No company default: only the product with its own override resolves.
        f.W.Db.TaxAssignments.RemoveRange(f.W.Db.TaxAssignments.Where(a => a.Scope == TaxScope.Company));
        await f.W.Db.SaveChangesAsync();
        var onlyOverride = await f.W.Resolver.ResolveCurrentSalesProfilesAsync(f.W.India.Id, TaxWorld.Today, ids);
        Assert.Equal([f.Reduced.Id], onlyOverride.Keys);
    }

    [Fact]
    public async Task TaxesList_IsDeprecated_AndSaysSoInTheResponseHeader()
    {
        var f = await SetupAsync();
        var controller = WithContext(new TaxesController(f.W.Db));

#pragma warning disable CS0618 // the endpoint is obsolete on purpose: this test proves the deprecation signal
        var result = await controller.List(default);
#pragma warning restore CS0618

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("true", controller.Response.Headers["Deprecation"].ToString());
        Assert.NotEmpty(typeof(TaxesController).GetMethod(nameof(TaxesController.List))!.GetCustomAttributes(typeof(ObsoleteAttribute), false));
    }
}
