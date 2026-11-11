using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>Shared branch rows for fixtures (Phase 2): the default company's Main, and extra branches for isolation tests.</summary>
public static class BranchBuilder
{
    /// <summary>Main of the default company, id <see cref="DefaultTenant.BranchId"/> (what DbSeeder adds).</summary>
    public static Branch MainBranch(Guid? companyId = null, Guid? id = null) => new()
    {
        Id = id ?? (companyId is null ? DefaultTenant.BranchId : Guid.NewGuid()), CompanyId = companyId ?? DefaultTenant.CompanyId,
        Code = DefaultTenant.BranchCode, Name = "Main", IsDefault = true, IsActive = true,
    };

    /// <summary>A non-default active branch (e.g. <c>BLR</c>).</summary>
    public static Branch Extra(string code, string? name = null, Guid? companyId = null, bool active = true) => new()
    {
        Id = Guid.NewGuid(), CompanyId = companyId ?? DefaultTenant.CompanyId, Code = code, Name = name ?? code + " branch", IsDefault = false, IsActive = active,
    };

    /// <summary>Adds Main to the default company if the context has none, and saves. Use in InMemory fixtures that create documents.</summary>
    public static async Task<Guid> EnsureMainAsync(SelloraDbContext db)
    {
        if (!db.Branches.Any(b => b.IsDefault))
        {
            db.Branches.Add(MainBranch());
            await db.SaveChangesAsync();
        }
        return DefaultTenant.BranchId;
    }

    /// <summary>The BranchStore location a branch owns (Phase 3): code/name/active mirror the branch; the default company's Main gets the reserved id.</summary>
    public static StockLocation Store(Branch branch) => new()
    {
        Id = branch.Id == DefaultTenant.BranchId ? DefaultTenant.MainStoreLocationId : Guid.NewGuid(), CompanyId = branch.CompanyId, BranchId = branch.Id,
        LocationType = StockLocationType.BranchStore, Code = branch.Code, Name = branch.Name, IsActive = branch.IsActive,
    };

    /// <summary>The company's single Transit location (the default company gets the reserved id).</summary>
    public static StockLocation Transit(Guid? companyId = null) => new()
    {
        Id = companyId is null || companyId == DefaultTenant.CompanyId ? DefaultTenant.TransitLocationId : Guid.NewGuid(), CompanyId = companyId ?? DefaultTenant.CompanyId,
        BranchId = null, LocationType = StockLocationType.Transit, Code = DefaultTenant.TransitCode, Name = "In transit", IsActive = true,
    };

    /// <summary>Adds the stores of every branch and the Transit of the default company where missing, and saves (InMemory fixtures that need locations; R2: branches added via the builder have none).</summary>
    public static async Task EnsureLocationsAsync(SelloraDbContext db)
    {
        foreach (var branch in db.Branches.IgnoreQueryFilters().ToList())
        {
            if (!db.StockLocations.IgnoreQueryFilters().Any(l => l.BranchId == branch.Id && l.LocationType == StockLocationType.BranchStore))
                db.StockLocations.Add(Store(branch));
        }
        if (!db.StockLocations.IgnoreQueryFilters().Any(l => l.CompanyId == DefaultTenant.CompanyId && l.LocationType == StockLocationType.Transit))
            db.StockLocations.Add(Transit());
        await db.SaveChangesAsync();
    }
}
