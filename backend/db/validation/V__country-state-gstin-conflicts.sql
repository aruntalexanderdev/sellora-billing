-- sellora:description Linked state differs from the row own strict-format GSTIN prefix (ids and prefixes only)
-- sellora:on-violation warn
WITH x AS (
  SELECT 'Branches' AS t, b."Id", b."StateId", upper(btrim(b."Gstin")) AS gstin
  FROM "Branches" b WHERE b."StateId" IS NOT NULL
  UNION ALL SELECT 'BusinessSettings', bs."Id", bs."StateId", upper(btrim(bs."Gstin")) FROM "BusinessSettings" bs WHERE bs."StateId" IS NOT NULL
  UNION ALL SELECT 'Suppliers', sp."Id", sp."StateId", upper(btrim(sp."Gstin")) FROM "Suppliers" sp WHERE sp."StateId" IS NOT NULL)
SELECT x.t, x."Id", s."Code" AS state_code, s."GstStateCode", substr(x.gstin,1,2) AS gstin_prefix,
  CASE WHEN substr(x.gstin,1,2)='25' AND s."GstStateCode"='26' AND s."Code"='DH'
       THEN 'legacy Daman and Diu GSTIN (re-registration under 26 expected)'
       WHEN c."Code" <> 'IN' THEN 'indian GSTIN on non-IN state'
       ELSE 'GSTIN prefix differs from state' END AS issue
FROM x JOIN "States" s ON s."Id"=x."StateId" JOIN "Countries" c ON c."Id"=s."CountryId"
WHERE x.gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'
  AND substr(x.gstin,1,2) IN (SELECT "GstStateCode" FROM "States" WHERE "GstStateCode" IS NOT NULL UNION SELECT '25')
  AND (c."Code" <> 'IN' OR substr(x.gstin,1,2) IS DISTINCT FROM s."GstStateCode");
