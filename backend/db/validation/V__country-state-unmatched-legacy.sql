-- sellora:description Branches/BusinessSettings whose legacy State text could not be linked (ids only)
-- sellora:on-violation warn
-- gst_prefix is shown only for a strict-format Indian GSTIN (D11); a Gulf TRN/VAT number or malformed value shows NULL.
SELECT 'Branches' AS t, "Id", CASE WHEN upper(btrim("Gstin")) ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$' THEN substr(upper(btrim("Gstin")),1,2) END AS gst_prefix FROM "Branches"
 WHERE "StateId" IS NULL AND "CountryId" IS NULL AND regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') <> ''
UNION ALL SELECT 'BusinessSettings', "Id", CASE WHEN upper(btrim("Gstin")) ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$' THEN substr(upper(btrim("Gstin")),1,2) END FROM "BusinessSettings"
 WHERE "StateId" IS NULL AND "CountryId" IS NULL AND regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') <> '';
