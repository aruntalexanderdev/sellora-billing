using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Posts stock adjustments entered in any active unit of the product. The entered quantity is converted to base units
/// once, here, with the unit row's stored factor; the entered quantity, unit, unit code, factor and entered cost are
/// snapshotted on the adjustment and never recomputed. Stock state (QOH, batches, movements) is base units only.
/// Lock order: product KEY SHARE → location KEY SHARE → StockItems upsert + FOR UPDATE → ADJ document number.
/// </summary>
public class StockAdjustmentService(SelloraDbContext db, IEffectivePermissionService? permissions = null) : IStockAdjustmentService
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
        // 1. Request-only checks (no reads).
        if (input.Quantity <= 0)
        {
            throw new DomainValidationException("Quantity", "Quantity must be greater than zero.");
        }

        var requiredDirection = RequiredDirection[input.Type];
        if (requiredDirection is not null && requiredDirection != input.Direction)
        {
            throw new DomainValidationException("Direction", $"{input.Type} adjustments must be a {requiredDirection}.");
        }

        var isIncrease = input.Direction == AdjustmentDirection.Increase;
        if (isIncrease && (input.UnitCost is null || input.UnitCost < 0))
        {
            throw new DomainValidationException("UnitCost", "Enter a unit cost for stock being added.");
        }

        // 2. Transaction + idempotent replay.
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.StockAdjustment, input.IdempotencyKey, ct) is { } existingId)
        {
            return await db.StockAdjustments.Include(x => x.Product).Include(x => x.User).SingleOrDefaultAsync(x => x.Id == existingId, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        }

        // 3. Product KEY SHARE before any product or unit read: a concurrent product-unit edit (FOR UPDATE on the
        // product) either committed before this point — and every read below sees it — or waits until this adjustment
        // commits and then sees it as stock history. The snapshots below can therefore never disagree with the row.
        await StockLocks.LockProductsKeyShareAsync(db, [input.ProductId], ct);

        // 4. Product (inactive products stay adjustable so stock can still be written off).
        var product = await db.Products.Include(p => p.BaseUnit).SingleOrDefaultAsync(p => p.Id == input.ProductId, ct)
            ?? throw new DomainValidationException("ProductId", "Select a valid product.");

        // 5. Entry unit: any active unit of this product (purchasable/sellable flags do not apply to physical counts).
        var productUnit = await db.ProductUnits.Include(u => u.Unit).SingleOrDefaultAsync(u => u.Id == input.ProductUnitId, ct)
            ?? throw new DomainValidationException("ProductUnitId", "Select a valid unit.");
        if (productUnit.ProductId != product.Id)
        {
            throw new DomainValidationException("ProductUnitId", "This unit does not belong to this product.");
        }
        var unitDisplayName = productUnit.Label ?? productUnit.Unit.Name;
        if (!productUnit.IsActive)
        {
            throw new DomainValidationException("ProductUnitId", $"{unitDisplayName} is no longer active.");
        }

        // 6. Quantities: entered decimals, exact base conversion, storable at 3 dp (never rounded), and the base
        // quantity within the base unit's own decimal places. The last rule is independent of the entered-decimal rule:
        // a fractional factor can push the base quantity past the base unit's precision (base kg at 2 dp, a 0.5 kg pack
        // at 2 dp: 0.01 pack = 0.005 kg), so it is checked explicitly. Phase 4/5 apply the same rule to purchase and
        // sales lines.
        StockQuantity.EnsureEnteredDecimals(input.Quantity, productUnit.Unit.DecimalPlaces, unitDisplayName, "Quantity");
        var factor = productUnit.ConversionFactorToBase;
        var baseQty = input.Quantity * factor;
        StockQuantity.EnsureStorable(baseQty, "Quantity");
        var baseUnit = product.BaseUnit;
        StockQuantity.EnsureWithinBaseDecimals(input.Quantity, productUnit.Unit.ShortCode, baseQty, baseUnit, "Quantity");

        // 7. Working branch (header branch or default) and the stock location, resolved BEFORE any stock lock (Phase 8b:
        // requested location or the working branch's store). Then the location KEY SHARE and the StockItem — created at QOH 0 if this
        // is the product's first stock at the location — held FOR UPDATE to commit.
        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        var companyLevel = await StockLocationRules.HasCompanyLevelAsync(permissions, "Inventory.Adjust", ct);
        var locationId = await StockLocationRules.ResolveAsync(db, PostingKind.Adjustment, input.StockLocationId, branch.Id, companyLevel, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, [locationId], ct);
        await StockLocationRules.ResolveAsync(db, PostingKind.Adjustment, locationId, branch.Id, companyLevel, ct);
        var stockItem = (await StockLocks.EnsureAndLockStockItemsAsync(db, [(locationId, product.Id)], ct))[(locationId, product.Id)];

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var beforeQty = stockItem.QuantityOnHand;

        // 8. The branch's ADJ document number.
        var adjustmentNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.StockAdjustment, branch, ct);

        // 9. The adjustment with its unit snapshots.
        var adjustment = new StockAdjustment
        {
            Id = Guid.NewGuid(),
            AdjustmentNo = adjustmentNo,
            BranchId = branch.Id,
            StockLocationId = locationId,
            ProductId = product.Id,
            Product = product,
            Type = input.Type,
            Direction = input.Direction,
            EnteredQuantity = input.Quantity,
            ProductUnitId = productUnit.Id,
            UnitId = productUnit.UnitId,
            UnitCode = productUnit.Unit.ShortCode,
            ConversionFactorToBase = factor,
            QuantityBaseUnits = baseQty,
            Reason = input.Reason,
            UserId = userId,
            User = user,
            BeforeQty = beforeQty,
        };

        Guid? batchId = null;
        IReadOnlyList<BatchAllocator.Allocation> consumed = [];
        var baseCode = product.BaseUnit.ShortCode;

        if (!isIncrease)
        {
            // 10. Decrease: never below zero; FIFO over batches; average cost unchanged. Cost input is ignored.
            if (baseQty > beforeQty)
            {
                throw new DomainValidationException("Quantity",
                    $"Cannot remove {StockQuantity.Format(input.Quantity)} {productUnit.Unit.ShortCode} ({StockQuantity.Format(baseQty)} {baseCode}) — only {StockQuantity.Format(beforeQty)} {baseCode} is in stock.");
            }

            consumed = await StockPosting.IssueFifoAsync(db, stockItem, baseQty,
                shortBy => new DomainValidationException("Quantity", $"Batch stock is short by {StockQuantity.Format(shortBy)} — cannot remove {StockQuantity.Format(baseQty)} {baseCode}."), ct);
        }
        else
        {
            // 11. Increase: cost entered per entered unit; per-base cost rounded to 6 dp explicitly (the column scale) and that same value feeds the batch, the adjustment and the average-cost maths.
            var enteredUnitCost = Math.Round(input.UnitCost!.Value, 4, MidpointRounding.AwayFromZero);
            var unitCost = StockCost.PerBaseUnit(enteredUnitCost / factor);
            adjustment.EnteredUnitCost = enteredUnitCost;
            adjustment.UnitCost = unitCost;

            batchId = Guid.NewGuid();
            var batch = new PurchaseBatch
            {
                Id = batchId.Value,
                StockAdjustmentId = adjustment.Id,
                ProductId = product.Id,
                ReceivedDate = DateOnly.FromDateTime(adjustment.CreatedAt.UtcDateTime),
                PurchasedBaseQty = baseQty,
                UnitCost = unitCost,
            };
            db.PurchaseBatches.Add(batch);
            StockPosting.Receive(db, stockItem, batch, baseQty, unitCost);
        }

        // 12. Ledger rows, idempotency record, save, commit.
        adjustment.AfterQty = stockItem.QuantityOnHand;
        db.StockAdjustments.Add(adjustment);

        // Unit context on every row; the entered quantity only when one row carries the whole adjustment (Phase 6a-D2).
        StockMovement Movement(decimal signedQty, Guid? forBatchId, decimal? enteredQty) => MovementSnapshot.Apply(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            StockLocationId = locationId,
            PurchaseBatchId = forBatchId,
            Type = input.Type == AdjustmentType.OpeningStock ? StockMovementType.OpeningStock : StockMovementType.Adjustment,
            QuantityBaseUnits = signedQty,
            ReferenceType = "StockAdjustment",
            ReferenceId = adjustment.Id,
            ReferenceNo = adjustmentNo,
            UserId = userId,
            Notes = input.Type.ToString(),
            ReasonCode = input.Type,
        }, adjustment.ProductUnitId, adjustment.UnitCode, adjustment.ConversionFactorToBase, enteredQty);

        if (isIncrease)
        {
            db.StockMovements.Add(Movement(baseQty, batchId, adjustment.EnteredQuantity));
        }
        else
        {
            // One ledger row per batch the removal drew from, so each batch's history shows exactly what the
            // adjustment took out of it; together they sum to the adjusted quantity.
            foreach (var allocation in consumed)
            {
                db.StockMovements.Add(Movement(-allocation.QtyBaseUnits, allocation.Batch.Id, consumed.Count == 1 ? adjustment.EnteredQuantity : null));
            }
        }

        Idempotency.Record(db, userId, Idempotency.StockAdjustment, input.IdempotencyKey, adjustment.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return adjustment;
    }
}
