using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Companies;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>Phase 3 review fix (2) — branch-owned locations follow branch access; company-level locations stay visible to every member.</summary>
public class StockLocationsControllerTests
{
    private static async Task<(BranchWorld World, Guid BlrStore, Guid VanId)> WorldAsync()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        db.StockLocations.Add(BranchBuilder.Transit());
        var van = new Sellora.Domain.Entities.StockLocation { Id = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, LocationType = StockLocationType.Vehicle, Code = "VAN1", Name = "Van" };
        db.StockLocations.Add(van);
        await db.SaveChangesAsync();
        var blrStore = db.StockLocations.Single(l => l.BranchId == w.Blr).Id;
        return (w, blrStore, van.Id);
    }

    private static StockLocationsController Controller(SelloraDbContext db) => new(db);

    private static async Task<List<StockLocationDto>> ListAsync(SelloraDbContext db) =>
        ((await Controller(db).List(null, null, false)).Result as OkObjectResult)!.Value is IReadOnlyList<StockLocationDto> l ? l.ToList() : throw new InvalidOperationException("not ok");

    [Fact]
    public async Task List_BranchContextExcludingABranch_OmitsItsStoreButKeepsOtherStoresAndCompanyLevelLocations()
    {
        var (w, blrStore, vanId) = await WorldAsync();
        await using var db = w.Db(allowed: new HashSet<Guid> { w.Main }, unrestricted: false);

        var ids = (await ListAsync(db)).Select(l => l.Id).ToHashSet();

        Assert.DoesNotContain(blrStore, ids);
        Assert.Contains(DefaultTenant.MainStoreLocationId, ids);
        Assert.Contains(DefaultTenant.TransitLocationId, ids);
        Assert.Contains(vanId, ids);
    }

    [Fact]
    public async Task List_UnrestrictedBranchContext_ShowsEveryStore()
    {
        var (w, blrStore, _) = await WorldAsync();
        await using var db = w.Db();

        Assert.Contains(blrStore, (await ListAsync(db)).Select(l => l.Id));
    }

    [Fact]
    public async Task Get_StoreOfAnInaccessibleBranch_Returns404WhileAccessibleAndCompanyLevelLocationsReturn200()
    {
        var (w, blrStore, vanId) = await WorldAsync();
        await using var db = w.Db(allowed: new HashSet<Guid> { w.Main }, unrestricted: false);
        var controller = Controller(db);

        Assert.IsType<NotFoundResult>((await controller.Get(blrStore, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.Get(DefaultTenant.MainStoreLocationId, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.Get(vanId, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.Get(DefaultTenant.TransitLocationId, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Get_StoreOfAnotherCompany_Returns404()
    {
        var (w, _, _) = await WorldAsync();
        var otherBranch = BranchBuilder.MainBranch(TestCompany.OtherId);
        var otherStore = BranchBuilder.Store(otherBranch);
        await using (var other = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted()))
        {
            other.Branches.Add(otherBranch);
            other.StockLocations.Add(otherStore);
            await other.SaveChangesAsync();
        }
        await using var db = w.Db();

        Assert.IsType<NotFoundResult>((await Controller(db).Get(otherStore.Id, CancellationToken.None)).Result);
        Assert.DoesNotContain(otherStore.Id, (await ListAsync(db)).Select(l => l.Id));
    }
}
