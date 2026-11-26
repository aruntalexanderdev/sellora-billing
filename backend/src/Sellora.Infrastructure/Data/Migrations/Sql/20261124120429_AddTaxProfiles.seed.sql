-- tax-profiles seed (docs/features/tax-profiles.md, TP-1 design, migration step 2). FROZEN: part of migration AddTaxProfiles; a test pins its SHA-256.
-- Twin of Sellora.Infrastructure.Data.TaxSeedData: same rows, same md5 id formulas (a Postgres drift test compares the two).
-- Must run in ONE transaction (ON COMMIT DROP temp tables): the migration never sets suppressTransaction. To re-run by hand wrap in BEGIN/COMMIT (or psql -1).
-- Idempotent: components are ON CONFLICT DO NOTHING; the profile rows carry no ON CONFLICT (a clash is a real error and fails the release); a standard profile is added only when the company has no profile of that country with the
-- same code or the same name (case-insensitive) and no Taxes row of the same name (case-insensitive). Skipped seeds are listed by scripts/sql/tax-profiles-release-notes.sql.
-- The text must not contain the dollar tag the EF idempotent script uses to wrap SQL; this file uses the p-tag.

-- P1 preflight: a company with taxes but no country cannot be mapped (regime and components come from the country).
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

CREATE TEMP TABLE _taxseed_company ON COMMIT DROP AS
SELECT b."CompanyId" AS company_id, b."CountryId" AS country_id, c."Code" AS cc,
       EXISTS (SELECT 1 FROM "Taxes" t WHERE t."CompanyId" = b."CompanyId") AS has_taxes
FROM "BusinessSettings" b JOIN "Countries" c ON c."Id" = b."CountryId";

CREATE TEMP TABLE _taxseed_component(cc text, code text, name text, kind text, method text) ON COMMIT DROP;
INSERT INTO _taxseed_component(cc, code, name, kind, method) VALUES
  ('IN','CGST','Central GST','Cgst','Percent'),
  ('IN','SGST','State GST','Sgst','Percent'),
  ('IN','UTGST','Union territory GST','Utgst','Percent'),
  ('IN','IGST','Integrated GST','Igst','Percent'),
  ('IN','CESS','Cess','Cess','Percent'),
  ('IN','CESS-UNIT','Cess per unit','Cess','AmountPerBaseUnit'),
  ('AE','VAT','Value added tax','Vat','Percent'),
  ('SA','VAT','Value added tax','Vat','Percent'),
  ('BH','VAT','Value added tax','Vat','Percent'),
  ('OM','VAT','Value added tax','Vat','Percent');

CREATE TEMP TABLE _taxseed_profile(cc text, code text, name text, regime text, treatment text, eff_from date, total numeric) ON COMMIT DROP;
INSERT INTO _taxseed_profile(cc, code, name, regime, treatment, eff_from, total) VALUES
  ('IN','GST-NIL','GST Nil rated','Gst','NilRated',date '2017-07-01',0),
  ('IN','GST-EXEMPT','GST Exempt','Gst','Exempt',date '2017-07-01',0),
  ('IN','GST-5','GST 5%','Gst','Taxable',date '2017-07-01',5),
  ('IN','GST-18','GST 18%','Gst','Taxable',date '2017-07-01',18),
  ('IN','GST-40','GST 40%','Gst','Taxable',date '2025-09-22',40),
  ('AE','VAT-5','VAT 5%','Vat','Taxable',date '2018-01-01',5),
  ('AE','VAT-ZERO','VAT Zero-rated','Vat','ZeroRated',date '2018-01-01',0),
  ('AE','VAT-EXEMPT','VAT Exempt','Vat','Exempt',date '2018-01-01',0),
  ('SA','VAT-15','VAT 15%','Vat','Taxable',date '2020-07-01',15),
  ('SA','VAT-ZERO','VAT Zero-rated','Vat','ZeroRated',date '2020-07-01',0),
  ('SA','VAT-EXEMPT','VAT Exempt','Vat','Exempt',date '2020-07-01',0),
  ('BH','VAT-10','VAT 10%','Vat','Taxable',date '2022-01-01',10),
  ('BH','VAT-ZERO','VAT Zero-rated','Vat','ZeroRated',date '2022-01-01',0),
  ('BH','VAT-EXEMPT','VAT Exempt','Vat','Exempt',date '2022-01-01',0),
  ('OM','VAT-5','VAT 5%','Vat','Taxable',date '2021-04-16',5),
  ('OM','VAT-ZERO','VAT Zero-rated','Vat','ZeroRated',date '2021-04-16',0),
  ('OM','VAT-EXEMPT','VAT Exempt','Vat','Exempt',date '2021-04-16',0);

CREATE TEMP TABLE _taxseed_vc(cc text, profile_code text, comp_code text, applicability text, rate numeric, sort_order int) ON COMMIT DROP;
INSERT INTO _taxseed_vc(cc, profile_code, comp_code, applicability, rate, sort_order) VALUES
  ('IN','GST-5','CGST','IntraState',2.5,1),
  ('IN','GST-5','SGST','IntraState',2.5,2),
  ('IN','GST-5','IGST','InterState',5,3),
  ('IN','GST-18','CGST','IntraState',9,1),
  ('IN','GST-18','SGST','IntraState',9,2),
  ('IN','GST-18','IGST','InterState',18,3),
  ('IN','GST-40','CGST','IntraState',20,1),
  ('IN','GST-40','SGST','IntraState',20,2),
  ('IN','GST-40','IGST','InterState',40,3),
  ('AE','VAT-5','VAT','Always',5,1),
  ('SA','VAT-15','VAT','Always',15,1),
  ('BH','VAT-10','VAT','Always',10,1),
  ('OM','VAT-5','VAT','Always',5,1);

-- Components per company by country (standard set).
INSERT INTO "TaxComponents" ("Id", "CompanyId", "CountryId", "Code", "Name", "Kind", "Method", "IsActive", "CreatedAt")
SELECT md5('sellora:taxcomponent:' || c.company_id::text || ':' || c.cc || ':' || s.code)::uuid, c.company_id, c.country_id, s.code, s.name, s.kind, s.method, true, now()
FROM _taxseed_company c JOIN _taxseed_component s ON s.cc = c.cc
ON CONFLICT DO NOTHING;

-- Legacy taxes of a company in a country without a standard seed (for example Qatar, Kuwait) map to one VAT component.
INSERT INTO "TaxComponents" ("Id", "CompanyId", "CountryId", "Code", "Name", "Kind", "Method", "IsActive", "CreatedAt")
SELECT md5('sellora:taxcomponent:' || c.company_id::text || ':' || c.cc || ':VAT')::uuid, c.company_id, c.country_id, 'VAT', 'Value added tax', 'Vat', 'Percent', true, now()
FROM _taxseed_company c
WHERE c.has_taxes AND c.cc NOT IN ('IN', 'AE', 'SA', 'BH', 'OM')
ON CONFLICT DO NOTHING;

-- The profiles to add, decided once before any insert (the mirror taxes inserted below would otherwise hide their own profiles).
CREATE TEMP TABLE _taxseed_todo ON COMMIT DROP AS
SELECT c.company_id, c.country_id, c.cc, s.code, s.name, s.regime, s.treatment, s.eff_from, s.total
FROM _taxseed_company c JOIN _taxseed_profile s ON s.cc = c.cc
WHERE NOT EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."CompanyId" = c.company_id AND p."CountryId" = c.country_id
                    AND (p."Code" = s.code OR lower(p."Name") = lower(s.name)))
  AND NOT EXISTS (SELECT 1 FROM "Taxes" t WHERE t."CompanyId" = c.company_id AND lower(t."Name") = lower(s.name));

-- Mirror tax of every seeded profile (D3), then profile, version and version components.
INSERT INTO "Taxes" ("Id", "CompanyId", "Name", "RatePercent", "IsActive")
SELECT md5('sellora:taxmirror:' || d.company_id::text || ':' || d.cc || ':' || d.code)::uuid, d.company_id, d.name, d.total, true
FROM _taxseed_todo d;

INSERT INTO "TaxProfiles" ("Id", "CompanyId", "CountryId", "Code", "Name", "Regime", "Treatment", "LegacyTaxId", "IsActive", "CreatedAt")
SELECT md5('sellora:taxprofile:' || d.company_id::text || ':' || d.cc || ':' || d.code)::uuid, d.company_id, d.country_id, d.code, d.name, d.regime, d.treatment,
       md5('sellora:taxmirror:' || d.company_id::text || ':' || d.cc || ':' || d.code)::uuid, true, now()
FROM _taxseed_todo d;

INSERT INTO "TaxProfileVersions" ("Id", "CompanyId", "TaxProfileId", "EffectiveFrom", "EffectiveTo", "TotalPercent", "CreatedAt", "CreatedByUserId")
SELECT md5('sellora:taxversion:' || d.company_id::text || ':' || d.cc || ':' || d.code || ':' || to_char(d.eff_from, 'YYYY-MM-DD'))::uuid, d.company_id,
       md5('sellora:taxprofile:' || d.company_id::text || ':' || d.cc || ':' || d.code)::uuid, d.eff_from, NULL, d.total, now(), NULL
FROM _taxseed_todo d;

INSERT INTO "TaxProfileVersionComponents" ("Id", "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Method", "Applicability", "RatePercent", "AmountPerBaseUnit", "SortOrder")
SELECT md5('sellora:taxvc:' || v.version_id::text || ':' || vc.comp_code || ':' || vc.applicability)::uuid, d.company_id, v.version_id,
       md5('sellora:taxcomponent:' || d.company_id::text || ':' || d.cc || ':' || vc.comp_code)::uuid, sc.method, vc.applicability, vc.rate, NULL, vc.sort_order
FROM _taxseed_todo d
JOIN _taxseed_vc vc ON vc.cc = d.cc AND vc.profile_code = d.code
JOIN _taxseed_component sc ON sc.cc = d.cc AND sc.code = vc.comp_code
CROSS JOIN LATERAL (SELECT md5('sellora:taxversion:' || d.company_id::text || ':' || d.cc || ':' || d.code || ':' || to_char(d.eff_from, 'YYYY-MM-DD'))::uuid AS version_id) v;
