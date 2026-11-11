using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>Phase 3 T3 — BranchService keeps each branch's BranchStore location in step: created with the branch, rename and status mirrored, code clash with a location code rejected, nothing orphaned.</summary>
public class BranchStoreTests
{
    private static CreateBranchInput New(string code, string? name = null) => new(code, name ?? code + " branch", null, null, null, null, null, null, null);
    private static UpdateBranchInput Edit(string name) => new(name, null, null, null, null, null, null, null);

    private static async Task<BranchWorld> WorldWithStoresAsync()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        await BranchBuilder.EnsureLocationsAsync(db);
        return w;
    }

    private static Task<StockLocation> StoreOfAsync(SelloraDbContext db, Guid branchId) =>
        db.StockLocations.SingleAsync(l => l.BranchId == branchId && l.LocationType == StockLocationType.BranchStore);

    [Fact]
    public async Task CreateAsync_NewBranch_CreatesAnActiveStoreWithTheBranchCodeAndName()
    {
        var w = await WorldWithStoresAsync();
        await using var db = w.Db();

        var branch = await new BranchService(db, new StockLocationUsageService(db)).CreateAsync(New(" kar ", "Karur"), w.UserId);

        var store = await StoreOfAsync(db, branch.Id);
        Assert.Equal(("KAR", "Karur", true, TestCompany.DefaultId), (store.Code, store.Name, store.IsActive, store.CompanyId));
        Assert.NotEqual(Guid.Empty, store.Id);
        Assert.Equal(1, await db.StockLocations.CountAsync(l => l.BranchId == branch.Id));
    }

    [Fact]
    public async Task CreateAsync_RejectedBranch_LeavesNoBranchAndNoStoreBehind()
    {
        var w = await WorldWithStoresAsync();
        await using var db = w.Db();
        var service = new BranchService(db, new StockLocationUsageService(db));
        var stores = await db.StockLocations.CountAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(New("BLR", "Another"), w.UserId));

        Assert.Equal(stores, await db.StockLocations.CountAsync());
        Assert.Equal(2, await db.Branches.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_CodeAlreadyUsedByAnotherLocation_RejectsOnCodeAndCreatesNothing()
    {
        var w = await WorldWithStoresAsync();
        await using var db = w.Db();
        db.StockLocations.Add(new StockLocation { Id = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, LocationType = StockLocationType.Vehicle, Code = "VAN1", Name = "Van" });
        await db.SaveChangesAsync();
        var branches = await db.Branches.CountAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db, new StockLocationUsageService(db)).CreateAsync(New("van1", "Van branch"), w.UserId));

        Assert.Equal("Code", ex.Field);
        Assert.Equal(branches, await db.Branches.CountAsync());
        Assert.Equal(0, await db.StockLocations.CountAsync(l => l.BranchId != null && l.Name == "Van branch"));
    }

    [Fact]
    public async Task CreateAsync_CodeUsedByALocationOfAnotherCompany_Succeeds()
    {
        var w = await WorldWithStoresAsync();
        await using (var other = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted()))
        {
            other.StockLocations.Add(new StockLocation { Id = Guid.NewGuid(), CompanyId = TestCompany.OtherId, LocationType = StockLocationType.Vehicle, Code = "KAR", Name = "Van" });
            await other.SaveChangesAsync();
        }
        await using var db = w.Db();

        var branch = await new BranchService(db, new StockLocationUsageService(db)).CreateAsync(New("KAR"), w.UserId);

        Assert.Equal("KAR", (await StoreOfAsync(db, branch.Id)).Code);
    }

    [Fact]
    public async Task UpdateAsync_Rename_MirrorsTheNameButNeverTheCode()
    {
        var w = await WorldWithStoresAsync();
        await using var db = w.Db();

        await new BranchService(db, new StockLocationUsageService(db)).UpdateAsync(w.Blr, Edit("Bangalore East"), w.UserId);

        var store = await StoreOfAsync(db, w.Blr);
        Assert.Equal(("BLR", "Bangalore East"), (store.Code, store.Name));
    }

    [Fact]
    public async Task SetStatusAsync_DeactivateAndReactivate_MirrorsToTheStore()
    {
        var w = await WorldWithStoresAsync();
        await using var db = w.Db();
        var service = new BranchService(db, new StockLocationUsageService(db));

        await service.SetStatusAsync(w.Blr, false, w.UserId);
        Assert.False((await StoreOfAsync(db, w.Blr)).IsActive);
        await service.SetStatusAsync(w.Blr, true, w.UserId);
        Assert.True((await StoreOfAsync(db, w.Blr)).IsActive);
    }

    [Fact]
    public async Task SetStatusAsync_DefaultBranchRefusedAndItsStoreStaysActive()
    {
        var w = await WorldWithStoresAsync();
        await using var db = w.Db();

        await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(w.Main, false, w.UserId));

        var store = await StoreOfAsync(db, w.Main);
        Assert.True(store.IsActive);
        Assert.Equal(DefaultTenant.MainStoreLocationId, store.Id);
    }

    [Fact]
    public async Task UpdateAndSetStatus_BranchWithoutAStore_ThrowBecauseEveryBranchMustOwnOne()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        db.StockLocations.RemoveRange(db.StockLocations.Where(l => l.BranchId == w.Blr));
        await db.SaveChangesAsync();
        var service = new BranchService(db, new StockLocationUsageService(db));

        var update = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(w.Blr, Edit("Renamed"), w.UserId));
        var status = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetStatusAsync(w.Blr, false, w.UserId));

        Assert.Contains("has no store location", update.Message);
        Assert.Contains("has no store location", status.Message);
        await using var check = w.Db(); // nothing was persisted
        var persisted = await check.Branches.SingleAsync(b => b.Id == w.Blr);
        Assert.Equal(("Bengaluru", true), (persisted.Name, persisted.IsActive));
    }

    [Fact]
    public async Task Mirroring_StoreOfAnotherCompanysBranch_IsNeverTouched()
    {
        var w = await WorldWithStoresAsync();
        var otherBranch = BranchBuilder.MainBranch(TestCompany.OtherId);
        await using (var other = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted()))
        {
            other.Branches.Add(otherBranch);
            other.StockLocations.Add(BranchBuilder.Store(otherBranch));
            await other.SaveChangesAsync();
        }
        await using var db = w.Db();

        await new BranchService(db, new StockLocationUsageService(db)).UpdateAsync(w.Blr, Edit("Bangalore East"), w.UserId);

        await using var check = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted());
        Assert.Equal("Main", (await StoreOfAsync(check, otherBranch.Id)).Name);
    }
}
