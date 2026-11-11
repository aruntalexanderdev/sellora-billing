using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>Phase 4 T3 — VehicleService: number normalisation, uniqueness, assignment / reassignment (location BranchId stays NULL), usage gate, mirroring.</summary>
public class VehicleServiceTests
{
    private static async Task<BranchWorld> WorldAsync()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        await BranchBuilder.EnsureLocationsAsync(db);
        return w;
    }

    private static VehicleService Service(SelloraDbContext db, FakeStockLocationUsage? usage = null) => new(db, usage ?? new FakeStockLocationUsage());
    private static Task<StockLocation> LocationOfAsync(SelloraDbContext db, Vehicle v) => db.StockLocations.SingleAsync(l => l.Id == v.StockLocationId);

    [Fact]
    public async Task CreateAsync_Vehicle_CreatesLocationWithoutABranchAndMirrorsNumberAndName()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var v = await Service(db).CreateAsync(new("TN 01 AB 1234", " Delivery van ", VehicleType.Van, w.Blr), w.UserId);

        var loc = await LocationOfAsync(db, v);
        Assert.Equal((StockLocationType.Vehicle, "TN-01-AB-1234", "Delivery van", null, true), (loc.LocationType, loc.Code, loc.Name, loc.BranchId, loc.IsActive));
        Assert.Equal(("TN-01-AB-1234", "Delivery van", VehicleType.Van, w.Blr, true, TestCompany.DefaultId), (v.VehicleNumber, v.VehicleName, v.VehicleType, v.AssignedBranchId, v.IsActive, v.CompanyId));
    }

    [Theory]
    [InlineData("wp cab 1234", "WP-CAB-1234")]
    [InlineData("  wp   cab--1234  ", "WP-CAB-1234")]
    [InlineData("wp - cab", "WP-CAB")]
    [InlineData("a--b", "A-B")]
    [InlineData("ka\t01", "KA-01")]
    [InlineData("A", "A")]
    [InlineData("abcdefghij-klmnopqrs", "ABCDEFGHIJ-KLMNOPQRS")] // exactly 20
    public async Task CreateAsync_VehicleNumber_IsNormalised(string raw, string expected)
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var v = await Service(db).CreateAsync(new(raw, "V", VehicleType.Car, null), w.UserId);

        Assert.Equal(expected, v.VehicleNumber);
        Assert.Equal(expected, (await LocationOfAsync(db, v)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("--ab--")] // leading/trailing separators remain after normalisation
    [InlineData("AB_12")]
    [InlineData("AB/12")]
    [InlineData("ABCDEFGHIJ KLMNOPQRST1")] // 21 after normalisation
    public async Task CreateAsync_BadVehicleNumber_RejectsOnVehicleNumber(string raw)
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new(raw, "V", VehicleType.Car, null), w.UserId));

        Assert.Equal("VehicleNumber", ex.Field);
        Assert.Equal(0, await db.Vehicles.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_DuplicateNumberIncludingInactiveAfterNormalisation_RejectsOnVehicleNumber()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var first = await Service(db).CreateAsync(new("TN 01", "One", VehicleType.Van, null), w.UserId);
        await Service(db).SetStatusAsync(first.Id, false, "test", w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("tn-01", "Two", VehicleType.Van, null), w.UserId));

        Assert.Equal("VehicleNumber", ex.Field);
        Assert.Equal(1, await db.Vehicles.CountAsync());
        Assert.Equal(1, await db.StockLocations.CountAsync(l => l.LocationType == StockLocationType.Vehicle));
    }

    [Theory]
    [InlineData("MAIN")]
    [InlineData("transit")]
    public async Task CreateAsync_NumberClashesWithABranchOrTransitCode_RejectsOnVehicleNumber(string number)
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new(number, "V", VehicleType.Van, null), w.UserId));

        Assert.Equal("VehicleNumber", ex.Field);
    }

    [Fact]
    public async Task CreateAsync_NumberClashesWithAWarehouseCode_RejectsOnVehicleNumber()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await new WarehouseService(db, new FakeStockLocationUsage()).CreateAsync(new("WH1", "One", null), w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("wh1", "V", VehicleType.Van, null), w.UserId));

        Assert.Equal("VehicleNumber", ex.Field);
    }

    [Fact]
    public async Task CreateAsync_SameNumberInAnotherCompany_Succeeds()
    {
        var w = await WorldAsync();
        await using (var other = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted()))
        {
            var loc = new StockLocation { Id = Guid.NewGuid(), CompanyId = TestCompany.OtherId, LocationType = StockLocationType.Vehicle, Code = "TN-01", Name = "Other" };
            other.StockLocations.Add(loc);
            other.Vehicles.Add(new Vehicle { Id = Guid.NewGuid(), CompanyId = TestCompany.OtherId, StockLocationId = loc.Id, LocationType = StockLocationType.Vehicle, VehicleNumber = "TN-01", VehicleName = "Other", VehicleType = VehicleType.Van, IsActive = true });
            await other.SaveChangesAsync();
        }
        await using var db = w.Db();

        await Service(db).CreateAsync(new("TN-01", "Mine", VehicleType.Van, null), w.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CreateAsync_EmptyName_RejectsOnVehicleName(string name)
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("V1", name, VehicleType.Van, null), w.UserId));

        Assert.Equal("VehicleName", ex.Field);
    }

    [Fact]
    public async Task CreateAsync_NameOver150OrUnknownType_Rejects()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var name = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("V1", new string('x', 151), VehicleType.Van, null), w.UserId));
        var type = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("V1", "n", (VehicleType)99, null), w.UserId));

        Assert.Equal(("VehicleName", "VehicleType"), (name.Field, type.Field));
    }

    [Fact]
    public async Task CreateAsync_DuplicateVehicleNamesAreAllowed()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        await Service(db).CreateAsync(new("V1", "Van", VehicleType.Van, null), w.UserId);

        await Service(db).CreateAsync(new("V2", "van", VehicleType.Van, null), w.UserId);
    }

    [Theory]
    [InlineData(VehicleType.Van)]
    [InlineData(VehicleType.Truck)]
    [InlineData(VehicleType.Car)]
    [InlineData(VehicleType.Motorcycle)]
    [InlineData(VehicleType.ThreeWheeler)]
    [InlineData(VehicleType.Other)]
    public async Task CreateAsync_EveryVehicleType_IsAccepted(VehicleType type)
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var v = await Service(db).CreateAsync(new("V1", "n", type, null), w.UserId);

        Assert.Equal(type, v.VehicleType);
    }

    [Fact]
    public async Task CreateAsync_UnknownForeignOrInactiveAssignedBranch_RejectsOnAssignedBranchId()
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
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).CreateAsync(new("V" + id.ToString("N")[..6], "n", VehicleType.Van, id), w.UserId));
            Assert.Equal("AssignedBranchId", ex.Field);
        }
        Assert.Equal(0, await db.Vehicles.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_AssignReassignUnassign_LeavesTheLocationBranchNull()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var v = await Service(db).CreateAsync(new("V1", "Van", VehicleType.Van, null), w.UserId);

        await Service(db).UpdateAsync(v.Id, new("Van", VehicleType.Van, w.Blr), w.UserId);
        Assert.Equal(w.Blr, v.AssignedBranchId);
        await Service(db).UpdateAsync(v.Id, new("Van", VehicleType.Van, w.Main), w.UserId);
        Assert.Equal(w.Main, v.AssignedBranchId);
        await Service(db).UpdateAsync(v.Id, new("Van", VehicleType.Van, null), w.UserId);
        Assert.Null(v.AssignedBranchId);

        Assert.Null((await LocationOfAsync(db, v)).BranchId);
    }

    [Fact]
    public async Task UpdateAsync_NameEdit_MirrorsToLocationAndKeepsNumberAndType()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var v = await Service(db).CreateAsync(new("V1", "Old", VehicleType.Van, null), w.UserId);

        await Service(db).UpdateAsync(v.Id, new(" New name ", VehicleType.Truck, null), w.UserId);

        var loc = await LocationOfAsync(db, v);
        Assert.Equal(("V1", "New name", "V1", VehicleType.Truck), (loc.Code, loc.Name, v.VehicleNumber, v.VehicleType));
        Assert.Equal(w.UserId, v.UpdatedByUserId);
    }

    [Fact]
    public async Task UpdateAsync_ReassignmentWhileInUse_RejectsOnAssignedBranchIdAndChangesNothing()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var v = await Service(db, usage).CreateAsync(new("V1", "Old", VehicleType.Van, w.Main), w.UserId);
        usage.InUse.Add(v.StockLocationId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db, usage).UpdateAsync(v.Id, new("Renamed", VehicleType.Truck, w.Blr), w.UserId));
        var unassign = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db, usage).UpdateAsync(v.Id, new("Renamed", VehicleType.Truck, null), w.UserId));

        Assert.Equal(("AssignedBranchId", "AssignedBranchId"), (ex.Field, unassign.Field));
        await using var check = w.Db();
        var stored = await check.Vehicles.SingleAsync();
        Assert.Equal((w.Main, "Old", VehicleType.Van), (stored.AssignedBranchId, stored.VehicleName, stored.VehicleType));
        Assert.Equal("Old", (await check.StockLocations.SingleAsync(l => l.Id == v.StockLocationId)).Name);
    }

    [Fact]
    public async Task UpdateAsync_InUseButBranchUnchanged_AllowsRename()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var v = await Service(db, usage).CreateAsync(new("V1", "Old", VehicleType.Van, w.Main), w.UserId);
        usage.Everything = true;

        await Service(db, usage).UpdateAsync(v.Id, new("Renamed", VehicleType.Van, w.Main), w.UserId);

        Assert.Equal("Renamed", (await LocationOfAsync(db, v)).Name);
    }

    [Fact]
    public async Task UpdateAsync_ReassignToInactiveOrUnknownBranch_RejectsOnAssignedBranchId()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var v = await Service(db).CreateAsync(new("V1", "n", VehicleType.Van, null), w.UserId);
        var off = BranchBuilder.Extra("OFF", active: false);
        db.Branches.Add(off);
        await db.SaveChangesAsync();

        foreach (var id in new[] { off.Id, Guid.NewGuid() })
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).UpdateAsync(v.Id, new("n", VehicleType.Van, id), w.UserId));
            Assert.Equal("AssignedBranchId", ex.Field);
        }
        Assert.Null(v.AssignedBranchId);
    }

    [Fact]
    public async Task SetStatusAsync_DeactivateAndReactivate_MirrorsToTheLocation()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var v = await Service(db).CreateAsync(new("V1", "n", VehicleType.Van, null), w.UserId);

        await Service(db).SetStatusAsync(v.Id, false, "test", w.UserId);
        Assert.False(v.IsActive);
        Assert.False((await LocationOfAsync(db, v)).IsActive);

        await Service(db).SetStatusAsync(v.Id, true, "test", w.UserId);
        Assert.True((await LocationOfAsync(db, v)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_ReactivationWhileAssignedBranchIsInactive_RejectsOnIsActiveAndChangesNothing()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var branch = BranchBuilder.Extra("VAN");
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        var v = await Service(db).CreateAsync(new("V1", "n", VehicleType.Van, branch.Id), w.UserId);
        await Service(db).SetStatusAsync(v.Id, false, "test", w.UserId);
        branch.IsActive = false; // the branch is deactivated after its vehicle (O-1 only counts active vehicles)
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db).SetStatusAsync(v.Id, true, "test", w.UserId));

        Assert.Equal("IsActive", ex.Field);
        await using var check = w.Db();
        Assert.False((await check.Vehicles.SingleAsync()).IsActive);
        Assert.False((await check.StockLocations.SingleAsync(l => l.Id == v.StockLocationId)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_DeactivationWhileInUse_RejectsOnIsActiveAndChangesNothing()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var v = await Service(db, usage).CreateAsync(new("V1", "n", VehicleType.Van, null), w.UserId);
        usage.InUse.Add(v.StockLocationId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Service(db, usage).SetStatusAsync(v.Id, false, "test", w.UserId));

        Assert.Equal("IsActive", ex.Field);
        await using var check = w.Db();
        Assert.True((await check.Vehicles.SingleAsync()).IsActive);
        Assert.True((await check.StockLocations.SingleAsync(l => l.Id == v.StockLocationId)).IsActive);
    }

    [Fact]
    public async Task UpdateAndSetStatus_UnknownId_ReturnNull()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        Assert.Null(await Service(db).UpdateAsync(Guid.NewGuid(), new("n", VehicleType.Van, null), w.UserId));
        Assert.Null(await Service(db).SetStatusAsync(Guid.NewGuid(), false, "test", w.UserId));
    }
}
