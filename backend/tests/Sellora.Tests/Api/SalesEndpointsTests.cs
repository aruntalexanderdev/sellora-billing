using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// Phase 5 endpoints run directly on EF InMemory: the barcode lookup and catalog search, the permission-claim to
/// CanOverridePrice mapping on sale creation, and the invoice detail's base-unit returned sums and per-unit MaxQty.
/// </summary>
public class SalesEndpointsTests
{
    private const string BoxCode = "8901234500012", PcCode = "8901234500005";
    private static readonly DateOnly D = new(2026, 9, 10);

    private sealed record Fixture(SelloraDbContext Db, SalesService Service, SalesController Sales, SalesReturnsController Returns,
        Guid UserId, Customer Customer, Product Pen, Dictionary<string, ProductUnit> Rows, Tax Tax, Unit Pc, Unit Box);

    private static ControllerContext Context(Guid userId, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(permissions.Select(p => new Claim(AuthClaimTypes.Permission, p)));
        var services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), RequestServices = services } };
    }

    private static async Task<Fixture> SeedAsync(ISalesService? salesOverride = null, params string[] permissions)
    {
        var db = TestDb.NewInMemory();
        var user = TestUsers.Admin();
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 0%", RatePercent = 0m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var (pc, box, hctn, fctn) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton());
        db.AddRange(user.Role, user, tax, inr, settings, supplier, customer, pc, box, hctn, fctn);
        await db.SaveChangesAsync();

        var pen = await new ProductService(db).CreateProductAsync(new UpsertProductInput("STN-PEN", "Ball Pen", null, pc.Id, tax.Id, 0m,
        [
            Item(pc.Id, null, null, PcCode), Item(box.Id, pc.Id, 10m, BoxCode), Item(hctn.Id, box.Id, 5m), Item(fctn.Id, hctn.Id, 2m),
        ]));
        var rows = await db.ProductUnits.Include(u => u.Unit).Where(u => u.ProductId == pen.Id).ToDictionaryAsync(u => u.Unit.ShortCode);
        var pricing = new PricingService(db);
        await pricing.SetPriceAsync(rows["pc"].Id, 12m, user.Id);
        await pricing.SetPriceAsync(rows["box"].Id, 100m, user.Id);
        // Half Carton and Full Carton stay unpriced.
        var purchases = new PurchaseService(db, new ExchangeRateService(db));
        await purchases.CreatePurchaseAsync(new CreatePurchaseInput(supplier.Id, D, null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(pen.Id, rows["fctn"].Id, 1m, 1000m, 0, 0)]), user.Id); // 100 pc

        var service = new SalesService(db);
        var ctx = Context(user.Id, permissions);
        return new Fixture(db, service, new SalesController(db, salesOverride ?? service) { ControllerContext = ctx },
            new SalesReturnsController(db, service) { ControllerContext = ctx }, user.Id, customer, pen, rows, tax, pc, box);
    }

    private static ProductUnitInputItem Item(Guid unitId, Guid? inner, decimal? qty, string? barcode = null, bool sellable = true, bool active = true) =>
        new(null, unitId, inner, qty, null, barcode, true, sellable, false, false, active);

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    private static void AssertNotFound(ActionResult<SaleBarcodeMatchDto> result)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(404, obj.StatusCode);
    }

    private static Task<SalesInvoice> SellAsync(Fixture f, ProductUnit unit, decimal qty, decimal price) =>
        f.Service.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, 0,
            [new SaleLineInput(unit.ProductId, unit.Id, qty, price, 0)]), f.UserId);

    // ------------------------------------------------------------------ barcode lookup

    [Fact]
    public async Task CatalogByBarcode_BoxBarcode_ReturnsTheProductAndTheMatchedUnitWithItsPrice()
    {
        var f = await SeedAsync();

        var match = Ok(await f.Sales.CatalogByBarcode(BoxCode, default));

        Assert.Equal(f.Rows["box"].Id, match.ProductUnitId);
        Assert.Equal((f.Pen.Id, 100m), (match.Product.ProductId, match.Product.QuantityOnHand));
        var box = match.Product.Units.Single(u => u.ProductUnitId == match.ProductUnitId);
        Assert.Equal(("box", 10m, 100m, BoxCode), (box.UnitCode, box.ConversionFactorToBase, box.CurrentPrice, box.Barcode));
        Assert.Equal(12m, match.Product.Units.Single(u => u.UnitCode == "pc").CurrentPrice);
    }

    [Fact]
    public async Task CatalogByBarcode_PieceBarcodeWithSurroundingSpaces_TrimmedAndMatchesTheBaseUnit()
    {
        var f = await SeedAsync();

        var match = Ok(await f.Sales.CatalogByBarcode($"  {PcCode} ", default));

        Assert.Equal(f.Rows["pc"].Id, match.ProductUnitId);
    }

    [Theory]
    [InlineData("0000000000000")]
    [InlineData("2012345012349")] // weighed / price-embedded EAN-13 (prefix 2x): out of scope, a plain miss
    [InlineData("   ")]
    public async Task CatalogByBarcode_UnknownOrBlankCode_NotFound(string code)
    {
        var f = await SeedAsync();

        AssertNotFound(await f.Sales.CatalogByBarcode(code, default));
    }

    [Fact]
    public async Task CatalogByBarcode_UnitNotSellable_NotFound()
    {
        var f = await SeedAsync();
        f.Rows["box"].IsSellable = false;
        await f.Db.SaveChangesAsync();

        AssertNotFound(await f.Sales.CatalogByBarcode(BoxCode, default));
    }

    [Fact]
    public async Task CatalogByBarcode_UnitInactive_NotFound()
    {
        var f = await SeedAsync();
        f.Rows["box"].IsActive = false;
        await f.Db.SaveChangesAsync();

        AssertNotFound(await f.Sales.CatalogByBarcode(BoxCode, default));
    }

    [Fact]
    public async Task CatalogByBarcode_ProductInactive_NotFound()
    {
        var f = await SeedAsync();
        f.Pen.IsActive = false;
        await f.Db.SaveChangesAsync();

        AssertNotFound(await f.Sales.CatalogByBarcode(BoxCode, default));
    }

    [Fact]
    public async Task CatalogByBarcode_UnpricedUnit_Returns200WithANullPriceSoThePosCanRefuseItPrecisely()
    {
        var f = await SeedAsync();
        f.Rows["hctn"].Barcode = "8901234500029";
        await f.Db.SaveChangesAsync();

        var match = Ok(await f.Sales.CatalogByBarcode("8901234500029", default));

        Assert.Equal(f.Rows["hctn"].Id, match.ProductUnitId);
        Assert.Null(match.Product.Units.Single(u => u.UnitCode == "hctn").CurrentPrice);
    }

    [Fact]
    public async Task Catalog_SearchByABarcodeOfAnyUnit_FindsTheProductAndExposesTheBarcodes()
    {
        var f = await SeedAsync();

        var items = Ok(await f.Sales.Catalog(BoxCode, default));

        var product = Assert.Single(items);
        Assert.Equal(f.Pen.Id, product.ProductId);
        Assert.Equal(BoxCode, product.Units.Single(u => u.UnitCode == "box").Barcode);
        Assert.Empty(Ok(await f.Sales.Catalog("8901234599999", default)));
    }

    // ------------------------------------------------------------------ permission claim -> CanOverridePrice

    private sealed class CapturingSales : ISalesService
    {
        public CreateSaleInput? Captured;

        public Task<SalesInvoice> CreateSaleAsync(CreateSaleInput input, Guid createdByUserId, CancellationToken ct = default)
        {
            Captured = input;
            throw new DomainValidationException("Lines", "captured");
        }

        public Task<SalesInvoice?> AddPaymentAsync(Guid salesInvoiceId, AddSalePaymentInput input, Guid receivedByUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SalesReturn> CreateReturnAsync(CreateSaleReturnInput input, Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SalesInvoice?> AddRefundAsync(Guid salesInvoiceId, AddSaleRefundInput input, Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SalesInvoice?> ReversePaymentAsync(Guid salesInvoiceId, Guid paymentId, ReversePaymentInput input, Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelSaleAsync(Guid salesInvoiceId, Guid cancelledByUserId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static CreateSaleRequest Request(Fixture f, bool overridePrice, decimal discount = 0m) =>
        new(f.Customer.Id, D, PaymentMethod.Cash, 0, [new CreateSaleLineRequest(f.Pen.Id, f.Rows["box"].Id, 1m, 90m, discount, overridePrice)]);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Create_PriceOverrideClaim_MapsToCanOverridePriceAndTheLineFlagsPassThrough(bool holdsPermission, bool expected)
    {
        var capture = new CapturingSales();
        var f = await SeedAsync(capture, holdsPermission ? ["Sales.Create", "Sales.PriceOverride"] : ["Sales.Create"]);

        var result = await f.Sales.Create(Request(f, overridePrice: true, discount: 5m), default);

        Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value); // the stub stops the call
        Assert.Equal(expected, capture.Captured!.CanOverridePrice);
        var line = capture.Captured.Lines.Single();
        Assert.Equal((90m, 5m, true), (line.UnitPrice, line.Discount, line.OverridePrice));
    }

    [Fact]
    public async Task Create_OtherPermissionsDoNotImplyOverride_OnlyTheExactClaimDoes()
    {
        var capture = new CapturingSales();
        var f = await SeedAsync(capture, "Sales.Create", "Sales.Cancel", "Inventory.Price", "Sales.PriceOverrides");

        await f.Sales.Create(Request(f, overridePrice: false), default);

        Assert.False(capture.Captured!.CanOverridePrice);
    }

    [Fact]
    public async Task Create_TamperedPriceWithoutThePermissionClaim_Is400OnLinesViaTheRealService()
    {
        var f = await SeedAsync(null, "Sales.Create");

        var result = await f.Sales.Create(Request(f, overridePrice: false), default);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal("The price of Ball Pen (Box) is now 100.00. Refresh the item and try again.", problem.Errors["Lines"].Single());
        Assert.Empty(await f.Db.SalesInvoices.ToListAsync());
    }

    [Fact]
    public async Task Create_OverrideWithTheClaim_SavesAndTheDetailShowsListPriceAndFlag()
    {
        var f = await SeedAsync(null, "Sales.Create", "Sales.PriceOverride", "Sales.View");

        var created = Ok(await f.Sales.Create(Request(f, overridePrice: true), default));

        var line = created.Lines.Single();
        Assert.Equal((90m, 100m, true), (line.UnitPrice, line.ListUnitPrice, line.PriceOverridden));
    }

    // ------------------------------------------------------------------ detail: returned sums in base, ReturnUnits / MaxQty

    [Fact]
    public async Task Get_BoxLineReturnedInBoxesAndPieces_ReturnedBaseQtySumsMixedUnitsAndMaxQtyIsTruncatedFromTheBaseRemainder()
    {
        var f = await SeedAsync();
        var sale = await SellAsync(f, f.Rows["box"], 3m, 100m); // 30 pc
        var line = sale.Lines.Single();
        await f.Service.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(line.Id, f.Rows["pc"].Id, 13m)]), f.UserId);
        await f.Service.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(line.Id, f.Rows["box"].Id, 1m)]), f.UserId);
        f.Db.ChangeTracker.Clear();

        var dto = Ok(await f.Sales.Get(sale.Id, default)).Lines.Single();

        Assert.Equal((23m, 7m), (dto.ReturnedBaseQty, dto.ReturnableBaseQty)); // 13 pc + 1 Box in base, not 14 "boxes"
        var max = dto.ReturnUnits.ToDictionary(u => u.UnitCode, u => u.MaxQty);
        Assert.Equal(new Dictionary<string, decimal> { ["pc"] = 7m, ["box"] = 0m, ["hctn"] = 0m, ["fctn"] = 0m }, max);
        Assert.Equal(["box"], dto.ReturnUnits.Where(u => u.IsLineUnit).Select(u => u.UnitCode));
        Assert.Equal(("box", 10m, 3m, 30m, 100m, 100m, false), (dto.UnitCode, dto.ConversionFactorToBase, dto.Qty, dto.BaseQty, dto.UnitPrice, dto.ListUnitPrice, dto.PriceOverridden));
        Assert.All(dto.ReturnUnits, u => Assert.True(u.MaxQty * u.ConversionFactorToBase <= dto.ReturnableBaseQty));
    }

    [Fact]
    public async Task Get_FreshBoxLine_MaxQtyPerUnitCoversTheWholeLine()
    {
        var f = await SeedAsync();
        var sale = await SellAsync(f, f.Rows["box"], 3m, 100m);

        var dto = Ok(await f.Sales.Get(sale.Id, default)).Lines.Single();

        Assert.Equal(new Dictionary<string, decimal> { ["pc"] = 30m, ["box"] = 3m, ["hctn"] = 0m, ["fctn"] = 0m }, dto.ReturnUnits.ToDictionary(u => u.UnitCode, u => u.MaxQty));
        Assert.Equal(["pc", "box", "hctn", "fctn"], dto.ReturnUnits.Select(u => u.UnitCode));
    }

    [Fact]
    public async Task Get_CancelledSale_HasNoReturnUnitsAndZeroReturnable()
    {
        var f = await SeedAsync();
        var sale = await SellAsync(f, f.Rows["box"], 3m, 100m);
        await f.Service.CancelSaleAsync(sale.Id, f.UserId);
        f.Db.ChangeTracker.Clear();

        var dto = Ok(await f.Sales.Get(sale.Id, default)).Lines.Single();

        Assert.Equal(0m, dto.ReturnableBaseQty);
        Assert.Empty(dto.ReturnUnits);
    }

    [Fact]
    public async Task GetReturn_ReturnInPiecesAgainstABoxLine_ShowsTheReturnLinesOwnUnit()
    {
        var f = await SeedAsync();
        var sale = await SellAsync(f, f.Rows["box"], 3m, 100m);
        var created = await f.Service.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(sale.Lines.Single().Id, f.Rows["pc"].Id, 4m)]), f.UserId);
        f.Db.ChangeTracker.Clear();

        var line = Ok(await f.Returns.Get(created.Id, default)).Lines.Single();

        Assert.Equal(("pc", "Piece", 4m, 4m), (line.UnitCode, line.UnitLabel, line.Qty, line.BaseQty));
    }

    [Fact]
    public async Task CreateReturn_UnitOfNoSuchProductUnit_Is400OnLines()
    {
        var f = await SeedAsync();
        var sale = await SellAsync(f, f.Rows["box"], 3m, 100m);

        var bad = await f.Returns.Create(new CreateSaleReturnRequest(sale.Id, D, null, [new SaleReturnLineRequest(sale.Lines.Single().Id, Guid.NewGuid(), 1m)]), default);

        var details = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(bad.Result).Value);
        Assert.Contains("Lines", details.Errors.Keys);
    }
}
