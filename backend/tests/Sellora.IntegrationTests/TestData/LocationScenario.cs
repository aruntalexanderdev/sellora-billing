using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.TestData;

/// <summary>Fixed company-level permission set for service tests (the services' optional IEffectivePermissionService).</summary>
public sealed class FakePermissions(params string[] companyCodes) : IEffectivePermissionService
{
    private readonly EffectivePermissionSet _set = new(false, companyCodes.ToHashSet(StringComparer.Ordinal), new Dictionary<Guid, IReadOnlySet<string>>());
    public Task<EffectivePermissionSet> GetAsync(Guid membershipId, long accessVersion, CancellationToken ct = default) => Task.FromResult(_set);
    public Task<EffectivePermissionSet> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_set);
}

/// <summary>
/// Phase 8b PostgreSQL fixture: the StockScenario masters (loose-kg product, priced) plus a branch BLR (its store), a company warehouse CW,
/// a BLR warehouse BW and a vehicle assigned to BLR (VAN). Default store = Main. Every step runs on its own context, like separate requests.
/// </summary>
public sealed record LocationScenario(
    TestDatabase Database, StockScenario S, Guid Blr, Guid MainStore, Guid BlrStore, Guid CompanyWarehouse, Guid BlrWarehouse, Guid BlrVan)
{
    public static readonly IEffectivePermissionService CompanyLevel = new FakePermissions("Purchase.Create", "Sales.Create", "Inventory.Adjust");

    public static async Task<LocationScenario> CreateAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var scenario = await StockScenario.SeedAsync(db);
        var usage = new StockLocationUsageService(db);
        var branch = await new BranchService(db, usage).CreateAsync(new CreateBranchInput("BLR", "Bengaluru", null, null, null, null, null, null, null), scenario.UserId);
        var cw = await new WarehouseService(db, usage).CreateAsync(new CreateWarehouseInput("CW", "Central warehouse", null), scenario.UserId);
        var bw = await new WarehouseService(db, usage).CreateAsync(new CreateWarehouseInput("BW", "Bengaluru warehouse", branch.Id), scenario.UserId);
        var van = await new VehicleService(db, usage).CreateAsync(new CreateVehicleInput("VAN-1", "Van one", VehicleType.Van, branch.Id), scenario.UserId);
        var main = await db.StockLocations.Where(l => l.BranchId == DefaultTenant.BranchId && l.LocationType == StockLocationType.BranchStore).Select(l => l.Id).SingleAsync();
        var blrStore = await db.StockLocations.Where(l => l.BranchId == branch.Id && l.LocationType == StockLocationType.BranchStore).Select(l => l.Id).SingleAsync();
        return new LocationScenario(database, scenario, branch.Id, main, blrStore, cw.StockLocationId, bw.StockLocationId, van.StockLocationId);
    }

    public SelloraDbContext Db(Guid? branch = null) => Database.CreateDbContext(null, branch is null ? null : BranchContext.Unrestricted(branch));

    public async Task<PurchaseOrder> BuyAsync(Guid? branch, Guid? location, decimal kg, decimal ratePerKg = 10m, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch);
        return await new PurchaseService(db, new ExchangeRateService(db), permissions ?? CompanyLevel).CreatePurchaseAsync(
            new CreatePurchaseInput(S.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(S.ProductId, S.LooseKgModeId, kg, ratePerKg, 0, 0)], StockLocationId: location), S.UserId);
    }

    public async Task<SalesInvoice> SellAsync(Guid? branch, Guid? location, decimal kg, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch);
        return await new SalesService(db, permissions ?? CompanyLevel).CreateSaleAsync(
            new CreateSaleInput(S.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0, [new SaleLineInput(S.ProductId, S.LooseKgModeId, kg, StockScenario.LooseKgPrice, 0)], StockLocationId: location), S.UserId);
    }

    public async Task<StockAdjustment> AdjustAsync(Guid? branch, Guid? location, AdjustmentDirection direction, decimal kg, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch);
        return await new StockAdjustmentService(db, permissions ?? CompanyLevel).CreateAdjustmentAsync(
            new CreateAdjustmentInput(S.ProductId, S.LooseKgModeId, AdjustmentType.Correction, direction, kg, direction == AdjustmentDirection.Increase ? 12m : null, "test", StockLocationId: location), S.UserId);
    }

    public async Task<decimal> QohAsync(Guid location)
    {
        await using var db = Db();
        return await db.StockItems.Where(i => i.StockLocationId == location && i.ProductId == S.ProductId).Select(i => i.QuantityOnHand).SingleOrDefaultAsync();
    }

    public async Task AssertInvariantsAsync(string step)
    {
        await using var db = Db();
        var violations = await StockInvariants.FindViolationsAsync(db);
        Assert.True(violations.Count == 0, $"after {step}: " + string.Join("; ", violations.Select(v => $"{v.Key}: {string.Join(", ", v.Value)}")));
        var negative = await StockInvariants.QueryIdsAsync(db, """SELECT "StockLocationId" FROM "StockItems" WHERE "QuantityOnHand" < 0 UNION ALL SELECT "PurchaseBatchId" FROM "BatchLocationBalances" WHERE "RemainingBaseQty" < 0""");
        Assert.True(negative.Count == 0, $"after {step}: negative stock");
    }
}
