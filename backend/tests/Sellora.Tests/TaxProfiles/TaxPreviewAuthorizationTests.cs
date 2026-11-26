using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-2 / BR19a / T15: the code matching the direction (403), Sales.BillDiscount for a sale with a bill discount (403) and the company cap (400 on billDiscount) of POST /tax/preview.</summary>
public class TaxPreviewAuthorizationTests
{
    private static readonly DateOnly D = TaxWorld.Today;

    private sealed record Fixture(TaxWorld W, Product Product, ProductUnit Unit, Supplier Supplier);

    private static async Task<Fixture> SetupAsync(decimal? cap = null)
    {
        var w = await TaxWorld.CreateAsync();
        var p = await w.AddProductAsync("P1", taxId: (await w.MirrorAsync(await w.ProfileAsync("GST-18", w.India))).Id);
        var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = p.Id, UnitId = w.Piece.Id, IsBaseUnit = true, ConversionFactorToBase = 1m };
        w.Db.ProductUnits.Add(unit);
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supp", CurrencyId = w.Db.Currencies.First().Id };
        w.Db.Suppliers.Add(supplier);
        w.Settings.StateId = Guid.NewGuid();
        w.Settings.MaxBillDiscountPercent = cap;
        await w.Db.SaveChangesAsync();
        return new Fixture(w, p, unit, supplier);
    }

    private static TaxPreviewController Controller(Fixture f, params string[] companyCodes) => Controller(f, new FakeEffectivePermissions(companyCodes));

    private static TaxPreviewController Controller(Fixture f, Sellora.Domain.Abstractions.IEffectivePermissionService permissions) =>
        new(new TaxDocumentPreviewService(f.W.Db, f.W.Resolver), permissions) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static TaxDocumentPreviewRequest Request(Fixture f, TaxDirection direction, decimal? pct = null, decimal? amount = null) =>
        new(direction, direction == TaxDirection.Purchase ? f.Supplier.Id : null, D, false, pct, amount, null, [new TaxPreviewLineRequest(f.Product.Id, f.Unit.Id, 10, 100)]);

    private static async Task<int> StatusAsync(TaxPreviewController c, TaxDocumentPreviewRequest r)
    {
        var result = (await c.Preview(r, default)).Result;
        return result switch
        {
            OkObjectResult => 200,
            ObjectResult o => o.StatusCode ?? 500,
            _ => throw new Xunit.Sdk.XunitException("Unexpected result " + result),
        };
    }

    private static async Task<string> BadRequestFieldAsync(TaxPreviewController c, TaxDocumentPreviewRequest r)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>((await c.Preview(r, default)).Result);
        Assert.Equal(400, result.StatusCode ?? 400);
        return Assert.Single(Assert.IsAssignableFrom<ValidationProblemDetails>(result.Value).Errors).Key;
    }

    [Fact]
    public async Task Preview_PurchaseCreateOnly_PreviewingSale_Is403()
    {
        var f = await SetupAsync();

        Assert.Equal(403, await StatusAsync(Controller(f, "Purchase.Create"), Request(f, TaxDirection.Sale)));
    }

    [Fact]
    public async Task Preview_SalesCreateOnly_PreviewingPurchase_Is403()
    {
        var f = await SetupAsync();

        Assert.Equal(403, await StatusAsync(Controller(f, "Sales.Create"), Request(f, TaxDirection.Purchase)));
    }

    [Fact]
    public async Task Preview_MatchingCode_IsOk_ForOwnDirection()
    {
        var f = await SetupAsync();

        Assert.Equal(200, await StatusAsync(Controller(f, "Sales.Create"), Request(f, TaxDirection.Sale)));
        Assert.Equal(200, await StatusAsync(Controller(f, "Purchase.Create"), Request(f, TaxDirection.Purchase)));
    }

    [Fact]
    public async Task Preview_SaleWithBillDiscount_WithoutBillDiscountCode_Is403_AndWithoutDiscountIsOk()
    {
        var f = await SetupAsync();
        var controller = Controller(f, "Sales.Create");

        Assert.Equal(403, await StatusAsync(controller, Request(f, TaxDirection.Sale, pct: 5m)));
        Assert.Equal(403, await StatusAsync(controller, Request(f, TaxDirection.Sale, amount: 50m)));
        Assert.Equal(200, await StatusAsync(controller, Request(f, TaxDirection.Sale)));
        Assert.Equal(200, await StatusAsync(controller, Request(f, TaxDirection.Sale, pct: 0m)));
    }

    [Fact]
    public async Task Preview_SaleBillDiscountAtCap_IsOk_AboveCap_Is400OnBillDiscount()
    {
        var f = await SetupAsync(cap: 10m);
        var controller = Controller(f, "Sales.Create", "Sales.BillDiscount");

        Assert.Equal(200, await StatusAsync(controller, Request(f, TaxDirection.Sale, pct: 10m)));
        Assert.Equal("billDiscount", await BadRequestFieldAsync(controller, Request(f, TaxDirection.Sale, pct: 12m)));
    }

    [Fact]
    public async Task Preview_SaleBillDiscount_WithNoCap_IsOk()
    {
        var f = await SetupAsync(cap: null);

        Assert.Equal(200, await StatusAsync(Controller(f, "Sales.Create", "Sales.BillDiscount"), Request(f, TaxDirection.Sale, pct: 50m)));
    }

    [Fact]
    public async Task Preview_PurchaseBillDiscount_NeedsNoBillDiscountCode_AndIgnoresTheSalesCap()
    {
        var f = await SetupAsync(cap: 10m);

        Assert.Equal(200, await StatusAsync(Controller(f, "Purchase.Create"), Request(f, TaxDirection.Purchase, pct: 50m)));
    }

    [Fact]
    public async Task Preview_CodeGrantedAtAnotherBranchOnly_Is403()
    {
        var f = await SetupAsync();
        var otherBranch = Guid.NewGuid();
        var elsewhere = FakeScopedPermissions.AtBranch(otherBranch, "Sales.Create", "Sales.BillDiscount", "Purchase.Create");

        Assert.Equal(403, await StatusAsync(Controller(f, elsewhere), Request(f, TaxDirection.Sale)));
        Assert.Equal(403, await StatusAsync(Controller(f, elsewhere), Request(f, TaxDirection.Purchase)));
    }

    [Fact]
    public async Task Preview_CodesGrantedAtTheWorkingBranch_AreAccepted()
    {
        var f = await SetupAsync(cap: 10m);
        var here = FakeScopedPermissions.AtBranch(TestCompany.MainBranchId, "Sales.Create", "Sales.BillDiscount", "Purchase.Create");

        Assert.Equal(200, await StatusAsync(Controller(f, here), Request(f, TaxDirection.Sale, pct: 5m)));
        Assert.Equal(200, await StatusAsync(Controller(f, here), Request(f, TaxDirection.Purchase)));
        Assert.Equal(403, await StatusAsync(Controller(f, FakeScopedPermissions.AtBranch(TestCompany.MainBranchId, "Sales.Create")), Request(f, TaxDirection.Sale, pct: 5m)));
    }

    [Fact]
    public async Task Preview_SaleBillDiscountAmountAboveCap_Is400OnBillDiscount()
    {
        var f = await SetupAsync(cap: 10m);
        var controller = Controller(f, "Sales.Create", "Sales.BillDiscount");

        Assert.Equal(200, await StatusAsync(controller, Request(f, TaxDirection.Sale, amount: 100m))); // 10% of 1,000.00
        Assert.Equal("billDiscount", await BadRequestFieldAsync(controller, Request(f, TaxDirection.Sale, amount: 100.01m)));
    }

    [Fact]
    public async Task Preview_MissingQtyOrUnitPrice_IsRejectedByTheContract()
    {
        var required = typeof(TaxPreviewLineRequest).GetConstructors().Single().GetParameters().Where(p => p.Name is "Qty" or "UnitPrice").ToList();

        Assert.Equal(2, required.Count);
        Assert.All(required, p =>
        {
            Assert.Equal(typeof(decimal?), p.ParameterType);
            Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>(p));
        });
    }
}
