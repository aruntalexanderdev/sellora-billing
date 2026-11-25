-- sellora:description Sales tax snapshots (TP-4): profile/version/company, mirror tax, line and header arithmetic, inclusive exact-price invariant, treatment on rows, place-of-supply component rules, returns carry the original's version and never exceed it
-- sellora:on-violation block
-- docs/features/tax-profiles.md, TP-4 design. Only documents saved by the TP-4 build (line TaxProfileId set, header TaxableTotal set) are checked, history is never touched.
-- Output: check label, table and row id only. Header money columns are numeric(18,2) while lines are numeric(18,4), so header-vs-line comparisons allow half a cent.
-- The calculator rounds the gross (Qty x UnitPrice) to the currency precision (0, 2 or 3 decimals), so the line checks accept any of the three roundings.
SELECT 'version-not-of-profile-or-company' AS chk, 'SalesInvoiceLines' AS tbl, l."Id" AS "RowId"
FROM "SalesInvoiceLines" l JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId"
JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" JOIN "TaxProfileVersions" v ON v."Id" = l."TaxProfileVersionId"
WHERE v."TaxProfileId" <> p."Id" OR p."CompanyId" <> i."CompanyId" OR v."CompanyId" <> i."CompanyId"
UNION ALL
SELECT 'version-not-of-profile-or-company', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l JOIN "SalesReturns" r ON r."Id" = l."SalesReturnId"
JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" JOIN "TaxProfileVersions" v ON v."Id" = l."TaxProfileVersionId"
WHERE v."TaxProfileId" <> p."Id" OR p."CompanyId" <> r."CompanyId" OR v."CompanyId" <> r."CompanyId"
UNION ALL
-- the line's TaxId is the sales profile's mirror tax (keeps the TP-3 build and the T25 "used version" check working)
SELECT 'taxid-not-profile-mirror', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId"
WHERE l."TaxId" <> p."LegacyTaxId"
UNION ALL
SELECT 'line-total-not-taxable-plus-tax', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l WHERE l."TaxProfileId" IS NOT NULL AND l."LineTotal" <> l."TaxableValue" + l."TaxAmount"
UNION ALL
SELECT 'line-total-not-taxable-plus-tax', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l WHERE l."TaxProfileId" IS NOT NULL AND l."LineTotal" <> l."TaxableValue" + l."TaxAmount"
UNION ALL
-- inclusive exact-price invariant (BR17, T9): without a bill-charge share the customer pays exactly shelf x qty - line discount - bill discount share
SELECT 'inclusive-line-total-not-exact-price', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l
WHERE l."TaxProfileId" IS NOT NULL AND l."PriceIncludesTax" AND l."BillChargeShare" = 0
  AND l."LineTotal" NOT IN (ROUND(l."Qty" * l."UnitPrice", 0) - l."Discount" - l."BillDiscountShare",
                            ROUND(l."Qty" * l."UnitPrice", 2) - l."Discount" - l."BillDiscountShare",
                            ROUND(l."Qty" * l."UnitPrice", 3) - l."Discount" - l."BillDiscountShare")
UNION ALL
-- exclusive lines (BR16): taxable = gross - line discount - bill discount share + bill charge share
SELECT 'exclusive-line-taxable-mismatch', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l
WHERE l."TaxProfileId" IS NOT NULL AND NOT l."PriceIncludesTax"
  AND l."TaxableValue" NOT IN (ROUND(l."Qty" * l."UnitPrice", 0) - l."Discount" - l."BillDiscountShare" + l."BillChargeShare",
                               ROUND(l."Qty" * l."UnitPrice", 2) - l."Discount" - l."BillDiscountShare" + l."BillChargeShare",
                               ROUND(l."Qty" * l."UnitPrice", 3) - l."Discount" - l."BillDiscountShare" + l."BillChargeShare")
UNION ALL
-- every row written by the calculator has a Treatment (NULL marks a legacy row only, which has no component either)
SELECT 'row-without-treatment', 'SalesInvoiceLineTaxes', x."Id"
FROM "SalesInvoiceLineTaxes" x JOIN "SalesInvoiceLines" l ON l."Id" = x."SalesInvoiceLineId"
WHERE x."Treatment" IS NULL AND (x."TaxComponentId" IS NOT NULL OR l."TaxProfileId" IS NOT NULL)
UNION ALL
SELECT 'row-without-treatment', 'SalesReturnLineTaxes', x."Id"
FROM "SalesReturnLineTaxes" x JOIN "SalesReturnLines" l ON l."Id" = x."SalesReturnLineId"
WHERE x."Treatment" IS NULL AND (x."TaxComponentId" IS NOT NULL OR l."TaxProfileId" IS NOT NULL)
UNION ALL
-- intra-state lines have no IGST row, inter-state lines no CGST/SGST row; a VAT line (place NotApplicable) has no GST row at all
SELECT 'intra-line-has-igst', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l WHERE l."PlaceOfSupply" = 'IntraState' AND EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = l."Id" AND x."Kind" = 'Igst')
UNION ALL
SELECT 'inter-line-has-cgst-sgst', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l WHERE l."PlaceOfSupply" = 'InterState' AND EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = l."Id" AND x."Kind" IN ('Cgst', 'Sgst'))
UNION ALL
SELECT 'vat-line-has-gst-row', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l WHERE l."PlaceOfSupply" = 'NotApplicable' AND EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = l."Id" AND x."Kind" IN ('Cgst', 'Sgst', 'Utgst', 'Igst'))
UNION ALL
SELECT 'intra-line-has-igst', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l WHERE l."PlaceOfSupply" = 'IntraState' AND EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" x WHERE x."SalesReturnLineId" = l."Id" AND x."Kind" = 'Igst')
UNION ALL
SELECT 'inter-line-has-cgst-sgst', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l WHERE l."PlaceOfSupply" = 'InterState' AND EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" x WHERE x."SalesReturnLineId" = l."Id" AND x."Kind" IN ('Cgst', 'Sgst'))
UNION ALL
SELECT 'vat-line-has-gst-row', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l WHERE l."PlaceOfSupply" = 'NotApplicable' AND EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" x WHERE x."SalesReturnLineId" = l."Id" AND x."Kind" IN ('Cgst', 'Sgst', 'Utgst', 'Igst'))
UNION ALL
-- CGST = SGST per line (the two halves of a GST rate)
SELECT 'cgst-not-equal-sgst', 'SalesInvoiceLines', l."Id"
FROM "SalesInvoiceLines" l
JOIN (SELECT "SalesInvoiceLineId" AS line_id, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Cgst'), 0) AS cgst, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Sgst'), 0) AS sgst
      FROM "SalesInvoiceLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE l."TaxProfileId" IS NOT NULL AND x.cgst <> x.sgst
UNION ALL
SELECT 'cgst-not-equal-sgst', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l
JOIN (SELECT "SalesReturnLineId" AS line_id, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Cgst'), 0) AS cgst, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Sgst'), 0) AS sgst
      FROM "SalesReturnLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE l."TaxProfileId" IS NOT NULL AND x.cgst <> x.sgst
UNION ALL
-- header sums equal line sums (TP-4 sales only: TaxableTotal is NULL on history). Shares and bill amounts are numeric(18,4) on both sides (exact); the old (18,2) columns allow half a cent.
SELECT 'header-taxable-not-sum-of-lines', 'SalesInvoices', i."Id"
FROM "SalesInvoices" i JOIN (SELECT "SalesInvoiceId" AS id, SUM("TaxableValue") AS taxable FROM "SalesInvoiceLines" GROUP BY 1) s ON s.id = i."Id"
WHERE i."TaxableTotal" IS NOT NULL AND i."TaxableTotal" <> s.taxable
UNION ALL
SELECT 'header-tax-not-sum-of-lines', 'SalesInvoices', i."Id"
FROM "SalesInvoices" i JOIN (SELECT "SalesInvoiceId" AS id, SUM("TaxAmount") AS tax FROM "SalesInvoiceLines" GROUP BY 1) s ON s.id = i."Id"
WHERE i."TaxableTotal" IS NOT NULL AND abs(i."TaxTotal" - s.tax) > 0.005
UNION ALL
SELECT 'header-grand-total-not-sum-of-lines', 'SalesInvoices', i."Id"
FROM "SalesInvoices" i JOIN (SELECT "SalesInvoiceId" AS id, SUM("LineTotal") AS total FROM "SalesInvoiceLines" GROUP BY 1) s ON s.id = i."Id"
WHERE i."TaxableTotal" IS NOT NULL AND abs(i."GrandTotal" - s.total) > 0.005
UNION ALL
SELECT 'header-discount-not-sum-of-lines', 'SalesInvoices', i."Id"
FROM "SalesInvoices" i JOIN (SELECT "SalesInvoiceId" AS id, SUM("Discount" + "BillDiscountShare") AS discount FROM "SalesInvoiceLines" GROUP BY 1) s ON s.id = i."Id"
WHERE i."TaxableTotal" IS NOT NULL AND abs(i."DiscountTotal" - s.discount) > 0.005
UNION ALL
SELECT 'header-subtotal-not-sum-of-lines', 'SalesInvoices', i."Id"
FROM "SalesInvoices" i JOIN (SELECT "SalesInvoiceId" AS id, SUM("Qty" * "UnitPrice") AS gross, COUNT(*) AS n FROM "SalesInvoiceLines" GROUP BY 1) s ON s.id = i."Id"
WHERE i."TaxableTotal" IS NOT NULL AND abs(i."Subtotal" - s.gross) > 0.005 * (s.n + 1)  -- each line's gross was rounded to the currency precision (<= half a unit per line)
UNION ALL
SELECT 'header-bill-discount-not-sum-of-shares', 'SalesInvoices', i."Id"
FROM "SalesInvoices" i JOIN (SELECT "SalesInvoiceId" AS id, SUM("BillDiscountShare") AS bill_discount, SUM("BillChargeShare") AS bill_charges FROM "SalesInvoiceLines" GROUP BY 1) s ON s.id = i."Id"
WHERE i."TaxableTotal" IS NOT NULL AND i."BillDiscountAmount" <> s.bill_discount
UNION ALL
SELECT 'header-bill-charges-not-sum-of-shares', 'SalesInvoices', i."Id"
FROM "SalesInvoices" i JOIN (SELECT "SalesInvoiceId" AS id, SUM("BillChargeShare") AS bill_charges FROM "SalesInvoiceLines" GROUP BY 1) s ON s.id = i."Id"
WHERE i."TaxableTotal" IS NOT NULL AND i."BillCharges" <> s.bill_charges
UNION ALL
-- a return line carries the version, profile and place of supply of the sales line it returns. Exception (design, rollback risk): a return made by the TP-3 build on a TP-4 line has
-- no profile/version/place and writes ONE legacy row (component NULL, Treatment NULL); such a line is tolerated, its line totals are still checked below.
SELECT 'return-line-version-differs-from-original', 'SalesReturnLines', l."Id"
FROM "SalesReturnLines" l JOIN "SalesInvoiceLines" o ON o."Id" = l."SalesInvoiceLineId"
WHERE o."TaxProfileId" IS NOT NULL
  AND (l."TaxProfileId" IS DISTINCT FROM o."TaxProfileId" OR l."TaxProfileVersionId" IS DISTINCT FROM o."TaxProfileVersionId" OR l."PlaceOfSupply" IS DISTINCT FROM o."PlaceOfSupply")
  AND NOT (l."TaxProfileId" IS NULL AND l."TaxProfileVersionId" IS NULL AND l."PlaceOfSupply" IS NULL
           AND EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" lg WHERE lg."SalesReturnLineId" = l."Id" AND lg."TaxComponentId" IS NULL AND lg."Treatment" IS NULL))
UNION ALL
-- returned taxable value and tax per sales line never exceed the original; a fully returned line (returned base quantity = sold) sums exactly (the last return takes the remainder)
SELECT 'returned-line-amounts-exceed-original', 'SalesInvoiceLines', o."Id"
FROM "SalesInvoiceLines" o JOIN (SELECT "SalesInvoiceLineId" AS id, SUM("TaxableValue") AS taxable, SUM("TaxAmount") AS tax FROM "SalesReturnLines" GROUP BY 1) r ON r.id = o."Id"
WHERE o."TaxProfileId" IS NOT NULL AND (r.taxable > o."TaxableValue" OR r.tax > o."TaxAmount")
UNION ALL
SELECT 'fully-returned-line-not-exact', 'SalesInvoiceLines', o."Id"
FROM "SalesInvoiceLines" o JOIN (SELECT "SalesInvoiceLineId" AS id, SUM("BaseQty") AS base_qty, SUM("TaxableValue") AS taxable, SUM("TaxAmount") AS tax FROM "SalesReturnLines" GROUP BY 1) r ON r.id = o."Id"
WHERE o."TaxProfileId" IS NOT NULL AND r.base_qty = o."BaseQty" AND (r.taxable <> o."TaxableValue" OR r.tax <> o."TaxAmount")
UNION ALL
-- the same per tax component (matched by component id, else by code). Originals that have a LEGACY return row (a return made by the TP-3 build on a TP-4 line writes one legacy row next to
-- the component rows, see the design's rollback risk) are skipped: their per-component split is approximate by design (the line totals above still apply).
SELECT 'returned-component-exceeds-original', 'SalesInvoiceLineTaxes', o."Id"
FROM "SalesInvoiceLineTaxes" o JOIN "SalesInvoiceLines" l ON l."Id" = o."SalesInvoiceLineId"
JOIN (SELECT rl."SalesInvoiceLineId" AS line_id, COALESCE(rt."TaxComponentId"::text, rt."ComponentCode") AS component, SUM(rt."TaxAmount") AS tax, SUM(rt."BaseAmount") AS base_amount
      FROM "SalesReturnLineTaxes" rt JOIN "SalesReturnLines" rl ON rl."Id" = rt."SalesReturnLineId" GROUP BY 1, 2) r
  ON r.line_id = o."SalesInvoiceLineId" AND r.component = COALESCE(o."TaxComponentId"::text, o."ComponentCode")
WHERE l."TaxProfileId" IS NOT NULL AND (r.tax > o."TaxAmount" OR r.base_amount > o."BaseAmount")
  AND NOT EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" lg JOIN "SalesReturnLines" lgl ON lgl."Id" = lg."SalesReturnLineId"
                  WHERE lgl."SalesInvoiceLineId" = l."Id" AND lg."TaxComponentId" IS NULL AND lg."Treatment" IS NULL)
UNION ALL
SELECT 'fully-returned-component-not-exact', 'SalesInvoiceLineTaxes', o."Id"
FROM "SalesInvoiceLineTaxes" o JOIN "SalesInvoiceLines" l ON l."Id" = o."SalesInvoiceLineId"
JOIN (SELECT "SalesInvoiceLineId" AS line_id, SUM("BaseQty") AS base_qty FROM "SalesReturnLines" GROUP BY 1) q ON q.line_id = l."Id"
LEFT JOIN (SELECT rl."SalesInvoiceLineId" AS line_id, COALESCE(rt."TaxComponentId"::text, rt."ComponentCode") AS component, SUM(rt."TaxAmount") AS tax, SUM(rt."BaseAmount") AS base_amount
      FROM "SalesReturnLineTaxes" rt JOIN "SalesReturnLines" rl ON rl."Id" = rt."SalesReturnLineId" GROUP BY 1, 2) r
  ON r.line_id = o."SalesInvoiceLineId" AND r.component = COALESCE(o."TaxComponentId"::text, o."ComponentCode")
WHERE l."TaxProfileId" IS NOT NULL AND q.base_qty = l."BaseQty"
  AND (r.line_id IS NULL OR r.tax <> o."TaxAmount" OR r.base_amount <> o."BaseAmount")
  AND NOT EXISTS (SELECT 1 FROM "SalesReturnLineTaxes" lg JOIN "SalesReturnLines" lgl ON lgl."Id" = lg."SalesReturnLineId"
                  WHERE lgl."SalesInvoiceLineId" = l."Id" AND lg."TaxComponentId" IS NULL AND lg."Treatment" IS NULL);
