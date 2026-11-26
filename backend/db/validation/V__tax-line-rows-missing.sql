-- sellora:description Sales and sales-return lines without a tax row (TP-4 and later: the sales write paths always write rows)
-- sellora:on-violation block
-- Block since TP-4 (was warn). A line saved by the TP-3 build between the TP-1 commit and the TP-4 swap has no row; AddSalesTaxSnapshot re-runs the frozen, idempotent
-- Data/Migrations/Sql/*_AddTaxProfiles.legacy-rows.sql, and so can you (remedy). Purchase and purchase-return lines: V__tax-line-rows-missing-purchases.sql. Output: table and line id only.
SELECT 'SalesInvoiceLines' AS tbl, l."Id" AS "LineId" FROM "SalesInvoiceLines" l WHERE NOT EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = l."Id")
UNION ALL
SELECT 'SalesReturnLines', l."Id" FROM "SalesReturnLines" l WHERE NOT EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" x WHERE x."SalesReturnLineId" = l."Id");
