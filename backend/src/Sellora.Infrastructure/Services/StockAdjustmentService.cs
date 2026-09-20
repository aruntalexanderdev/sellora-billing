using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class StockAdjustmentService(SelloraDbContext db) : IStockAdjustmentService
{
    private static readonly Dictionary<AdjustmentType, AdjustmentDirection?> RequiredDirection = new()
    {
        [AdjustmentType.Damage] = AdjustmentDirection.Decrease,
        [AdjustmentType.Loss] = AdjustmentDirection.Decrease,
        [AdjustmentType.Expiry] = AdjustmentDirection.Decrease,
        [AdjustmentType.OpeningStock] = AdjustmentDirection.Increase,
        [AdjustmentType.Correction] = null, // either direction
    };

    public async Task<StockAdjustment> CreateAdjustmentAsync(CreateAdjustmentInput input, Guid userId, CancellationToken ct = default)
    {
        if (input.QuantityBaseUnits <= 0)
        {
            throw new DomainValidationException("QuantityBaseUnits", "Quantity must be greater than zero.");
        }

        var requiredDirection = RequiredDirection[input.Type];
        if (requiredDirection is not null && requiredDirection != input.Direction)
        {
            throw new DomainValidationException("Direction", $"{input.Type} adjustments must be a {requiredDirection}.");
        }

        var product = await db.Products.Include(p => p.BaseUnit).SingleOrDefaultAsync(p => p.Id == input.ProductId, ct)
            ?? throw new DomainValidationException("ProductId", "Select a valid product.");

        var stockItem = await db.StockItems.SingleOrDefaultAsync(s => s.ProductId == product.Id, ct);
        if (stockItem is null)
        {
            stockItem = new StockItem { ProductId = product.Id, QuantityOnHand = 0, AverageCost = 0 };
            db.StockItems.Add(stockItem);
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var beforeQty = stockItem.QuantityOnHand;
        var adjustmentNo = await NextAdjustmentNoAsync(ct);

        var adjustment = new StockAdjustment
        {
            Id = Guid.NewGuid(),
            AdjustmentNo = adjustmentNo,
            ProductId = product.Id,
            Product = product,
            Type = input.Type,
            Direction = input.Direction,
            QuantityBaseUnits = input.QuantityBaseUnits,
            Reason = input.Reason,
            UserId = userId,
            User = user,
            BeforeQty = beforeQty,
        };

        if (input.Direction == AdjustmentDirection.Decrease)
        {
            if (input.QuantityBaseUnits > beforeQty)
            {
                throw new DomainValidationException("QuantityBaseUnits", $"Cannot remove {input.QuantityBaseUnits} {product.BaseUnit.ShortCode} — only {beforeQty} {product.BaseUnit.ShortCode} is currently in stock.");
            }

            await ConsumeBatchesFifoAsync(product.Id, input.QuantityBaseUnits, ct);
            stockItem.QuantityOnHand = beforeQty - input.QuantityBaseUnits;
        }
        else
        {
            if (input.UnitCost is null || input.UnitCost < 0)
            {
                throw new DomainValidationException("UnitCost", "Enter a unit cost for stock being added.");
            }

            adjustment.UnitCost = input.UnitCost;

            db.PurchaseBatches.Add(new PurchaseBatch
            {
                Id = Guid.NewGuid(),
                StockAdjustmentId = adjustment.Id,
                ProductId = product.Id,
                ReceivedDate = DateOnly.FromDateTime(adjustment.CreatedAt.UtcDateTime),
                PurchasedBaseQty = input.QuantityBaseUnits,
                RemainingBaseQty = input.QuantityBaseUnits,
                UnitCost = input.UnitCost.Value,
            });

            var newQty = beforeQty + input.QuantityBaseUnits;
            stockItem.AverageCost = newQty > 0
                ? (beforeQty * stockItem.AverageCost + input.QuantityBaseUnits * input.UnitCost.Value) / newQty
                : 0;
            stockItem.QuantityOnHand = newQty;
        }

        adjustment.AfterQty = stockItem.QuantityOnHand;

        db.StockAdjustments.Add(adjustment);
        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            Type = StockMovementType.Adjustment,
            QuantityBaseUnits = input.Direction == AdjustmentDirection.Increase ? input.QuantityBaseUnits : -input.QuantityBaseUnits,
            ReferenceType = "StockAdjustment",
            ReferenceId = adjustment.Id,
            ReferenceNo = adjustmentNo,
            UserId = userId,
            Notes = input.Type.ToString(),
        });

        await db.SaveChangesAsync(ct);
        return adjustment;
    }

    private async Task ConsumeBatchesFifoAsync(Guid productId, decimal quantityToConsume, CancellationToken ct)
    {
        var remaining = quantityToConsume;
        var batches = await db.PurchaseBatches
            .Where(b => b.ProductId == productId && b.RemainingBaseQty > 0)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

        foreach (var batch in batches)
        {
            if (remaining <= 0) break;
            var take = Math.Min(batch.RemainingBaseQty, remaining);
            batch.RemainingBaseQty -= take;
            remaining -= take;
        }
    }

    private async Task<string> NextAdjustmentNoAsync(CancellationToken ct)
    {
        var count = await db.StockAdjustments.CountAsync(ct);
        return $"ADJ-{count + 1:D4}";
    }
}
