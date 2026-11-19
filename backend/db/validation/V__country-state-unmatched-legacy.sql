-- sellora:description Branches/BusinessSettings whose legacy State text could not be linked (ids only)
-- sellora:on-violation warn
SELECT 'Branches' AS t, "Id", substr("Gstin",1,2) AS gst_prefix FROM "Branches"
 WHERE "StateId" IS NULL AND "CountryId" IS NULL AND regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') <> ''
UNION ALL SELECT 'BusinessSettings', "Id", substr("Gstin",1,2) FROM "BusinessSettings"
 WHERE "StateId" IS NULL AND "CountryId" IS NULL AND regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') <> '';
