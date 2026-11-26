-- sellora:description GST tax profiles: complete CGST + SGST/UTGST and IGST set, equal halves, IGST = CGST + SGST, no VAT component, TotalPercent = intra-state percent sum
-- sellora:on-violation block
-- BR3/D6 (docs/features/tax-profiles.md). Percent components only (a per-unit cess never counts toward the rate). Output: check label and ids only.
WITH v AS (
  SELECT ver."Id" AS version_id, ver."TaxProfileId" AS profile_id, ver."TotalPercent", p."Regime", p."Treatment",
    COUNT(*) FILTER (WHERE k."Kind" = 'Cgst' AND c."Applicability" = 'IntraState') AS n_cgst,
    COUNT(*) FILTER (WHERE k."Kind" IN ('Sgst', 'Utgst') AND c."Applicability" = 'IntraState') AS n_sgst,
    COUNT(*) FILTER (WHERE k."Kind" = 'Igst' AND c."Applicability" = 'InterState') AS n_igst,
    COUNT(*) FILTER (WHERE k."Kind" = 'Vat') AS n_vat,
    COALESCE(SUM(c."RatePercent") FILTER (WHERE k."Kind" = 'Cgst' AND c."Applicability" = 'IntraState'), 0) AS cgst,
    COALESCE(SUM(c."RatePercent") FILTER (WHERE k."Kind" IN ('Sgst', 'Utgst') AND c."Applicability" = 'IntraState'), 0) AS sgst,
    COALESCE(SUM(c."RatePercent") FILTER (WHERE k."Kind" = 'Igst' AND c."Applicability" = 'InterState'), 0) AS igst,
    COALESCE(SUM(c."RatePercent") FILTER (WHERE c."Method" = 'Percent' AND c."Applicability" IN ('IntraState', 'Always')), 0) AS intra_total
  FROM "TaxProfileVersions" ver
  JOIN "TaxProfiles" p ON p."Id" = ver."TaxProfileId" AND p."Regime" = 'Gst'
  LEFT JOIN "TaxProfileVersionComponents" c ON c."TaxProfileVersionId" = ver."Id"
  LEFT JOIN "TaxComponents" k ON k."Id" = c."TaxComponentId"
  GROUP BY ver."Id", ver."TaxProfileId", ver."TotalPercent", p."Regime", p."Treatment"
)
SELECT 'GST taxable version missing CGST, SGST/UTGST or IGST' AS chk, v.profile_id AS "ProfileId", v.version_id AS "VersionId"
FROM v WHERE v."Treatment" = 'Taxable' AND (v.n_cgst = 0 OR v.n_sgst = 0 OR v.n_igst = 0)
UNION ALL
SELECT 'CGST differs from SGST/UTGST', v.profile_id, v.version_id
FROM v WHERE v."Treatment" = 'Taxable' AND v.n_cgst > 0 AND v.n_sgst > 0 AND v.cgst <> v.sgst
UNION ALL
SELECT 'IGST differs from CGST + SGST/UTGST', v.profile_id, v.version_id
FROM v WHERE v."Treatment" = 'Taxable' AND v.n_igst > 0 AND v.igst <> v.cgst + v.sgst
UNION ALL
SELECT 'VAT component in a GST profile', v.profile_id, v.version_id
FROM v WHERE v.n_vat > 0
UNION ALL
SELECT 'TotalPercent differs from the percent components', v.profile_id, v.version_id
FROM v WHERE v."TotalPercent" <> v.intra_total;
