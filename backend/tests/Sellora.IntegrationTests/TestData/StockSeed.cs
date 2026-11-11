using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Hand-built opening stock at one location (StockItem + one batch + its balance + an OpeningStock movement, dual-write consistent), with no
/// numbered document - so fixtures can stock a branch's own store (8b: a branch sells from its own store) without shifting document numbers.
/// Fixtures only; production code posts through StockPosting.
/// </summary>
public static class StockSeed
{

    public static async Task<Guid> StoreOfAsync(SelloraDbContext db, Guid branchId) =>
        await db.StockLocations.Where(l => l.BranchId == branchId && l.LocationType == StockLocationType.BranchStore).Select(l => l.Id).SingleAsync();

    public static async Task AddAsync(SelloraDbContext db, Guid locationId, Guid productId, decimal qty, decimal unitCost, Guid userId)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var item = await db.StockItems.FindAsync(locationId, productId);
        if (item is null)
        {
            item = new StockItem { CompanyId = companyId, StockLocationId = locationId, ProductId = productId, QuantityOnHand = 0, AverageCost = unitCost };
            db.StockItems.Add(item);
            await db.SaveChangesAsync(); // the balance's FK needs the stock row first
        }
        item.AverageCost = (item.QuantityOnHand * item.AverageCost + qty * unitCost) / (item.QuantityOnHand + qty);
        item.QuantityOnHand += qty;

        var batch = new PurchaseBatch
        {
            CompanyId = companyId, Id = Guid.NewGuid(), ProductId = productId, OriginStockLocationId = locationId,
            ReceivedDate = new DateOnly(2026, 11, 1), PurchasedBaseQty = qty, RemainingBaseQty = qty, UnitCost = unitCost,
        };
        db.PurchaseBatches.Add(batch);
        await db.SaveChangesAsync();
        db.BatchLocationBalances.Add(new BatchLocationBalance { CompanyId = companyId, PurchaseBatchId = batch.Id, StockLocationId = locationId, ProductId = productId, RemainingBaseQty = qty });
        db.StockMovements.Add(new StockMovement
        {
            CompanyId = companyId, Id = Guid.NewGuid(), ProductId = productId, StockLocationId = locationId, Type = StockMovementType.OpeningStock, ReasonCode = AdjustmentType.OpeningStock, QuantityBaseUnits = qty,
            PurchaseBatchId = batch.Id, ReferenceType = "Seed", ReferenceId = batch.Id, ReferenceNo = "SEED", UserId = userId,
        });
        await db.SaveChangesAsync();
    }
}
