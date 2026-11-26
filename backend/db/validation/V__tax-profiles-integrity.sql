-- sellora:description Tax profiles: versions never overlap, every profile has one open version, components fit the treatment and country, every profile has its mirror tax
-- sellora:on-violation block
-- Design D5/D6 (docs/features/tax-profiles.md): no exclusion constraint for overlap, so this check is the safety net behind the append-only service rule.
-- Output: check label and ids only. Must return no rows.
SELECT 'version overlap' AS chk, a."TaxProfileId" AS "ProfileId", a."Id" AS "VersionId"
FROM "TaxProfileVersions" a
JOIN "TaxProfileVersions" b ON b."TaxProfileId" = a."TaxProfileId" AND b."Id" <> a."Id"
 AND a."EffectiveFrom" <= COALESCE(b."EffectiveTo", date 'infinity') AND b."EffectiveFrom" <= COALESCE(a."EffectiveTo", date 'infinity')
UNION ALL
SELECT 'profile without open version', p."Id", NULL::uuid
FROM "TaxProfiles" p
WHERE NOT EXISTS (SELECT 1 FROM "TaxProfileVersions" v WHERE v."TaxProfileId" = p."Id" AND v."EffectiveTo" IS NULL)
UNION ALL
SELECT 'taxable version without components', v."TaxProfileId", v."Id"
FROM "TaxProfileVersions" v JOIN "TaxProfiles" p ON p."Id" = v."TaxProfileId"
WHERE p."Treatment" = 'Taxable' AND NOT EXISTS (SELECT 1 FROM "TaxProfileVersionComponents" c WHERE c."TaxProfileVersionId" = v."Id")
UNION ALL
SELECT 'non-taxable version with components', v."TaxProfileId", v."Id"
FROM "TaxProfileVersions" v JOIN "TaxProfiles" p ON p."Id" = v."TaxProfileId"
WHERE p."Treatment" <> 'Taxable' AND EXISTS (SELECT 1 FROM "TaxProfileVersionComponents" c WHERE c."TaxProfileVersionId" = v."Id")
UNION ALL
SELECT 'component country differs from profile country', v."TaxProfileId", c."Id"
FROM "TaxProfileVersionComponents" c
JOIN "TaxProfileVersions" v ON v."Id" = c."TaxProfileVersionId"
JOIN "TaxProfiles" p ON p."Id" = v."TaxProfileId"
JOIN "TaxComponents" k ON k."Id" = c."TaxComponentId"
WHERE k."CountryId" <> p."CountryId"
UNION ALL
SELECT 'profile without mirror tax', p."Id", NULL::uuid
FROM "TaxProfiles" p
WHERE NOT EXISTS (SELECT 1 FROM "Taxes" t WHERE t."Id" = p."LegacyTaxId" AND t."CompanyId" = p."CompanyId");
