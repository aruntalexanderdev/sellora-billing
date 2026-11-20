-- sellora:description Country/State seed present (minimum expected counts per seeded country)
-- sellora:on-violation block
SELECT e.cc, e.n AS expected, count(s."Id") AS actual
FROM (VALUES ('IN',36),('AE',7),('SA',13),('QA',8),('KW',6),('BH',4),('OM',11)) e(cc,n)
LEFT JOIN "Countries" c ON c."Code"=e.cc AND c."IsSystem"
LEFT JOIN "States" s ON s."CountryId"=c."Id" AND s."IsSystem"
GROUP BY e.cc, e.n, c."Id" HAVING c."Id" IS NULL OR count(s."Id") < e.n;
