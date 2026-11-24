-- tax-profiles backfill (docs/features/tax-profiles.md, TP-1 design, migration step 2; T10, T21, AC17). FROZEN: part of migration AddTaxProfiles; a test pins its SHA-256.
-- Runs AFTER the .seed.sql resource (components and standard profiles exist). Must run in ONE transaction (ON COMMIT DROP temp tables); to re-run by hand wrap in BEGIN/COMMIT.
-- Idempotent (the legacy set excludes taxes that already have a profile; the profile, version and component inserts carry no ON CONFLICT, so a clash fails the release). Legacy taxes are only READ and never modified: Taxes, Products.TaxId and every line TaxId stay exactly as they are.
-- The text must not contain the dollar tag the EF idempotent script uses to wrap SQL; this file uses the p-tag.

-- P1 preflight (same check as the seed resource, so this file is safe on its own).
DO $p$
DECLARE bad text;
BEGIN
  SELECT string_agg(t."CompanyId"::text, ', ' ORDER BY t."CompanyId") INTO bad
  FROM (SELECT DISTINCT "CompanyId" FROM "Taxes") t
  WHERE NOT EXISTS (SELECT 1 FROM "BusinessSettings" b WHERE b."CompanyId" = t."CompanyId" AND b."CountryId" IS NOT NULL);
  IF bad IS NOT NULL THEN
    RAISE EXCEPTION 'AddTaxProfiles preflight failed: these companies have taxes but no country in their business settings (set BusinessSettings.CountryId first): %', bad;
  END IF;
END $p$;

-- 1. One legacy profile + one version from 2000-01-01 per Taxes row that has no profile yet.
--    India: CGST/SGST halves intra-state, IGST inter-state; every other country: one VAT line. Rate 0: India NilRated, others ZeroRated (deviation e), no components.
--    Two taxes whose names differ only by case get a numeric suffix on the later ones (profile names are unique per company and country, case-insensitive).
CREATE TEMP TABLE _taxfill_legacy ON COMMIT DROP AS
SELECT t."Id" AS tax_id, t."CompanyId" AS company_id, b."CountryId" AS country_id, c."Code" AS cc, t."Name" AS name,
       t."RatePercent" AS rate, t."IsActive" AS is_active,
       row_number() OVER (PARTITION BY t."CompanyId", lower(t."Name") ORDER BY t."Id") AS rn,
       md5('sellora:taxprofile:legacy:' || t."Id"::text)::uuid AS profile_id,
       md5('sellora:taxversion:legacy:' || t."Id"::text)::uuid AS version_id
FROM "Taxes" t
JOIN "BusinessSettings" b ON b."CompanyId" = t."CompanyId"
JOIN "Countries" c ON c."Id" = b."CountryId"
WHERE NOT EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."CompanyId" = t."CompanyId" AND p."LegacyTaxId" = t."Id");

INSERT INTO "TaxProfiles" ("Id", "CompanyId", "CountryId", "Code", "Name", "Regime", "Treatment", "LegacyTaxId", "IsActive", "CreatedAt")
SELECT l.profile_id, l.company_id, l.country_id, 'LEGACY-' || upper(right(replace(l.tax_id::text, '-', ''), 12)),
       CASE WHEN l.rn = 1 THEN l.name ELSE left(l.name, 55) || ' (' || l.rn::text || ')' END,
       CASE WHEN l.cc = 'IN' THEN 'Gst' ELSE 'Vat' END,
       CASE WHEN l.rate > 0 THEN 'Taxable' WHEN l.cc = 'IN' THEN 'NilRated' ELSE 'ZeroRated' END,
       l.tax_id, l.is_active, now()
FROM _taxfill_legacy l;

INSERT INTO "TaxProfileVersions" ("Id", "CompanyId", "TaxProfileId", "EffectiveFrom", "EffectiveTo", "TotalPercent", "CreatedAt", "CreatedByUserId")
SELECT l.version_id, l.company_id, l.profile_id, date '2000-01-01', NULL, CASE WHEN l.rate > 0 THEN l.rate ELSE 0 END, now(), NULL
FROM _taxfill_legacy l;

INSERT INTO "TaxProfileVersionComponents" ("Id", "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Method", "Applicability", "RatePercent", "AmountPerBaseUnit", "SortOrder")
SELECT md5('sellora:taxvc:' || l.version_id::text || ':' || x.code || ':' || x.app)::uuid, l.company_id, l.version_id,
       md5('sellora:taxcomponent:' || l.company_id::text || ':' || l.cc || ':' || x.code)::uuid, 'Percent', x.app,
       CASE x.part WHEN 'half' THEN l.rate / 2 ELSE l.rate END, NULL, x.sort_order
FROM _taxfill_legacy l
JOIN (VALUES ('IN', 'CGST', 'IntraState', 'half', 1), ('IN', 'SGST', 'IntraState', 'half', 2), ('IN', 'IGST', 'InterState', 'full', 3),
             ('*', 'VAT', 'Always', 'full', 1)) x(cc, code, app, part, sort_order)
  ON x.cc = l.cc OR (x.cc = '*' AND l.cc <> 'IN')
WHERE l.rate > 0;

-- 2. AC17: every product gets a product-level assignment to the profile of its tax, for the company's country (purchase profile NULL = same as sales).
INSERT INTO "TaxAssignments" ("Id", "CompanyId", "Scope", "CountryId", "CategoryId", "SubcategoryId", "ProductId", "SalesTaxProfileId", "PurchaseTaxProfileId", "CreatedAt", "UpdatedAt")
SELECT md5('sellora:taxassignment:' || pr."CompanyId"::text || ':' || c."Code" || ':product:' || pr."Id"::text)::uuid, pr."CompanyId", 'Product', p."CountryId",
       NULL, NULL, pr."Id", p."Id", NULL, now(), NULL
FROM "Products" pr
JOIN "TaxProfiles" p ON p."CompanyId" = pr."CompanyId" AND p."LegacyTaxId" = pr."TaxId"
JOIN "Countries" c ON c."Id" = p."CountryId"
ON CONFLICT DO NOTHING;

-- 3. T21: the company default is the standard-rate profile (the seeded one, or the existing profile that made the seed skip, by code first then by name);
--    only when there is none, the most-used active Taxable legacy profile (products using its tax; ties by name, id); otherwise no default.
CREATE TEMP TABLE _taxfill_standard(cc text, code text, name text) ON COMMIT DROP;
INSERT INTO _taxfill_standard(cc, code, name) VALUES
  ('IN', 'GST-18', 'GST 18%'),
  ('AE', 'VAT-5', 'VAT 5%'),
  ('SA', 'VAT-15', 'VAT 15%'),
  ('BH', 'VAT-10', 'VAT 10%'),
  ('OM', 'VAT-5', 'VAT 5%');

INSERT INTO "TaxAssignments" ("Id", "CompanyId", "Scope", "CountryId", "CategoryId", "SubcategoryId", "ProductId", "SalesTaxProfileId", "PurchaseTaxProfileId", "CreatedAt", "UpdatedAt")
SELECT md5('sellora:taxassignment:' || c.company_id::text || ':' || c.cc || ':company')::uuid, c.company_id, 'Company', c.country_id, NULL, NULL, NULL, chosen.profile_id, NULL, now(), NULL
FROM (SELECT b."CompanyId" AS company_id, b."CountryId" AS country_id, co."Code" AS cc
      FROM "BusinessSettings" b JOIN "Countries" co ON co."Id" = b."CountryId") c
CROSS JOIN LATERAL (
  SELECT COALESCE(
    (SELECT p."Id" FROM "TaxProfiles" p JOIN _taxfill_standard s ON s.cc = c.cc
      WHERE p."CompanyId" = c.company_id AND p."CountryId" = c.country_id AND (p."Code" = s.code OR lower(p."Name") = lower(s.name))
      ORDER BY (p."Code" = s.code) DESC, p."Id" LIMIT 1),
    (SELECT p."Id" FROM "TaxProfiles" p
      JOIN "Products" pr ON pr."CompanyId" = p."CompanyId" AND pr."TaxId" = p."LegacyTaxId"
      WHERE p."CompanyId" = c.company_id AND p."CountryId" = c.country_id AND p."IsActive" AND p."Treatment" = 'Taxable'
      GROUP BY p."Id", p."Name" ORDER BY count(pr."Id") DESC, p."Name", p."Id" LIMIT 1)
  ) AS profile_id
) chosen
WHERE chosen.profile_id IS NOT NULL
ON CONFLICT DO NOTHING;
