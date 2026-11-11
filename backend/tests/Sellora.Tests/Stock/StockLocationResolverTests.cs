using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Stock;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 8b design D3 (+ owner O2, O4): which location a new sale, purchase or adjustment may post at. Failures are 400 on
/// <c>stockLocationId</c> and nothing is written. InMemory: the rules are service-level (the lock-time re-check is in the Postgres tests).
/// </summary>
public class StockLocationResolverTests
{
    private const string Field = "stockLocationId";

    private static async Task AssertRefusedAsync(LocationWorld lw, Func<Task> act, string because)
    {
        var before = await CountsAsync(lw);
        Assert.True(Field == await LocationWorld.FieldAsync(act), because);
        Assert.Equal(before, await CountsAsync(lw)); // nothing written
    }

    private static async Task<(int Docs, int Moves, int Items)> CountsAsync(LocationWorld lw)
    {
        await using var db = lw.W.Db();
        return (await db.SalesInvoices.CountAsync() + await db.PurchaseOrders.CountAsync() + await db.StockAdjustments.CountAsync(), await db.StockMovements.CountAsync(), await db.StockItems.CountAsync());
    }

    private static async Task StockAsync(LocationWorld lw, Guid location, decimal qty = 50m)
    {
        await using var db = lw.W.Db();
        await StockSeed.AddAsync(db, location, lw.W.ProductId, qty, 10m, lw.W.UserId);
    }

    // ---- sale ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sale_NoLocation_UsesTheWorkingBranchStore()
    {
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.BlrStore);

        var atBlr = await lw.SellAsync(lw.W.Blr, null);
        var atMain = await lw.SellAsync(lw.W.Main, null);
        var noBranch = await lw.SellAsync(null, null); // default branch

        Assert.Equal((lw.BlrStore, lw.MainStore, lw.MainStore), (atBlr.StockLocationId, atMain.StockLocationId, noBranch.StockLocationId));
        await lw.AssertInvariantsAsync("sale default");
    }

    [Fact]
    public async Task Sale_ExplicitStoreOfTheOwnBranch_IsAccepted()
    {
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.BlrStore);

        var sale = await lw.SellAsync(lw.W.Blr, lw.BlrStore);

        Assert.Equal(lw.BlrStore, sale.StockLocationId);
        Assert.Equal(48m, await lw.QohAsync(lw.BlrStore));
        Assert.Equal(100m, await lw.QohAsync(lw.MainStore));
    }

    [Fact]
    public async Task Sale_ExplicitStoreOfAnotherBranch_IsRefusedEvenWithCompanyLevelSalesCreate()
    {
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.BlrStore);

        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Main, lw.BlrStore), "branch user picks another branch's store");
        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Blr, lw.MainStore, permissions: LocationWorld.Company("Sales.Create")), "company-level grant does not open another branch's store (arch 13.2)");
    }

    [Fact]
    public async Task Sale_VehicleAssignedToTheWorkingBranch_IsAccepted_WithoutAnyCompanyLevelGrant()
    {
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.BlrVan);

        var sale = await lw.SellAsync(lw.W.Blr, lw.BlrVan, permissions: LocationWorld.Company());

        Assert.Equal(lw.BlrVan, sale.StockLocationId);
        Assert.Equal(48m, await lw.QohAsync(lw.BlrVan));
        await lw.AssertInvariantsAsync("sale from van");
    }

    [Fact]
    public async Task Sale_VehicleAssignedToAnotherBranch_IsRefused()
    {
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.BlrVan);

        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Main, lw.BlrVan), "Main user sells from BLR's van");
    }

    [Fact]
    public async Task Sale_VehicleAssignedToAnotherBranch_IsRefused_AlsoWithCompanyLevelGrant()
    {
        // Design D3 / test list: "vehicle of another branch 400". The company-level exception is for CENTRAL vehicles only.
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.BlrVan);

        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Main, lw.BlrVan, permissions: LocationWorld.Company("Sales.Create")), "company-level grant must not reach a vehicle assigned to another branch");
    }

    [Fact]
    public async Task Sale_CentralVehicle_NeedsCompanyLevelSalesCreate()
    {
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.CentralVan);

        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Blr, lw.CentralVan), "no permission service");
        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Blr, lw.CentralVan, permissions: LocationWorld.Company()), "permission not held");
        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Blr, lw.CentralVan, permissions: LocationWorld.Company("Purchase.Create", "Inventory.Adjust")), "other codes at company level do not count");

        var sale = await lw.SellAsync(lw.W.Blr, lw.CentralVan, permissions: LocationWorld.Company("Sales.Create"));
        Assert.Equal(lw.CentralVan, sale.StockLocationId);
        Assert.Equal(48m, await lw.QohAsync(lw.CentralVan));
    }

    [Fact]
    public async Task Sale_DeactivatedVehicle_IsRefused()
    {
        var lw = await LocationWorld.CreateAsync();
        await StockAsync(lw, lw.BlrVan);
        await using (var db = lw.W.Db())
        {
            (await db.Vehicles.SingleAsync(v => v.StockLocationId == lw.BlrVan)).IsActive = false;
            (await db.StockLocations.SingleAsync(l => l.Id == lw.BlrVan)).IsActive = false;
            await db.SaveChangesAsync();
        }

        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Blr, lw.BlrVan), "inactive vehicle");
    }

    [Fact]
    public async Task Sale_WarehouseAndTransit_AreRefused_CapabilityQ5()
    {
        var lw = await LocationWorld.CreateAsync();
        foreach (var location in new[] { lw.CompanyWarehouse, lw.MainWarehouse, lw.BlrWarehouse, lw.Transit }) await StockAsync(lw, location);

        foreach (var (branch, location, name) in new[] { (lw.W.Main, lw.CompanyWarehouse, "company warehouse"), (lw.W.Main, lw.MainWarehouse, "main warehouse"), (lw.W.Blr, lw.BlrWarehouse, "blr warehouse"), (lw.W.Blr, lw.Transit, "transit") })
        {
            await AssertRefusedAsync(lw, () => lw.SellAsync(branch, location, permissions: LocationWorld.Company("Sales.Create")), name + " cannot sell");
        }
    }

    [Fact]
    public async Task Sale_UnknownOrInactiveStore_IsRefusedWithTheSameField()
    {
        var lw = await LocationWorld.CreateAsync();
        await using (var db = lw.W.Db())
        {
            (await db.StockLocations.SingleAsync(l => l.Id == lw.BlrStore)).IsActive = false;
            await db.SaveChangesAsync();
        }

        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Main, Guid.NewGuid()), "unknown");
        await AssertRefusedAsync(lw, () => lw.SellAsync(lw.W.Blr, lw.BlrStore), "inactive store");
    }

    // ---- purchase (O2) ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Purchase_NoLocation_ReceivesIntoTheWorkingBranchStore()
    {
        var lw = await LocationWorld.CreateAsync();

        var atBlr = await lw.BuyAsync(lw.W.Blr, null);
        var atMain = await lw.BuyAsync(lw.W.Main, null);

        Assert.Equal((lw.BlrStore, lw.MainStore), (atBlr.StockLocationId, atMain.StockLocationId));
        Assert.Equal((110m, 10m), (await lw.QohAsync(lw.MainStore), await lw.QohAsync(lw.BlrStore)));
        await lw.AssertInvariantsAsync("purchase default");
    }

    [Fact]
    public async Task Purchase_OwnBranchWarehouse_IsAccepted_AnotherBranchsWarehouseIsRefused()
    {
        var lw = await LocationWorld.CreateAsync();

        var ok = await lw.BuyAsync(lw.W.Blr, lw.BlrWarehouse);
        Assert.Equal(lw.BlrWarehouse, ok.StockLocationId);
        Assert.Equal(10m, await lw.QohAsync(lw.BlrWarehouse));

        await AssertRefusedAsync(lw, () => lw.BuyAsync(lw.W.Blr, lw.MainWarehouse), "another branch's warehouse");
        await AssertRefusedAsync(lw, () => lw.BuyAsync(lw.W.Blr, lw.MainStore, permissions: LocationWorld.Company("Purchase.Create")), "another branch's store, even with the company-level grant");
        await lw.AssertInvariantsAsync("purchase into warehouse");
    }

    [Fact]
    public async Task Purchase_CompanyWarehouse_NeedsCompanyLevelPurchaseCreate_O2()
    {
        var lw = await LocationWorld.CreateAsync();

        await AssertRefusedAsync(lw, () => lw.BuyAsync(lw.W.Blr, lw.CompanyWarehouse), "no permission service (fail closed)");
        await AssertRefusedAsync(lw, () => lw.BuyAsync(lw.W.Blr, lw.CompanyWarehouse, permissions: LocationWorld.Company("Sales.Create")), "wrong code");

        var ok = await lw.BuyAsync(lw.W.Blr, lw.CompanyWarehouse, permissions: LocationWorld.Company("Purchase.Create"));
        Assert.Equal(lw.CompanyWarehouse, ok.StockLocationId);
        Assert.Equal((10m, 100m), (await lw.QohAsync(lw.CompanyWarehouse), await lw.QohAsync(lw.MainStore)));
        await lw.AssertInvariantsAsync("purchase into company warehouse");
    }

    [Fact]
    public async Task Purchase_VehicleAndTransit_AreRefused_CannotReceivePurchase()
    {
        var lw = await LocationWorld.CreateAsync();
        var all = LocationWorld.Company("Purchase.Create");

        await AssertRefusedAsync(lw, () => lw.BuyAsync(lw.W.Blr, lw.BlrVan, permissions: all), "vehicle");
        await AssertRefusedAsync(lw, () => lw.BuyAsync(lw.W.Blr, lw.CentralVan, permissions: all), "central vehicle");
        await AssertRefusedAsync(lw, () => lw.BuyAsync(lw.W.Blr, lw.Transit, permissions: all), "transit");
    }

    // ---- adjustment ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Adjustment_AnyNonTransitLocationOfTheBranch_IsAccepted()
    {
        var lw = await LocationWorld.CreateAsync();

        foreach (var location in new[] { lw.BlrStore, lw.BlrWarehouse, lw.BlrVan })
        {
            var adj = await lw.AdjustAsync(lw.W.Blr, location);
            Assert.Equal(location, adj.StockLocationId);
            Assert.Equal(5m, await lw.QohAsync(location));
        }
        var defaulted = await lw.AdjustAsync(lw.W.Blr, null);
        Assert.Equal(lw.BlrStore, defaulted.StockLocationId);
        await lw.AssertInvariantsAsync("adjustments");
    }

    [Fact]
    public async Task Adjustment_CompanyLevelLocations_NeedCompanyLevelInventoryAdjust_O2()
    {
        var lw = await LocationWorld.CreateAsync();

        foreach (var location in new[] { lw.CompanyWarehouse, lw.CentralVan })
        {
            await AssertRefusedAsync(lw, () => lw.AdjustAsync(lw.W.Blr, location), "no permission service");
            await AssertRefusedAsync(lw, () => lw.AdjustAsync(lw.W.Blr, location, permissions: LocationWorld.Company("Purchase.Create")), "wrong code");
            var ok = await lw.AdjustAsync(lw.W.Blr, location, permissions: LocationWorld.Company("Inventory.Adjust"));
            Assert.Equal(location, ok.StockLocationId);
        }
        await lw.AssertInvariantsAsync("company-level adjustments");
    }

    [Fact]
    public async Task Adjustment_TransitOtherBranchOrUnknown_IsRefused()
    {
        var lw = await LocationWorld.CreateAsync();
        var all = LocationWorld.Company("Inventory.Adjust");

        await AssertRefusedAsync(lw, () => lw.AdjustAsync(lw.W.Blr, lw.Transit, permissions: all), "transit");
        await AssertRefusedAsync(lw, () => lw.AdjustAsync(lw.W.Blr, lw.MainWarehouse, permissions: all), "another branch's warehouse");
        await AssertRefusedAsync(lw, () => lw.AdjustAsync(lw.W.Blr, lw.MainStore, permissions: all), "another branch's store");
        await AssertRefusedAsync(lw, () => lw.AdjustAsync(lw.W.Blr, Guid.NewGuid(), permissions: all), "unknown");
    }

    [Fact]
    public async Task Adjustment_BranchUserAdjustingAnotherBranchVehicle_IsRefused()
    {
        var lw = await LocationWorld.CreateAsync();

        await AssertRefusedAsync(lw, () => lw.AdjustAsync(lw.W.Main, lw.BlrVan), "Main user adjusts BLR's van");
    }
}
