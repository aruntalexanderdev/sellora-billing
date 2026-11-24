-- Tax profiles (AddTaxProfiles + ValidateTaxLineForeignKeys): RELEASE-NOTES REPORT. Read-only; INFORMATIVE rows for the owner, not zero-row checks.
-- Never a V__ file: its rows do not clear (legacy profiles are permanent). Run once with psql per environment and attach the output to the release notes:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/sql/tax-profiles-release-notes.sql
-- Section P runs on the OLD schema BEFORE the migration (also enforced by the migration's own preflight); sections R1-R7 run AFTER it.
-- Output: company ids, tax and profile names, rates and counts (no personal data). Names are the company's own tax names.

-- P  (BEFORE) companies with taxes but no country in their business settings: the migration refuses until BusinessSettings.CountryId is set.
SELECT 'P' AS rpt, t."CompanyId", COUNT(*) AS taxes
FROM "Taxes" t
WHERE NOT EXISTS (SELECT 1 FROM "BusinessSettings" b WHERE b."CompanyId" = t."CompanyId" AND b."CountryId" IS NOT NULL)
GROUP BY t."CompanyId" ORDER BY t."CompanyId";

-- P2 (BEFORE) case-insensitive duplicate tax names in one company (the later ones get a numeric suffix on their profile name).
SELECT 'P2' AS rpt, t."CompanyId", lower(t."Name") AS lower_name, COUNT(*) AS taxes
FROM "Taxes" t GROUP BY t."CompanyId", lower(t."Name") HAVING COUNT(*) > 1 ORDER BY 2, 3;

-- R1 (AFTER) zero-rate legacy taxes converted: India -> NilRated, GCC/other -> ZeroRated (deviation e). Review that the treatment is right for each.
SELECT 'R1' AS rpt, p."CompanyId", c."Code" AS country, p."Name", p."Treatment"
FROM "TaxProfiles" p JOIN "Countries" c ON c."Id" = p."CountryId"
WHERE p."Code" LIKE 'LEGACY-%' AND p."Treatment" IN ('NilRated', 'ZeroRated') ORDER BY p."CompanyId", p."Name";

-- R2 (AFTER) standard seeds (T12) that were SKIPPED because a profile or tax with the same code or name already exists, with the existing match.
-- The VALUES list is the same data as TaxSeedData / the frozen seed resource (country, code, name).
SELECT 'R2' AS rpt, b."CompanyId", s.cc AS country, s.code, s.name AS seed_name,
       (SELECT p."Name" FROM "TaxProfiles" p WHERE p."CompanyId" = b."CompanyId" AND p."CountryId" = b."CountryId" AND (p."Code" = s.code OR lower(p."Name") = lower(s.name)) LIMIT 1) AS existing_profile
FROM "BusinessSettings" b
JOIN "Countries" co ON co."Id" = b."CountryId"
JOIN (VALUES
  ('IN','GST-NIL','GST Nil rated'),('IN','GST-EXEMPT','GST Exempt'),('IN','GST-5','GST 5%'),('IN','GST-18','GST 18%'),('IN','GST-40','GST 40%'),
  ('AE','VAT-5','VAT 5%'),('AE','VAT-ZERO','VAT Zero-rated'),('AE','VAT-EXEMPT','VAT Exempt'),
  ('SA','VAT-15','VAT 15%'),('SA','VAT-ZERO','VAT Zero-rated'),('SA','VAT-EXEMPT','VAT Exempt'),
  ('BH','VAT-10','VAT 10%'),('BH','VAT-ZERO','VAT Zero-rated'),('BH','VAT-EXEMPT','VAT Exempt'),
  ('OM','VAT-5','VAT 5%'),('OM','VAT-ZERO','VAT Zero-rated'),('OM','VAT-EXEMPT','VAT Exempt')) s(cc, code, name) ON s.cc = co."Code"
WHERE NOT EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."Id" = md5('sellora:taxprofile:' || b."CompanyId"::text || ':' || s.cc || ':' || s.code)::uuid)
ORDER BY 2, 3, 4;

-- R3 (AFTER) company default chosen per company (T21): which profile, its rate, and where it came from.
-- source: seeded = the standard profile this migration created; existing match = a profile that made the seed skip (check its rate against the standard);
--         fallback = the most-used active Taxable legacy profile (no standard profile exists for the country).
SELECT 'R3' AS rpt, b."CompanyId", co."Code" AS country, p."Code", p."Name", p."IsActive", v."TotalPercent" AS rate,
       CASE WHEN p."Id" = md5('sellora:taxprofile:' || b."CompanyId"::text || ':' || co."Code" || ':' || p."Code")::uuid THEN 'seeded'
            WHEN co."Code" IN ('IN','AE','SA','BH','OM') THEN 'existing match' ELSE 'fallback' END AS source
FROM "BusinessSettings" b
JOIN "Countries" co ON co."Id" = b."CountryId"
LEFT JOIN "TaxAssignments" a ON a."CompanyId" = b."CompanyId" AND a."CountryId" = b."CountryId" AND a."Scope" = 'Company'
LEFT JOIN "TaxProfiles" p ON p."Id" = a."SalesTaxProfileId"
LEFT JOIN LATERAL (SELECT x."TotalPercent" FROM "TaxProfileVersions" x WHERE x."TaxProfileId" = p."Id" AND x."EffectiveTo" IS NULL LIMIT 1) v ON true
ORDER BY b."CompanyId";

-- R3b (AFTER) companies WITHOUT a company default (no standard profile and no used Taxable legacy tax): the owner must pick one in Tax setup.
SELECT 'R3b' AS rpt, b."CompanyId", co."Code" AS country
FROM "BusinessSettings" b JOIN "Countries" co ON co."Id" = b."CountryId"
WHERE NOT EXISTS (SELECT 1 FROM "TaxAssignments" a WHERE a."CompanyId" = b."CompanyId" AND a."CountryId" = b."CountryId" AND a."Scope" = 'Company')
  AND EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."CompanyId" = b."CompanyId")
ORDER BY b."CompanyId";

-- R4 (AFTER) companies with no country: nothing was seeded and they have no default (they cannot have taxes, the migration would have refused).
SELECT 'R4' AS rpt, c."Id" AS "CompanyId", c."Code" AS company_code
FROM "Companies" c
WHERE NOT EXISTS (SELECT 1 FROM "BusinessSettings" b WHERE b."CompanyId" = c."Id" AND b."CountryId" IS NOT NULL) ORDER BY c."Id";

-- R5 (AFTER) before/after counts. Before: Taxes (legacy), Products, lines. After: every Tax has exactly one profile, the seeded mirrors add Taxes rows,
-- every product has an assignment, every line has exactly one tax row.
SELECT 'R5' AS rpt, x.what, x.n FROM (
  SELECT 1 AS o, 'Taxes (legacy, before)' AS what, COUNT(*) AS n FROM "Taxes" t WHERE EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."LegacyTaxId" = t."Id" AND p."Code" LIKE 'LEGACY-%')
  UNION ALL SELECT 2, 'Taxes (mirrors of seeded profiles)', COUNT(*) FROM "Taxes" t WHERE EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."LegacyTaxId" = t."Id" AND p."Code" NOT LIKE 'LEGACY-%')
  UNION ALL SELECT 3, 'TaxProfiles', COUNT(*) FROM "TaxProfiles"
  UNION ALL SELECT 4, 'TaxProfileVersions', COUNT(*) FROM "TaxProfileVersions"
  UNION ALL SELECT 5, 'TaxProfileVersionComponents', COUNT(*) FROM "TaxProfileVersionComponents"
  UNION ALL SELECT 6, 'TaxComponents', COUNT(*) FROM "TaxComponents"
  UNION ALL SELECT 7, 'Products', COUNT(*) FROM "Products"
  UNION ALL SELECT 8, 'Product assignments', COUNT(*) FROM "TaxAssignments" WHERE "Scope" = 'Product'
  UNION ALL SELECT 9, 'Company default assignments', COUNT(*) FROM "TaxAssignments" WHERE "Scope" = 'Company'
  UNION ALL SELECT 10, 'SalesInvoiceLines / tax rows', (SELECT COUNT(*) FROM "SalesInvoiceLines") * 1000000 + (SELECT COUNT(*) FROM "SalesInvoiceLineTaxes")
  UNION ALL SELECT 11, 'PurchaseOrderLines / tax rows', (SELECT COUNT(*) FROM "PurchaseOrderLines") * 1000000 + (SELECT COUNT(*) FROM "PurchaseOrderLineTaxes")
  UNION ALL SELECT 12, 'SalesReturnLines / tax rows', (SELECT COUNT(*) FROM "SalesReturnLines") * 1000000 + (SELECT COUNT(*) FROM "SalesReturnLineTaxes")
  UNION ALL SELECT 13, 'PurchaseReturnLines / tax rows', (SELECT COUNT(*) FROM "PurchaseReturnLines") * 1000000 + (SELECT COUNT(*) FROM "PurchaseReturnLineTaxes")
) x ORDER BY x.o;
-- Rows 10-13 read as lines*1000000 + rows: the two numbers must be equal (for example 4000004 = 4 lines, 4 rows).

-- R6 (AFTER) legacy profiles whose name had to change (case-insensitive duplicate tax names) and legacy taxes that are inactive.
SELECT 'R6' AS rpt, p."CompanyId", t."Name" AS tax_name, p."Name" AS profile_name, p."IsActive"
FROM "TaxProfiles" p JOIN "Taxes" t ON t."Id" = p."LegacyTaxId"
WHERE p."Code" LIKE 'LEGACY-%' AND (p."Name" <> t."Name" OR NOT p."IsActive") ORDER BY p."CompanyId", t."Name";

-- R7 (AFTER) a company default whose rate differs from the standard rate of its country (a tax with the standard name but another rate made the seed skip).
SELECT 'R7' AS rpt, b."CompanyId", co."Code" AS country, p."Name", v."TotalPercent" AS rate, d.rate AS standard_rate
FROM "BusinessSettings" b
JOIN "Countries" co ON co."Id" = b."CountryId"
JOIN (VALUES ('IN', 18), ('AE', 5), ('SA', 15), ('BH', 10), ('OM', 5)) d(cc, rate) ON d.cc = co."Code"
JOIN "TaxAssignments" a ON a."CompanyId" = b."CompanyId" AND a."CountryId" = b."CountryId" AND a."Scope" = 'Company'
JOIN "TaxProfiles" p ON p."Id" = a."SalesTaxProfileId"
JOIN "TaxProfileVersions" v ON v."TaxProfileId" = p."Id" AND v."EffectiveTo" IS NULL
WHERE v."TotalPercent" <> d.rate ORDER BY b."CompanyId";
