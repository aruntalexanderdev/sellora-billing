using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 9 D7 / D9 on EF InMemory: the Transit location is visible only to a company-level reader of the permission (never to a branch-scoped
/// reader nor to a permission-less load such as the POS catalog); the usage hook reports open transfers (every status except Completed / Cancelled / Rejected)
/// even when the caller's Branch filter would hide the transfer, and the warehouse / vehicle / branch admin rules honour it.
/// </summary>
public class StockTransferReadsTests
{
    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    private static async Task<TransferWorld> InTransitAsync()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 4m)); // 40 pc
        await w.DispatchAsRequestedAsync(t.Id);
        return w;
    }

    // ---- StockVisibility: Transit -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stock_CompanyLevelReader_SeesTransit_InOverviewAndDetail()
    {
        var w = await InTransitAsync();
        var permissions = new FakeEffectivePermissions("Inventory.View");
        await using var db = w.Db();
        var controller = new StockController(db, permissions);

        var row = Ok(await controller.Overview(null, null, null, null)).Items.Single(i => i.ProductId == w.PenId);
        Assert.Equal(100m, row.QuantityOnHand); // 60 at Main + 40 in transit: company value is conserved
        Assert.Contains(row.Locations!, l => l.LocationId == w.L.Transit && l.Type == "Transit" && l.Qty == 40m);

        var detail = Ok(await controller.Detail(w.PenId, default));
        Assert.Contains(detail.Locations!, l => l.LocationId == w.L.Transit && l.Qty == 40m);
        Assert.Contains(detail.Movements, m => m.StockLocationId == w.L.Transit);
    }

    [Fact]
    public async Task Stock_BranchScopedReader_NeverSeesTransit_NotInSumsLocationsMovementsOrExplicitFilter()
    {
        var w = await InTransitAsync();
        var permissions = FakeScopedPermissions.AtBranch(w.Blr, "Inventory.View");
        await using var db = w.W.Db(w.Blr, allowed: new HashSet<Guid> { w.Blr });
        var controller = new StockController(db, permissions);

        var row = Ok(await controller.Overview(null, null, null, null)).Items.SingleOrDefault(i => i.ProductId == w.PenId);
        Assert.DoesNotContain(row?.Locations ?? [], l => l.LocationId == w.L.Transit);
        Assert.Equal(0m, row?.QuantityOnHand ?? 0m); // BLR holds nothing yet; the 40 in transit are on the transfer document, not in its stock
        var detail = Ok(await controller.Detail(w.PenId, default));
        Assert.DoesNotContain(detail.Locations ?? [], l => l.LocationId == w.L.Transit);
        Assert.DoesNotContain(detail.Movements, m => m.StockLocationId == w.L.Transit);
        Assert.Equal(0m, detail.QuantityOnHand);

        var denied = (await controller.Detail(w.PenId, default, w.L.Transit)).Result as ObjectResult;
        Assert.Equal(403, denied!.StatusCode);
    }

    [Fact]
    public async Task Stock_ReaderWhoseCodeIsHeldAtOneBranchOnly_NeverSeesTransit_EvenWithAccessToEveryBranch()
    {
        var w = await InTransitAsync();
        var permissions = FakeScopedPermissions.AtBranch(w.Main, "Inventory.View"); // not company-level, although the context can access both branches
        await using var db = w.W.Db();
        var row = Ok(await new StockController(db, permissions).Overview(null, null, null, null)).Items.Single(i => i.ProductId == w.PenId);

        Assert.DoesNotContain(row.Locations!, l => l.LocationId == w.L.Transit);
        Assert.Equal(60m, row.QuantityOnHand);
    }

    [Fact]
    public async Task StockVisibility_PermissionLessLoad_ExcludesTransit_AndCompanyLevelLoadIncludesIt()
    {
        var w = await InTransitAsync();
        var type = typeof(StockController).Assembly.GetType("Sellora.Api.Controllers.StockVisibility")!;
        var load = type.GetMethod("LoadAsync", BindingFlags.Public | BindingFlags.Static)!;
        var isVisible = type.GetMethod("IsVisible")!;
        async Task<bool> TransitVisible(IEffectivePermissionService p, string? code)
        {
            await using var db = w.Db();
            var task = (Task)load.Invoke(null, [db, p, code, CancellationToken.None])!;
            await task;
            return (bool)isVisible.Invoke(task.GetType().GetProperty("Result")!.GetValue(task), [w.L.Transit])!;
        }

        Assert.False(await TransitVisible(new FakeEffectivePermissions("Inventory.View"), null));                                  // POS catalog style: branch access only
        Assert.True(await TransitVisible(new FakeEffectivePermissions("Inventory.View"), "Inventory.View"));
        Assert.False(await TransitVisible(FakeScopedPermissions.AtBranch(w.Main, "Inventory.View"), "Inventory.View"));
        Assert.False(await TransitVisible(new FakeEffectivePermissions("Reports.Stock"), "Inventory.View"));                         // another code held: nothing visible at all
    }

    [Theory]
    [InlineData(true, false)]   // AssignedBranches member (restricted context) with a company-level code: no Transit
    [InlineData(false, true)]   // company-wide member with a company-level code: Transit
    public async Task StockVisibility_CompanyLevelCode_ShowsTransitOnlyToAnUnrestrictedContext(bool restricted, bool transitVisible)
    {
        var w = await InTransitAsync();
        var type = typeof(StockController).Assembly.GetType("Sellora.Api.Controllers.StockVisibility")!;
        var load = type.GetMethod("LoadAsync", BindingFlags.Public | BindingFlags.Static)!;
        var isVisible = type.GetMethod("IsVisible")!;

        await using var db = restricted ? w.W.Db(w.Main, allowed: new HashSet<Guid> { w.Main }) : w.W.Db();
        var task = (Task)load.Invoke(null, [db, new FakeEffectivePermissions("Inventory.View"), "Inventory.View", CancellationToken.None])!;
        await task;
        var visibility = task.GetType().GetProperty("Result")!.GetValue(task);

        Assert.Equal(transitVisible, (bool)isVisible.Invoke(visibility, [w.L.Transit])!);
        Assert.True((bool)isVisible.Invoke(visibility, [w.L.MainStore])!);
    }

    // ---- HasOpenTransfers ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StockTransferStatus.Draft, true)]
    [InlineData(StockTransferStatus.Requested, true)]
    [InlineData(StockTransferStatus.Approved, true)]
    [InlineData(StockTransferStatus.Dispatched, true)]
    [InlineData(StockTransferStatus.PartiallyReceived, true)]
    [InlineData(StockTransferStatus.Completed, false)]
    [InlineData(StockTransferStatus.Cancelled, false)]
    [InlineData(StockTransferStatus.Rejected, false)]
    public async Task HasOpenTransfers_ByStatus_ForBothEndpoints_AndNotForOthers(StockTransferStatus status, bool open)
    {
        var (w, _) = await StockTransferLifecycleTests.InAsync(status); // Main store -> BLR store
        await using var db = w.Db();
        var usage = new StockLocationUsageService(db);

        Assert.Equal(open, (await usage.GetAsync(w.L.MainStore)).HasOpenTransfers);
        Assert.Equal(open, (await usage.GetAsync(w.L.BlrStore)).HasOpenTransfers);
        Assert.False((await usage.GetAsync(w.L.BlrWarehouse)).HasOpenTransfers);
        Assert.False((await usage.GetAsync(w.L.Transit)).HasOpenTransfers); // Transit is only ever the carrier, never an endpoint
    }

    [Fact]
    public async Task HasOpenTransfers_WithABranchRestrictedContext_StillSeesTransfersTheFilterHides()
    {
        var w = await TransferWorld.CreateAsync();
        // A Main-only transfer (Main store -> Main warehouse, created at Main) is invisible to a BLR-only context...
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.MainWarehouse, [w.Pen(w.Box, 1m)], submit: true);
        await using var db = w.W.Db(w.Blr, allowed: new HashSet<Guid> { w.Blr });
        Assert.Null(await db.StockTransfers.SingleOrDefaultAsync(x => x.Id == t.Id));

        // ...but the integrity check of a location it touches still counts it (company-wide integrity, Branch filter bypassed).
        var usage = new StockLocationUsageService(db);
        Assert.True((await usage.GetAsync(w.L.MainWarehouse)).HasOpenTransfers);
        Assert.True((await usage.GetAsync(w.L.MainStore)).HasOpenTransfers);
    }

    [Fact]
    public async Task WarehouseDeactivation_WhileATransferIsOpen_IsRefused_AndAllowedOnceItIsCancelled()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrWarehouse, [w.Pen(w.Box, 1m)]);
        Guid warehouseId;
        await using (var db = w.Db()) warehouseId = await db.Warehouses.Where(x => x.StockLocationId == w.L.BlrWarehouse).Select(x => x.Id).SingleAsync();

        await using (var db = w.Db())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(warehouseId, false, "test", w.W.UserId));
        }
        await using (var db = w.Db()) await new StockTransferService(db, TransferWorld.Everything()).CancelAsync(t.Id, "no", w.W.UserId);
        await using (var db = w.Db())
        {
            await new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(warehouseId, false, "test", w.W.UserId);
        }
        await using var check = w.Db();
        Assert.False((await check.StockLocations.SingleAsync(l => l.Id == w.L.BlrWarehouse)).IsActive);
    }

    [Fact]
    public async Task VehicleDeactivationAndReassignment_WhileATransferIsOpen_AreRefused()
    {
        var w = await TransferWorld.CreateAsync();
        await w.CreateTransferAsync(w.L.MainStore, w.L.BlrVan, [w.Pen(w.Box, 1m)]);
        Guid vanId;
        await using (var db = w.Db()) vanId = await db.Vehicles.Where(v => v.StockLocationId == w.L.BlrVan).Select(v => v.Id).SingleAsync();

        await using (var db = w.Db())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, new StockLocationUsageService(db)).SetStatusAsync(vanId, false, "test", w.W.UserId));
        }
        await using (var db = w.Db())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, new StockLocationUsageService(db)).UpdateAsync(vanId, new UpdateVehicleInput("Blr van", VehicleType.Van, w.Main), w.W.UserId));
        }
        await using var check = w.Db();
        var van = await check.Vehicles.SingleAsync(v => v.Id == vanId);
        Assert.Equal((true, w.Blr), (van.IsActive, van.AssignedBranchId));
    }

    [Fact]
    public async Task BranchDeactivation_WhileATransferTouchesItsStore_IsRefused()
    {
        var w = await TransferWorld.CreateAsync();
        Guid warehouseId, vanId;
        await using (var db = w.Db())
        {
            warehouseId = await db.Warehouses.Where(x => x.StockLocationId == w.L.BlrWarehouse).Select(x => x.Id).SingleAsync();
            vanId = await db.Vehicles.Where(v => v.StockLocationId == w.L.BlrVan).Select(v => v.Id).SingleAsync();
            var usage = new StockLocationUsageService(db);
            await new WarehouseService(db, usage).SetStatusAsync(warehouseId, false, "test", w.W.UserId);
            await new VehicleService(db, usage).SetStatusAsync(vanId, false, "test", w.W.UserId);
        }
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)]);

        await using (var db = w.Db())
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(w.Blr, false, "test", w.W.UserId));
            Assert.Equal("IsActive", ex.Field);
        }
        await using (var db = w.Db()) await new StockTransferService(db, TransferWorld.Everything()).CancelAsync(t.Id, "no", w.W.UserId);
        await using (var db = w.Db())
        {
            await new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(w.Blr, false, "test", w.W.UserId);
        }
    }

    // ---- Transit is never a posting target for documents ----------------------------------------------------------------------------

    [Fact]
    public async Task SaleAtTransit_IsRefused_EvenWhenTheGoodsAreThere()
    {
        var w = await InTransitAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => w.L.SellAsync(w.Main, w.L.Transit, 1m, LocationWorld.Company("Sales.Create")));
        Assert.Equal(40m, await w.QohAsync(w.L.Transit));
    }
}
