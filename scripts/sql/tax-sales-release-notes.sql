-- Tax profiles TP-4 (AddSalesTaxSnapshot + ValidateSalesTaxConstraints): RELEASE-NOTES REPORT and RUNBOOK. Read-only (the runbook section at the end is the one place with DDL).
-- INFORMATIVE rows for the owner, not zero-row checks. Never a V__ file (the blocking checks are backend/db/validation/V__tax-sales-snapshot.sql and V__tax-line-rows-missing.sql).
-- Run with psql per environment and attach the output to the release notes:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/sql/tax-sales-release-notes.sql
-- P1, P2, P3 run BEFORE the migration (they read only columns that exist since TP-1); R1 runs BEFORE and AFTER; R2, R3 and the rounding note belong to the AFTER report.
-- Output: company ids, branch ids, product ids and counts (no personal data).

-- P1 (BEFORE, BLOCKING for the release) Indian branches that cannot place a sale after TP-4: neither the branch nor the company has a state. The POS refuses the sale with
-- "Set the state of the branch (or of the company in the business settings) before billing GST documents." Set one of the two states first (Settings, or Branches).
-- The document country is the branch's country, else the company's (same rule as TaxResolver.ResolveDocumentCountryAsync); GCC companies (VAT) are not affected.
SELECT 'P1' AS rpt, b."CompanyId", b."Id" AS "BranchId", b."IsActive"
FROM "Branches" b
JOIN "BusinessSettings" s ON s."CompanyId" = b."CompanyId"
JOIN "Countries" c ON c."Id" = COALESCE(b."CountryId", s."CountryId")
WHERE c."Code" = 'IN' AND b."StateId" IS NULL AND s."StateId" IS NULL
ORDER BY b."CompanyId", b."Id";

-- P2 (BEFORE) active products with a sellable unit that cannot be sold after TP-4: their sales profile (product, subcategory, category, company assignment, same order as
-- V__tax-products-resolve.sql) does not resolve, or has no version valid TODAY (T19, future-dated versions, is lifted: sales resolve the version by invoice date). The POS refuses
-- such a line ("lines" error). Fix the assignment or the profile's dated versions first.
SELECT 'P2' AS rpt, pr."CompanyId", pr."Id" AS "ProductId",
       CASE WHEN r.profile_id IS NULL THEN 'sales profile does not resolve' ELSE 'no profile version valid today' END AS reason
FROM "Products" pr
LEFT JOIN "BusinessSettings" b ON b."CompanyId" = pr."CompanyId"
LEFT JOIN LATERAL (
  SELECT a."SalesTaxProfileId" AS profile_id
  FROM "TaxAssignments" a
  WHERE a."CompanyId" = pr."CompanyId" AND a."CountryId" = b."CountryId"
    AND ((a."Scope" = 'Product' AND a."ProductId" = pr."Id")
      OR (a."Scope" = 'Subcategory' AND a."SubcategoryId" = pr."SubcategoryId")
      OR (a."Scope" = 'Category' AND a."CategoryId" = pr."CategoryId")
      OR a."Scope" = 'Company')
  ORDER BY CASE a."Scope" WHEN 'Product' THEN 1 WHEN 'Subcategory' THEN 2 WHEN 'Category' THEN 3 ELSE 4 END
  LIMIT 1
) r ON true
WHERE pr."IsActive" AND EXISTS (SELECT 1 FROM "ProductUnits" u WHERE u."ProductId" = pr."Id" AND u."IsSellable" AND u."IsActive")
  AND (r.profile_id IS NULL
       OR NOT EXISTS (SELECT 1 FROM "TaxProfileVersions" v
                      WHERE v."TaxProfileId" = r.profile_id AND v."EffectiveFrom" <= CURRENT_DATE AND (v."EffectiveTo" IS NULL OR v."EffectiveTo" >= CURRENT_DATE)))
ORDER BY pr."CompanyId", pr."Id";

-- P3 (BEFORE) TP4-Q2: companies with SellingPricesIncludeTax = true (the default since TP-1) that ALREADY have sales. Those sales were priced tax-exclusive; from TP-4 the stored
-- selling prices of such a company count as tax-inclusive shelf prices, so customers pay LESS tax on top (the price no longer grows by the tax). Decide per company BEFORE the release:
-- keep inclusive (the prices are shelf labels) or set BusinessSettings.SellingPricesIncludeTax = false (existing behaviour). Product-level overrides (Products.PriceIncludesTax) win.
SELECT 'P3' AS rpt, s."CompanyId", COUNT(i."Id") AS sales_invoices, MIN(i."InvoiceDate") AS first_sale, MAX(i."InvoiceDate") AS last_sale
FROM "BusinessSettings" s
JOIN "SalesInvoices" i ON i."CompanyId" = s."CompanyId"
WHERE s."SellingPricesIncludeTax" = true
GROUP BY s."CompanyId" ORDER BY s."CompanyId";

-- R1 (BEFORE and AFTER) sales and sales-return lines WITHOUT a tax row = lines saved by the previous build between the TP-1 commit and the app swap.
-- BEFORE: the number of legacy rows AddSalesTaxSnapshot will insert (it re-runs the frozen, idempotent AddTaxProfiles.legacy-rows.sql first). AFTER: must be 0 rows.
SELECT 'R1' AS rpt, x.tbl, COUNT(*) AS lines_without_tax_row FROM (
  SELECT 'SalesInvoiceLines' AS tbl FROM "SalesInvoiceLines" l WHERE NOT EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" t WHERE t."SalesInvoiceLineId" = l."Id")
  UNION ALL
  SELECT 'SalesReturnLines' FROM "SalesReturnLines" l WHERE NOT EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" t WHERE t."SalesReturnLineId" = l."Id")
) x GROUP BY x.tbl ORDER BY x.tbl;

-- R2 (AFTER) named (non-walk-in) customers whose state differs from the state of a branch of their company: from TP-4 a sale to them from that branch is INTER-state (IGST instead of
-- CGST + SGST). The branch state is the branch's own, else the company's (business settings). Check the customer's state is right before the first sale. used = customers with a sale.
SELECT 'R2' AS rpt, c."CompanyId", COUNT(DISTINCT c."Id") AS customers_in_other_state,
       COUNT(DISTINCT c."Id") FILTER (WHERE EXISTS (SELECT 1 FROM "SalesInvoices" i WHERE i."CustomerId" = c."Id")) AS used_in_sales
FROM "Customers" c
JOIN "Branches" b ON b."CompanyId" = c."CompanyId"
JOIN "BusinessSettings" s ON s."CompanyId" = c."CompanyId"
WHERE NOT c."IsWalkIn" AND c."IsActive" AND c."StateId" IS NOT NULL AND COALESCE(b."StateId", s."StateId") IS NOT NULL AND c."StateId" <> COALESCE(b."StateId", s."StateId")
GROUP BY c."CompanyId" ORDER BY c."CompanyId";

-- R3 (AFTER) walk-in customers that carry a state: ignored from TP-4 (TP4-Q3: a walk-in is always intra-state), listed so nobody expects IGST for them.
SELECT 'R3' AS rpt, c."CompanyId", c."Id" AS "CustomerId", c."StateId"
FROM "Customers" c WHERE c."IsWalkIn" AND c."StateId" IS NOT NULL
ORDER BY c."CompanyId", c."Id";

-- Rounding note (DQ-TP2-2), no query: from TP-4 the sales calculator rounds gross, tax and totals to the currency precision of the document (per component, per line), and the header
-- GrandTotal is the sum of the line totals. The CGST/SGST split can move an exclusive cart by one smallest currency unit (0.01) against the old formula for the same input. An all-inclusive
-- cart without bill charges totals exactly the sum of (shelf price x qty - line discount) - bill discount (T9). Existing invoices keep their stored amounts (history is never recomputed);
-- their TaxableTotal is NULL and the API derives it as Subtotal - DiscountTotal. The pre-existing header money columns stay numeric(18,2); the new ones are numeric(18,4).

-- ======================================================================================================================================================================
-- RUNBOOK (TP4-Q9), BETWEEN the two migrations, on production-sized databases. Commented out: copy each statement into its own psql call (CONCURRENTLY cannot run in a transaction or a
-- multi-statement script). They build the two partial indexes on SalesInvoiceLines without blocking sales; AddSalesTaxSnapshot/ValidateSalesTaxConstraints then find them
-- (CREATE INDEX IF NOT EXISTS) and skip the build. The indexes can only be created AFTER the columns exist, so the practical order is:
--   1. apply AddSalesTaxSnapshot (metadata only, milliseconds) up to but NOT including ValidateSalesTaxConstraints
--        (dotnet ef migrations script <ValidatePurchaseTaxConstraints> <AddSalesTaxSnapshot> --idempotent  ->  psql -f),
--   2. run the two statements below (sales keep flowing),
--   3. apply ValidateSalesTaxConstraints (VALIDATE of the constraints, the other four plain indexes on small tables, no build on SalesInvoiceLines).
--      Even when the indexes exist, CREATE INDEX IF NOT EXISTS still takes a brief SHARE lock on SalesInvoiceLines: a sale that is being saved at that
--      moment makes the migration wait up to the 5 s lock_timeout and fail with 55P03; nothing is applied, simply re-run it (ideally in a quiet minute).
-- Small databases may skip the runbook: ValidateSalesTaxConstraints builds the indexes itself (a plain CREATE INDEX, it blocks writes to SalesInvoiceLines while it runs).
--
--   CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SalesInvoiceLines_TaxProfileId" ON "SalesInvoiceLines" ("TaxProfileId") WHERE "TaxProfileId" IS NOT NULL;
--   CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SalesInvoiceLines_TaxProfileVersionId" ON "SalesInvoiceLines" ("TaxProfileVersionId") WHERE "TaxProfileVersionId" IS NOT NULL;
--
-- A failed CONCURRENTLY build leaves an INVALID index of the same name: check, DROP INDEX CONCURRENTLY it and re-run the statement (the migration refuses to continue past an invalid one):
--   SELECT c.relname, i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relname LIKE 'IX\_SalesInvoiceLines\_TaxProfile%';

-- ======================================================================================================================================================================
-- RUNBOOK additions (T31)
--
-- 1. After the release: if the blocking V__tax-line-rows-missing.sql still flags lines (a TP-3 build instance inserted a sale between the catch-up and the app swap), re-run the frozen,
--    idempotent catch-up file backend/src/Sellora.Infrastructure/Data/Migrations/Sql/20261124120429_AddTaxProfiles.legacy-rows.sql (never edit it; running it again is a no-op for
--    lines that already have a row), then re-run V__tax-line-rows-missing.sql. It must return no rows.
--
-- 2. BEFORE rolling back to a pre-TP-4 build: the POS catalog and the product screen of that build show the mirror tax (Taxes.RatePercent / IsActive), which only changes when a
--    tax write happens. A version that took effect by date since the last write (T19 lifted: future-dated versions) has not refreshed it. Refresh every profile's mirror from the
--    version valid today (run in one transaction, review the SELECT first; no version valid today = 0):
--
--      SELECT t."Id", t."RatePercent" AS mirror_rate, COALESCE(v."TotalPercent", 0) AS current_rate, t."IsActive" AS mirror_active, p."IsActive" AS profile_active
--      FROM "TaxProfiles" p
--      JOIN "Taxes" t ON t."Id" = p."LegacyTaxId" AND t."CompanyId" = p."CompanyId"
--      LEFT JOIN "TaxProfileVersions" v ON v."TaxProfileId" = p."Id" AND v."EffectiveFrom" <= CURRENT_DATE AND (v."EffectiveTo" IS NULL OR v."EffectiveTo" >= CURRENT_DATE)
--      WHERE t."RatePercent" <> COALESCE(v."TotalPercent", 0) OR t."IsActive" <> p."IsActive";
--
--      UPDATE "Taxes" t SET "RatePercent" = COALESCE(v."TotalPercent", 0), "IsActive" = p."IsActive"
--      FROM "TaxProfiles" p
--      LEFT JOIN "TaxProfileVersions" v ON v."TaxProfileId" = p."Id" AND v."EffectiveFrom" <= CURRENT_DATE AND (v."EffectiveTo" IS NULL OR v."EffectiveTo" >= CURRENT_DATE)
--      WHERE t."Id" = p."LegacyTaxId" AND t."CompanyId" = p."CompanyId"
--        AND (t."RatePercent" <> COALESCE(v."TotalPercent", 0) OR t."IsActive" <> p."IsActive");
--
--    V__tax-mirror-drift.sql reports the same drift; it must return no rows afterwards. A return made by the old build on a TP-4 line writes one legacy row next to the component rows
--    (totals stay right, the per-component split is approximate).
--
-- 3. Accepted (T31e), no action: the pre-existing SalesInvoices header money columns stay numeric(18,2), so a base currency with 3 decimals (OMR, KWD, BHD) stores the header
--    Subtotal/TaxTotal/DiscountTotal/GrandTotal at 2 decimals while the lines (numeric(18,4)) and TaxableTotal keep the exact value; and SalesInvoiceLines.TaxRatePercent is numeric(6,3)
--    (display only: a Σ applied percent with more than 3 decimals is rounded in that column; amounts are unaffected).
