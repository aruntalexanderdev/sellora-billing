using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 8b reads (design D6) with the same product stocked at 2+ locations: Main store 100 kg @10, BLR store 30 @20, central
/// warehouse CW 20 @50 (+ BLR warehouse 5 @8 where noted). No 500s (the one-row-per-product assumptions are gone), per-location
/// breakdown, per-product sums, weighted average cost, and StockVisibility narrowing for branch-only holders.
/// </summary>
public class StockReadsAtMultipleLocationsTests
{
    private static readonly string[] StockCodes = ["Inventory.View", "Reports.Stock", "Reports.Movement"];

    private static async Task<LocationWorld> SeedAsync(bool blrWarehouse = false)
    {
        var lw = await LocationWorld.CreateAsync();
        await using var db = lw.W.Db();
        await StockSeed.AddAsync(db, lw.BlrStore, lw.W.ProductId, 30m, 20m, lw.W.UserId);
        await StockSeed.AddAsync(db, lw.CompanyWarehouse, lw.W.ProductId, 20m, 50m, lw.W.UserId);
        if (blrWarehouse) await StockSeed.AddAsync(db, lw.BlrWarehouse, lw.W.ProductId, 5m, 8m, lw.W.UserId);
        return lw;
    }

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    private static void AssertForbiddenBranchAccess(ObjectResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, problem.StatusCode);
        var details = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ProblemDetails>(problem.Value);
        Assert.Equal("branch_access_denied", details.Extensions["code"]?.ToString());
    }

    private static StockController Stock(SelloraDbContext db, IEffectivePermissionService p) => new(db, p);
    private static ReportsController Reports(SelloraDbContext db, IEffectivePermissionService p) => new(db, p);
    private static IEffectivePermissionService Everything() => new FakeEffectivePermissions(StockCodes);

    /// <summary>A branch-only holder at BLR: access to BLR only, the stock codes held at BLR only.</summary>
    private static (SelloraDbContext Db, IEffectivePermissionService Permissions) BlrOnly(LocationWorld lw) =>
        (lw.W.Db(lw.W.Blr, allowed: new HashSet<Guid> { lw.W.Blr }), FakeScopedPermissions.AtBranch(lw.W.Blr, StockCodes));

    // ---- company-level reader: everything visible -----------------------------------------------------------------------------------

    [Fact]
    public async Task Overview_ProductAtThreeLocations_SumsQuantityAndValueAndListsEachLocation()
    {
        var lw = await SeedAsync();
        await using var db = lw.W.Db();

        var page = Ok(await Stock(db, Everything()).Overview(null, null, null, null));

        var row = Assert.Single(page.Items);
        Assert.Equal((150m, 2600m), (row.QuantityOnHand, row.StockValue));                 // 100 + 30 + 20 ; 1000 + 600 + 1000
        Assert.Equal(2600m / 150m, row.AverageCost);                                       // weighted company average
        Assert.Equal(3, row.Locations!.Count);
        Assert.Equal(new[] { (lw.MainStore, 100m, 10m), (lw.BlrStore, 30m, 20m), (lw.CompanyWarehouse, 20m, 50m) }.OrderBy(x => x.Item1),
            row.Locations.Select(l => (l.LocationId, l.Qty, l.AvgCost)).OrderBy(x => x.LocationId));
        Assert.Equal(row.StockValue, row.Locations.Sum(l => l.Value));
    }

    [Fact]
    public async Task Overview_ExplicitLocation_ShowsOnlyThatLocation()
    {
        var lw = await SeedAsync();
        await using var db = lw.W.Db();

        var row = Assert.Single(Ok(await Stock(db, Everything()).Overview(null, null, null, null, stockLocationId: lw.BlrStore)).Items);

        Assert.Equal((30m, 20m, 600m), (row.QuantityOnHand, row.AverageCost, row.StockValue));
        Assert.Equal(lw.BlrStore, Assert.Single(row.Locations!).LocationId);
    }

    [Fact]
    public async Task Detail_ProductAtThreeLocations_DoesNotThrow_AndBreaksDownLocationsMovementsAndBatches()
    {
        var lw = await SeedAsync();
        await using var db = lw.W.Db();

        var detail = Ok(await Stock(db, Everything()).Detail(lw.W.ProductId, default));

        Assert.Equal((150m, 2600m), (detail.QuantityOnHand, detail.StockValue));
        Assert.Equal(3, detail.Locations!.Count);
        Assert.Equal(3, detail.Movements.Select(m => m.StockLocationId).Distinct().Count());
        Assert.All(detail.Movements, m => Assert.False(string.IsNullOrEmpty(m.StockLocationCode)));
        Assert.Equal(3, detail.Batches.Count);
        Assert.All(detail.Batches, b => Assert.Equal(b.RemainingBaseQty, Assert.Single(b.Locations!).Qty)); // each batch sits at one location
        Assert.Equal(150m, detail.Batches.Sum(b => b.RemainingBaseQty));
    }

    [Fact]
    public async Task Detail_ExplicitLocation_ScopesQuantityMovementsAndBatchesToIt()
    {
        var lw = await SeedAsync();
        await using var db = lw.W.Db();

        var detail = Ok(await Stock(db, Everything()).Detail(lw.W.ProductId, default, lw.CompanyWarehouse));

        Assert.Equal((20m, 50m, 1000m), (detail.QuantityOnHand, detail.AverageCost, detail.StockValue));
        Assert.Equal(lw.CompanyWarehouse, Assert.Single(detail.Locations!).LocationId);
        Assert.All(detail.Movements, m => Assert.Equal(lw.CompanyWarehouse, m.StockLocationId));
        Assert.Equal(20m, Assert.Single(detail.Batches).RemainingBaseQty);
    }

    [Fact]
    public async Task CurrentStockReport_AndCategories_ProductAtThreeLocations_SumsPerProduct()
    {
        var lw = await SeedAsync();
        await using var db = lw.W.Db();
        var controller = Reports(db, Everything());

        var row = Assert.Single(Ok(await controller.CurrentStock(null, null, null)).Items);
        Assert.Equal((150m, 2600m), (row.QuantityOnHand, row.StockValue));
        Assert.Equal(3, row.Locations!.Count);
        Assert.IsType<OkObjectResult>((await controller.CurrentStockCategories(default)).Result);

        var one = Assert.Single(Ok(await controller.CurrentStock(null, null, null, stockLocationId: lw.MainStore)).Items);
        Assert.Equal((100m, 1000m), (one.QuantityOnHand, one.StockValue));
    }

    [Fact]
    public async Task StockMovementReport_ShowsEveryLocationWithItsCode_AndFiltersByLocation()
    {
        var lw = await SeedAsync();
        await using var db = lw.W.Db();
        var controller = Reports(db, Everything());

        var all = Ok(await controller.StockMovement(null, null, null, null, 1, 50, default));
        Assert.Equal(3, all.Total);
        Assert.Equal(new[] { "BLR", "CW", "MAIN" }.Length, all.Items.Select(r => r.StockLocationCode).Distinct().Count());
        Assert.All(all.Items, r => Assert.False(string.IsNullOrEmpty(r.StockLocationCode)));

        var filtered = Ok(await controller.StockMovement(null, null, null, null, 1, 50, lw.BlrStore));
        Assert.Equal(lw.BlrStore, Assert.Single(filtered.Items).StockLocationId);
        Assert.Equal(30m, filtered.Items.Single().QuantityBaseUnits);
    }

    [Fact]
    public async Task Dashboard_StockValueAndLowStock_UseTheSumOverLocations()
    {
        var lw = await SeedAsync();
        await using (var db = lw.W.Db())
        {
            (await db.Products.SingleAsync()).ReorderLevel = 200m; // 150 on hand in total, but each location is below it too
            await db.SaveChangesAsync();
        }
        var branch = new BranchContext();
        branch.Set(null, null);
        await using var scoped = new SelloraDbContext(lw.W.Options, TestCompany.Context, branch);

        var dashboard = Ok(await new DashboardController(scoped, new FakeEffectivePermissions("Inventory.View"), branch).Get(default));

        Assert.Equal(2600m, dashboard.CurrentStockValue);
        var low = Assert.Single(dashboard.LowStockItems!);
        Assert.Equal(150m, low.QuantityOnHand);
    }

    [Fact]
    public async Task SalesCatalog_ShowsAvailabilityAtTheSellingLocation_AndNeverThrowsWithStockElsewhere()
    {
        var lw = await SeedAsync();
        var permissions = new FakeEffectivePermissions("Sales.Create");

        // Working branch MAIN (default): Main's store.
        await using (var db = lw.W.Db())
        {
            var item = Assert.Single(Ok(await new SalesController(db, ListPricedSales.For(db), permissions).Catalog(null)));
            Assert.Equal((100m, lw.MainStore), (item.QuantityOnHand, item.StockLocationId));
        }
        // Working branch BLR: BLR's store, not the 150 the product holds in total.
        await using (var db = lw.W.Db(lw.W.Blr))
        {
            var item = Assert.Single(Ok(await new SalesController(db, ListPricedSales.For(db), permissions).Catalog(null)));
            Assert.Equal((30m, lw.BlrStore), (item.QuantityOnHand, item.StockLocationId));
        }
        // Explicit location the caller can sell from: the central van with Sales.Create company-wide.
        await using (var db = lw.W.Db())
        {
            await StockSeed.AddAsync(db, lw.CentralVan, lw.W.ProductId, 4m, 10m, lw.W.UserId);
            var item = Assert.Single(Ok(await new SalesController(db, ListPricedSales.For(db, permissions), permissions).Catalog(null, lw.CentralVan)));
            Assert.Equal((4m, lw.CentralVan), (item.QuantityOnHand, item.StockLocationId));
        }
    }

    [Fact]
    public async Task SalesCatalog_ExplicitLocation_OnlyWhereTheCallerCouldSell_ElseOne403()
    {
        var lw = await SeedAsync();
        var permissions = new FakeEffectivePermissions("Sales.Create");

        // A warehouse cannot sell (CanSell): no stock peek through the catalog.
        await using (var db = lw.W.Db())
        {
            AssertForbiddenBranchAccess((await new SalesController(db, ListPricedSales.For(db), permissions).Catalog(null, lw.CompanyWarehouse)).Result as ObjectResult);
        }
        // Another branch's van, even with Sales.Create company-wide.
        await using (var db = lw.W.Db(lw.W.Main))
        {
            AssertForbiddenBranchAccess((await new SalesController(db, ListPricedSales.For(db), permissions).Catalog(null, lw.BlrVan)).Result as ObjectResult);
            AssertForbiddenBranchAccess((await new SalesController(db, ListPricedSales.For(db), permissions).CatalogByBarcode("none", lw.BlrVan)).Result as ObjectResult);
        }
        // The working branch's own van is fine.
        await using (var db = lw.W.Db(lw.W.Blr))
        {
            await StockSeed.AddAsync(db, lw.BlrVan, lw.W.ProductId, 3m, 10m, lw.W.UserId);
            var item = Assert.Single(Ok(await new SalesController(db, ListPricedSales.For(db), permissions).Catalog(null, lw.BlrVan)));
            Assert.Equal((3m, lw.BlrVan), (item.QuantityOnHand, item.StockLocationId));
        }
        // A central van without the company-wide grant.
        await using (var db = lw.W.Db(lw.W.Blr))
        {
            var branchOnly = FakeScopedPermissions.AtBranch(lw.W.Blr, "Sales.Create");
            AssertForbiddenBranchAccess((await new SalesController(db, ListPricedSales.For(db, branchOnly), branchOnly).Catalog(null, lw.CentralVan)).Result as ObjectResult);
        }
    }

    [Fact]
    public async Task BranchOnlyInventoryView_SeesItsOwnVehicle_NotAnotherBranchsVehicle_AndCentralVehiclesAsCompanyLevel()
    {
        var lw = await SeedAsync();
        await using (var seed = lw.W.Db())
        {
            await StockSeed.AddAsync(seed, lw.BlrVan, lw.W.ProductId, 3m, 10m, lw.W.UserId);
            await StockSeed.AddAsync(seed, lw.CentralVan, lw.W.ProductId, 4m, 10m, lw.W.UserId);
        }

        // MAIN-only reader: Main store 100 + CW 20 + central van 4; BLR store and BLR's van are hidden.
        await using (var db = lw.W.Db(lw.W.Main, allowed: new HashSet<Guid> { lw.W.Main }))
        {
            var permissions = FakeScopedPermissions.AtBranch(lw.W.Main, StockCodes);
            var overview = Assert.Single(Ok(await Stock(db, permissions).Overview(null, null, null, null)).Items);
            Assert.Equal(124m, overview.QuantityOnHand);
            Assert.DoesNotContain(overview.Locations!, l => l.LocationId == lw.BlrVan);
            AssertForbiddenBranchAccess((await Stock(db, permissions).Overview(null, null, null, null, stockLocationId: lw.BlrVan)).Result as ObjectResult);
        }
        // BLR-only reader sees its own van.
        var (blrDb, blrPermissions) = BlrOnly(lw);
        await using (blrDb)
        {
            var overview = Assert.Single(Ok(await Stock(blrDb, blrPermissions).Overview(null, null, null, null)).Items);
            Assert.Contains(overview.Locations!, l => l.LocationId == lw.BlrVan);
            Assert.Contains(overview.Locations!, l => l.LocationId == lw.CentralVan);
        }
    }

    [Fact]
    public async Task Pricing_ProductAtThreeLocations_DoesNotThrow_AndUsesTheWeightedAverageCost()
    {
        var lw = await SeedAsync();
        await using var db = lw.W.Db();

        var rows = Ok(await new PricingController(db, new PricingService(db)).List(null, default));

        var row = Assert.Single(rows);
        Assert.Equal(2600m / 150m, row.CostPerUnit); // base unit, factor 1
    }

    [Fact]
    public async Task PurchaseDetail_ReturnableQuantity_IsTheBalanceAtThePurchasesLocation()
    {
        var lw = await SeedAsync();
        var purchase = await lw.BuyAsync(lw.W.Blr, lw.BlrWarehouse, 10m, 10m);
        await using (var db = lw.W.Db(lw.W.Blr)) await ListPricedSales.For(db).CreateSaleAsync(
            new Sellora.Domain.Sales.CreateSaleInput(lw.W.CustomerId, BranchWorld.Day, PaymentMethod.Cash, 0, [new Sellora.Domain.Sales.SaleLineInput(lw.W.ProductId, lw.W.ProductUnitId, 3m, 20m, 0)]), lw.W.UserId);

        await using var read = lw.W.Db();
        var service = new PurchaseService(read, new ExchangeRateService(read));
        var line = Ok(await new PurchasesController(read, service).Get(purchase.Id, default)).Lines.Single();

        Assert.Equal(10m, line.ReturnableBaseQty); // the sale drew BLR's store (30), not the warehouse batch; 20 more kg at CW do not matter
    }

    // ---- branch-only holder: narrowing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BranchOnlyInventoryView_SeesOwnBranchAndCompanyLevelLocations_NotOtherBranches()
    {
        var lw = await SeedAsync(blrWarehouse: true);
        await using (var other = lw.W.Db())
        {
            await StockSeed.AddAsync(other, lw.MainWarehouse, lw.W.ProductId, 7m, 9m, lw.W.UserId); // another branch's warehouse
        }
        var (db, permissions) = BlrOnly(lw);
        await using var _ = db;

        var overview = Assert.Single(Ok(await Stock(db, permissions).Overview(null, null, null, null)).Items);

        // BLR store 30 + BLR warehouse 5 + company-level CW 20 = 55; Main store (100) and Main warehouse (7) are hidden.
        Assert.Equal(55m, overview.QuantityOnHand);
        Assert.Equal(new[] { lw.BlrStore, lw.BlrWarehouse, lw.CompanyWarehouse }.Order(), overview.Locations!.Select(l => l.LocationId).Order());
        Assert.Equal(30m * 20m + 5m * 8m + 20m * 50m, overview.StockValue);

        var detail = Ok(await Stock(db, permissions).Detail(lw.W.ProductId, default));
        Assert.Equal(55m, detail.QuantityOnHand);
        Assert.DoesNotContain(detail.Movements, m => m.StockLocationId == lw.MainStore || m.StockLocationId == lw.MainWarehouse);
        Assert.DoesNotContain(detail.Batches, b => b.Locations!.Any(l => l.LocationId == lw.MainStore));

        var report = Assert.Single(Ok(await Reports(db, permissions).CurrentStock(null, null, null)).Items);
        Assert.Equal(55m, report.QuantityOnHand);
        var movements = Ok(await Reports(db, permissions).StockMovement(null, null, null, null, 1, 50, default));
        Assert.DoesNotContain(movements.Items, r => r.StockLocationId == lw.MainStore || r.StockLocationId == lw.MainWarehouse);
        Assert.Equal(3, movements.Total);
    }

    [Theory]
    [InlineData("main store")]
    [InlineData("main warehouse")]
    public async Task BranchOnlyInventoryView_ExplicitHiddenLocation_Is403BranchAccessDenied_OnEveryRead(string which)
    {
        var lw = await SeedAsync();
        var hidden = which == "main store" ? lw.MainStore : lw.MainWarehouse;
        var (db, permissions) = BlrOnly(lw);
        await using var _ = db;

        AssertForbiddenBranchAccess((await Stock(db, permissions).Overview(null, null, null, null, stockLocationId: hidden)).Result as ObjectResult);
        AssertForbiddenBranchAccess((await Stock(db, permissions).Detail(lw.W.ProductId, default, hidden)).Result as ObjectResult);
        AssertForbiddenBranchAccess((await Reports(db, permissions).CurrentStock(null, null, null, stockLocationId: hidden)).Result as ObjectResult);
        AssertForbiddenBranchAccess((await Reports(db, permissions).StockMovement(null, null, null, null, 1, 20, hidden, default)).Result as ObjectResult);
        AssertForbiddenBranchAccess((await new SalesController(db, ListPricedSales.For(db), permissions).Catalog(null, hidden)).Result as ObjectResult);
        AssertForbiddenBranchAccess((await Reports(db, permissions).CurrentStock(null, null, null, stockLocationId: Guid.NewGuid())).Result as ObjectResult); // unknown: same answer
    }

    [Fact]
    public async Task BranchOnlyInventoryView_ExplicitCompanyLevelLocation_IsVisible()
    {
        var lw = await SeedAsync();
        var (db, permissions) = BlrOnly(lw);
        await using var _ = db;

        var row = Assert.Single(Ok(await Stock(db, permissions).Overview(null, null, null, null, stockLocationId: lw.CompanyWarehouse)).Items);

        Assert.Equal(20m, row.QuantityOnHand);
    }

    [Fact]
    public async Task InventoryViewHeldAtAnotherBranchOnly_NarrowsEvenWhenTheCallerCanAccessBothBranches()
    {
        var lw = await SeedAsync();
        // Access to Main AND BLR, but Inventory.View is granted at BLR only: PermittedBranches = {BLR} narrows the read.
        await using var db = lw.W.Db(lw.W.Blr, allowed: new HashSet<Guid> { lw.W.Blr, lw.W.Main });
        var permissions = FakeScopedPermissions.AtBranch(lw.W.Blr, "Inventory.View");

        var row = Assert.Single(Ok(await Stock(db, permissions).Overview(null, null, null, null)).Items);

        Assert.Equal(50m, row.QuantityOnHand); // BLR store 30 + company-level 20; Main's 100 hidden
        AssertForbiddenBranchAccess((await Stock(db, permissions).Overview(null, null, null, null, stockLocationId: lw.MainStore)).Result as ObjectResult);
    }

    [Fact]
    public async Task Dashboard_BranchOnlyInventoryView_ValueCoversOnlyVisibleLocations()
    {
        var lw = await SeedAsync();
        var branch = new BranchContext();
        branch.Set(lw.W.Blr, new HashSet<Guid> { lw.W.Blr });
        await using var db = new SelloraDbContext(lw.W.Options, TestCompany.Context, branch);

        var dashboard = Ok(await new DashboardController(db, FakeScopedPermissions.AtBranch(lw.W.Blr, "Inventory.View"), branch).Get(default));

        Assert.Equal(30m * 20m + 20m * 50m, dashboard.CurrentStockValue); // BLR store + CW; Main's 1000 hidden
    }
}
