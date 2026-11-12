-- Unit restructuring: PRE-RELEASE PROFILE (analysis 22.1). Read-only; INFORMATIVE counts, not zero-row checks.
-- Run on a production COPY at the OLD schema (origin/main @ 20260920114635_AddIdempotencyKeysAndCancelledBy), BEFORE applying any
-- migration of this release. Tables used: "ProductSellingModes", "Units", "Products", "PurchaseOrderLines", "SalesInvoiceLines",
-- "SellingPrices", "BusinessSettings" (old names; they do not exist after ExtendUnitMaster / IntroduceProductUnits).
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/sql/unit-restructuring-profile.sql
-- Output: counts and ids only (no names, no PII). Rows flagged "REVIEW" must be understood and resolved (or accepted in the release
-- report) before the migration; the migrations' own guards (P-blocks) abort on the hard cases (factor <= 0, mode of another product,
-- Qty x factor <> BaseQty, discount outside 0..gross, more than one BusinessSettings row, case-insensitive duplicate units).
-- Note: old ConversionFactorToBase is numeric(18,3), so "more than 3 dp" can only be 0 here; the check stays as a tripwire for copies
-- that were altered, and for the Down() path.

-- P1 modes per product: histogram (how many products have N modes) + overall totals.
SELECT 'P1' AS chk, n_modes, COUNT(*) AS products
FROM (SELECT p."Id", COUNT(m."Id") AS n_modes FROM "Products" p LEFT JOIN "ProductSellingModes" m ON m."ProductId" = p."Id" GROUP BY p."Id") x
GROUP BY n_modes ORDER BY n_modes;
SELECT 'P1-total' AS chk, (SELECT COUNT(*) FROM "Products") AS products, (SELECT COUNT(*) FROM "ProductSellingModes") AS modes,
       (SELECT COUNT(*) FROM "ProductSellingModes" WHERE "IsActive") AS active_modes;

-- P2 products with NO factor-1 mode (IntroduceProductUnits inserts a new base row with a new Id) -- REVIEW count; ids listed.
SELECT 'P2' AS chk, p."Id" AS product_id
FROM "Products" p
WHERE NOT EXISTS (SELECT 1 FROM "ProductSellingModes" m WHERE m."ProductId" = p."Id" AND m."ConversionFactorToBase" = 1);

-- P3 products with MORE THAN ONE factor-1 mode (first becomes the base, the rest become Packaging rows flagged for review) -- REVIEW.
SELECT 'P3' AS chk, m."ProductId" AS product_id, COUNT(*) AS factor1_modes
FROM "ProductSellingModes" m WHERE m."ConversionFactorToBase" = 1 GROUP BY m."ProductId" HAVING COUNT(*) > 1;

-- P4 distinct labels: how many distinct labels (case-insensitive), and how many of them have NO case-insensitive match on
-- Units.Name / Units.ShortCode (those become new Packaging units named from the label).
SELECT 'P4' AS chk, COUNT(*) AS distinct_labels,
       COUNT(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM "Units" u WHERE lower(u."Name") = l.lbl OR lower(u."ShortCode") = l.lbl)) AS labels_without_unit_match,
       COUNT(*) FILTER (WHERE l.lbl <> btrim(l.lbl) OR l.lbl = '') AS labels_blank_or_untrimmed
FROM (SELECT DISTINCT lower("Label") AS lbl FROM "ProductSellingModes") l;

-- P4b labels without a unit match, with usage counts (the reviewed mapping input, analysis 22.2). Label text is catalogue data, not PII.
SELECT 'P4b' AS chk, lower(m."Label") AS label_lc, COUNT(*) AS modes, COUNT(DISTINCT m."ProductId") AS products
FROM "ProductSellingModes" m
WHERE NOT EXISTS (SELECT 1 FROM "Units" u WHERE lower(u."Name") = lower(m."Label") OR lower(u."ShortCode") = lower(m."Label"))
GROUP BY lower(m."Label") ORDER BY modes DESC, label_lc;

-- P5 factors with more than 3 decimals, <= 0, or below 1 (below 1 = fraction of the base, e.g. 0.2 kg box -> Packaging InnerQuantity 0.2).
SELECT 'P5' AS chk,
       COUNT(*) FILTER (WHERE "ConversionFactorToBase" <> round("ConversionFactorToBase", 3)) AS more_than_3dp,
       COUNT(*) FILTER (WHERE "ConversionFactorToBase" <= 0) AS non_positive,
       COUNT(*) FILTER (WHERE "ConversionFactorToBase" > 0 AND "ConversionFactorToBase" < 1) AS below_one,
       COUNT(*) FILTER (WHERE "ConversionFactorToBase" > 1 AND "ConversionFactorToBase" <> trunc("ConversionFactorToBase")) AS fractional_above_one
FROM "ProductSellingModes";

-- P6 modes referenced by history: purchase lines, sales lines, selling prices (referenced rows keep their Id and become immutable).
SELECT 'P6' AS chk,
       (SELECT COUNT(*) FROM "ProductSellingModes" m WHERE EXISTS (SELECT 1 FROM "PurchaseOrderLines" l WHERE l."SellingModeId" = m."Id")) AS modes_in_purchase_lines,
       (SELECT COUNT(*) FROM "ProductSellingModes" m WHERE EXISTS (SELECT 1 FROM "SalesInvoiceLines" l WHERE l."SellingModeId" = m."Id")) AS modes_in_sales_lines,
       (SELECT COUNT(*) FROM "ProductSellingModes" m WHERE EXISTS (SELECT 1 FROM "SellingPrices" s WHERE s."ProductSellingModeId" = m."Id")) AS modes_with_prices,
       (SELECT COUNT(*) FROM "ProductSellingModes" m
         WHERE EXISTS (SELECT 1 FROM "PurchaseOrderLines" l WHERE l."SellingModeId" = m."Id")
            OR EXISTS (SELECT 1 FROM "SalesInvoiceLines" l WHERE l."SellingModeId" = m."Id")
            OR EXISTS (SELECT 1 FROM "SellingPrices" s WHERE s."ProductSellingModeId" = m."Id")) AS modes_referenced_any,
       (SELECT COUNT(*) FROM "ProductSellingModes") AS modes_total;

-- P7 row counts that the validation must see unchanged afterwards (SellingPrices count before = after, analysis 22.7). RECORD THESE.
SELECT 'P7' AS chk, (SELECT COUNT(*) FROM "SellingPrices") AS selling_prices, (SELECT COUNT(*) FROM "ProductSellingModes") AS selling_modes,
       (SELECT COUNT(*) FROM "PurchaseOrderLines") AS purchase_lines, (SELECT COUNT(*) FROM "PurchaseReturnLines") AS purchase_return_lines,
       (SELECT COUNT(*) FROM "SalesInvoiceLines") AS sales_lines, (SELECT COUNT(*) FROM "SalesReturnLines") AS sales_return_lines,
       (SELECT COUNT(*) FROM "StockAdjustments") AS adjustments, (SELECT COUNT(*) FROM "StockMovements") AS movements,
       (SELECT COUNT(*) FROM "StockItems") AS stock_items, (SELECT COUNT(*) FROM "PurchaseBatches") AS batches;

-- P8 BusinessSettings row count: AddCompaniesAndMemberships aborts when > 1 (one company per BusinessSettings row is not supported).
SELECT 'P8' AS chk, COUNT(*) AS business_settings_rows FROM "BusinessSettings";

-- P9 data the migration guards reject (each must be 0; ids listed so they can be fixed on the copy first).
-- P9a mode of a purchase/sales line belongs to ANOTHER product (AddPurchaseUnitSnapshots P2 / AddSalesUnitSnapshots P1).
SELECT 'P9a-purchase' AS chk, l."Id" AS line_id FROM "PurchaseOrderLines" l JOIN "ProductSellingModes" m ON m."Id" = l."SellingModeId" WHERE m."ProductId" <> l."ProductId"
UNION ALL
SELECT 'P9a-sales', l."Id" FROM "SalesInvoiceLines" l JOIN "ProductSellingModes" m ON m."Id" = l."SellingModeId" WHERE m."ProductId" <> l."ProductId";
-- P9b Qty <= 0 or Qty x factor <> BaseQty on a purchase / sales line (P3 / P2 of the snapshot migrations).
SELECT 'P9b-purchase' AS chk, "Id" FROM "PurchaseOrderLines" WHERE "Qty" <= 0 OR "Qty" * "ConversionFactorToBase" <> "BaseQty"
UNION ALL
SELECT 'P9b-sales', "Id" FROM "SalesInvoiceLines" WHERE "Qty" <= 0 OR "Qty" * "ConversionFactorToBase" <> "BaseQty";
-- P9c return line of another product than its parent line (AddPurchaseUnitSnapshots P4 / AddSalesUnitSnapshots P3).
SELECT 'P9c-purchase' AS chk, r."Id" FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId" WHERE l."ProductId" <> r."ProductId"
UNION ALL
SELECT 'P9c-sales', r."Id" FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId" WHERE l."ProductId" <> r."ProductId";
-- P9d return line Qty x parent factor <> BaseQty (P5 / P4 of the snapshot migrations).
SELECT 'P9d-purchase' AS chk, r."Id" FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId" WHERE r."Qty" * l."ConversionFactorToBase" <> r."BaseQty"
UNION ALL
SELECT 'P9d-sales', r."Id" FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId" WHERE r."Qty" * l."ConversionFactorToBase" <> r."BaseQty";
-- P9e sales line with Discount < 0 or Discount > Qty x UnitPrice (AddSalesUnitSnapshots P6 -> CK_SalesInvoiceLines_Discount_WithinGross).
SELECT 'P9e' AS chk, "Id" FROM "SalesInvoiceLines" WHERE "Discount" < 0 OR "Discount" > "Qty" * "UnitPrice";
-- P9f units that collide case-insensitively on Name or ShortCode (ExtendUnitMaster aborts).
SELECT 'P9f' AS chk, lower("Name") AS key_lc, COUNT(*) FROM "Units" GROUP BY lower("Name") HAVING COUNT(*) > 1
UNION ALL
SELECT 'P9f', lower("ShortCode"), COUNT(*) FROM "Units" GROUP BY lower("ShortCode") HAVING COUNT(*) > 1;
-- P9g stock adjustments of a product without any mode (AddStockAdjustmentUnitSnapshots P2 relies on the base ProductUnit that IntroduceProductUnits creates; informational).
SELECT 'P9g' AS chk, COUNT(DISTINCT a."ProductId") AS adjusted_products_without_factor1_mode
FROM "StockAdjustments" a
WHERE NOT EXISTS (SELECT 1 FROM "ProductSellingModes" m WHERE m."ProductId" = a."ProductId" AND m."ConversionFactorToBase" = 1);

-- P10 pre-release stock baseline (must already reconcile at the old schema; the migrations also check it, AddStockLocationDimension G3).
-- Zero rows when healthy: QOH vs ledger, QOH vs batches, per product.
SELECT 'P10-ledger' AS chk, s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m ON m."ProductId" = s."ProductId"
WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
UNION ALL
SELECT 'P10-batches', s."ProductId" FROM "StockItems" s
LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b ON b."ProductId" = s."ProductId"
WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
