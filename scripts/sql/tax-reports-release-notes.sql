-- Tax profiles TP-5a (AddTaxReportSettings + PrepareTaxHistoryBackfill + ValidateTaxHistoryBackfill + data script tax-history-profile-backfill): RELEASE-NOTES REPORT and RUNBOOK. Read-only.
-- INFORMATIVE rows for the owner, not zero-row checks. Never a V__ file (the checks are backend/db/validation/V__tax-history-profile.sql (warn) and the TP-3/TP-4 snapshot files).
-- Run with psql per environment and attach the output to the release notes:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/sql/tax-reports-release-notes.sql
-- P1 and P2 run BEFORE and AFTER the release (they read only columns that exist since TP-4); R1 and R2 belong to the AFTER report.
-- Output: company ids, product ids and counts (no personal data).
--
-- RUNBOOK (order matters; docs/features/tax-profiles.md, "TP-5 design"):
--   1. BEFORE: run P1. Products that are GST + Taxable and have no HSN (on the product or on its subcategory) cannot be sold or bought once a company sets
--      BusinessSettings.HsnRequiredFrom. The date stays NULL for every existing company (warning only), so the release itself blocks nobody. Fix the HSN codes first, THEN set the date per company.
--   2. Back up. Apply the three EF migrations IN THIS ORDER (they sort by name; Database releases page, never at startup, D10):
--        a) AddTaxReportSettings: Reports.Tax permission and grants, Units.Uqc with its seed, BusinessSettings.HsnRequiredFrom. Short locks on Units and BusinessSettings only, released at its commit.
--        b) PrepareTaxHistoryBackfill: the ONLY migration that locks the four line tables (one LOCK TABLE, catalog-only changes, lock_timeout 5 s). It holds nothing else while it waits, so it
--           cannot deadlock with a sale that reads Units; on 55P03 it fails fast and changes nothing: re-run it.
--        c) ValidateTaxHistoryBackfill: SHARE UPDATE EXCLUSIVE only (writes continue).
--   3. The data script backend/db/scripts/2026.11.25.001__tax-history-profile-backfill.sql runs in the same release right after the migrations (transaction: none, a commit per 5,000 rows).
--      It is RESUMABLE and idempotent and can run WHILE SALES CONTINUE. If it is cancelled or times out, run it again (retry on the Database releases page, or psql -f): finished batches stay.
--      Watch the progress from a second session with P2 / R1. It scans every line table once per run: on a very large tenant raise the "-- sellora:timeout" header BEFORE merging
--      (14400 s today), or run the file by hand with psql.
--   4. AFTER: run P2 (must show no backfillable lines), R1, R2, then the V__ checks: every V__tax-*.sql returns 0 rows. V__tax-history-profile.sql only warns in 5a but must be 0 before TP-5b.
--      RETRY: every migration here takes lock_timeout 5 s. If one fails with SQLSTATE 55P03 (lock_not_available) or 40P01 (deadlock_detected) nothing was changed (each runs in one
--      transaction): wait a few seconds and re-run it, ideally off-peak. Never raise lock_timeout to "make it pass".
--   3b. AFTER the backfill finishes: VACUUM (ANALYZE) "SalesInvoiceLines", "SalesReturnLines", "PurchaseOrderLines", "PurchaseReturnLines";  The backfill updates every history row
--      (the TaxProfileId column is indexed, so the updates are not HOT): expect table and index bloat and a burst of WAL. Run the VACUUM (not FULL) once, outside peak hours,
--      and watch replication lag while the script runs.
--   5. The APP can still be rolled back to the TP-4 build: it ignores the backfilled profile of history lines, and its own inserts (profile + version) satisfy the relaxed constraint.
--      After the backfill the TP-4 build's sales and purchase line DTOs carry a profile with a NULL version for history lines. That is tolerated: TaxProfileVersionId is nullable in the
--      DTOs and models of both builds (Guid? / optional in purchases.models.ts) and no TP-4 sales or purchase detail component reads it (checked with a search of frontend/src/app/features).

-- P1 (BEFORE and AFTER) per company: active products that are GST + Taxable (their resolved sales OR purchase profile, resolution order as in V__tax-products-resolve.sql) with no HSN on the product
-- and none on its subcategory. The HSN becomes mandatory for them on documents dated on or after BusinessSettings.HsnRequiredFrom. product_ids is capped at 200 per company (see products for the rest).
SELECT 'P1' AS rpt, x."CompanyId", COUNT(*) AS products_without_hsn, (array_agg(x."ProductId" ORDER BY x."ProductId"))[1:200] AS product_ids
FROM (
  SELECT pr."CompanyId", pr."Id" AS "ProductId"
  FROM "Products" pr
  LEFT JOIN "ProductSubcategories" sc ON sc."Id" = pr."SubcategoryId"
  LEFT JOIN "BusinessSettings" b ON b."CompanyId" = pr."CompanyId"
  LEFT JOIN LATERAL (
    SELECT a."SalesTaxProfileId" AS sales_profile, COALESCE(a."PurchaseTaxProfileId", a."SalesTaxProfileId") AS purchase_profile
    FROM "TaxAssignments" a
    WHERE a."CompanyId" = pr."CompanyId" AND a."CountryId" = b."CountryId"
      AND ((a."Scope" = 'Product' AND a."ProductId" = pr."Id")
        OR (a."Scope" = 'Subcategory' AND a."SubcategoryId" = pr."SubcategoryId")
        OR (a."Scope" = 'Category' AND a."CategoryId" = pr."CategoryId")
        OR a."Scope" = 'Company')
    ORDER BY CASE a."Scope" WHEN 'Product' THEN 1 WHEN 'Subcategory' THEN 2 WHEN 'Category' THEN 3 ELSE 4 END
    LIMIT 1
  ) r ON true
  WHERE pr."IsActive"
    AND COALESCE(NULLIF(trim(pr."HsnCode"), ''), NULLIF(trim(sc."HsnCode"), '')) IS NULL
    AND EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."Id" IN (r.sales_profile, r.purchase_profile) AND p."Regime" = 'Gst' AND p."Treatment" = 'Taxable')
) x
GROUP BY x."CompanyId" ORDER BY x."CompanyId";

-- P2 (BEFORE and AFTER) lines still without a tax profile, per table, split into: backfillable (the script will set them: the document company has a mirror profile for the line's TaxId, or the
-- return's original has a profile) and unmappable (no mirror profile in the company / original without one: a data problem to fix by hand, they stay NULL and V__tax-history-profile.sql lists them).
-- AFTER the backfill has finished, backfillable must be 0 (rows locked during a run are picked up by the next run).
SELECT 'P2' AS rpt, x.tbl, COUNT(*) FILTER (WHERE x.backfillable) AS backfillable, COUNT(*) FILTER (WHERE NOT x.backfillable) AS unmappable
FROM (
  SELECT 'SalesInvoiceLines' AS tbl, EXISTS (SELECT 1 FROM "SalesInvoices" h JOIN "TaxProfiles" p ON p."CompanyId" = h."CompanyId" AND p."LegacyTaxId" = l."TaxId" WHERE h."Id" = l."SalesInvoiceId") AS backfillable
  FROM "SalesInvoiceLines" l WHERE l."TaxProfileId" IS NULL
  UNION ALL
  SELECT 'PurchaseOrderLines', EXISTS (SELECT 1 FROM "PurchaseOrders" h JOIN "TaxProfiles" p ON p."CompanyId" = h."CompanyId" AND p."LegacyTaxId" = l."TaxId" WHERE h."Id" = l."PurchaseOrderId")
  FROM "PurchaseOrderLines" l WHERE l."TaxProfileId" IS NULL
  UNION ALL
  SELECT 'SalesReturnLines', EXISTS (SELECT 1 FROM "SalesInvoiceLines" o WHERE o."Id" = l."SalesInvoiceLineId" AND (o."TaxProfileId" IS NOT NULL
         OR EXISTS (SELECT 1 FROM "SalesInvoices" h JOIN "TaxProfiles" p ON p."CompanyId" = h."CompanyId" AND p."LegacyTaxId" = o."TaxId" WHERE h."Id" = o."SalesInvoiceId")))
  FROM "SalesReturnLines" l WHERE l."TaxProfileId" IS NULL
  UNION ALL
  SELECT 'PurchaseReturnLines', EXISTS (SELECT 1 FROM "PurchaseOrderLines" o WHERE o."Id" = l."PurchaseOrderLineId" AND (o."TaxProfileId" IS NOT NULL
         OR EXISTS (SELECT 1 FROM "PurchaseOrders" h JOIN "TaxProfiles" p ON p."CompanyId" = h."CompanyId" AND p."LegacyTaxId" = o."TaxId" WHERE h."Id" = o."PurchaseOrderId")))
  FROM "PurchaseReturnLines" l WHERE l."TaxProfileId" IS NULL
) x
GROUP BY x.tbl ORDER BY x.tbl;

-- R1 (AFTER) backfill result per table: total lines, lines written by the TP-3/TP-4 builds (profile AND version: untouched by the backfill), history lines that now carry the profile only
-- (the version stays NULL by design, BR24), lines still without a profile (= P2). total = with_version + history_with_profile + without_profile.
SELECT 'R1' AS rpt, x.tbl, COUNT(*) AS total_lines,
       COUNT(*) FILTER (WHERE x.ver IS NOT NULL) AS with_version,
       COUNT(*) FILTER (WHERE x.prof IS NOT NULL AND x.ver IS NULL) AS history_with_profile,
       COUNT(*) FILTER (WHERE x.prof IS NULL) AS without_profile
FROM (
  SELECT 'SalesInvoiceLines' AS tbl, "TaxProfileId" AS prof, "TaxProfileVersionId" AS ver FROM "SalesInvoiceLines"
  UNION ALL SELECT 'PurchaseOrderLines', "TaxProfileId", "TaxProfileVersionId" FROM "PurchaseOrderLines"
  UNION ALL SELECT 'SalesReturnLines', "TaxProfileId", "TaxProfileVersionId" FROM "SalesReturnLines"
  UNION ALL SELECT 'PurchaseReturnLines', "TaxProfileId", "TaxProfileVersionId" FROM "PurchaseReturnLines"
) x
GROUP BY x.tbl ORDER BY x.tbl;

-- R2 (AFTER, information only) history sales/purchase lines whose profile's CURRENT version (valid today) has a different total rate than the rate stored on the line (TaxRatePercent).
-- The stored rate is what was charged and stays the amount of record; the reports show such lines in the "Legacy (unsplit)" bucket by their STORED rate, never by today's rate.
-- Typical cause: the rate of the tax was edited after the document (rates were editable before TP-1). One row per company, table, stored rate and current rate.
SELECT 'R2' AS rpt, x."CompanyId", x.tbl, x.stored_rate, x.current_rate, COUNT(*) AS lines
FROM (
  SELECT h."CompanyId", 'SalesInvoiceLines' AS tbl, l."TaxRatePercent" AS stored_rate, v."TotalPercent" AS current_rate
  FROM "SalesInvoiceLines" l
  JOIN "SalesInvoices" h ON h."Id" = l."SalesInvoiceId"
  JOIN "TaxProfileVersions" v ON v."TaxProfileId" = l."TaxProfileId" AND v."EffectiveFrom" <= CURRENT_DATE AND (v."EffectiveTo" IS NULL OR v."EffectiveTo" >= CURRENT_DATE)
  WHERE l."TaxProfileVersionId" IS NULL AND l."TaxRatePercent" <> v."TotalPercent"
  UNION ALL
  SELECT h."CompanyId", 'PurchaseOrderLines', l."TaxRatePercent", v."TotalPercent"
  FROM "PurchaseOrderLines" l
  JOIN "PurchaseOrders" h ON h."Id" = l."PurchaseOrderId"
  JOIN "TaxProfileVersions" v ON v."TaxProfileId" = l."TaxProfileId" AND v."EffectiveFrom" <= CURRENT_DATE AND (v."EffectiveTo" IS NULL OR v."EffectiveTo" >= CURRENT_DATE)
  WHERE l."TaxProfileVersionId" IS NULL AND l."TaxRatePercent" <> v."TotalPercent"
) x
GROUP BY x."CompanyId", x.tbl, x.stored_rate, x.current_rate
ORDER BY x."CompanyId", x.tbl, x.stored_rate, x.current_rate;
