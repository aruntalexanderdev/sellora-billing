-- Tax profiles TP-3 (AddPurchaseTaxSnapshot + ValidatePurchaseTaxConstraints): RELEASE-NOTES REPORT. Read-only; INFORMATIVE rows for the owner, not zero-row checks.
-- Never a V__ file (the blocking checks are backend/db/validation/V__tax-purchase-snapshot.sql and V__tax-line-rows-missing-purchases.sql). Run with psql per environment
-- and attach the output to the release notes:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/sql/tax-purchases-release-notes.sql
-- Section P runs BEFORE the migration (it only reads columns that exist since AddCountryStateMasters); R1 runs BEFORE and AFTER; R2 and the rounding note belong to the AFTER report.
-- Output: company ids, branch ids and counts (no personal data).

-- P  (BEFORE) Indian branches that cannot place a purchase after TP-3: neither the branch nor the company has a state. The purchase is refused with
-- "Set the state of the branch (or of the company in the business settings) before billing GST documents." Set one of the two states first (Settings, or Branches).
-- The document country is the branch's country, else the company's (same rule as TaxResolver.ResolveDocumentCountryAsync); GCC companies (VAT) are not affected.
SELECT 'P' AS rpt, b."CompanyId", b."Id" AS "BranchId", b."IsActive"
FROM "Branches" b
JOIN "BusinessSettings" s ON s."CompanyId" = b."CompanyId"
JOIN "Countries" c ON c."Id" = COALESCE(b."CountryId", s."CountryId")
WHERE c."Code" = 'IN' AND b."StateId" IS NULL AND s."StateId" IS NULL
ORDER BY b."CompanyId", b."Id";

-- R1 (BEFORE and AFTER) purchase and purchase-return lines WITHOUT a tax row = lines saved by the previous build between the TP-1 commit and the app swap.
-- BEFORE: the number of legacy rows AddPurchaseTaxSnapshot will insert (it re-runs the frozen, idempotent AddTaxProfiles.legacy-rows.sql first). AFTER: must be 0 rows.
SELECT 'R1' AS rpt, x.tbl, COUNT(*) AS lines_without_tax_row FROM (
  SELECT 'PurchaseOrderLines' AS tbl FROM "PurchaseOrderLines" l WHERE NOT EXISTS (SELECT 1 FROM "PurchaseOrderLineTaxes" t WHERE t."PurchaseOrderLineId" = l."Id")
  UNION ALL
  SELECT 'PurchaseReturnLines' FROM "PurchaseReturnLines" l WHERE NOT EXISTS (SELECT 1 FROM "PurchaseReturnLineTaxes" t WHERE t."PurchaseReturnLineId" = l."Id")
) x GROUP BY x.tbl ORDER BY x.tbl;

-- R2 (AFTER) suppliers without a state, per company: a purchase from them is treated as INTRA-state and the form warns ("Set the supplier's state").
-- Same-country suppliers without a state therefore always get CGST + SGST; set the state to get IGST for out-of-state suppliers. used = suppliers with at least one purchase.
SELECT 'R2' AS rpt, sp."CompanyId", COUNT(*) AS suppliers_without_state,
       COUNT(*) FILTER (WHERE EXISTS (SELECT 1 FROM "PurchaseOrders" o WHERE o."SupplierId" = sp."Id")) AS used_in_purchases
FROM "Suppliers" sp
WHERE sp."StateId" IS NULL AND sp."IsActive"
GROUP BY sp."CompanyId" ORDER BY sp."CompanyId";

-- Rounding note (DQ-TP2-2), no query: from TP-3 the purchase calculator rounds gross, tax and totals to the currency precision of the document (per component, per line),
-- and the header GrandTotal is the sum of the line totals. A new purchase can therefore differ from the old formula by up to one smallest currency unit for the same
-- input. Existing purchases keep their stored amounts (history is never recomputed); their TaxableTotal is NULL and the API derives it as
-- Subtotal + OtherChargesTotal - DiscountTotal. The pre-existing header money columns stay numeric(18,2); the new ones are numeric(18,4) (TP3-Q2).
