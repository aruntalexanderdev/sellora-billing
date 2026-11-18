-- sellora:description Stock transfers V1-V8
-- Applied by Sellora.DbMigrator on every deploy (docs/features/db-release-automation.md); can still be run by hand with psql.
-- Phase 9 stock transfers: validation (V1-V8). Read-only; EVERY query must return no rows.
-- Run after applying AddStockTransfers, after any transfer flow in tests/smoke, and before the release sign-off:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f backend/db/validation/V__stock-transfers-validation.sql
-- Transit is an ordinary location here: its StockItems / movements / balances must equal the in-transit quantities (V4).

-- V1 per (location, product): QOH = SUM(movements), including Transit.
SELECT 'V1' AS chk, s."StockLocationId", s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "CompanyId", "StockLocationId", "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1, 2, 3) m
  ON m."CompanyId" = s."CompanyId" AND m."StockLocationId" = s."StockLocationId" AND m."ProductId" = s."ProductId"
WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
UNION ALL
SELECT 'V1', m."StockLocationId", m."ProductId" FROM "StockMovements" m
LEFT JOIN "StockItems" s ON s."CompanyId" = m."CompanyId" AND s."StockLocationId" = m."StockLocationId" AND s."ProductId" = m."ProductId"
WHERE s."ProductId" IS NULL GROUP BY 1, 2, 3 HAVING SUM(m."QuantityBaseUnits") <> 0;

-- V2 per (location, product): QOH = SUM(BatchLocationBalances).
SELECT 'V2' AS chk, s."StockLocationId", s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "CompanyId", "StockLocationId", "ProductId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2, 3) b
  ON b."CompanyId" = s."CompanyId" AND b."StockLocationId" = s."StockLocationId" AND b."ProductId" = s."ProductId"
WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);

-- V3 per batch: SUM(balances over locations) = PurchaseBatches.RemainingBaseQty (batch totals do not change when a batch moves).
SELECT 'V3' AS chk, pb."Id" FROM "PurchaseBatches" pb
LEFT JOIN (SELECT "CompanyId", "PurchaseBatchId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2) b
  ON b."CompanyId" = pb."CompanyId" AND b."PurchaseBatchId" = pb."Id"
WHERE pb."RemainingBaseQty" <> COALESCE(b.q, 0);

-- V4 Transit holds exactly the in-transit quantity, per batch.
-- Lines tagged "-- [P06: <text>]" exist only from AddReceiptApprovalAndDispositions on (AcceptedCorrection / ConsignmentLines): the test harness runs this script
-- against older schemas by replacing each tagged line with <text>. A plain psql run (the supported use) just treats the tag as a comment.
SELECT 'V4' AS chk, COALESCE(t."PurchaseBatchId", o."PurchaseBatchId") FROM
 (SELECT bl."CompanyId", bl."PurchaseBatchId", bl."RemainingBaseQty" q FROM "BatchLocationBalances" bl
    JOIN "StockLocations" l ON l."CompanyId" = bl."CompanyId" AND l."Id" = bl."StockLocationId" AND l."LocationType" = 'Transit') t
 FULL JOIN (SELECT "CompanyId", "PurchaseBatchId", SUM("DispatchedBaseQty" - "ReceivedBaseQty" - "DamagedBaseQty" - "WrittenOffBaseQty" - "ReturnedBaseQty"
      - "AcceptedCorrectionBaseQty" -- [P06: - 0]
      ) q
    FROM "StockTransferLineBatches" GROUP BY 1, 2) o USING ("PurchaseBatchId")
 WHERE COALESCE(t.q, 0) <> COALESCE(o.q, 0);

-- V5 line counters = SUM(LineBatches) = SUM(receipt lines), per line and counter.
-- AcceptedCorrection (FI-D40, accepted to destination by a disposition) exists on line and batch rows only: it has no receipt-line side.
-- The receipt-line comparison applies to LEGACY lines (direct dispatch and receive: StockTransferReceiptLines carry every counter). A SHIPMENT-flow line
-- (referenced by a ConsignmentLine) is settled by consignment receipts and dispositions, which write no StockTransferReceiptLines, so for it only the
-- line-vs-batches comparison applies (its consignment-side identities are in transit-reconciliation.sql).
SELECT 'V5' AS chk, l."Id" FROM "StockTransferLines" l
LEFT JOIN (SELECT "CompanyId", "LineId", SUM("DispatchedBaseQty") d, SUM("ReceivedBaseQty") r, SUM("DamagedBaseQty") m, SUM("WrittenOffBaseQty") w, SUM("ReturnedBaseQty") t
           , SUM("AcceptedCorrectionBaseQty") a -- [P06: , 0 a]
           FROM "StockTransferLineBatches" GROUP BY 1, 2) b ON b."CompanyId" = l."CompanyId" AND b."LineId" = l."Id"
LEFT JOIN (SELECT "CompanyId", "TransferLineId", SUM("AcceptedBaseQty") r, SUM("DamagedBaseQty") m, SUM("WrittenOffBaseQty") w, SUM("ReturnedBaseQty") t
           FROM "StockTransferReceiptLines" GROUP BY 1, 2) rl ON rl."CompanyId" = l."CompanyId" AND rl."TransferLineId" = l."Id"
WHERE l."DispatchedBaseQty" <> COALESCE(b.d, 0) OR l."ReceivedBaseQty" <> COALESCE(b.r, 0) OR l."DamagedBaseQty" <> COALESCE(b.m, 0)
   OR l."WrittenOffBaseQty" <> COALESCE(b.w, 0) OR l."ReturnedBaseQty" <> COALESCE(b.t, 0)
   OR l."AcceptedCorrectionBaseQty" <> COALESCE(b.a, 0) -- [P06: OR FALSE]
   OR (TRUE
       AND NOT EXISTS (SELECT 1 FROM "ConsignmentLines" cl WHERE cl."CompanyId" = l."CompanyId" AND cl."StockTransferLineId" = l."Id") -- [P06: AND TRUE]
       AND (l."ReceivedBaseQty" <> COALESCE(rl.r, 0) OR l."DamagedBaseQty" <> COALESCE(rl.m, 0)
         OR l."WrittenOffBaseQty" <> COALESCE(rl.w, 0) OR l."ReturnedBaseQty" <> COALESCE(rl.t, 0)));

-- V6a Completed => outstanding = 0 on every line.
SELECT 'V6a' AS chk, t."Id" FROM "StockTransfers" t JOIN "StockTransferLines" l ON l."CompanyId" = t."CompanyId" AND l."TransferId" = t."Id"
WHERE t."Status" = 'Completed'
  AND l."DispatchedBaseQty" - l."ReceivedBaseQty" - l."DamagedBaseQty" - l."WrittenOffBaseQty" - l."ReturnedBaseQty"
      - l."AcceptedCorrectionBaseQty" -- [P06: - 0]
      <> 0;
-- V6b Status outside {Dispatched, PartiallyReceived, Completed} => no LineBatches and no movements. Shipment-flow transfers (Phase 03-05) carry batches and movements
-- from their first dispatch on: PartiallyFulfilled / Fulfilled, and Closed (a request closed after a partial dispatch) are dispatched states too.
SELECT 'V6b' AS chk, t."Id" FROM "StockTransfers" t
WHERE t."Status" NOT IN ('Dispatched', 'PartiallyReceived', 'Completed', 'PartiallyFulfilled', 'Fulfilled', 'Closed')
  AND (EXISTS (SELECT 1 FROM "StockTransferLines" l JOIN "StockTransferLineBatches" b ON b."CompanyId" = l."CompanyId" AND b."LineId" = l."Id"
               WHERE l."CompanyId" = t."CompanyId" AND l."TransferId" = t."Id")
    OR EXISTS (SELECT 1 FROM "StockMovements" m WHERE m."CompanyId" = t."CompanyId" AND m."ReferenceType" = 'StockTransfer' AND m."ReferenceId" = t."Id"));

-- V7 per transfer: SUM('StockTransfer' movements) = -(SUM Damaged + WrittenOff) base (moves net to zero; write-offs leave the company).
SELECT 'V7' AS chk, t."Id" FROM "StockTransfers" t
LEFT JOIN (SELECT "CompanyId", "ReferenceId", SUM("QuantityBaseUnits") q FROM "StockMovements" WHERE "ReferenceType" = 'StockTransfer' GROUP BY 1, 2) m
  ON m."CompanyId" = t."CompanyId" AND m."ReferenceId" = t."Id"
LEFT JOIN (SELECT "CompanyId", "TransferId", SUM("DamagedBaseQty" + "WrittenOffBaseQty") q FROM "StockTransferLines" GROUP BY 1, 2) l
  ON l."CompanyId" = t."CompanyId" AND l."TransferId" = t."Id"
WHERE COALESCE(m.q, 0) <> -COALESCE(l.q, 0);

-- V8 no 'StockTransfer' movement at a location other than that transfer's source, destination or Transit.
SELECT 'V8' AS chk, m."Id" FROM "StockMovements" m
JOIN "StockTransfers" t ON t."CompanyId" = m."CompanyId" AND t."Id" = m."ReferenceId"
WHERE m."ReferenceType" = 'StockTransfer' AND m."StockLocationId" NOT IN (t."SourceLocationId", t."DestinationLocationId", t."TransitLocationId");
