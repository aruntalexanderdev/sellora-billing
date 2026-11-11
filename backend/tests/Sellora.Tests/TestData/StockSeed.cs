using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Tests.TestData;

/// <summary>
/// Hand-built opening stock at one location, written as the posting engine would (StockItem + one batch + its balance + an OpeningStock
/// movement; dual-write consistent) but without a numbered document, so fixtures can stock a second branch's store without shifting
/// document numbers. For fixtures only - production code posts through StockPosting.
/// </summary>
public static class StockSeed
{
    private static long _sequence = 1_000_000;

    public static async Task AddAsync(SelloraDbContext db, Guid locationId, Guid productId, decimal qty, decimal unitCost, Guid userId)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var item = await db.StockItems.FindAsync(locationId, productId);
        if (item is null)
        {
            item = new StockItem { CompanyId = companyId, StockLocationId = locationId, ProductId = productId, QuantityOnHand = 0, AverageCost = unitCost };
            db.StockItems.Add(item);
        }
        item.AverageCost = item.QuantityOnHand + qty == 0 ? unitCost : (item.QuantityOnHand * item.AverageCost + qty * unitCost) / (item.QuantityOnHand + qty);
        item.QuantityOnHand += qty;

        var batch = new PurchaseBatch
        {
            CompanyId = companyId, Id = Guid.NewGuid(), ProductId = productId, OriginStockLocationId = locationId, BatchSequence = Interlocked.Increment(ref _sequence),
            ReceivedDate = new DateOnly(2026, 11, 1), PurchasedBaseQty = qty, RemainingBaseQty = qty, UnitCost = unitCost,
        };
        db.PurchaseBatches.Add(batch);
        db.BatchLocationBalances.Add(new BatchLocationBalance { CompanyId = companyId, PurchaseBatchId = batch.Id, StockLocationId = locationId, ProductId = productId, RemainingBaseQty = qty });
        db.StockMovements.Add(new StockMovement
        {
            CompanyId = companyId, Id = Guid.NewGuid(), ProductId = productId, StockLocationId = locationId, Type = StockMovementType.OpeningStock, ReasonCode = AdjustmentType.OpeningStock, QuantityBaseUnits = qty,
            PurchaseBatchId = batch.Id, ReferenceType = "Seed", ReferenceId = batch.Id, ReferenceNo = "SEED", UserId = userId,
        });
        await db.SaveChangesAsync();
    }
}
