---
name: stock-ledger-invariants
description: Sellora stock model invariants — base-unit ledger, transaction snapshots, FIFO batches, locking/transactions, movement types, negative-stock policy and reconciliation queries. Load for any change to purchases, sales, returns, adjustments, transfers, stock queries or reports.
user-invocable: false
---

# Stock ledger invariants

Source: analysis §5, §11–§13, §21. Verify against code before relying on line numbers.

## State lives in base units only
| Table | Quantity |
|---|---|
| `StockItems` (today PK `ProductId`; Phase B → `(ProductId, LocationId)`) | `QuantityOnHand`, `AverageCost` per base unit |
| `StockMovements` | signed `QuantityBaseUnits` (+ in, − out) |
| `PurchaseBatches` | `PurchasedBaseQty`, `RemainingBaseQty`, `UnitCost` per base unit |
| `SalesInvoiceLineBatchConsumptions` | `QtyBaseUnits`, `ReturnedQtyBaseUnits` |

**Invariant (per product, per location after Phase B):**
`Σ StockMovements.QuantityBaseUnits = StockItems.QuantityOnHand = Σ PurchaseBatches.RemainingBaseQty`

## Snapshot rule
Every document line that changes stock stores: entered quantity, `ProductUnitId`, `UnitId` + `UnitCode` snapshot, `ConversionFactorToBase` snapshot, `BaseQty`. Applies to purchase lines, sales lines, purchase/sales return lines (their **own** unit), stock adjustments, transfer lines. New `StockMovements` rows carry nullable `EnteredQuantity`, `EnteredProductUnitId`, `ConversionFactorSnapshot`. Never recompute history from current configuration.

## Write path pattern (all stock-changing operations)
1. `await using var tx = await StockLocks.BeginAsync(db, ct);`
2. Idempotency check where the endpoint supports it (`Idempotency.FindAsync`).
3. Ensure `StockItem` rows exist (upsert QOH 0) **before** `StockLocks.LockStockItemsAsync(db, productIds, ct)` — fixes the first-stock race.
4. Build lines: resolve ProductUnit (active + purchasable/sellable), validate decimals, compute `BaseQty`, `StockQuantity.EnsureStorable`.
5. Aggregate base qty per product across lines before availability checks (mixed units in one document).
6. Apply: batches (create or FIFO consume via the shared allocator), movements, StockItem QOH/AverageCost.
7. `SaveChanges`, commit.

## Policies
- **Negative stock is never allowed** (sales and decreases are refused).
- FIFO order: `PurchaseBatch.CreatedAt`, then `BatchSequence` (FEFO by `ExpiryDate` once D7 lands).
- One FIFO implementation (`BatchAllocator`) shared by sales, adjustments, transfers.
- Returns restore into the exact consumed batches; validate in **base qty**: `Σ prior BaseQty + new BaseQty ≤ line.BaseQty`; prorate money by `baseQty / line.BaseQty`; last return takes the exact remainder.
- Opening/breaking a pack is **not** a stock movement (D4).
- Cancellations use distinct movement types (`PurchaseCancellation`, `SaleCancellation`), not reversed-sign reuse.

## Movement types (target)
Purchase, PurchaseReturn, PurchaseCancellation, Sale, SaleReturn, SaleCancellation, Adjustment (+ `ReasonCode` from `AdjustmentType`), OpeningStock, TransferOut, TransferIn. Enums persist as strings — additions are non-breaking; update `frontend/.../inventory/stock/stock.models.ts`.

## Reconciliation SQL (must return no rows)
```sql
-- QOH vs ledger
SELECT s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
-- QOH vs batches
SELECT s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
-- line arithmetic
SELECT "Id" FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
SELECT "Id" FROM "PurchaseOrderLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
```

## Reporting rule
Never sum quantities across products; aggregate value only. Display base qty with the base unit's `DecimalPlaces`, plus optional breakdown.
