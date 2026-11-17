-- FI-D34..D41 transit reconciliation. Every query must return no rows. Read-only; safe on production.
-- Assumptions: Transit stock is the company Transit location (StockLocations.LocationType = 'Transit'); quantities are base units per product; sums are per product, never across products.
-- A consumed reservation's BaseQty is the quantity actually dispatched (the unloaded remainder is released and not part of it).

-- 1. Consumed reservations = dispatched per consignment line.
SELECT l."Id", l."DispatchedBaseQty", COALESCE(r.q, 0) AS "ConsumedReservations"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT "ConsignmentLineId", SUM("BaseQty") q FROM "StockReservations" WHERE "Status" = 'Consumed' GROUP BY 1) r ON r."ConsignmentLineId" = l."Id"
WHERE l."DispatchedBaseQty" <> COALESCE(r.q, 0);

-- 2. Receipt lines sum to the consignment line's cumulative counters.
--    FI-D39: a RejectedForCorrection receipt keeps its lines (audit) but its quantities were subtracted from the counters, so it is excluded.
SELECT l."Id", l."AcceptedBaseQty", l."DamagedBaseQty", l."RejectedBaseQty", l."MissingBaseQty"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT rl."ConsignmentLineId", SUM(rl."AcceptedBaseQty") a, SUM(rl."DamagedBaseQty") d, SUM(rl."RejectedBaseQty") j, SUM(rl."MissingBaseQty") m
           FROM "ConsignmentReceiptLines" rl JOIN "ConsignmentReceipts" r ON r."Id" = rl."ReceiptId" AND r."Status" <> 'RejectedForCorrection'
           GROUP BY 1) x ON x."ConsignmentLineId" = l."Id"
WHERE (l."AcceptedBaseQty", l."DamagedBaseQty", l."RejectedBaseQty", l."MissingBaseQty") IS DISTINCT FROM (COALESCE(x.a, 0), COALESCE(x.d, 0), COALESCE(x.j, 0), COALESCE(x.m, 0));

-- 3. PostedBaseQty = accepted quantity of Posted receipts.
SELECT l."Id", l."PostedBaseQty", COALESCE(p.q, 0) AS "AcceptedInPostedReceipts"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT rl."ConsignmentLineId", SUM(rl."AcceptedBaseQty") q FROM "ConsignmentReceiptLines" rl JOIN "ConsignmentReceipts" r ON r."Id" = rl."ReceiptId" AND r."Status" = 'Posted' GROUP BY 1) p ON p."ConsignmentLineId" = l."Id"
WHERE l."PostedBaseQty" <> COALESCE(p.q, 0);

-- 4. Transit on-hand per product = SUM over every dispatched batch row (legacy direct dispatch AND shipment-dispatched) of
--    Dispatched - Received - Damaged - WrittenOff - Returned (same per-batch identity as stock-transfers-validation.sql V4, rolled up per product).
--    FI-D40/D41: for shipment batch rows Transit = Dispatched - Received (posted accepted) - WrittenOff - Returned - AcceptedCorrection (accepted to destination by a disposition).
--    Shipment receiving keeps damaged/rejected/missing quantity IN Transit, so the service must NOT add it to StockTransferLineBatches.DamagedBaseQty (legacy semantics: damaged leaves Transit);
--    it stays in the ConsignmentLines/ConsignmentReceiptLines counters until a disposition moves it into WrittenOff / Returned / AcceptedCorrection.
WITH expected AS (
    SELECT l."CompanyId", l."ProductId", SUM(b."DispatchedBaseQty" - b."ReceivedBaseQty" - b."DamagedBaseQty" - b."WrittenOffBaseQty" - b."ReturnedBaseQty" - b."AcceptedCorrectionBaseQty") q
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

-- 7. Disposition lines sum, per consignment line and bucket, to the line's Dispositioned* counters.
SELECT l."Id", l."DispositionedDamagedBaseQty", l."DispositionedRejectedBaseQty", l."DispositionedMissingBaseQty"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT "ConsignmentLineId",
                  SUM("BaseQty") FILTER (WHERE "Bucket" = 'Damaged') d, SUM("BaseQty") FILTER (WHERE "Bucket" = 'Rejected') j, SUM("BaseQty") FILTER (WHERE "Bucket" = 'Missing') m
           FROM "ConsignmentDispositionLines" GROUP BY 1) x ON x."ConsignmentLineId" = l."Id"
WHERE (l."DispositionedDamagedBaseQty", l."DispositionedRejectedBaseQty", l."DispositionedMissingBaseQty") IS DISTINCT FROM (COALESCE(x.d, 0), COALESCE(x.j, 0), COALESCE(x.m, 0));

-- 8. Per-action totals on the shipment batch rows = disposition lines per action:
--    WriteOff -> WrittenOffBaseQty, ReturnToSource -> ReturnedBaseQty, AcceptToDestination -> AcceptedCorrectionBaseQty (shipment rows only; legacy rows carry their own resolve history).
SELECT l."Id", COALESCE(b.w, 0) AS "BatchWrittenOff", COALESCE(b.r, 0) AS "BatchReturned", COALESCE(b.a, 0) AS "BatchAcceptedCorrection"
FROM "ConsignmentLines" l
LEFT JOIN (SELECT "ConsignmentLineId", SUM("WrittenOffBaseQty") w, SUM("ReturnedBaseQty") r, SUM("AcceptedCorrectionBaseQty") a
           FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NOT NULL GROUP BY 1) b ON b."ConsignmentLineId" = l."Id"
LEFT JOIN (SELECT "ConsignmentLineId",
                  SUM("BaseQty") FILTER (WHERE "Action" = 'WriteOff') w, SUM("BaseQty") FILTER (WHERE "Action" = 'ReturnToSource') r, SUM("BaseQty") FILTER (WHERE "Action" = 'AcceptToDestination') a
           FROM "ConsignmentDispositionLines" GROUP BY 1) x ON x."ConsignmentLineId" = l."Id"
WHERE (COALESCE(b.w, 0), COALESCE(b.r, 0), COALESCE(b.a, 0)) IS DISTINCT FROM (COALESCE(x.w, 0), COALESCE(x.r, 0), COALESCE(x.a, 0));

-- 9. Completion states (FI-D41): Reconciled consignment = every dispatched unit accounted and posted, nothing left to disposition; Completed shipment = all non-cancelled consignments Reconciled.
SELECT c."Id", c."Status"
FROM "Consignments" c
WHERE c."Status" = 'Reconciled'
  AND EXISTS (SELECT 1 FROM "ConsignmentLines" l WHERE l."ConsignmentId" = c."Id" AND l."Status" = 'Active'
              AND (l."AcceptedBaseQty" + l."DamagedBaseQty" + l."RejectedBaseQty" + l."MissingBaseQty" <> l."DispatchedBaseQty" OR l."PostedBaseQty" <> l."AcceptedBaseQty"
                   OR l."DispositionedDamagedBaseQty" <> l."DamagedBaseQty" OR l."DispositionedRejectedBaseQty" <> l."RejectedBaseQty" OR l."DispositionedMissingBaseQty" <> l."MissingBaseQty"))
UNION ALL
SELECT s."Id", s."Status"
FROM "Shipments" s
WHERE s."Status" = 'Completed'
  AND EXISTS (SELECT 1 FROM "Consignments" c WHERE c."ShipmentId" = s."Id" AND c."Status" NOT IN ('Cancelled', 'Reconciled'));
