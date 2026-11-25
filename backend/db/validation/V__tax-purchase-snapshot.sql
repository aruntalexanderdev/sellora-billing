-- sellora:description Purchase tax snapshots (TP-3): profile/version/company, mirror tax, line and header arithmetic, treatment on rows, place-of-supply component rules, returns carry the original's version
-- sellora:on-violation block
-- docs/features/tax-profiles.md, TP-3 design. Only documents saved by the TP-3 build (line TaxProfileVersionId set, header TaxableTotal set; TP-5a: history lines may carry a backfilled profile with no version) are checked, history is never touched.
-- Output: check label, table and row id only. Header money columns are numeric(18,2) while lines are numeric(18,4), so header-vs-line comparisons allow half a cent.
SELECT 'version-not-of-profile-or-company' AS chk, 'PurchaseOrderLines' AS tbl, l."Id" AS "RowId"
FROM "PurchaseOrderLines" l JOIN "PurchaseOrders" o ON o."Id" = l."PurchaseOrderId"
JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" JOIN "TaxProfileVersions" v ON v."Id" = l."TaxProfileVersionId"
WHERE v."TaxProfileId" <> p."Id" OR p."CompanyId" <> o."CompanyId" OR v."CompanyId" <> o."CompanyId"
UNION ALL
SELECT 'version-not-of-profile-or-company', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l JOIN "PurchaseReturns" r ON r."Id" = l."PurchaseReturnId"
JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" JOIN "TaxProfileVersions" v ON v."Id" = l."TaxProfileVersionId"
WHERE v."TaxProfileId" <> p."Id" OR p."CompanyId" <> r."CompanyId" OR v."CompanyId" <> r."CompanyId"
UNION ALL
-- the line's TaxId is the purchase profile's mirror tax (keeps the TP-2 build and the T25 "used version" check working)
SELECT 'taxid-not-profile-mirror', 'PurchaseOrderLines', l."Id"
FROM "PurchaseOrderLines" l JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId"
WHERE l."TaxId" <> p."LegacyTaxId"
UNION ALL
SELECT 'line-total-not-taxable-plus-tax', 'PurchaseOrderLines', l."Id"
FROM "PurchaseOrderLines" l WHERE l."TaxProfileVersionId" IS NOT NULL AND l."LineTotal" <> l."TaxableValue" + l."TaxAmount"
UNION ALL
SELECT 'line-total-not-taxable-plus-tax', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l WHERE l."TaxProfileVersionId" IS NOT NULL AND l."LineTotal" <> l."TaxableValue" + l."TaxAmount"
UNION ALL
-- every row written by the calculator has a Treatment (NULL marks a legacy row only, which has no component either)
SELECT 'row-without-treatment', 'PurchaseOrderLineTaxes', x."Id"
FROM "PurchaseOrderLineTaxes" x JOIN "PurchaseOrderLines" l ON l."Id" = x."PurchaseOrderLineId"
WHERE x."Treatment" IS NULL AND (x."TaxComponentId" IS NOT NULL OR l."TaxProfileVersionId" IS NOT NULL)
UNION ALL
SELECT 'row-without-treatment', 'PurchaseReturnLineTaxes', x."Id"
FROM "PurchaseReturnLineTaxes" x JOIN "PurchaseReturnLines" l ON l."Id" = x."PurchaseReturnLineId"
WHERE x."Treatment" IS NULL AND (x."TaxComponentId" IS NOT NULL OR l."TaxProfileVersionId" IS NOT NULL)
UNION ALL
-- intra-state lines have no IGST row, inter-state lines no CGST/SGST row
SELECT 'intra-line-has-igst', 'PurchaseOrderLines', l."Id"
FROM "PurchaseOrderLines" l WHERE l."PlaceOfSupply" = 'IntraState' AND EXISTS (SELECT 1 FROM "PurchaseOrderLineTaxes" x WHERE x."PurchaseOrderLineId" = l."Id" AND x."Kind" = 'Igst')
UNION ALL
SELECT 'inter-line-has-cgst-sgst', 'PurchaseOrderLines', l."Id"
FROM "PurchaseOrderLines" l WHERE l."PlaceOfSupply" = 'InterState' AND EXISTS (SELECT 1 FROM "PurchaseOrderLineTaxes" x WHERE x."PurchaseOrderLineId" = l."Id" AND x."Kind" IN ('Cgst', 'Sgst'))
UNION ALL
SELECT 'intra-line-has-igst', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l WHERE l."PlaceOfSupply" = 'IntraState' AND EXISTS (SELECT 1 FROM "PurchaseReturnLineTaxes" x WHERE x."PurchaseReturnLineId" = l."Id" AND x."Kind" = 'Igst')
UNION ALL
SELECT 'inter-line-has-cgst-sgst', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l WHERE l."PlaceOfSupply" = 'InterState' AND EXISTS (SELECT 1 FROM "PurchaseReturnLineTaxes" x WHERE x."PurchaseReturnLineId" = l."Id" AND x."Kind" IN ('Cgst', 'Sgst'))
UNION ALL
-- CGST = SGST per line (the two halves of a GST rate)
SELECT 'cgst-not-equal-sgst', 'PurchaseOrderLines', l."Id"
FROM "PurchaseOrderLines" l
JOIN (SELECT "PurchaseOrderLineId" AS line_id, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Cgst'), 0) AS cgst, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Sgst'), 0) AS sgst
      FROM "PurchaseOrderLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE l."TaxProfileVersionId" IS NOT NULL AND x.cgst <> x.sgst
UNION ALL
SELECT 'cgst-not-equal-sgst', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l
JOIN (SELECT "PurchaseReturnLineId" AS line_id, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Cgst'), 0) AS cgst, COALESCE(SUM("TaxAmount") FILTER (WHERE "Kind" = 'Sgst'), 0) AS sgst
      FROM "PurchaseReturnLineTaxes" GROUP BY 1) x ON x.line_id = l."Id"
WHERE l."TaxProfileVersionId" IS NOT NULL AND x.cgst <> x.sgst
UNION ALL
-- header sums equal line sums (TP-3 purchases only: TaxableTotal is NULL on history)
SELECT 'header-taxable-not-sum-of-lines', 'PurchaseOrders', o."Id"
FROM "PurchaseOrders" o JOIN (SELECT "PurchaseOrderId" AS id, SUM("TaxableValue") AS taxable FROM "PurchaseOrderLines" GROUP BY 1) s ON s.id = o."Id"
WHERE o."TaxableTotal" IS NOT NULL AND o."TaxableTotal" <> s.taxable
UNION ALL
SELECT 'header-tax-not-sum-of-lines', 'PurchaseOrders', o."Id"
FROM "PurchaseOrders" o JOIN (SELECT "PurchaseOrderId" AS id, SUM("TaxAmount") AS tax FROM "PurchaseOrderLines" GROUP BY 1) s ON s.id = o."Id"
WHERE o."TaxableTotal" IS NOT NULL AND abs(o."TaxTotal" - s.tax) > 0.005
UNION ALL
SELECT 'header-grand-total-not-sum-of-lines', 'PurchaseOrders', o."Id"
FROM "PurchaseOrders" o JOIN (SELECT "PurchaseOrderId" AS id, SUM("LineTotal") AS total FROM "PurchaseOrderLines" GROUP BY 1) s ON s.id = o."Id"
WHERE o."TaxableTotal" IS NOT NULL AND abs(o."GrandTotal" - s.total) > 0.005
UNION ALL
SELECT 'header-original-grand-total-not-sum-of-lines', 'PurchaseOrders', o."Id"
FROM "PurchaseOrders" o JOIN (SELECT "PurchaseOrderId" AS id, SUM("OriginalLineTotal") AS total, COUNT(*) FILTER (WHERE "OriginalLineTotal" IS NULL) AS missing FROM "PurchaseOrderLines" GROUP BY 1) s ON s.id = o."Id"
WHERE o."TaxableTotal" IS NOT NULL AND (s.missing > 0 OR o."OriginalGrandTotal" <> s.total)
UNION ALL
-- a return line carries the version, profile and place of supply of the purchase line it returns
SELECT 'return-line-version-differs-from-original', 'PurchaseReturnLines', l."Id"
FROM "PurchaseReturnLines" l JOIN "PurchaseOrderLines" o ON o."Id" = l."PurchaseOrderLineId"
WHERE o."TaxProfileVersionId" IS NOT NULL
  AND (l."TaxProfileId" IS DISTINCT FROM o."TaxProfileId" OR l."TaxProfileVersionId" IS DISTINCT FROM o."TaxProfileVersionId" OR l."PlaceOfSupply" IS DISTINCT FROM o."PlaceOfSupply");
