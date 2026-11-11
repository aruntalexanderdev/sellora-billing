using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>Phase 8b D7: a location is in use while any StockItem there has a non-zero quantity; the hook is wired into warehouse/vehicle deactivation, vehicle reassignment and branch-store deactivation. (Locks and races: PostgreSQL tests.)</summary>
public class StockLocationUsageServiceTests
{
    private static async Task<(LocationWorld Lw, Guid WarehouseId, Guid VanId)> SeedAsync()
    {
        var lw = await LocationWorld.CreateAsync();
        await using var db = lw.W.Db();
        var warehouseId = await db.Warehouses.Where(w => w.StockLocationId == lw.BlrWarehouse).Select(w => w.Id).SingleAsync();
        var vanId = await db.Vehicles.Where(v => v.StockLocationId == lw.BlrVan).Select(v => v.Id).SingleAsync();
        return (lw, warehouseId, vanId);
    }

    private static async Task StockAsync(LocationWorld lw, Guid location, decimal qty)
    {
        await using var db = lw.W.Db();
        await StockSeed.AddAsync(db, location, lw.W.ProductId, qty, 10m, lw.W.UserId);
    }

    /// <summary>Branch deactivation first requires its warehouses and vehicles to be inactive/reassigned (separate rule); do that so the store rule is what is tested.</summary>
    private static async Task ClearBranchDependentsAsync(LocationWorld lw, Guid warehouseId, Guid vanId)
    {
        await using var db = lw.W.Db();
        var usage = new StockLocationUsageService(db);
        await new WarehouseService(db, usage).SetStatusAsync(warehouseId, false, lw.W.UserId);
        await new VehicleService(db, usage).SetStatusAsync(vanId, false, lw.W.UserId);
    }

    private static async Task SetQtyAsync(LocationWorld lw, Guid location, decimal qty)
    {
        await using var db = lw.W.Db();
        (await db.StockItems.SingleAsync(i => i.StockLocationId == location)).QuantityOnHand = qty;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAsync_HasStockOnlyForNonZeroQuantityAtThatLocation()
    {
        var (lw, _, _) = await SeedAsync();
        await StockAsync(lw, lw.BlrWarehouse, 5m);
        await using var db = lw.W.Db();
        var usage = new StockLocationUsageService(db);

        Assert.True((await usage.GetAsync(lw.BlrWarehouse)).HasStock);
        Assert.True((await usage.GetAsync(lw.MainStore)).HasStock);       // the seeded 100 kg
        Assert.False((await usage.GetAsync(lw.BlrStore)).HasStock);       // another location's stock does not count
        Assert.False((await usage.GetAsync(Guid.NewGuid())).HasStock);
        Assert.False((await usage.GetAsync(lw.BlrWarehouse)).HasOpenTransfers);
    }

    [Fact]
    public async Task WarehouseDeactivation_WhileItHoldsStock_IsRefused_AndAllowedOnceEmpty()
    {
        var (lw, warehouseId, _) = await SeedAsync();
        await StockAsync(lw, lw.BlrWarehouse, 5m);

        await using (var db = lw.W.Db())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(warehouseId, false, lw.W.UserId));
        }
        await SetQtyAsync(lw, lw.BlrWarehouse, 0m); // the zero-quantity row stays and must not block
        await using (var db = lw.W.Db())
        {
            await new WarehouseService(db, new StockLocationUsageService(db)).SetStatusAsync(warehouseId, false, lw.W.UserId);
        }
        await using var check = lw.W.Db();
        Assert.False((await check.StockLocations.SingleAsync(l => l.Id == lw.BlrWarehouse)).IsActive);
    }

    [Fact]
    public async Task VehicleDeactivationAndReassignment_WhileItHoldsStock_AreRefused()
    {
        var (lw, _, vanId) = await SeedAsync();
        await StockAsync(lw, lw.BlrVan, 3m);

        await using (var db = lw.W.Db())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, new StockLocationUsageService(db)).SetStatusAsync(vanId, false, lw.W.UserId));
        }
        await using (var db = lw.W.Db())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, new StockLocationUsageService(db)).UpdateAsync(vanId, new UpdateVehicleInput("Blr van", VehicleType.Van, lw.W.Main), lw.W.UserId));
        }
        await using (var db = lw.W.Db())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, new StockLocationUsageService(db)).UpdateAsync(vanId, new UpdateVehicleInput("Blr van", VehicleType.Van, null), lw.W.UserId));
        }
        await using var check = lw.W.Db();
        var van = await check.Vehicles.SingleAsync(v => v.Id == vanId);
        Assert.Equal((true, lw.W.Blr), (van.IsActive, van.AssignedBranchId));

        await SetQtyAsync(lw, lw.BlrVan, 0m);
        await using (var db = lw.W.Db())
        {
            await new VehicleService(db, new StockLocationUsageService(db)).UpdateAsync(vanId, new UpdateVehicleInput("Blr van", VehicleType.Van, lw.W.Main), lw.W.UserId);
        }
    }

    [Fact]
    public async Task BranchDeactivation_WhileItsStoreHoldsStock_IsRefused_AndAllowedAfterTheStockIsSoldOut()
    {
        var (lw, warehouseId, vanId) = await SeedAsync();
        await ClearBranchDependentsAsync(lw, warehouseId, vanId);
        await lw.BuyAsync(lw.W.Blr, lw.BlrStore, 4m);

        await using (var db = lw.W.Db())
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(lw.W.Blr, false, lw.W.UserId));
            Assert.Equal("IsActive", ex.Field);
        }
        Assert.True((await new BranchServiceProbe(lw).IsActiveAsync()));

        await lw.SellAsync(lw.W.Blr, lw.BlrStore, 4m);
        await using (var db = lw.W.Db())
        {
            await new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(lw.W.Blr, false, lw.W.UserId);
        }
        Assert.False(await new BranchServiceProbe(lw).IsActiveAsync());
    }

    [Fact]
    public async Task BranchDeactivation_StockAtAnotherBranchOrCompanyLevel_DoesNotBlock()
    {
        var (lw, warehouseId, vanId) = await SeedAsync(); // Main store holds 100 kg; BLR's store is empty
        await ClearBranchDependentsAsync(lw, warehouseId, vanId);
        await StockAsync(lw, lw.CompanyWarehouse, 9m);

        await using var db = lw.W.Db();
        await new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(lw.W.Blr, false, lw.W.UserId);
    }

    private sealed class BranchServiceProbe(LocationWorld lw)
    {
        public async Task<bool> IsActiveAsync()
        {
            await using var db = lw.W.Db();
            return (await db.Branches.SingleAsync(b => b.Id == lw.W.Blr)).IsActive;
        }
    }
}
