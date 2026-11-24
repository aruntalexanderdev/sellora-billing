-- sellora:description Line tax rows add up to the line's TaxAmount, and a legacy row's base equals the line's TaxableValue
-- sellora:on-violation block
-- AC16/BR23 (docs/features/tax-profiles.md). Lines without rows are reported by V__tax-line-rows-missing.sql. Output: table and line id only.
SELECT 'SalesInvoiceLines' AS tbl, l."Id" AS "LineId" FROM "SalesInvoiceLines" l
JOIN (SELECT "SalesInvoiceLineId" AS line_id, SUM("TaxAmount") AS tax, SUM("BaseAmount") FILTER (WHERE "TaxComponentId" IS NULL) AS legacy_base FROM "SalesInvoiceLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE x.tax <> l."TaxAmount" OR (x.legacy_base IS NOT NULL AND x.legacy_base <> l."TaxableValue")
UNION ALL
SELECT 'PurchaseOrderLines', l."Id" FROM "PurchaseOrderLines" l
JOIN (SELECT "PurchaseOrderLineId" AS line_id, SUM("TaxAmount") AS tax, SUM("BaseAmount") FILTER (WHERE "TaxComponentId" IS NULL) AS legacy_base FROM "PurchaseOrderLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE x.tax <> l."TaxAmount" OR (x.legacy_base IS NOT NULL AND x.legacy_base <> l."TaxableValue")
UNION ALL
SELECT 'SalesReturnLines', l."Id" FROM "SalesReturnLines" l
JOIN (SELECT "SalesReturnLineId" AS line_id, SUM("TaxAmount") AS tax, SUM("BaseAmount") FILTER (WHERE "TaxComponentId" IS NULL) AS legacy_base FROM "SalesReturnLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE x.tax <> l."TaxAmount" OR (x.legacy_base IS NOT NULL AND x.legacy_base <> l."TaxableValue")
UNION ALL
SELECT 'PurchaseReturnLines', l."Id" FROM "PurchaseReturnLines" l
JOIN (SELECT "PurchaseReturnLineId" AS line_id, SUM("TaxAmount") AS tax, SUM("BaseAmount") FILTER (WHERE "TaxComponentId" IS NULL) AS legacy_base FROM "PurchaseReturnLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE x.tax <> l."TaxAmount" OR (x.legacy_base IS NOT NULL AND x.legacy_base <> l."TaxableValue");
