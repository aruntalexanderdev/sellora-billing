-- FI-D25 pre-deploy check for migration AddStockItemsNonNegativeCheck (CK_StockItems_QuantityOnHand_NonNegative).
-- Must return no rows. Any row listed makes the migration refuse; fix via a reviewed stock adjustment, never by editing StockItems directly.
SELECT s."CompanyId", s."StockLocationId", l."Code" AS "LocationCode", s."ProductId", p."Sku", s."QuantityOnHand"
FROM "StockItems" s
LEFT JOIN "StockLocations" l ON l."Id" = s."StockLocationId"
LEFT JOIN "Products" p ON p."Id" = s."ProductId"
WHERE s."QuantityOnHand" < 0
ORDER BY s."CompanyId", s."StockLocationId", s."ProductId";
