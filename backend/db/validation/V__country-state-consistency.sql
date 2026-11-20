-- sellora:description StateId belongs to CountryId on Branches, BusinessSettings, Customers, Suppliers (safety net behind the composite FK)
-- sellora:on-violation block
SELECT x.t, x."Id" FROM (
  SELECT 'Branches' AS t, "Id", "StateId", "CountryId" FROM "Branches"
  UNION ALL SELECT 'BusinessSettings', "Id", "StateId", "CountryId" FROM "BusinessSettings"
  UNION ALL SELECT 'Customers', "Id", "StateId", "CountryId" FROM "Customers"
  UNION ALL SELECT 'Suppliers', "Id", "StateId", "CountryId" FROM "Suppliers") x
LEFT JOIN "States" s ON s."Id" = x."StateId"
WHERE x."StateId" IS NOT NULL AND (x."CountryId" IS NULL OR s."CountryId" IS DISTINCT FROM x."CountryId");
