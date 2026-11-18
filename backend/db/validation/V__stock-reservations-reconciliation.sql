-- sellora:description Stock reservations reconciliation
-- Applied by Sellora.DbMigrator on every deploy (docs/features/db-release-automation.md); can still be run by hand with psql.
-- FI-D5 reconciliation: StockItems.ReservedQuantity must equal the sum of Active StockReservations per (location, product), and
-- 0 <= ReservedQuantity <= QuantityOnHand. Every query must return no rows. Read-only; safe on production.

-- 1. Reserved column vs Active reservations (both directions: stock rows with a wrong sum, and Active rows with no matching stock row).
SELECT s."CompanyId", s."StockLocationId", s."ProductId", s."ReservedQuantity", COALESCE(r.q, 0) AS "ActiveReservations"
FROM "StockItems" s
LEFT JOIN (SELECT "SourceLocationId", "ProductId", SUM("BaseQty") q FROM "StockReservations" WHERE "Status" = 'Active' GROUP BY 1, 2) r
       ON r."SourceLocationId" = s."StockLocationId" AND r."ProductId" = s."ProductId"
WHERE s."ReservedQuantity" <> COALESCE(r.q, 0);

-- 2. Bounds (also enforced by CK_StockItems_Reserved_Valid).
SELECT "CompanyId", "StockLocationId", "ProductId", "QuantityOnHand", "ReservedQuantity"
FROM "StockItems" WHERE "ReservedQuantity" < 0 OR "ReservedQuantity" > "QuantityOnHand";

-- 3. An Active reservation must belong to an Active line of a non-cancelled consignment, and match its allocation.
SELECT r."Id", r."BaseQty", l."AllocatedBaseQty", l."Status" AS "LineStatus", c."Status" AS "ConsignmentStatus"
FROM "StockReservations" r
JOIN "ConsignmentLines" l ON l."CompanyId" = r."CompanyId" AND l."Id" = r."ConsignmentLineId"
JOIN "Consignments" c ON c."CompanyId" = l."CompanyId" AND c."Id" = l."ConsignmentId"
WHERE r."Status" = 'Active' AND (l."Status" <> 'Active' OR c."Status" = 'Cancelled' OR r."BaseQty" <> l."AllocatedBaseQty" OR r."ProductId" <> l."ProductId");

-- 4. Transfer-line allocation = sum of Active consignment-line allocations (not yet dispatched part is Phase 05; until then they are equal).
SELECT tl."Id", tl."AllocatedBaseQty", COALESCE(a.q, 0) AS "ActiveConsignmentAllocations"
FROM "StockTransferLines" tl
LEFT JOIN (SELECT "StockTransferLineId", SUM("AllocatedBaseQty") q FROM "ConsignmentLines" WHERE "Status" = 'Active' GROUP BY 1) a ON a."StockTransferLineId" = tl."Id"
WHERE tl."AllocatedBaseQty" <> COALESCE(a.q, 0);
