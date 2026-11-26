using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.TaxProfiles;

/// <summary>
/// TP-5a (T11, TP5-Q4, Q5): a GST-taxable product without an HSN/SAC is an error once the company has an <c>HsnRequiredFrom</c> date: product saves (400 <c>hsnCode</c>),
/// sale and purchase creates dated on/after it (400 <c>lines[i].hsnCode</c>) and the preview. Without a date it stays a warning. Returns, cancellations and other
/// documents never block. The matrix is treatment (taxable, nil, exempt, VAT) x date (unset, before, on, after) x where the HSN comes from (product, subcategory, none).
/// </summary>
public class TaxHsnRequiredTests
{
    private static readonly DateOnly D = TaxWorld.Today; // 2026-11-24

    private sealed record Fx(TaxWorld W, ProductService Products, SalesService Sales, PurchaseService Purchases, TaxDocumentPreviewService Preview, Guid UserId, Supplier Supplier, Customer Walk, ProductCategory Category, ProductSubcategory SubWithHsn, ProductSubcategory SubNoHsn);

    private static async Task<Fx> SetupAsync(string country = "IN", DateOnly? required = null)
    {
        var w = await TaxWorld.CreateAsync(country);
        var home = new State { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "KA", Name = "Karnataka", Kind = StateKind.State, GstStateCode = "29" };
        w.Db.States.Add(home);
        w.Settings.StateId = home.Id;
        w.Settings.SellingPricesIncludeTax = false;
        w.Settings.HsnRequiredFrom = required;
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = (await w.Db.Currencies.FirstAsync()).Id, StateId = home.Id };
        var walk = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var user = TestUsers.Admin();
        w.Db.AddRange(supplier, walk, user.Role, user);
        await w.Db.SaveChangesAsync();
        var category = await w.AddCategoryAsync("GRO", "Grocery");
        var withHsn = await w.AddSubcategoryAsync(category, "RICE", "Rice", "1006");
        var noHsn = await w.AddSubcategoryAsync(category, "MISC", "Misc");
        var perms = new FakeEffectivePermissions("Sales.Create", "Sales.BillDiscount", "Sales.PriceOverride");
        return new Fx(w, new ProductService(w.Db), new SalesService(w.Db, perms, w.Resolver), new PurchaseService(w.Db, new ExchangeRateService(w.Db), null, w.Resolver),
            new TaxDocumentPreviewService(w.Db, w.Resolver), user.Id, supplier, walk, category, withHsn, noHsn);
    }

    private static UpsertProductInput Input(Fx f, string sku, string? hsn = null, Guid? subcategoryId = null, Guid? sales = null, Guid? purchase = null) =>
        new(sku, "Item " + sku, subcategoryId is null ? null : f.Category.Id, subcategoryId, f.W.Piece.Id, 0m,
            [new ProductUnitInputItem(null, f.W.Piece.Id, null, 1m, "Each", null, true, true, true, true, true)], hsn, null, sales, purchase);

    /// <summary>A product (with a base-unit row) as a document line needs it, written directly so the HSN rule is exercised only by the document under test.</summary>
    private static async Task<(Product Product, ProductUnit Unit)> DocProductAsync(Fx f, string? sales = null, string? purchase = null, string? hsn = null, Guid? subcategoryId = null, Country? country = null)
    {
        var salesProfile = sales is null ? null : await f.W.ProfileAsync(sales, country);
        var purchaseProfile = purchase is null ? null : await f.W.ProfileAsync(purchase, country);
        var product = await f.W.AddProductAsync("P" + Guid.NewGuid().ToString("N")[..6], f.Category.Id, subcategoryId, hsn, salesProfile is null ? null : (await f.W.MirrorAsync(salesProfile)).Id);
        if (salesProfile is not null)
        {
            f.W.Db.TaxAssignments.Add(new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = salesProfile.CountryId, SalesTaxProfileId = salesProfile.Id,
                PurchaseTaxProfileId = purchaseProfile?.Id, CreatedAt = DateTime.UtcNow,
            });
        }
        var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = f.W.Piece.Id, Unit = f.W.Piece, IsBaseUnit = true, ConversionFactorToBase = 1m };
        f.W.Db.ProductUnits.Add(unit);
        await f.W.Db.SaveChangesAsync();
        return (product, unit);
    }

    private static async Task StockAsync(Fx f, (Product Product, ProductUnit Unit) p, DateOnly? on = null)
    {
        // stock is bought before the HSN date comes into force (the setting is lifted for the purchase, then restored)
        var saved = f.W.Settings.HsnRequiredFrom;
        f.W.Settings.HsnRequiredFrom = null;
        await f.W.Db.SaveChangesAsync();
        await f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, on ?? D.AddDays(-60), null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(p.Product.Id, p.Unit.Id, 100m, 1m, 0, 0)]), f.UserId);
        f.W.Settings.HsnRequiredFrom = saved;
        await f.W.Db.SaveChangesAsync();
    }

    private static async Task SetPriceAsync(Fx f, ProductUnit unit, decimal price)
    {
        foreach (var open in await f.W.Db.SellingPrices.Where(p => p.ProductUnitId == unit.Id && p.EffectiveTo == null).ToListAsync()) open.EffectiveTo = DateTimeOffset.UtcNow;
        f.W.Db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = unit.Id, Price = price, CreatedByUserId = f.UserId });
        await f.W.Db.SaveChangesAsync();
    }

    private static Task<SalesInvoice> SellAsync(Fx f, params (Product Product, ProductUnit Unit)[] items) => SellAsync(f, D, items);

    private static async Task<SalesInvoice> SellAsync(Fx f, DateOnly date, params (Product Product, ProductUnit Unit)[] items)
    {
        foreach (var i in items) await SetPriceAsync(f, i.Unit, 100m);
        return await f.Sales.CreateSaleAsync(new CreateSaleInput(f.Walk.Id, date, PaymentMethod.Cash, 0m, items.Select(i => new SaleLineInput(i.Product.Id, i.Unit.Id, 1m, 100m, 0m)).ToList(), null, null, true), f.UserId);
    }

    private static Task<PurchaseOrder> BuyAsync(Fx f, DateOnly date, params (Product Product, ProductUnit Unit)[] items) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, date, null, null, PaymentMethod.Cash, 0, items.Select(i => new PurchaseLineInput(i.Product.Id, i.Unit.Id, 1m, 50m, 0, 0)).ToList()), f.UserId);

    private static async Task<IReadOnlyList<FieldError>> ErrorsAsync(Func<Task> act) => (await Assert.ThrowsAsync<DomainValidationException>(act)).Errors;

    // ------------------------------------------------------------------ product save

    [Fact]
    public async Task ProductSave_GstTaxableWithoutHsn_IsAWarningWhileNoDateIsSet()
    {
        var f = await SetupAsync(required: null);

        var product = await f.Products.CreateProductAsync(Input(f, "A1"));

        Assert.Null(product.HsnCode);
        var detail = (await new ProductsController(f.W.Db, f.Products, f.W.Resolver).Get(product.Id, default)).Result;
        var dto = (ProductDetailDto)Assert.IsType<OkObjectResult>(detail).Value!;
        Assert.Equal("hsnCode", Assert.Single(dto.Warnings!).Field);
    }

    [Fact]
    public async Task ProductSave_GstTaxableWithoutHsn_IsRefusedOnHsnCode_OnceADateIsSet_NoMatterHowFarOut()
    {
        foreach (var offset in new[] { -30, 0, 365 })
        {
            var f = await SetupAsync(required: D.AddDays(offset));

            var errors = await ErrorsAsync(() => f.Products.CreateProductAsync(Input(f, "A1")));

            Assert.Equal("hsnCode", Assert.Single(errors).Field);
            Assert.Contains("HSN", errors[0].Message);
            Assert.Empty(f.W.Db.Products); // nothing saved
            Assert.DoesNotContain(f.W.Db.TaxAssignments.Local, a => a.Scope == TaxScope.Product);
        }
    }

    [Fact]
    public async Task ProductSave_WithAnHsnOfItsOwn_OrOfItsSubcategory_IsAccepted()
    {
        var f = await SetupAsync(required: D);

        var own = await f.Products.CreateProductAsync(Input(f, "A1", hsn: "1905"));
        var inherited = await f.Products.CreateProductAsync(Input(f, "A2", subcategoryId: f.SubWithHsn.Id));
        var missing = await ErrorsAsync(() => f.Products.CreateProductAsync(Input(f, "A3", subcategoryId: f.SubNoHsn.Id))); // its subcategory has none either

        Assert.Equal("1905", own.HsnCode);
        Assert.Null(inherited.HsnCode);
        Assert.Equal("hsnCode", Assert.Single(missing).Field);
    }

    [Theory]
    [InlineData("GST-NIL")]
    [InlineData("GST-EXEMPT")]
    public async Task ProductSave_NilRatedOrExemptProfile_NeverNeedsAnHsn(string profile)
    {
        var f = await SetupAsync(required: D);
        var p = await f.W.ProfileAsync(profile);

        var product = await f.Products.CreateProductAsync(Input(f, "N1", sales: p.Id));

        Assert.Null(product.HsnCode);
    }

    [Fact]
    public async Task ProductSave_SalesNilButPurchaseTaxable_StillNeedsTheHsn()
    {
        var f = await SetupAsync(required: D);

        var (nil, gst18) = ((await f.W.ProfileAsync("GST-NIL")).Id, (await f.W.ProfileAsync("GST-18")).Id);
        var errors = await ErrorsAsync(() => f.Products.CreateProductAsync(Input(f, "M1", sales: nil, purchase: gst18)));

        Assert.Equal("hsnCode", Assert.Single(errors).Field);
    }

    [Fact]
    public async Task ProductSave_VatCompany_NeverNeedsAnHsn()
    {
        var f = await SetupAsync("AE", required: D);

        var product = await f.Products.CreateProductAsync(Input(f, "V1", sales: (await f.W.ProfileAsync("VAT-5", f.W.Uae)).Id));

        Assert.Null(product.HsnCode);
    }

    [Fact]
    public async Task ProductUpdate_WithoutAnHsn_IsRefusedAndKeepsTheStoredRow_AddingOneSucceeds()
    {
        var f = await SetupAsync(required: null);
        var created = await f.Products.CreateProductAsync(Input(f, "U1")); // saved while the control was off
        f.W.Settings.HsnRequiredFrom = D;
        await f.W.Db.SaveChangesAsync();

        var errors = await ErrorsAsync(() => f.Products.UpdateProductAsync(created.Id, Input(f, "U1") with { Name = "Renamed" }));

        Assert.Equal("hsnCode", Assert.Single(errors).Field);
        f.W.Db.ChangeTracker.Clear();
        Assert.Equal("Item U1", (await f.W.Db.Products.SingleAsync(p => p.Id == created.Id)).Name);

        var updated = await f.Products.UpdateProductAsync(created.Id, Input(f, "U1", hsn: "100610"));
        Assert.Equal("100610", updated!.HsnCode);
    }

    [Fact]
    public async Task ProductSave_InvalidHsnFormat_StillFailsOnHsnCode_BeforeAnyDateRule()
    {
        var f = await SetupAsync(required: null);

        var errors = await ErrorsAsync(() => f.Products.CreateProductAsync(Input(f, "F1", hsn: "12")));

        Assert.Equal("hsnCode", Assert.Single(errors).Field);
        Assert.Contains("4, 6 or 8 digits", errors[0].Message);
    }

    // ------------------------------------------------------------------ sales

    [Fact]
    public async Task Sale_DatedOnOrAfterTheRequiredDate_RefusesEveryLineWithoutHsn()
    {
        var f = await SetupAsync(required: D);
        var missingA = await DocProductAsync(f, "GST-18");
        var missingB = await DocProductAsync(f, "GST-18");
        var fine = await DocProductAsync(f, "GST-18", hsn: "1006");
        foreach (var p in new[] { missingA, missingB, fine }) await StockAsync(f, p);

        var onDate = await ErrorsAsync(() => SellAsync(f, D, missingA, fine, missingB));
        var after = await ErrorsAsync(() => SellAsync(f, D.AddDays(1), missingA));

        Assert.Equal(["lines[0].hsnCode", "lines[2].hsnCode"], onDate.Select(e => e.Field));
        Assert.Equal(["lines[0].hsnCode"], after.Select(e => e.Field));
        Assert.Empty(f.W.Db.SalesInvoices); // nothing saved, no stock moved
        Assert.Equal(100m, f.W.Db.StockItems.Single(s => s.ProductId == missingA.Product.Id).QuantityOnHand);
    }

    [Fact]
    public async Task Sale_DatedBeforeTheRequiredDate_OrWithNoDate_SavesWithTheWarning()
    {
        var f = await SetupAsync(required: D);
        var missing = await DocProductAsync(f, "GST-18");
        await StockAsync(f, missing);

        var before = await SellAsync(f, D.AddDays(-1), missing);

        Assert.Equal("lines[0].hsnCode", Assert.Single(before.Warnings).Field);

        f.W.Settings.HsnRequiredFrom = null;
        await f.W.Db.SaveChangesAsync();
        var unset = await SellAsync(f, D.AddDays(5), missing);
        Assert.Equal("lines[0].hsnCode", Assert.Single(unset.Warnings).Field);
    }

    [Fact]
    public async Task Sale_HsnFromTheSubcategory_NilAndExemptProfiles_AreNeverBlocked()
    {
        var f = await SetupAsync(required: D);
        var viaSub = await DocProductAsync(f, "GST-18", subcategoryId: f.SubWithHsn.Id);
        var nil = await DocProductAsync(f, "GST-NIL");
        var exempt = await DocProductAsync(f, "GST-EXEMPT");
        foreach (var p in new[] { viaSub, nil, exempt }) await StockAsync(f, p);

        var sale = await SellAsync(f, viaSub, nil, exempt);

        Assert.Equal(3, sale.Lines.Count);
        Assert.DoesNotContain(sale.Warnings, w => w.Field.StartsWith("lines[1]") || w.Field.StartsWith("lines[2]"));
    }

    [Fact]
    public async Task Sale_OnlyTheSalesProfileDecides_ASalesNilProductWithATaxablePurchaseProfileCanBeSold()
    {
        var f = await SetupAsync(required: D);
        var p = await DocProductAsync(f, sales: "GST-NIL", purchase: "GST-18");
        await StockAsync(f, p);

        var sale = await SellAsync(f, p);
        var purchase = await ErrorsAsync(() => BuyAsync(f, D, p));

        Assert.Single(sale.Lines);
        Assert.Equal("lines[0].hsnCode", Assert.Single(purchase).Field); // the purchase profile is GST taxable
    }

    // ------------------------------------------------------------------ purchases

    [Fact]
    public async Task Purchase_DatedOnOrAfterTheRequiredDate_RefusesLinesWithoutHsn()
    {
        var f = await SetupAsync(required: D);
        var missing = await DocProductAsync(f, "GST-18");
        var fine = await DocProductAsync(f, "GST-18", hsn: "1006");
        var nil = await DocProductAsync(f, "GST-NIL");

        var errors = await ErrorsAsync(() => BuyAsync(f, D, fine, missing, nil));
        var before = await BuyAsync(f, D.AddDays(-1), missing);
        var ok = await BuyAsync(f, D, fine, nil);

        Assert.Equal(["lines[1].hsnCode"], errors.Select(e => e.Field));
        Assert.Equal("lines[0].hsnCode", Assert.Single(before.Warnings).Field);
        Assert.Equal(2, ok.Lines.Count);
        Assert.Equal(2, f.W.Db.PurchaseOrders.Count()); // the refused one was not saved
    }

    // ------------------------------------------------------------------ preview

    [Fact]
    public async Task Preview_BlocksLikeTheSave_AndWarnsBeforeTheDate()
    {
        var f = await SetupAsync(required: D);
        var missing = await DocProductAsync(f, "GST-18");
        TaxDocumentPreviewInput Input(TaxDirection direction, DateOnly date) =>
            new(direction, direction == TaxDirection.Purchase ? f.Supplier.Id : null, date, false, null, null, null, [new TaxPreviewLineInput(missing.Product.Id, missing.Unit.Id, 1m, 100m, 0m)]);

        var sale = await ErrorsAsync(() => f.Preview.PreviewAsync(Input(TaxDirection.Sale, D)));
        var purchase = await ErrorsAsync(() => f.Preview.PreviewAsync(Input(TaxDirection.Purchase, D.AddDays(3))));
        var early = await f.Preview.PreviewAsync(Input(TaxDirection.Sale, D.AddDays(-1)));

        Assert.Equal("lines[0].hsnCode", Assert.Single(sale).Field);
        Assert.Equal("lines[0].hsnCode", Assert.Single(purchase).Field);
        Assert.Equal("lines[0].hsnCode", Assert.Single(early.Warnings, w => w.Field.EndsWith("hsnCode")).Field);
    }

    // ------------------------------------------------------------------ returns and cancellations never block

    [Fact]
    public async Task ReturnsAndCancellations_AreNeverBlocked_EvenWhenTheProductHasNoHsn()
    {
        var f = await SetupAsync(required: null);
        var missing = await DocProductAsync(f, "GST-18");
        await StockAsync(f, missing);
        var sale = await SellAsync(f, D.AddDays(-10), missing); // saved while only a warning
        var cancelled = await SellAsync(f, D.AddDays(-9), missing);
        var purchase = await BuyAsync(f, D.AddDays(-10), missing);
        var cancelledPurchase = await BuyAsync(f, D.AddDays(-8), missing);
        f.W.Settings.HsnRequiredFrom = D.AddDays(-20); // now every date is covered

        var saleReturn = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 1m)]), f.UserId);
        await f.Sales.CancelSaleAsync(cancelled.Id, f.UserId);
        await f.Purchases.CancelPurchaseAsync(cancelledPurchase.Id, f.UserId);
        var purchaseReturn = await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 1m)]), f.UserId);

        Assert.Single(saleReturn.Lines);
        Assert.Single(purchaseReturn.Lines);
        Assert.Equal(SalesStatus.Cancelled, (await f.W.Db.SalesInvoices.SingleAsync(i => i.Id == cancelled.Id)).Status);
        Assert.Equal(PurchaseStatus.Cancelled, (await f.W.Db.PurchaseOrders.SingleAsync(i => i.Id == cancelledPurchase.Id)).Status);
    }

    [Fact]
    public async Task DeactivatingAProductWithoutHsn_IsNeverBlocked_WhenHsnRequiredFromIsSet()
    {
        var f = await SetupAsync(required: D);
        var missing = await DocProductAsync(f, "GST-18");
        var controller = new ProductsController(f.W.Db, f.Products, f.W.Resolver) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        Assert.IsType<NoContentResult>(await controller.SetActive(missing.Product.Id, new SetActiveRequest(false), default));

        f.W.Db.ChangeTracker.Clear();
        Assert.False((await f.W.Db.Products.SingleAsync(p => p.Id == missing.Product.Id)).IsActive);
    }

    // ------------------------------------------------------------------ assignment / subcategory changes warn with a count

    [Fact]
    public async Task AssignmentChange_LeavingProductsWithoutHsn_IsAllowed_AndReturnsACountWarning()
    {
        var f = await SetupAsync(required: D);
        await DocProductAsync(f, "GST-5"); // GST taxable, no HSN
        await DocProductAsync(f, "GST-5");
        await DocProductAsync(f, "GST-5", hsn: "1006");
        await DocProductAsync(f, "GST-NIL");
        var controller = new TaxAssignmentsController(f.W.Db, f.W.Assignments, f.W.Resolver) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.Upsert(new UpsertTaxAssignmentRequest(TaxScope.Company, null, f.W.India.Id, (await f.W.ProfileAsync("GST-18")).Id, null), default);

        var dto = Assert.IsType<TaxAssignmentDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var warning = Assert.Single(dto.Warnings!);
        Assert.Equal("hsnCode", warning.Field);
        Assert.StartsWith("2 products have no HSN", warning.Message);
        Assert.Equal(2, await HsnRules.CountProductsWithoutHsnAsync(f.W.Db, f.W.India.Id, default));
    }

    [Fact]
    public async Task CountProductsWithoutHsn_UsesTheEffectiveHsn_ActiveProductsAndGstTaxableProfilesOnly()
    {
        var f = await SetupAsync(required: null);
        await DocProductAsync(f, "GST-18");                                               // counted
        await DocProductAsync(f, "GST-18", subcategoryId: f.SubNoHsn.Id);                 // counted
        await DocProductAsync(f, "GST-18", subcategoryId: f.SubWithHsn.Id);               // HSN from the subcategory
        await DocProductAsync(f, "GST-18", hsn: "1006");                                  // own HSN
        await DocProductAsync(f, "GST-EXEMPT");                                           // not taxable
        var inactive = await DocProductAsync(f, "GST-18");
        inactive.Product.IsActive = false;
        await f.W.Db.SaveChangesAsync();

        Assert.Equal(2, await HsnRules.CountProductsWithoutHsnAsync(f.W.Db, f.W.India.Id, default));
        Assert.Equal(0, await HsnRules.CountProductsWithoutHsnAsync(f.W.Db, f.W.Uae.Id, default));
    }

    [Fact]
    public async Task SubcategoryUpdate_ClearingTheHsn_IsAllowed_AndReturnsACountWarning()
    {
        var f = await SetupAsync(required: D);
        await DocProductAsync(f, "GST-18", subcategoryId: f.SubWithHsn.Id); // relies on the subcategory's HSN
        var service = new ProductCategoryService(f.W.Db);
        var controller = new ProductCategoriesController(f.W.Db, service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.UpdateSubcategory(f.Category.Id, f.SubWithHsn.Id, new UpsertProductClassificationRequest(f.SubWithHsn.Code, f.SubWithHsn.Name, 0, null), default);

        var dto = Assert.IsType<ProductSubcategoryDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Null(dto.HsnCode);
        Assert.StartsWith("1 product has no HSN", Assert.Single(dto.Warnings!).Message);
    }

    // ------------------------------------------------------------------ business settings

    private static async Task<(SettingsController Controller, Guid CurrencyId, Sellora.Infrastructure.Data.SelloraDbContext Db)> SettingsAsync()
    {
        var w = await BranchWorld.CreateAsync();
        var db = w.Db();
        db.Companies.Add(new Company { Id = Sellora.Domain.Companies.DefaultTenant.CompanyId, Code = "DEF", Name = "Sellora Mart" });
        await db.SaveChangesAsync();
        var controller = new SettingsController(db, new BranchService(db, new StockLocationUsageService(db)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, w.UserId.ToString())], "test")) },
            },
        };
        return (controller, w.CurrencyId, db);
    }

    [Fact]
    public async Task SettingsPut_SetsHsnRequiredFrom_OmittedKeepsIt_ClearFlagRemovesIt()
    {
        var (controller, currency, db) = await SettingsAsync();
        var prefix = (await db.BusinessSettings.SingleAsync()).InvoicePrefix;
        UpdateBusinessSettingsRequest Request(DateOnly? from, bool? clear = null) =>
            new("Sellora Mart", null, null, null, null, null, null, prefix, currency, HsnRequiredFrom: from, ClearHsnRequiredFrom: clear);
        BusinessSettingsDto Dto(ActionResult<BusinessSettingsDto> r) => Assert.IsType<BusinessSettingsDto>(Assert.IsType<OkObjectResult>(r.Result).Value);

        Assert.Null(Dto(await controller.Get(CancellationToken.None)).HsnRequiredFrom);
        Assert.Equal(D, Dto(await controller.Update(Request(D), CancellationToken.None)).HsnRequiredFrom);
        Assert.Equal(D, Dto(await controller.Update(Request(null), CancellationToken.None)).HsnRequiredFrom); // an old client that does not post it must not lift the control
        Assert.Equal(D.AddDays(7), Dto(await controller.Update(Request(D.AddDays(7)), CancellationToken.None)).HsnRequiredFrom);
        Assert.Null(Dto(await controller.Update(Request(D, clear: true), CancellationToken.None)).HsnRequiredFrom);
        Assert.Null((await db.BusinessSettings.SingleAsync()).HsnRequiredFrom);
    }

    [Fact]
    public async Task SettingsGet_ReportsTheProductsWithoutHsn()
    {
        var f = await SetupAsync(required: null);
        await DocProductAsync(f, "GST-18");
        await DocProductAsync(f, "GST-18", hsn: "1006");
        var withCountry = new SettingsController(f.W.Db, new BranchService(f.W.Db, new StockLocationUsageService(f.W.Db)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        f.W.Db.Companies.Add(new Company { Id = Sellora.Domain.Companies.DefaultTenant.CompanyId, Code = "DEF", Name = "Sellora Mart" });
        await f.W.Db.SaveChangesAsync();

        var read = Assert.IsType<BusinessSettingsDto>(Assert.IsType<OkObjectResult>((await withCountry.Get(CancellationToken.None)).Result).Value);
        Assert.Equal(1, read.ProductsWithoutHsnCount);
        Assert.Null(read.HsnRequiredFrom);
    }
}
