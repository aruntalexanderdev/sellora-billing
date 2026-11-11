using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// Phase 10 (design D3, D4, D6) controller behaviour on EF InMemory: purchase detail at an explicit location (403 for a hidden one, returnable follows the
/// location, locationBalances only over visible locations and no Transit for branch readers), the <c>stockLocationId</c> list filter of the four lists, and the
/// Idempotency-Key header on both return POSTs (invalid = 400, valid replay = same document).
/// </summary>
public class ReturnLocationEndpointsTests
{
    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    private static ControllerContext Context(Guid userId, string? idempotencyKey = null)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")) };
        if (idempotencyKey is not null) http.Request.Headers["Idempotency-Key"] = idempotencyKey;
        return new ControllerContext { HttpContext = http };
    }

    private static PurchasesController Purchases(SelloraDbContext db, IEffectivePermissionService permissions, Guid userId) =>
        new(db, new PurchaseService(db, new ExchangeRateService(db), permissions), permissions) { ControllerContext = Context(userId) };

    private static (SelloraDbContext Db, IEffectivePermissionService Permissions) BlrOnly(LocationWorld lw) =>
        (lw.W.Db(lw.W.Blr, allowed: new HashSet<Guid> { lw.W.Blr }), FakeScopedPermissions.AtBranch(lw.W.Blr, "Purchase.View"));

    private static async Task MoveAsync(TransferWorld tw, Guid source, Guid destination, decimal qty, bool receive = true)
    {
        var transfer = await tw.ApprovedAsync(source, destination, TransferWorld.Line(tw.L.W.ProductId, tw.L.W.ProductUnitId, qty));
        await tw.DispatchAsRequestedAsync(transfer.Id);
        if (!receive) return;
        var lineId = (await tw.ReloadAsync(transfer.Id)).Lines.Single().Id;
        await tw.ReceiveAsync(transfer.Id, lineId, tw.L.W.ProductUnitId, qty);
    }

    // ---- D3: purchase detail -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ExplicitHiddenLocation_Is403BranchAccessDenied()
    {
        var lw = await LocationWorld.CreateAsync();
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        var (db, permissions) = BlrOnly(lw);
        await using var _ = db;

        var result = await Purchases(db, permissions, lw.W.UserId).Get(purchase.Id, lw.MainStore, default);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, problem.StatusCode);
        Assert.Equal("branch_access_denied", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]?.ToString());
        var unknown = Assert.IsType<ObjectResult>((await Purchases(db, permissions, lw.W.UserId).Get(purchase.Id, Guid.NewGuid(), default)).Result);
        Assert.Equal(403, unknown.StatusCode); // unknown location: the same answer
    }

    [Fact]
    public async Task Get_ExplicitVisibleLocation_ReturnableFollowsThatLocationsBalance()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.BlrWarehouse, 4m);
        var (db, permissions) = BlrOnly(lw);
        await using var _ = db;
        var controller = Purchases(db, permissions, lw.W.UserId);

        Assert.Equal(6m, Ok(await controller.Get(purchase.Id, null, default)).Lines.Single().ReturnableBaseQty);          // default: the purchase's own location
        Assert.Equal(4m, Ok(await controller.Get(purchase.Id, lw.BlrWarehouse, default)).Lines.Single().ReturnableBaseQty);
        Assert.Equal(6m, Ok(await controller.Get(purchase.Id, lw.BlrStore, default)).Lines.Single().ReturnableBaseQty);
    }

    [Fact]
    public async Task Get_BranchReader_LocationBalancesListVisibleLocationsOnlyAndNeverTransit()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.BlrWarehouse, 2m);
        await MoveAsync(tw, lw.BlrStore, lw.MainStore, 3m);
        await MoveAsync(tw, lw.BlrStore, lw.MainWarehouse, 1m, receive: false); // 1 kg still in transit
        var (db, permissions) = BlrOnly(lw);
        await using var _ = db;

        var balances = Ok(await Purchases(db, permissions, lw.W.UserId).Get(purchase.Id, null, default)).Lines.Single().LocationBalances!;

        Assert.Equal(new[] { (lw.BlrStore, 4m), (lw.BlrWarehouse, 2m) }.OrderBy(x => x.Item1),
            balances.Select(b => (b.StockLocationId, b.BaseQty)).OrderBy(x => x.Item1));
        Assert.DoesNotContain(balances, b => b.StockLocationId == lw.MainStore || b.StockLocationId == lw.Transit);
        Assert.All(balances, b => Assert.False(string.IsNullOrEmpty(b.Code)));
    }

    [Fact]
    public async Task Get_CompanyLevelReader_LocationBalancesIncludeEveryLocationWithBalanceAndTransit()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.MainStore, 3m);
        await MoveAsync(tw, lw.BlrStore, lw.MainWarehouse, 1m, receive: false);
        await using var db = lw.W.Db();

        var balances = Ok(await Purchases(db, LocationWorld.Company("Purchase.View"), lw.W.UserId).Get(purchase.Id, null, default)).Lines.Single().LocationBalances!;

        Assert.Equal(new[] { (lw.BlrStore, 6m), (lw.MainStore, 3m), (lw.Transit, 1m) }.OrderBy(x => x.Item1),
            balances.Select(b => (b.StockLocationId, b.BaseQty)).OrderBy(x => x.Item1));
        Assert.DoesNotContain(balances, b => b.BaseQty <= 0m);
    }

    // ---- D4: list filters ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lists_StockLocationIdFilter_NarrowsSalesSalesReturnsPurchasesAndPurchaseReturns()
    {
        var lw = await LocationWorld.CreateAsync();
        await using (var seed = lw.W.Db()) await StockSeed.AddAsync(seed, lw.BlrVan, lw.W.ProductId, 20m, 10m, lw.W.UserId);
        await lw.W.SeedBranchStockAsync(lw.W.Blr, 20m);
        var vanSale = await lw.SellAsync(lw.W.Blr, lw.BlrVan, 5m);
        var storeSale = await lw.SellAsync(lw.W.Blr, lw.BlrStore, 5m);
        SalesReturn vanReturn;
        await using (var db = lw.W.Db(lw.W.Blr))
        {
            vanReturn = await ListPricedSales.For(db).CreateReturnAsync(new CreateSaleReturnInput(vanSale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(vanSale.Lines.Single().Id, null, 1m)]), lw.W.UserId);
            await ListPricedSales.For(db).CreateReturnAsync(new CreateSaleReturnInput(storeSale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(storeSale.Lines.Single().Id, null, 1m)]), lw.W.UserId);
        }
        var storePurchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        var warehousePurchase = await lw.BuyAsync(lw.W.Blr, lw.BlrWarehouse, 10m, 10m, LocationWorld.Company("Purchase.Create"));
        await using (var db = lw.W.Db(lw.W.Blr))
        {
            var service = new PurchaseService(db, new ExchangeRateService(db));
            await service.CreateReturnAsync(new Sellora.Domain.Purchasing.CreatePurchaseReturnInput(storePurchase.Id, BranchWorld.Day, null, [new(storePurchase.Lines.Single().Id, null, 1m)]), lw.W.UserId);
        }

        await using var read = lw.W.Db();
        var permissions = LocationWorld.Company("Sales.View", "Purchase.View");
        var sales = Ok(await new SalesController(read, ListPricedSales.For(read), permissions) { ControllerContext = Context(lw.W.UserId) }.List(null, null, null, null, null, null, 1, 50, null, lw.BlrVan));
        var salesReturns = Ok(await new SalesReturnsController(read, ListPricedSales.For(read)) { ControllerContext = Context(lw.W.UserId) }.List(null, null, null, null, 1, 50, null, lw.BlrVan));
        var purchases = Ok(await new PurchasesController(read, new PurchaseService(read, new ExchangeRateService(read)), permissions) { ControllerContext = Context(lw.W.UserId) }.List(null, null, null, null, null, 1, 50, null, lw.BlrWarehouse));
        var purchaseReturns = Ok(await new PurchaseReturnsController(read, new PurchaseService(read, new ExchangeRateService(read))) { ControllerContext = Context(lw.W.UserId) }.List(null, null, null, null, 1, 50, null, lw.BlrStore));

        Assert.Equal(vanSale.Id, Assert.Single(sales.Items).Id);
        Assert.Equal(vanReturn.Id, Assert.Single(salesReturns.Items).Id);
        Assert.Equal(warehousePurchase.Id, Assert.Single(purchases.Items).Id);
        Assert.Single(purchaseReturns.Items);
        // Without the filter every document is back (the filter narrows, nothing else changed).
        Assert.Equal(2, Ok(await new SalesController(read, ListPricedSales.For(read), permissions) { ControllerContext = Context(lw.W.UserId) }.List(null, null, null, null, null, null, 1, 50, null, null)).Total);
        Assert.Contains(Ok(await new PurchasesController(read, new PurchaseService(read, new ExchangeRateService(read)), permissions) { ControllerContext = Context(lw.W.UserId) }.List(null, null, null, null, null, 1, 50, null, null)).Items, p => p.Id == storePurchase.Id);
    }

    // ---- D6: Idempotency-Key header ------------------------------------------------------------------------------------------------------------

    private static async Task<(LocationWorld Lw, SalesInvoice Sale, PurchaseOrder Purchase)> SeedDocumentsAsync()
    {
        var lw = await LocationWorld.CreateAsync();
        await using (var seed = lw.W.Db()) await StockSeed.AddAsync(seed, lw.BlrVan, lw.W.ProductId, 20m, 10m, lw.W.UserId);
        var sale = await lw.SellAsync(lw.W.Blr, lw.BlrVan, 5m);
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        return (lw, sale, purchase);
    }

    [Theory]
    [InlineData("sales")]
    [InlineData("purchase")]
    public async Task CreateReturn_InvalidIdempotencyKeyHeader_Is400AndNothingIsPosted(string which)
    {
        var (lw, sale, purchase) = await SeedDocumentsAsync();
        await using var db = lw.W.Db(lw.W.Blr);
        var tooLong = new string('k', 101);
        var before = await db.StockMovements.CountAsync();

        ActionResult result = which == "sales"
            ? (await new SalesReturnsController(db, ListPricedSales.For(db)) { ControllerContext = Context(lw.W.UserId, tooLong) }.Create(
                new CreateSaleReturnRequest(sale.Id, BranchWorld.Day, null, [new SaleReturnLineRequest(sale.Lines.Single().Id, null, 1m)]), default)).Result!
            : (await new PurchaseReturnsController(db, new PurchaseService(db, new ExchangeRateService(db))) { ControllerContext = Context(lw.W.UserId, tooLong) }.Create(
                new CreatePurchaseReturnRequest(purchase.Id, BranchWorld.Day, null, [new PurchaseReturnLineRequest(purchase.Lines.Single().Id, null, 1m)]), default)).Result!;

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(before, await db.StockMovements.CountAsync());
    }

    [Fact]
    public async Task CreateReturn_SameIdempotencyKeyHeaderTwice_ReturnsTheSameSalesReturnAndOnlyRestocksOnce()
    {
        var (lw, sale, _) = await SeedDocumentsAsync();
        var request = new CreateSaleReturnRequest(sale.Id, BranchWorld.Day, null, [new SaleReturnLineRequest(sale.Lines.Single().Id, null, 2m)]);

        Guid first, second;
        await using (var db = lw.W.Db(lw.W.Blr)) first = Ok(await new SalesReturnsController(db, ListPricedSales.For(db)) { ControllerContext = Context(lw.W.UserId, "retry-1") }.Create(request, default)).Id;
        await using (var db = lw.W.Db(lw.W.Blr)) second = Ok(await new SalesReturnsController(db, ListPricedSales.For(db)) { ControllerContext = Context(lw.W.UserId, "retry-1") }.Create(request, default)).Id;

        Assert.Equal(first, second);
        Assert.Equal(17m, await lw.QohAsync(lw.BlrVan));
        await lw.AssertInvariantsAsync("controller replay");
    }

    [Fact]
    public async Task CreateReturn_PurchaseReturnWithStockLocationIdAndKey_IssuesAtThatLocationOnceAndMapsATransitLocationTo400OnStockLocationId()
    {
        var tw = await TransferWorld.CreateAsync();
        var lw = tw.L;
        var purchase = await lw.BuyAsync(lw.W.Blr, null, 10m);
        await MoveAsync(tw, lw.BlrStore, lw.BlrWarehouse, 4m);
        CreatePurchaseReturnRequest At(Guid location) =>
            new(purchase.Id, BranchWorld.Day, null, [new PurchaseReturnLineRequest(purchase.Lines.Single().Id, null, 3m)], StockLocationId: location);

        Guid first, second;
        await using (var db = lw.W.Db(lw.W.Blr)) first = Ok(await new PurchaseReturnsController(db, new PurchaseService(db, new ExchangeRateService(db), LocationWorld.Company("Purchase.Return"))) { ControllerContext = Context(lw.W.UserId, "p-1") }.Create(At(lw.BlrWarehouse), default)).Id;
        await using (var db = lw.W.Db(lw.W.Blr)) second = Ok(await new PurchaseReturnsController(db, new PurchaseService(db, new ExchangeRateService(db), LocationWorld.Company("Purchase.Return"))) { ControllerContext = Context(lw.W.UserId, "p-1") }.Create(At(lw.BlrWarehouse), default)).Id;

        Assert.Equal(first, second);
        Assert.Equal((6m, 1m), (await lw.QohAsync(lw.BlrStore), await lw.QohAsync(lw.BlrWarehouse)));
        await using var bad = lw.W.Db(lw.W.Blr);
        var result = await new PurchaseReturnsController(bad, new PurchaseService(bad, new ExchangeRateService(bad))) { ControllerContext = Context(lw.W.UserId) }.Create(At(lw.Transit), default);
        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(problem.Value);
        Assert.Equal(400, details.Status ?? problem.StatusCode ?? 400);
        Assert.Contains("stockLocationId", details.Errors.Keys);
        await tw.AssertInvariantsAsync("purchase return via the controller");
    }
}
