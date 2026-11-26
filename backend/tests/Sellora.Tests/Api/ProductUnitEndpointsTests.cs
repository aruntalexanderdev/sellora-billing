using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;
using V = Sellora.Domain.Inventory.ProductUnitConfigurationValidator;

namespace Sellora.Tests.Api;

/// <summary>
/// Controller-level behaviour of the Phase 2 product-unit endpoints, run directly against EF InMemory (the pattern of
/// <c>AccountsReportsTests</c>; there is no in-process host in the test suite): product detail lock projection, the
/// unit-preview endpoint, multi-error ValidationProblemDetails, the pricing grid (active + sellable rows in
/// hierarchy order, SuggestedPrice), the POS catalog and the pricing PUT error mapping.
/// </summary>
public class ProductUnitEndpointsTests
{
    private sealed record Fixture(
        SelloraDbContext Db, Product Pen, ProductUnit Pc, ProductUnit Box, ProductUnit HCtn, ProductUnit FCtn,
        Unit PcUnit, Unit BoxUnit, Unit HCtnUnit, Unit FCtnUnit, Tax Tax, User User);

    /// <summary>Ball Pen: Piece 1, Box 10, Half Carton 50, Full Carton 100 (SortOrder 0–3).</summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var (pcUnit, boxUnit, hctnUnit, fctnUnit) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton());
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 12%", RatePercent = 12m };
        ProductUnit pc = null!, box = null!, hctn = null!, fctn = null!;
        var pen = new ProductBuilder().WithSku("STN-PEN").WithName("Ball Pen").WithBaseUnit(pcUnit).WithTax(tax)
            .WithUnits(p =>
            {
                pc = TestProductUnits.Base(p);
                box = TestProductUnits.Pack(p, boxUnit, pc, 10m);
                hctn = TestProductUnits.Pack(p, hctnUnit, box, 5m);
                fctn = TestProductUnits.Pack(p, fctnUnit, hctn, 2m);
            })
            .Build();
        db.AddRange(role, user, pcUnit, boxUnit, hctnUnit, fctnUnit, tax, pen);
        await db.SaveChangesAsync();
        await TaxFixtures.UseAsCompanyDefaultAsync(db, tax);
        return new Fixture(db, pen, pc, box, hctn, fctn, pcUnit, boxUnit, hctnUnit, fctnUnit, tax, user);
    }

    private static T WithContext<T>(T controller, Guid? userId = null) where T : ControllerBase
    {
        var http = new DefaultHttpContext();
        if (userId is { } id) http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>The POS catalog uses a filtered Include on a tracking query; the fixture's context already tracks every
    /// row (fixup would add them all back), so run it as a fresh request would: with an empty change tracker.</summary>
    private static SalesController Catalog(Fixture f)
    {
        f.Db.ChangeTracker.Clear();
        return WithContext(new SalesController(f.Db, new SalesService(f.Db), new FakeEffectivePermissions()));
    }

    private static ProductsController Products(Fixture f) => WithContext(new ProductsController(f.Db, new ProductService(f.Db), new TaxResolver(f.Db)));
    private static PricingController Pricing(Fixture f) => WithContext(new PricingController(f.Db, new PricingService(f.Db), new FakeEffectivePermissions("Inventory.Price", "Inventory.View"), new Sellora.Infrastructure.Services.TaxResolver(f.Db)), f.User.Id);

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    private static ValidationProblemDetails Problem(IActionResult result) =>
        Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);

    private static async Task SetPriceAsync(Fixture f, ProductUnit unit, decimal price) =>
        await new PricingService(f.Db).SetPriceAsync(unit.Id, price, f.User.Id);

    private static UpsertProductUnitRequest Req(Guid unitId, Guid? inner, decimal? qty, Guid? id = null, string? barcode = null) =>
        new(id, unitId, inner, qty, null, barcode);

    // ------------------------------------------------------------------ products: detail and preview

    [Fact]
    public async Task ProductsGet_PenWithPricedHalfCarton_ProjectsHierarchyAndLocks()
    {
        var f = await SeedAsync();
        await SetPriceAsync(f, f.HCtn, 450m);

        var detail = Ok(await Products(f).Get(f.Pen.Id, default));

        Assert.True(detail.IsBaseUnitLocked);
        Assert.Equal(new[] { "pc", "box", "hctn", "fctn" }, detail.Units.Select(u => u.UnitCode));
        Assert.Equal(new[] { 0, 1, 2, 3 }, detail.Units.Select(u => u.Depth));
        Assert.Equal(new Guid?[] { null, f.PcUnit.Id, f.BoxUnit.Id, f.HCtnUnit.Id }, detail.Units.Select(u => u.InnerUnitId));
        Assert.Equal(new[] { 1m, 10m, 50m, 100m }, detail.Units.Select(u => u.ConversionFactorToBase));
        Assert.Equal(new string?[] { ProductUnitUsage.ContainedInLockedUnit, ProductUnitUsage.ContainedInLockedUnit, ProductUnitUsage.Prices, null },
            detail.Units.Select(u => u.LockReason));
        Assert.Equal(new[] { true, true, true, false }, detail.Units.Select(u => u.IsLocked));
        Assert.Equal(new[] { "Piece", "Box", "Half Carton", "Full Carton" }, detail.Units.Select(u => u.DisplayName));
    }

    [Fact]
    public async Task ProductsGet_NoLocksNoHistory_BaseUnitNotLocked()
    {
        var f = await SeedAsync();

        var detail = Ok(await Products(f).Get(f.Pen.Id, default));

        Assert.False(detail.IsBaseUnitLocked);
        Assert.All(detail.Units, u => Assert.Null(u.LockReason));
    }

    [Fact]
    public async Task PreviewUnits_InvalidHierarchy_Returns200WithIsValidFalseAndFieldErrors()
    {
        var f = await SeedAsync();

        var response = Ok(await Products(f).PreviewUnits(new UnitPreviewRequest(null, f.PcUnit.Id,
        [
            Req(f.PcUnit.Id, null, 1m),
            Req(f.BoxUnit.Id, f.HCtnUnit.Id, 2m),
            Req(f.HCtnUnit.Id, f.BoxUnit.Id, 5m),
        ]), default));

        Assert.False(response.IsValid);
        Assert.Contains(response.Errors, e => e.Field == "Units[1].InnerUnitId");
        Assert.Contains(response.Errors, e => e.Field == "Units[2].InnerUnitId");
        Assert.Equal("1 Piece", Assert.Single(response.Rows).Chain);
    }

    [Fact]
    public async Task PreviewUnits_ExistingProductWithLockedChange_ReturnsLockedErrorAndRows()
    {
        var f = await SeedAsync();
        await SetPriceAsync(f, f.Box, 95m);

        var response = Ok(await Products(f).PreviewUnits(new UnitPreviewRequest(f.Pen.Id, f.PcUnit.Id,
        [
            Req(f.PcUnit.Id, null, 1m, f.Pc.Id),
            Req(f.BoxUnit.Id, f.PcUnit.Id, 12m, f.Box.Id),
            Req(f.HCtnUnit.Id, f.BoxUnit.Id, 5m, f.HCtn.Id),
            Req(f.FCtnUnit.Id, f.HCtnUnit.Id, 2m, f.FCtn.Id),
        ]), default));

        Assert.False(response.IsValid);
        Assert.Contains(response.Errors, e => e.Field == "Units[1].InnerQuantity" && e.Message == V.LockedMessage);
        Assert.Equal(new[] { true, true, false, false }, response.Rows.OrderBy(r => r.Index).Select(r => r.IsLocked));
    }

    [Fact]
    public async Task PreviewUnits_ValidPen_ReturnsChainsAndSortOrder()
    {
        var f = await SeedAsync();

        var response = Ok(await Products(f).PreviewUnits(new UnitPreviewRequest(null, f.PcUnit.Id,
        [
            Req(f.FCtnUnit.Id, f.HCtnUnit.Id, 2m),
            Req(f.PcUnit.Id, null, null),
            Req(f.HCtnUnit.Id, f.BoxUnit.Id, 5m),
            Req(f.BoxUnit.Id, f.PcUnit.Id, 10m),
        ]), default));

        Assert.True(response.IsValid);
        Assert.Empty(response.Errors);
        Assert.Equal(new[] { 1, 3, 2, 0 }, response.Rows.Select(r => r.Index));   // ordered by SortOrder
        Assert.Equal(new short[] { 0, 1, 2, 3 }, response.Rows.Select(r => r.SortOrder));
        Assert.Equal("1 Full Carton = 2 Half Carton = 10 Box = 100 Piece", response.Rows[^1].Chain);
    }

    [Fact]
    public async Task PreviewUnits_UnknownProductId_Returns404()
    {
        var f = await SeedAsync();

        var result = await Products(f).PreviewUnits(new UnitPreviewRequest(Guid.NewGuid(), f.PcUnit.Id, [Req(f.PcUnit.Id, null, 1m)]), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ProductsCreate_SeveralHierarchyErrors_ValidationProblemCarriesEveryError()
    {
        var f = await SeedAsync();
        var request = new UpsertProductRequest("STN-PEN-2", "Gel Pen", null, null, f.PcUnit.Id, 0m,
        [
            Req(f.PcUnit.Id, null, 1m, barcode: "12 34"),
            Req(f.BoxUnit.Id, f.BoxUnit.Id, 10m),
            Req(f.HCtnUnit.Id, f.PcUnit.Id, 2.5m),
        ]);

        var result = await Products(f).Create(request, default);

        var problem = Problem(result.Result!);
        Assert.Equal(400, problem.Status ?? 400);
        Assert.Equal(new[] { "Units[0].Barcode", "Units[1].InnerUnitId", "Units[2].InnerQuantity" }, problem.Errors.Keys.Order());
        Assert.Equal("A unit cannot contain itself.", Assert.Single(problem.Errors["Units[1].InnerUnitId"]));
        Assert.Equal(1, await f.Db.Products.CountAsync());
    }

    [Fact]
    public async Task ProductsUpdate_LockedChangeAndBaseChange_ValidationProblemCarriesEveryError()
    {
        var f = await SeedAsync();
        await SetPriceAsync(f, f.Box, 95m);
        var dozen = TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null);
        f.Db.Units.Add(dozen);
        await f.Db.SaveChangesAsync();
        var request = new UpsertProductRequest("STN-PEN", "Ball Pen", null, null, dozen.Id, 0m,
        [
            Req(dozen.Id, null, 1m, f.Pc.Id),
            Req(f.BoxUnit.Id, dozen.Id, 12m, f.Box.Id),
            Req(f.HCtnUnit.Id, f.BoxUnit.Id, 5m, f.HCtn.Id),
            Req(f.FCtnUnit.Id, f.HCtnUnit.Id, 2m, f.FCtn.Id),
        ]);

        var problem = Problem((await Products(f).Update(f.Pen.Id, request, default)).Result!);

        Assert.Equal(V.BaseChangeWithLockedUnitsMessage, Assert.Single(problem.Errors["BaseUnitId"]));
        Assert.Contains(problem.Errors.Keys, k => k.StartsWith("Units[1]."));
    }

    // ------------------------------------------------------------------ pricing grid

    private static async Task<Product> AddOtherProductAsync(Fixture f, string name, bool active = true)
    {
        var builder = new ProductBuilder().WithSku(name.ToUpperInvariant()).WithName(name).WithBaseUnit(f.PcUnit).WithTax(f.Tax).WithBaseUnitRow();
        if (!active) builder.Inactive();
        var product = builder.Build();
        f.Db.Add(product);
        await f.Db.SaveChangesAsync();
        return product;
    }

    [Fact]
    public async Task PricingList_OnlyActiveSellableRowsOfActiveProducts_InNameThenSortOrder()
    {
        var f = await SeedAsync();
        f.HCtn.IsSellable = false;
        f.FCtn.IsActive = false;
        await f.Db.SaveChangesAsync();
        var apple = await AddOtherProductAsync(f, "Apple Sharpener");
        await AddOtherProductAsync(f, "Zz Retired", active: false);

        var rows = Ok(await Pricing(f).List(null, default));

        Assert.Equal(new[] { (apple.Id, "pc"), (f.Pen.Id, "pc"), (f.Pen.Id, "box") }, rows.Select(r => (r.ProductId, r.UnitCode)));
        Assert.Equal(new short[] { 0, 0, 1 }, rows.Select(r => r.SortOrder));
        Assert.Equal(new[] { true, true, false }, rows.Select(r => r.IsBaseUnit));
        Assert.Equal("pc", rows[2].BaseUnitLabel);
        Assert.Equal(10m, rows[2].ConversionFactorToBase);
    }

    [Fact]
    public async Task PricingList_BasePriced_SuggestsBasePriceTimesFactor()
    {
        var f = await SeedAsync();
        await SetPriceAsync(f, f.Pc, 10m);
        await SetPriceAsync(f, f.HCtn, 450m);

        var rows = Ok(await Pricing(f).List(null, default));

        Assert.Equal(new decimal?[] { 10m, 100m, 500m, 1000m }, rows.Select(r => r.SuggestedPrice));
        Assert.Equal(new decimal?[] { 10m, null, 450m, null }, rows.Select(r => r.CurrentPrice));
    }

    [Fact]
    public async Task PricingList_OnlyBoxPriced_SuggestsFromBoxPerPiece()
    {
        var f = await SeedAsync();
        await SetPriceAsync(f, f.Box, 95m);

        var rows = Ok(await Pricing(f).List(null, default));

        Assert.Equal(new decimal?[] { 9.5m, 95m, 475m, 950m }, rows.Select(r => r.SuggestedPrice));
    }

    [Fact]
    public async Task PricingList_SeveralNonBasePriced_AnchorIsSmallestFactor()
    {
        var f = await SeedAsync();
        await SetPriceAsync(f, f.FCtn, 800m);
        await SetPriceAsync(f, f.Box, 95m);

        var rows = Ok(await Pricing(f).List(null, default));

        Assert.Equal(9.5m, rows.Single(r => r.ProductUnitId == f.Pc.Id).SuggestedPrice);
        Assert.Equal(950m, rows.Single(r => r.ProductUnitId == f.FCtn.Id).SuggestedPrice);
    }

    [Fact]
    public async Task PricingList_NothingPriced_SuggestedPriceNull()
    {
        var f = await SeedAsync();

        var rows = Ok(await Pricing(f).List(null, default));

        Assert.All(rows, r => Assert.Null(r.SuggestedPrice));
        Assert.All(rows, r => Assert.Null(r.CurrentPrice));
    }

    [Fact]
    public async Task PricingList_SuggestedPriceRoundedToTwoDecimals()
    {
        var f = await SeedAsync();
        await SetPriceAsync(f, f.Box, 99.99m); // 9.999 per piece

        var rows = Ok(await Pricing(f).List(null, default));

        Assert.Equal(10.00m, rows.Single(r => r.ProductUnitId == f.Pc.Id).SuggestedPrice);
        Assert.Equal(499.95m, rows.Single(r => r.ProductUnitId == f.HCtn.Id).SuggestedPrice);
    }

    [Fact]
    public async Task PricingSetPrice_InactiveUnit_ValidationProblemOnProductUnitId()
    {
        var f = await SeedAsync();
        f.FCtn.IsActive = false;
        await f.Db.SaveChangesAsync();

        var problem = Problem(await Pricing(f).SetPrice(f.FCtn.Id, new SetPriceRequest(800m), default));

        Assert.Equal("Full Carton is no longer active and cannot be priced.", Assert.Single(problem.Errors["ProductUnitId"]));
    }

    [Fact]
    public async Task PricingSetPriceAndHistory_ByProductUnitId()
    {
        var f = await SeedAsync();
        var controller = Pricing(f);

        Assert.IsType<NoContentResult>(await controller.SetPrice(f.Box.Id, new SetPriceRequest(95m), default));
        var history = Ok(await controller.History(f.Box.Id, default));

        Assert.Equal(95m, Assert.Single(history).Price);
        Assert.Empty(Ok(await controller.History(f.Pc.Id, default)));
    }

    // ------------------------------------------------------------------ POS catalog

    [Fact]
    public async Task SalesCatalog_ActiveSellableUnitsInSortOrderWithDefaultsDecimalsCodesAndPrices()
    {
        var f = await SeedAsync();
        f.HCtn.IsSellable = false;
        f.FCtn.IsActive = false;
        f.Box.IsDefaultSalesUnit = true;
        f.Box.Label = "Box of 10";
        await f.Db.SaveChangesAsync();
        await SetPriceAsync(f, f.Box, 95m);

        var catalog = Ok(await Catalog(f).Catalog(null, default));

        var pen = Assert.Single(catalog);
        Assert.Equal("pc", pen.BaseUnitLabel);
        Assert.Equal(
            new[] { (f.Pc.Id, f.PcUnit.Id, "pc", "Piece", 1m, (short)0, true, false, (decimal?)null), (f.Box.Id, f.BoxUnit.Id, "box", "Box of 10", 10m, (short)0, false, true, 95m) },
            pen.Units.Select(u => (u.ProductUnitId, u.UnitId, u.UnitCode, u.DisplayName, u.ConversionFactorToBase, u.DecimalPlaces, u.IsBaseUnit, u.IsDefault, u.CurrentPrice)));
    }

    [Fact]
    public async Task SalesCatalog_UnitsOrderedBySortOrderNotInsertionOrder()
    {
        var f = await SeedAsync();
        f.Pc.SortOrder = 3;
        f.FCtn.SortOrder = 0;
        await f.Db.SaveChangesAsync();

        var catalog = Ok(await Catalog(f).Catalog(null, default));

        Assert.Equal(new[] { "fctn", "box", "hctn", "pc" }, Assert.Single(catalog).Units.Select(u => u.UnitCode));
    }

    [Fact]
    public async Task SalesCatalog_KilogramBase_ReportsBaseDecimalPlaces()
    {
        var f = await SeedAsync();
        var kg = TestUnits.Kilogram();
        var rice = new ProductBuilder().WithSku("RICE").WithName("Rice").WithBaseUnit(kg).WithTax(f.Tax).WithBaseUnitRow("Loose kg").Build();
        f.Db.AddRange(kg, rice);
        await f.Db.SaveChangesAsync();

        var catalog = Ok(await Catalog(f).Catalog("rice", default));

        var unit = Assert.Single(Assert.Single(catalog).Units);
        Assert.Equal(("kg", "Loose kg", (short)3), (unit.UnitCode, unit.DisplayName, unit.DecimalPlaces));
    }
    // ------------------------------------------------------------------ 50-row limit (review m3)

    private static UpsertProductUnitRequest[] FiftyOneRows(Fixture f) =>
        Enumerable.Range(0, V.MaxUnits).Select(i => Req(Guid.NewGuid(), f.PcUnit.Id, i + 2m)).Prepend(Req(f.PcUnit.Id, null, 1m)).ToArray();

    [Theory]
    [InlineData(typeof(UpsertProductRequest))]
    [InlineData(typeof(UnitPreviewRequest))]
    public void UpsertAndPreviewRequests_UnitsParameterCappedAt50ForModelValidation(Type request)
    {
        // Positional records: MVC validates the constructor parameter's DataAnnotations, and [ApiController] answers a
        // violation with 400 ValidationProblem keyed "Units" before the action runs.
        var parameter = request.GetConstructors().Single().GetParameters().Single(p => p.Name == "Units");
        var maxLength = Assert.Single(parameter.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), false)
            .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>());

        Assert.Equal(V.MaxUnits, maxLength.Length);
        Assert.False(maxLength.IsValid(new UpsertProductUnitRequest[V.MaxUnits + 1]));
        Assert.True(maxLength.IsValid(new UpsertProductUnitRequest[V.MaxUnits]));
    }

    [Fact]
    public async Task ProductsCreate_MoreThan50UnitsPastModelValidation_400OnUnits()
    {
        var f = await SeedAsync();

        var problem = Problem((await Products(f).Create(new UpsertProductRequest("BIG", "Big", null, null, f.PcUnit.Id, 0m, FiftyOneRows(f)), default)).Result!);

        Assert.Equal(V.TooManyUnitsMessage, Assert.Single(Assert.Single(problem.Errors, e => e.Key == "Units").Value));
        Assert.Single(problem.Errors);
    }

    [Fact]
    public async Task ProductsUpdate_MoreThan50UnitsPastModelValidation_400OnUnits()
    {
        var f = await SeedAsync();

        var problem = Problem((await Products(f).Update(f.Pen.Id, new UpsertProductRequest("STN-PEN", "Ball Pen", null, null, f.PcUnit.Id, 0m, FiftyOneRows(f)), default)).Result!);

        Assert.Equal(new[] { "Units" }, problem.Errors.Keys);
    }

    [Fact]
    public async Task PreviewUnits_MoreThan50UnitsPastModelValidation_200WithIsValidFalseOnUnits()
    {
        var f = await SeedAsync();

        var response = Ok(await Products(f).PreviewUnits(new UnitPreviewRequest(f.Pen.Id, f.PcUnit.Id, FiftyOneRows(f)), default));

        Assert.False(response.IsValid);
        Assert.Equal(new FieldErrorDto("Units", V.TooManyUnitsMessage), Assert.Single(response.Errors));
        Assert.Empty(response.Rows);
    }
}
