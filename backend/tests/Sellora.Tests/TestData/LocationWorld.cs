using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;

namespace Sellora.Tests.TestData;

/// <summary>
/// Phase 8b InMemory fixture on top of <see cref="BranchWorld"/> (Main + BLR, each with its store, product Rice/kg, 100 kg at Main):
/// a company warehouse CW, a Main-branch warehouse MW, a BLR warehouse BW, a vehicle assigned to BLR (VAN-B), a central vehicle (VAN-C),
/// and the company's Transit. Nothing is stocked except Main's 100 kg; tests add stock with <see cref="StockSeed"/> / the services.
/// </summary>
public sealed class LocationWorld
{
    public required BranchWorld W { get; init; }
    public required Guid MainStore { get; init; }
    public required Guid BlrStore { get; init; }
    public required Guid CompanyWarehouse { get; init; }
    public required Guid MainWarehouse { get; init; }
    public required Guid BlrWarehouse { get; init; }
    public required Guid BlrVan { get; init; }
    public required Guid CentralVan { get; init; }
    public required Guid Transit { get; init; }

    public static async Task<LocationWorld> CreateAsync()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        await BranchBuilder.EnsureLocationsAsync(db);
        var usage = new FakeStockLocationUsage();
        var warehouses = new WarehouseService(db, usage);
        var vehicles = new VehicleService(db, usage);
        var cw = await warehouses.CreateAsync(new CreateWarehouseInput("CW", "Central warehouse", null), w.UserId);
        var mw = await warehouses.CreateAsync(new CreateWarehouseInput("MW", "Main warehouse", w.Main), w.UserId);
        var bw = await warehouses.CreateAsync(new CreateWarehouseInput("BW", "Blr warehouse", w.Blr), w.UserId);
        var vb = await vehicles.CreateAsync(new CreateVehicleInput("VAN-B", "Blr van", VehicleType.Van, w.Blr), w.UserId);
        var vc = await vehicles.CreateAsync(new CreateVehicleInput("VAN-C", "Central van", VehicleType.Van, null), w.UserId);
        var transit = await db.StockLocations.Where(l => l.LocationType == StockLocationType.Transit).Select(l => l.Id).SingleAsync();
        return new LocationWorld
        {
            W = w, MainStore = await w.StoreOfAsync(w.Main), BlrStore = await w.StoreOfAsync(w.Blr),
            CompanyWarehouse = cw.StockLocationId, MainWarehouse = mw.StockLocationId, BlrWarehouse = bw.StockLocationId,
            BlrVan = vb.StockLocationId, CentralVan = vc.StockLocationId, Transit = transit,
        };
    }

    /// <summary>Permission service holding the given codes company-wide.</summary>
    public static IEffectivePermissionService Company(params string[] codes) => new FakeEffectivePermissions(codes);

    public Task<SalesInvoice> SellAsync(Guid? branch, Guid? location, decimal qty = 2m, IEffectivePermissionService? permissions = null)
    {
        var db = W.Db(branch);
        return ListPricedSales.For(db, permissions).CreateSaleAsync(
            new CreateSaleInput(W.CustomerId, BranchWorld.Day, PaymentMethod.Cash, 0, [new SaleLineInput(W.ProductId, W.ProductUnitId, qty, 20m, 0)], StockLocationId: location), W.UserId);
    }

    public Task<PurchaseOrder> BuyAsync(Guid? branch, Guid? location, decimal qty = 10m, decimal rate = 10m, IEffectivePermissionService? permissions = null)
    {
        var db = W.Db(branch);
        return new PurchaseService(db, new ExchangeRateService(db), permissions).CreatePurchaseAsync(
            new CreatePurchaseInput(W.SupplierId, BranchWorld.Day, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(W.ProductId, W.ProductUnitId, qty, rate, 0, 0)], StockLocationId: location), W.UserId);
    }

    public Task<StockAdjustment> AdjustAsync(Guid? branch, Guid? location, AdjustmentDirection direction = AdjustmentDirection.Increase, decimal qty = 5m, IEffectivePermissionService? permissions = null)
    {
        var db = W.Db(branch);
        return new StockAdjustmentService(db, permissions).CreateAdjustmentAsync(
            new CreateAdjustmentInput(W.ProductId, W.ProductUnitId, AdjustmentType.Correction, direction, qty, direction == AdjustmentDirection.Increase ? 12m : null, "t", StockLocationId: location), W.UserId);
    }

    public async Task<decimal> QohAsync(Guid location)
    {
        await using var db = W.Db();
        return await db.StockItems.Where(i => i.StockLocationId == location && i.ProductId == W.ProductId).Select(i => i.QuantityOnHand).SingleOrDefaultAsync();
    }

    /// <summary>Per-(location, product) reconciliation plus dual-write: QOH = Σ movements = Σ balances; batch remaining = Σ its balances; balances non-negative.</summary>
    public async Task AssertInvariantsAsync(string step)
    {
        await using var db = W.Db();
        var batches = await db.PurchaseBatches.AsNoTracking().ToListAsync();
        var balances = await db.BatchLocationBalances.AsNoTracking().ToListAsync();
        var items = await db.StockItems.AsNoTracking().ToListAsync();
        var moves = await db.StockMovements.AsNoTracking().ToListAsync();
        foreach (var b in batches) Assert.True(b.RemainingBaseQty == balances.Where(x => x.PurchaseBatchId == b.Id).Sum(x => x.RemainingBaseQty), $"{step}: batch {b.BatchSequence} dual-write");
        foreach (var i in items)
        {
            Assert.True(i.QuantityOnHand == moves.Where(m => m.ProductId == i.ProductId && m.StockLocationId == i.StockLocationId).Sum(m => m.QuantityBaseUnits), $"{step}: ledger at {i.StockLocationId}");
            Assert.True(i.QuantityOnHand == balances.Where(x => x.ProductId == i.ProductId && x.StockLocationId == i.StockLocationId).Sum(x => x.RemainingBaseQty), $"{step}: balances at {i.StockLocationId}");
            Assert.True(i.QuantityOnHand >= 0, $"{step}: negative stock at {i.StockLocationId}");
        }
        Assert.All(moves, m => Assert.Contains(items, i => i.StockLocationId == m.StockLocationId && i.ProductId == m.ProductId));
    }

    public static async Task<string?> FieldAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        return ex.Field;
    }
}
