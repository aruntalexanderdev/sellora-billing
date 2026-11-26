-- sellora:description Sales lines without a tax row (warn until TP-4; lines saved by the previous build between the M1 commit and the app swap show here until .legacy-rows.sql is re-run)
-- sellora:on-violation warn
-- Becomes block from TP-4 (design table). Purchase and purchase-return lines are blocked by V__tax-line-rows-missing-purchases.sql since TP-3.
-- Re-run the frozen Data/Migrations/Sql/*_AddTaxProfiles.legacy-rows.sql (idempotent) to catch up. Output: table and line id only.
SELECT 'SalesInvoiceLines' AS tbl, l."Id" AS "LineId" FROM "SalesInvoiceLines" l WHERE NOT EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = l."Id")
UNION ALL
SELECT 'SalesReturnLines', l."Id" FROM "SalesReturnLines" l WHERE NOT EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" x WHERE x."SalesReturnLineId" = l."Id");
