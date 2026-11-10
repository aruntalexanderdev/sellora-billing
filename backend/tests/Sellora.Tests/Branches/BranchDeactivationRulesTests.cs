using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>Phase 4 T4 / O-1 — a branch cannot be deactivated while it has active branch warehouses or active assigned vehicles; the rule lifts as they are deactivated or reassigned; codes of warehouses/vehicles are live in the branch code check.</summary>
public class BranchDeactivationRulesTests
{
    private static async Task<BranchWorld> WorldAsync()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        await BranchBuilder.EnsureLocationsAsync(db);
        return w;
    }

    [Fact]
    public async Task SetStatusAsync_DeactivateWithAnActiveBranchWarehouse_RejectsOnIsActiveAndChangesNothing()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await new WarehouseService(db, new FakeStockLocationUsage()).CreateAsync(new("BW1", "Back store", w.Blr), w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId));

        Assert.Equal("IsActive", ex.Field);
        await using var check = w.Db();
        Assert.True((await check.Branches.SingleAsync(b => b.Id == w.Blr)).IsActive);
        Assert.True((await check.StockLocations.SingleAsync(l => l.BranchId == w.Blr && l.LocationType == StockLocationType.BranchStore)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_DeactivateWithAnAssignedActiveVehicle_RejectsOnIsActive()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await new VehicleService(db, new FakeStockLocationUsage()).CreateAsync(new("V1", "Van", VehicleType.Van, w.Blr), w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId));

        Assert.Equal("IsActive", ex.Field);
        Assert.True((await db.Branches.SingleAsync(b => b.Id == w.Blr)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_AfterWarehouseAndVehicleAreDeactivated_IsAllowed_AndMirrorsToTheStore()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var warehouse = await new WarehouseService(db, usage).CreateAsync(new("BW1", "Back store", w.Blr), w.UserId);
        var vehicle = await new VehicleService(db, usage).CreateAsync(new("V1", "Van", VehicleType.Van, w.Blr), w.UserId);
        await new WarehouseService(db, usage).SetStatusAsync(warehouse.Id, false, w.UserId);
        await new VehicleService(db, usage).SetStatusAsync(vehicle.Id, false, w.UserId);

        var branch = await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId);

        Assert.False(branch!.IsActive);
        Assert.False((await db.StockLocations.SingleAsync(l => l.BranchId == w.Blr && l.LocationType == StockLocationType.BranchStore)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_AfterTheVehicleIsReassignedOrUnassigned_IsAllowed()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var vehicle = await new VehicleService(db, new FakeStockLocationUsage()).CreateAsync(new("V1", "Van", VehicleType.Van, w.Blr), w.UserId);
        await new VehicleService(db, new FakeStockLocationUsage()).UpdateAsync(vehicle.Id, new("Van", VehicleType.Van, w.Main), w.UserId);

        Assert.False((await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId))!.IsActive);
        Assert.Equal(w.Main, vehicle.AssignedBranchId);
    }

    [Fact]
    public async Task SetStatusAsync_CompanyWarehouseAndOtherBranchesWarehouses_NeverBlock()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var other = BranchBuilder.Extra("OTH");
        db.Branches.Add(other);
        db.StockLocations.Add(BranchBuilder.Store(other));
        await db.SaveChangesAsync();
        var usage = new FakeStockLocationUsage();
        await new WarehouseService(db, usage).CreateAsync(new("CW1", "Company wh", null), w.UserId);
        await new WarehouseService(db, usage).CreateAsync(new("OW1", "Other wh", other.Id), w.UserId);
        await new VehicleService(db, usage).CreateAsync(new("V1", "Van", VehicleType.Van, other.Id), w.UserId);

        var branch = await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId);

        Assert.False(branch!.IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_Reactivation_IsNeverBlockedByWarehousesOrVehicles()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId);

        var branch = await new BranchService(db).SetStatusAsync(w.Blr, true, w.UserId);

        Assert.True(branch!.IsActive);
    }

    [Theory]
    [InlineData("WH-1", "wh-1")]
    [InlineData("VAN-1", "van-1")]
    public async Task CreateBranchAsync_CodeUsedByAWarehouseOrVehicle_RejectsOnCode(string existing, string branchCode)
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        if (existing.StartsWith("WH")) await new WarehouseService(db, usage).CreateAsync(new(existing, "A warehouse", null), w.UserId);
        else await new VehicleService(db, usage).CreateAsync(new(existing, "A van", VehicleType.Van, null), w.UserId);
        var branches = await db.Branches.CountAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db).CreateAsync(
            new CreateBranchInput(branchCode, "New branch", null, null, null, null, null, null, null), w.UserId));

        Assert.Equal("Code", ex.Field);
        Assert.Equal(branches, await db.Branches.CountAsync());
    }
}
