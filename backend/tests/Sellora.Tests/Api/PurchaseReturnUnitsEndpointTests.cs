using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// The purchase GET projection and purchase-return GET of Phase 4, run directly on EF InMemory (the pattern of
/// <see cref="StockAdjustmentsControllerTests"/>): unit snapshot fields, returned/returnable in base units and the
/// per-unit MaxQty (returnable base / factor truncated to the unit's decimals) for every active unit of the product.
/// </summary>
public class PurchaseReturnUnitsEndpointTests
{
    private sealed record Fixture(SelloraDbContext Db, PurchaseService Service, PurchasesController Purchases, PurchaseReturnsController Returns,
        Guid UserId, Supplier Supplier, Dictionary<string, ProductUnit> Pen, Product PenProduct, Unit Gram, Unit Kg);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var user = TestUsers.Admin();
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 0%", RatePercent = 0m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var (pc, box, hctn, fctn, kg, g) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton(), TestUnits.Kilogram(), TestUnits.Gram());
        db.AddRange(user.Role, user, tax, inr, settings, supplier, pc, box, hctn, fctn, kg, g);
        await db.SaveChangesAsync();

        static ProductUnitInputItem Row(Guid unitId, Guid? inner, decimal? qty) => new(null, unitId, inner, qty, null, null, true, true, false, false, true);
        var pen = await new ProductService(db).CreateProductAsync(new UpsertProductInput("STN-PEN", "Ball Pen", null, pc.Id, tax.Id, 0m,
            [Row(pc.Id, null, null), Row(box.Id, pc.Id, 10m), Row(hctn.Id, box.Id, 5m), Row(fctn.Id, hctn.Id, 2m)]));
        var rows = await db.ProductUnits.Include(u => u.Unit).Where(u => u.ProductId == pen.Id).ToDictionaryAsync(u => u.Unit.ShortCode);

        var service = new PurchaseService(db, new ExchangeRateService(db));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test")) };
        var ctx = new ControllerContext { HttpContext = http };
        return new Fixture(db, service, new PurchasesController(db, service) { ControllerContext = ctx },
            new PurchaseReturnsController(db, service) { ControllerContext = ctx }, user.Id, supplier, rows, pen, g, kg);
    }

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    private static Task<PurchaseOrder> BuyAsync(Fixture f, ProductUnit unit, decimal qty, decimal rate) =>
        f.Service.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 9, 10), null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(unit.ProductId, unit.Id, qty, rate, 0, 0)]), f.UserId);

    [Fact]
    public async Task Get_AfterThirtySevenPiecesReturnedAgainstTwoCartons_MaxQtyPerUnitIsTruncatedFromTheBaseRemainder()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Pen["fctn"], 2m, 1000m);
        await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, new DateOnly(2026, 9, 11), null,
            [new PurchaseReturnLineInput(purchase.Lines.Single().Id, f.Pen["pc"].Id, 37m)]), f.UserId);
        f.Db.ChangeTracker.Clear();

        var line = Ok(await f.Purchases.Get(purchase.Id, default)).Lines.Single();

        Assert.Equal((37m, 163m), (line.ReturnedBaseQty, line.ReturnableBaseQty));
        var max = line.ReturnUnits.ToDictionary(u => u.UnitCode, u => u.MaxQty);
        Assert.Equal(new Dictionary<string, decimal> { ["pc"] = 163m, ["box"] = 16m, ["hctn"] = 3m, ["fctn"] = 1m }, max);
        Assert.Equal(["pc", "box", "hctn", "fctn"], line.ReturnUnits.Select(u => u.UnitCode)); // SortOrder
        Assert.Equal(["fctn"], line.ReturnUnits.Where(u => u.IsLineUnit).Select(u => u.UnitCode));
        Assert.Equal(("fctn", 100m, 0, (short)0, (short)0), (line.UnitCode, line.ConversionFactorToBase, 0, line.UnitDecimalPlaces, line.BaseUnitDecimalPlaces));
        Assert.Equal((2m, 200m), (line.Qty, line.BaseQty));
        Assert.All(line.ReturnUnits, u => Assert.True(u.MaxQty * u.ConversionFactorToBase <= line.ReturnableBaseQty));
    }

    [Fact]
    public async Task Get_AfterASaleLeavesLessInStock_ReturnableAndMaxQtyAreCappedByTheBatch()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Pen["fctn"], 2m, 1000m);
        var batch = await f.Db.PurchaseBatches.SingleAsync();
        batch.RemainingBaseQty = 25m; // 175 pc sold
        (await f.Db.StockItems.SingleAsync()).QuantityOnHand = 25m;
        (await f.Db.BatchLocationBalances.SingleAsync()).RemainingBaseQty = 25m; // 8b: returnable is read from the balance at the purchase's location
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var line = Ok(await f.Purchases.Get(purchase.Id, default)).Lines.Single();

        Assert.Equal((0m, 25m), (line.ReturnedBaseQty, line.ReturnableBaseQty));
        Assert.Equal(new Dictionary<string, decimal> { ["pc"] = 25m, ["box"] = 2m, ["hctn"] = 0m, ["fctn"] = 0m }, line.ReturnUnits.ToDictionary(u => u.UnitCode, u => u.MaxQty));
    }

    [Fact]
    public async Task Get_CancelledPurchase_HasNoReturnUnitsAndZeroReturnable()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Pen["fctn"], 2m, 1000m);
        await f.Service.CancelPurchaseAsync(purchase.Id, f.UserId);
        f.Db.ChangeTracker.Clear();

        var line = Ok(await f.Purchases.Get(purchase.Id, default)).Lines.Single();

        Assert.Equal(0m, line.ReturnableBaseQty);
        Assert.Empty(line.ReturnUnits);
    }

    [Fact]
    public async Task Get_FullyReturned_HasNoReturnUnits()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Pen["box"], 1m, 100m);
        await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, new DateOnly(2026, 9, 11), null,
            [new PurchaseReturnLineInput(purchase.Lines.Single().Id, f.Pen["pc"].Id, 10m)]), f.UserId);
        f.Db.ChangeTracker.Clear();

        var line = Ok(await f.Purchases.Get(purchase.Id, default)).Lines.Single();

        Assert.Equal((10m, 0m), (line.ReturnedBaseQty, line.ReturnableBaseQty));
        Assert.Empty(line.ReturnUnits);
    }

    [Fact]
    public async Task Get_LineUnitDeactivatedSincePurchase_StaysInReturnUnitsOtherInactiveUnitsDoNot()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Pen["fctn"], 1m, 1000m);
        // Deactivate the Full Carton (the line's unit, a leaf) directly: it must stay offered, flagged as the line unit.
        f.Pen["fctn"].IsActive = false;
        f.Db.Update(await f.Db.ProductUnits.SingleAsync(u => u.Id == f.Pen["fctn"].Id));
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var line = Ok(await f.Purchases.Get(purchase.Id, default)).Lines.Single();

        Assert.Contains(line.ReturnUnits, u => u.UnitCode == "fctn" && u.IsLineUnit && u.MaxQty == 1m);
    }

    [Fact]
    public async Task PurchaseReturnGet_MixedUnitReturn_ShowsTheReturnLinesOwnUnitSnapshot()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Pen["fctn"], 2m, 1000m);
        var created = await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, new DateOnly(2026, 9, 11), null,
            [new PurchaseReturnLineInput(purchase.Lines.Single().Id, f.Pen["box"].Id, 4m)]), f.UserId);
        f.Db.ChangeTracker.Clear();

        var line = Ok(await f.Returns.Get(created.Id, default)).Lines.Single();

        Assert.Equal((f.Pen["box"].Id, "box", 10m, 4m, 40m), (line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, line.Qty, line.BaseQty));
        Assert.Equal("pc", line.BaseUnitLabel);
        Assert.Equal(400m, line.TaxableValue); // 2000 * 40 / 200
    }

    [Fact]
    public async Task PurchaseReturnPost_ProductUnitIdOmitted_UsesTheLineUnit_AndAUnitOfAnotherProductMapsToTheLinesField()
    {
        var f = await SeedAsync();
        var purchase = await BuyAsync(f, f.Pen["fctn"], 2m, 1000m);
        var lineId = purchase.Lines.Single().Id;

        var ok = await f.Returns.Create(new CreatePurchaseReturnRequest(purchase.Id, new DateOnly(2026, 9, 11), null,
            [new PurchaseReturnLineRequest(lineId, null, 1m)]), default);
        Assert.Equal("fctn", Ok(ok).Lines.Single().UnitCode);

        var bad = await f.Returns.Create(new CreatePurchaseReturnRequest(purchase.Id, new DateOnly(2026, 9, 11), null,
            [new PurchaseReturnLineRequest(lineId, Guid.NewGuid(), 1m)]), default);
        var problem = Assert.IsType<ObjectResult>(bad.Result);
        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Contains("Lines", details.Errors.Keys);
    }

    [Theory]
    [InlineData(1.234, 0.5, 3, 2, 2.46)] // 0.5 kg pack on a 2-dp base: whole-pack-pairs only, 2.46 x 0.5 = 1.23
    [InlineData(25, 0.5, 3, 0, 50)]      // 0-dp base: even quantities of a half-kg pack
    [InlineData(163, 10, 0, 0, 16)]
    [InlineData(163, 1, 0, 0, 163)]
    public void MaxReturnQty_SatisfiesUnitAndBaseDecimals(double returnable, double factor, int unitDp, int baseDp, double expected)
    {
        var max = StockQuantity.MaxEnterableQty((decimal)returnable, (decimal)factor, unitDp, baseDp);
        Assert.Equal((decimal)expected, max);
        Assert.Equal(max * (decimal)factor, Math.Round(max * (decimal)factor, baseDp));
        Assert.True(max * (decimal)factor <= (decimal)returnable);
    }
}
