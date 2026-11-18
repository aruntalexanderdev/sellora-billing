-- sellora:description Location-aware returns and van operations R1-R5
-- Applied by Sellora.DbMigrator on every deploy (docs/features/db-release-automation.md); can still be run by hand with psql.
-- Phase 10 location-aware sales/purchase returns and van operations: validation (R1-R5). Read-only; EVERY query must return no rows.
-- Run in the release AFTER the Phase 8b + 9 validation blocks (alongside backend/db/validation/V__stock-transfers-validation.sql V1-V8),
-- after any return flow in tests/smoke, and before the release sign-off:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f backend/db/validation/V__van-operations-validation.sql
-- No schema change in Phase 10: SalesReturns / PurchaseReturns."StockLocationId" (Phase 8) holds the restock / issuing location.

-- R1 every SaleReturn movement sits at its SalesReturns header location (and the header exists).
SELECT 'R1' AS chk, m."CompanyId", m."Id" AS "MovementId", m."ReferenceId" AS "SalesReturnId", m."ReferenceNo", m."StockLocationId" AS "MovementLocationId", r."StockLocationId" AS "HeaderLocationId"
FROM "StockMovements" m
LEFT JOIN "SalesReturns" r ON r."CompanyId" = m."CompanyId" AND r."Id" = m."ReferenceId"
WHERE m."Type" = 'SaleReturn'
  AND (r."Id" IS NULL OR m."StockLocationId" <> r."StockLocationId");

-- R2 every PurchaseReturn movement sits at its PurchaseReturns header location (and the header exists).
SELECT 'R2' AS chk, m."CompanyId", m."Id" AS "MovementId", m."ReferenceId" AS "PurchaseReturnId", m."ReferenceNo", m."StockLocationId" AS "MovementLocationId", r."StockLocationId" AS "HeaderLocationId"
FROM "StockMovements" m
LEFT JOIN "PurchaseReturns" r ON r."CompanyId" = m."CompanyId" AND r."Id" = m."ReferenceId"
WHERE m."Type" = 'PurchaseReturn'
  AND (r."Id" IS NULL OR m."StockLocationId" <> r."StockLocationId");

-- R3 a sales return restocked away from its invoice's location only when the invoice location is a Vehicle.
SELECT 'R3' AS chk, r."CompanyId", r."Id" AS "SalesReturnId", r."ReturnNo", i."Id" AS "SalesInvoiceId", i."InvoiceNo",
       r."StockLocationId" AS "ReturnLocationId", i."StockLocationId" AS "InvoiceLocationId", il."LocationType" AS "InvoiceLocationType"
FROM "SalesReturns" r
JOIN "SalesInvoices" i ON i."CompanyId" = r."CompanyId" AND i."Id" = r."SalesInvoiceId"
LEFT JOIN "StockLocations" il ON il."CompanyId" = i."CompanyId" AND il."Id" = i."StockLocationId"
WHERE r."StockLocationId" <> i."StockLocationId"
  AND COALESCE(il."LocationType", '') <> 'Vehicle';

-- R4 no SaleReturn / PurchaseReturn movement at a Transit location.
SELECT 'R4' AS chk, m."CompanyId", m."Id" AS "MovementId", m."Type", m."ReferenceId", m."ReferenceNo", m."StockLocationId"
FROM "StockMovements" m
JOIN "StockLocations" l ON l."CompanyId" = m."CompanyId" AND l."Id" = m."StockLocationId"
WHERE m."Type" IN ('SaleReturn', 'PurchaseReturn')
  AND l."LocationType" = 'Transit';

-- R5 no batch consumption with ReturnedQtyBaseUnits > QtyBaseUnits (consumptions are tenant-scoped via line -> invoice).
SELECT 'R5' AS chk, i."CompanyId", c."Id" AS "ConsumptionId", i."Id" AS "SalesInvoiceId", i."InvoiceNo", c."SalesInvoiceLineId", c."PurchaseBatchId",
       c."QtyBaseUnits", c."ReturnedQtyBaseUnits"
FROM "SalesInvoiceLineBatchConsumptions" c
JOIN "SalesInvoiceLines" l ON l."Id" = c."SalesInvoiceLineId"
JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId"
WHERE c."ReturnedQtyBaseUnits" > c."QtyBaseUnits";
