using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>Phase 4 T2 — WarehouseService: location and subtype created together, code/name rules, mirroring, usage gate, branch rules.</summary>
public class WarehouseServiceTests
{
    private static async Task<BranchWorld> WorldAsync()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        await BranchBuilder.EnsureLocationsAsync(db);
        return w;
    }

    private static WarehouseService Service(SelloraDbContext db, FakeStockLocationUsage? usage = null) => new(db, usage ?? new FakeStockLocationUsage());

    private static Task<StockLocation> LocationOfAsync(SelloraDbContext db, Warehouse w) => db.StockLocations.SingleAsync(l => l.Id == w.StockLocationId);

    [Fact]
    public async Task CreateAsync_CompanyWarehouse_CreatesLocationAndSubtypeTogether()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var wh = await Service(db).CreateAsync(new(" central-1 ", " Central ", null), w.UserId);

        var loc = await LocationOfAsync(db, wh);
        Assert.Equal((StockLocationType.CompanyWarehouse, "CENTRAL-1", "Central", null, true, TestCompany.DefaultId), (loc.LocationType, loc.Code, loc.Name, loc.BranchId, loc.IsActive, loc.CompanyId));
        Assert.Equal((StockLocationType.CompanyWarehouse, TestCompany.DefaultId, w.UserId), (wh.LocationType, wh.CompanyId, wh.CreatedByUserId));
        Assert.NotEqual(Guid.Empty, wh.Id);
        Assert.Equal(1, await db.Warehouses.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_BranchWarehouse_LocationCarriesTheBranch()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var wh = await Service(db).CreateAsync(new("BLRWH", "Bengaluru back store", w.Blr), w.UserId);

        var loc = await LocationOfAsync(db, wh);
        Assert.Equal((StockLocationType.BranchWarehouse, w.Blr), (loc.LocationType, loc.BranchId));
        Assert.Equal(StockLocationType.BranchWarehouse, wh.LocationType);
    }

    [Theory]
    [InlineData("MAIN")]     // branch store code
    [InlineData("main")]     // case-insensitive
    [InlineData("TRANSIT")]  // system transit
    [InlineData("BLR")]
    public async Task CreateAsync_CodeClashesWithABranchOrTransitCode_RejectsOnCode(string code)
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var locations = await db.StockLocations.CountAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new(code, "X", null), w.UserId));

        Assert.Equal("Code", ex.Field);
        Assert.Equal(locations, await db.StockLocations.CountAsync());
        Assert.Equal(0, await db.Warehouses.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_CodeClashesWithAnotherWarehouseOrAVehicleNumber_RejectsOnCode()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await Service(db).CreateAsync(new("WH1", "One", null), w.UserId);
        await new VehicleService(db, new FakeStockLocationUsage()).CreateAsync(new("TN-01", "Van", VehicleType.Van, null), w.UserId);

        var dup = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("wh1", "Two", null), w.UserId));
        var veh = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("TN-01", "Three", null), w.UserId));

        Assert.Equal(("Code", "Code"), (dup.Field, veh.Field));
        Assert.Equal(1, await db.Warehouses.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_SameCodeInAnotherCompany_Succeeds()
    {
        var w = await WorldAsync();
        await using (var other = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted()))
        {
            other.StockLocations.Add(new StockLocation { Id = Guid.NewGuid(), CompanyId = TestCompany.OtherId, LocationType = StockLocationType.CompanyWarehouse, Code = "WH1", Name = "Other WH" });
            await other.SaveChangesAsync();
        }
        await using var db = w.Db();

        var wh = await Service(db).CreateAsync(new("WH1", "Other WH", null), w.UserId);

        Assert.Equal(TestCompany.DefaultId, wh.CompanyId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("A B")]
    [InlineData("-A")]
    [InlineData("A--B")]
    [InlineData("A_B")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")] // 21
    public async Task CreateAsync_BadCode_RejectsOnCode(string code)
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new(code, "N", null), w.UserId));

        Assert.Equal("Code", ex.Field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_EmptyName_RejectsOnName(string name)
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("W1", name, null), w.UserId));

        Assert.Equal("Name", ex.Field);
    }

    [Fact]
    public async Task CreateAsync_NameOver150_RejectsOnName()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("W1", new string('n', 151), null), w.UserId));

        Assert.Equal("Name", ex.Field);
        await Service(db).CreateAsync(new("W2", new string('n', 150), null), w.UserId);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameInTheCompanyCaseInsensitive_RejectsOnName()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await Service(db).CreateAsync(new("W1", "Cold Store", null), w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("W2", " cold store ", w.Blr), w.UserId));

        Assert.Equal("Name", ex.Field);
        Assert.Equal(1, await db.Warehouses.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_WarehouseNameEqualToABranchOrVehicleName_IsAllowed()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await new VehicleService(db, new FakeStockLocationUsage()).CreateAsync(new("V1", "Shared name", VehicleType.Van, null), w.UserId);

        await Service(db).CreateAsync(new("W1", "Shared name", null), w.UserId);
        await Service(db).CreateAsync(new("W2", "Main", null), w.UserId); // name of the Main branch store
    }

    [Fact]
    public async Task CreateAsync_NameUsedInAnotherCompany_Succeeds()
    {
        var w = await WorldAsync();
        var otherBranch = BranchBuilder.Extra("OB", companyId: TestCompany.OtherId);
        await using (var other = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted()))
        {
            var loc = new StockLocation { Id = Guid.NewGuid(), CompanyId = TestCompany.OtherId, LocationType = StockLocationType.CompanyWarehouse, Code = "OW", Name = "Cold Store" };
            other.StockLocations.Add(loc);
            other.Warehouses.Add(new Warehouse { Id = Guid.NewGuid(), CompanyId = TestCompany.OtherId, StockLocationId = loc.Id, LocationType = StockLocationType.CompanyWarehouse });
            await other.SaveChangesAsync();
        }
        _ = otherBranch;
        await using var db = w.Db();

        await Service(db).CreateAsync(new("W1", "Cold Store", null), w.UserId);
    }

    [Fact]
    public async Task CreateAsync_UnknownForeignOrInactiveBranch_RejectsOnBranchId()
    {
        var w = await WorldAsync();
        var inactive = BranchBuilder.Extra("OFF", active: false);
        var foreign = BranchBuilder.Extra("FOR", companyId: TestCompany.OtherId);
        await using (var seed = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted()))
        {
            seed.Branches.Add(foreign);
            await seed.SaveChangesAsync();
        }
        await using var db = w.Db();
        db.Branches.Add(inactive);
        await db.SaveChangesAsync();

        foreach (var id in new[] { Guid.NewGuid(), foreign.Id, inactive.Id })
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("W" + id.ToString("N")[..6], "N" + id, id), w.UserId));
            Assert.Equal("BranchId", ex.Field);
        }
        Assert.Equal(0, await db.Warehouses.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_Rename_UpdatesTheLocationNameButNotTheCode()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var wh = await Service(db).CreateAsync(new("W1", "Old", w.Blr), w.UserId);

        var updated = await Service(db).UpdateAsync(wh.Id, new(" New name "), w.UserId);

        Assert.NotNull(updated);
        var loc = await LocationOfAsync(db, wh);
        Assert.Equal(("W1", "New name", w.Blr), (loc.Code, loc.Name, loc.BranchId));
        Assert.NotNull(updated!.UpdatedAt);
        Assert.Equal(w.UserId, loc.UpdatedByUserId);
    }

    [Fact]
    public async Task UpdateAsync_RenameToAnotherWarehousesName_RejectsButSameNameOnItselfIsFine()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var a = await Service(db).CreateAsync(new("A", "Alpha", null), w.UserId);
        await Service(db).CreateAsync(new("B", "Beta", null), w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).UpdateAsync(a.Id, new("BETA"), w.UserId));
        await Service(db).UpdateAsync(a.Id, new("ALPHA"), w.UserId);

        Assert.Equal("Name", ex.Field);
    }

    [Fact]
    public async Task UpdateAsync_UnknownOrForeignId_ReturnsNull()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        Assert.Null(await Service(db).UpdateAsync(Guid.NewGuid(), new("X"), w.UserId));
        Assert.Null(await Service(db).SetStatusAsync(Guid.NewGuid(), false, "test", w.UserId));
    }

    [Fact]
    public async Task SetStatusAsync_DeactivateAndReactivate_MirrorsToTheLocation()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var wh = await Service(db).CreateAsync(new("W1", "One", null), w.UserId);

        await Service(db).SetStatusAsync(wh.Id, false, "test", w.UserId);
        Assert.False((await LocationOfAsync(db, wh)).IsActive);

        await Service(db).SetStatusAsync(wh.Id, true, "test", w.UserId);
        Assert.True((await LocationOfAsync(db, wh)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_UsageReportsInUse_RejectsOnIsActiveAndChangesNothing()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var wh = await Service(db, usage).CreateAsync(new("W1", "One", null), w.UserId);
        usage.InUse.Add(wh.StockLocationId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db, usage).SetStatusAsync(wh.Id, false, "test", w.UserId));

        Assert.Equal("IsActive", ex.Field);
        await using var check = w.Db();
        Assert.True((await check.StockLocations.SingleAsync(l => l.Id == wh.StockLocationId)).IsActive);
        Assert.Contains(wh.StockLocationId, usage.Asked);
    }

    [Fact]
    public async Task SetStatusAsync_ReactivationNeverConsultsUsage()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var wh = await Service(db, usage).CreateAsync(new("W1", "One", null), w.UserId);
        await Service(db, usage).SetStatusAsync(wh.Id, false, "test", w.UserId);
        usage.Everything = true;
        usage.Asked.Clear();

        await Service(db, usage).SetStatusAsync(wh.Id, true, "test", w.UserId);

        Assert.Empty(usage.Asked);
        Assert.True((await LocationOfAsync(db, wh)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_ReactivatingWhileTheBranchIsInactive_RejectsOnIsActive()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var wh = await Service(db).CreateAsync(new("W1", "One", w.Blr), w.UserId);
        await Service(db).SetStatusAsync(wh.Id, false, "test", w.UserId);
        await new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(w.Blr, false, "test", w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).SetStatusAsync(wh.Id, true, "test", w.UserId));
        Assert.Equal("IsActive", ex.Field);

        await new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(w.Blr, true, "test", w.UserId);
        await Service(db).SetStatusAsync(wh.Id, true, "test", w.UserId);
        Assert.True((await LocationOfAsync(db, wh)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_DeactivatingTwice_IsANoOp()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var wh = await Service(db, usage).CreateAsync(new("W1", "One", null), w.UserId);
        await Service(db, usage).SetStatusAsync(wh.Id, false, "test", w.UserId);
        usage.Everything = true;

        var again = await Service(db, usage).SetStatusAsync(wh.Id, false, "test", w.UserId);

        Assert.NotNull(again);
    }
}
