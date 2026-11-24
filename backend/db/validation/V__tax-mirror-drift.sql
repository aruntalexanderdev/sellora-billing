-- sellora:description Mirror tax rate or active flag differs from the profile's current version (a future-dated version makes the mirror lag until its date)
-- sellora:on-violation warn
-- D3 (docs/features/tax-profiles.md): Taxes.RatePercent = TotalPercent of the version valid today, IsActive = the profile's. Output: profile id and reason only.
SELECT p."Id" AS "ProfileId",
       CASE WHEN v."Id" IS NULL THEN 'no version valid today'
            WHEN t."RatePercent" <> v."TotalPercent" THEN 'mirror rate differs from the current version'
            ELSE 'mirror IsActive differs from the profile' END AS reason
FROM "TaxProfiles" p
JOIN "Taxes" t ON t."Id" = p."LegacyTaxId" AND t."CompanyId" = p."CompanyId"
LEFT JOIN "TaxProfileVersions" v ON v."TaxProfileId" = p."Id" AND v."EffectiveFrom" <= current_date AND (v."EffectiveTo" IS NULL OR v."EffectiveTo" >= current_date)
WHERE v."Id" IS NULL OR t."RatePercent" <> v."TotalPercent" OR t."IsActive" <> p."IsActive";
