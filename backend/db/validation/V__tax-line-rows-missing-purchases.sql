-- sellora:description Purchase and purchase-return lines without a tax row (TP-3 and later: the purchase write paths always write rows)
-- sellora:on-violation block
-- A line saved by the TP-2 build between the TP-1 commit and the TP-3 swap has no row; AddPurchaseTaxSnapshot re-runs the frozen, idempotent
-- Data/Migrations/Sql/*_AddTaxProfiles.legacy-rows.sql, and so can you (remedy). Sales lines stay a warning in V__tax-line-rows-missing.sql until TP-4. Output: table and line id only.
SELECT 'PurchaseOrderLines' AS tbl, l."Id" AS "LineId" FROM "PurchaseOrderLines" l WHERE NOT EXISTS (SELECT 1 FROM "PurchaseOrderLineTaxes" x WHERE x."PurchaseOrderLineId" = l."Id")
UNION ALL
SELECT 'PurchaseReturnLines', l."Id" FROM "PurchaseReturnLines" l WHERE NOT EXISTS (SELECT 1 FROM "PurchaseReturnLineTaxes" x WHERE x."PurchaseReturnLineId" = l."Id");
