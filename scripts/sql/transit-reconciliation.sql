-- FI-D34..D37 transit reconciliation. Every query must return no rows. Read-only; safe on production.
-- Assumptions: Transit stock is the company Transit location (StockLocations.LocationType = 'Transit'); quantities are base units per product; sums are per product, never across products.
-- A consumed reservation's BaseQty is the quantity actually dispatched (the unloaded remainder is released and not part of it).

-- 1. Consumed reservations = dispatched per consignment line.
SELECT l."Id", l."DispatchedBaseQty", COALESCE(r.q, 0) AS "ConsumedReservations"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT "ConsignmentLineId", SUM("BaseQty") q FROM "StockReservations" WHERE "Status" = 'Consumed' GROUP BY 1) r ON r."ConsignmentLineId" = l."Id"
WHERE l."DispatchedBaseQty" <> COALESCE(r.q, 0);

-- 2. Receipt lines sum to the consignment line's cumulative counters.
SELECT l."Id", l."AcceptedBaseQty", l."DamagedBaseQty", l."RejectedBaseQty", l."MissingBaseQty"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT "ConsignmentLineId", SUM("AcceptedBaseQty") a, SUM("DamagedBaseQty") d, SUM("RejectedBaseQty") j, SUM("MissingBaseQty") m FROM "ConsignmentReceiptLines" GROUP BY 1) x ON x."ConsignmentLineId" = l."Id"
WHERE (l."AcceptedBaseQty", l."DamagedBaseQty", l."RejectedBaseQty", l."MissingBaseQty") IS DISTINCT FROM (COALESCE(x.a, 0), COALESCE(x.d, 0), COALESCE(x.j, 0), COALESCE(x.m, 0));

-- 3. PostedBaseQty = accepted quantity of Posted receipts.
SELECT l."Id", l."PostedBaseQty", COALESCE(p.q, 0) AS "AcceptedInPostedReceipts"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT rl."ConsignmentLineId", SUM(rl."AcceptedBaseQty") q FROM "ConsignmentReceiptLines" rl JOIN "ConsignmentReceipts" r ON r."Id" = rl."ReceiptId" AND r."Status" = 'Posted' GROUP BY 1) p ON p."ConsignmentLineId" = l."Id"
WHERE l."PostedBaseQty" <> COALESCE(p.q, 0);

-- 4. Transit on-hand per product = SUM over every dispatched batch row (legacy direct dispatch AND shipment-dispatched) of
--    Dispatched - Received - Damaged - WrittenOff - Returned (same per-batch identity as stock-transfers-validation.sql V4, rolled up per product).
--    For shipment lines this equals SUM(ConsignmentLines.DispatchedBaseQty - PostedBaseQty) - written off - returned. ASSUMPTION: in Phase 05 shipment receiving keeps
--    damaged/rejected/missing quantity IN Transit, so the service must NOT add it to StockTransferLineBatches.DamagedBaseQty (legacy semantics: damaged leaves Transit);
--    it lives only in ConsignmentLines/ConsignmentReceiptLines counters until Phase 06 dispositions it.
WITH expected AS (
    SELECT l."CompanyId", l."ProductId", SUM(b."DispatchedBaseQty" - b."ReceivedBaseQty" - b."DamagedBaseQty" - b."WrittenOffBaseQty" - b."ReturnedBaseQty") q
    FROM "StockTransferLineBatches" b
    JOIN "StockTransferLines" l ON l."CompanyId" = b."CompanyId" AND l."Id" = b."LineId"
    GROUP BY l."CompanyId", l."ProductId"
)
SELECT s."CompanyId", s."ProductId", s."QuantityOnHand" AS "TransitOnHand", COALESCE(e.q, 0) AS "OpenTransitFromBatches"
FROM "StockItems" s
JOIN "StockLocations" loc ON loc."CompanyId" = s."CompanyId" AND loc."Id" = s."StockLocationId" AND loc."LocationType" = 'Transit'
LEFT JOIN expected e ON e."CompanyId" = s."CompanyId" AND e."ProductId" = s."ProductId"
WHERE s."QuantityOnHand" <> COALESCE(e.q, 0)
UNION ALL
SELECT e."CompanyId", e."ProductId", 0, e.q
FROM expected e
WHERE e.q <> 0 AND NOT EXISTS (SELECT 1 FROM "StockItems" s JOIN "StockLocations" loc ON loc."CompanyId" = s."CompanyId" AND loc."Id" = s."StockLocationId" AND loc."LocationType" = 'Transit'
                               WHERE s."CompanyId" = e."CompanyId" AND s."ProductId" = e."ProductId");

-- 5. Shipment-dispatched batch rows reconcile to their consignment line: sum of batch Dispatched = line DispatchedBaseQty.
SELECT l."Id", l."DispatchedBaseQty", COALESCE(b.q, 0) AS "BatchDispatched"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT "ConsignmentLineId", SUM("DispatchedBaseQty") q FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NOT NULL GROUP BY 1) b ON b."ConsignmentLineId" = l."Id"
WHERE l."DispatchedBaseQty" <> COALESCE(b.q, 0);

-- 6. Shipment-dispatched batches: received on the batch rows = PostedBaseQty of the consignment line.
SELECT l."Id", l."PostedBaseQty", COALESCE(b.q, 0) AS "BatchReceived"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT "ConsignmentLineId", SUM("ReceivedBaseQty") q FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NOT NULL GROUP BY 1) b ON b."ConsignmentLineId" = l."Id"
WHERE l."PostedBaseQty" <> COALESCE(b.q, 0);
