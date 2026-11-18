-- sellora:description Unit restructuring post-migration checks
-- Applied by Sellora.DbMigrator on every deploy (docs/features/db-release-automation.md); can still be run by hand with psql.
-- Unit restructuring: POST-MIGRATION VALIDATION (analysis 22.7). Read-only; EVERY query must return no rows when healthy. Ids only, no PII.
-- Run after applying the idempotent release script (through at least 20261109053202_AddMovementTypesAndReasonCode), before the app deploy,
-- and again after the smoke test / any unit, product or document change:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f backend/db/validation/V__unit-restructuring-validation.sql
-- Row-count comparisons (SellingPrices before = after) are not zero-row checks: record the P7 line of unit-restructuring-profile.sql before the
-- release and compare after it with: SELECT COUNT(*) FROM "SellingPrices"  (and "ProductUnits" = old "ProductSellingModes" + products that had
-- no factor-1 mode, profile P2). The migrations assert the same counts in-transaction (IntroduceProductUnits "row counts changed").
-- Most rules below are also CHECK / FK / unique-index constraints (all validated); the queries are the belt-and-braces proof for
-- databases migrated from legacy data and the only proof for the rules that SQL constraints cannot express.
-- Stock reconciliation (QOH vs ledger vs batches) lives in backend/db/validation/V__stock-reconciliation.sql.

-- U1 every product has exactly ONE base ProductUnit (IntroduceProductUnits V-block; ProductService validation rule 1;
--    unique index IX_ProductUnits_ProductId_Base only guards "at most one"). The base row is always active (CK_ProductUnits_Base_Active).
SELECT 'U1' AS chk, p."Id" AS product_id FROM "Products" p
WHERE (SELECT COUNT(*) FROM "ProductUnits" x WHERE x."ProductId" = p."Id" AND x."IsBaseUnit" AND x."IsActive") <> 1
   OR (SELECT COUNT(*) FROM "ProductUnits" x WHERE x."ProductId" = p."Id" AND x."IsBaseUnit") <> 1;

-- U2 the base row's unit is the product's BaseUnitId, and it has InnerQuantity = 1, factor = 1 (IntroduceProductUnits 3.x; CK_ProductUnits_Base_Identity).
SELECT 'U2' AS chk, x."Id" AS product_unit_id FROM "ProductUnits" x JOIN "Products" p ON p."Id" = x."ProductId"
WHERE x."IsBaseUnit" AND (x."UnitId" <> p."BaseUnitId" OR x."InnerQuantity" <> 1 OR x."ConversionFactorToBase" <> 1);

-- U3 base unit category is not Packaging (unit-conversion-rules validation 1; service-level rule, not a constraint).
SELECT 'U3' AS chk, p."Id" AS product_id FROM "Products" p JOIN "Units" u ON u."Id" = p."BaseUnitId" WHERE u."MeasurementCategory" = 'Packaging';

-- U4 ProductUnit factor > 0 and InnerQuantity > 0 (CK_ProductUnits_Factor_Positive / _InnerQuantity_Positive); factor within 1e-6 .. 1e9 (rule 6, service-level).
SELECT 'U4' AS chk, "Id" AS product_unit_id FROM "ProductUnits"
WHERE "ConversionFactorToBase" <= 0 OR "InnerQuantity" <= 0 OR "ConversionFactorToBase" < 0.000001 OR "ConversionFactorToBase" > 1000000000;

-- U5 hierarchy: a non-base row has an inner row of the SAME product and factor = InnerQuantity x inner factor (IntroduceProductUnits V-block
--    "broken inner/factor"; compared at the stored scale numeric(28,10) because the service materialises with 10 dp). A tolerance of 1e-10
--    absorbs the rounding-mode difference at the 11th digit (PostgreSQL round() is half-away-from-zero, .NET Math.Round is half-even).
SELECT 'U5' AS chk, x."Id" AS product_unit_id FROM "ProductUnits" x
LEFT JOIN "ProductUnits" i ON i."Id" = x."InnerProductUnitId"
WHERE NOT x."IsBaseUnit"
  AND (i."Id" IS NULL OR i."ProductId" <> x."ProductId" OR abs(x."ConversionFactorToBase" - x."InnerQuantity" * i."ConversionFactorToBase") > 0.0000000001);

-- U6 hierarchy walks to the base: no cycle and no orphan chain (rule 2, service-level; the FK only guarantees the inner row exists).
WITH RECURSIVE walk AS (
    SELECT x."Id" AS start_id, x."InnerProductUnitId" AS cur, x."IsBaseUnit" AS at_base, 1 AS depth FROM "ProductUnits" x
    UNION ALL
    SELECT w.start_id, n."InnerProductUnitId", n."IsBaseUnit", w.depth + 1
    FROM walk w JOIN "ProductUnits" n ON n."Id" = w.cur WHERE NOT w.at_base AND w.depth < 64
)
SELECT 'U6' AS chk, x."Id" AS product_unit_id FROM "ProductUnits" x
WHERE NOT EXISTS (SELECT 1 FROM walk w WHERE w.start_id = x."Id" AND w.at_base)
  AND NOT x."IsBaseUnit";

-- U7 orphans on ProductUnit references (FKs exist; this also catches drift on a restored/partial copy).
SELECT 'U7-unit' AS chk, x."Id" FROM "ProductUnits" x LEFT JOIN "Units" u ON u."Id" = x."UnitId" WHERE u."Id" IS NULL
UNION ALL SELECT 'U7-product', x."Id" FROM "ProductUnits" x LEFT JOIN "Products" p ON p."Id" = x."ProductId" WHERE p."Id" IS NULL
UNION ALL SELECT 'U7-company', x."Id" FROM "ProductUnits" x JOIN "Products" p ON p."Id" = x."ProductId" WHERE p."CompanyId" <> x."CompanyId"
UNION ALL SELECT 'U7-purchase-line', l."Id" FROM "PurchaseOrderLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" AND x."ProductId" = l."ProductId" WHERE x."Id" IS NULL
UNION ALL SELECT 'U7-purchase-return-line', l."Id" FROM "PurchaseReturnLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" AND x."ProductId" = l."ProductId" WHERE x."Id" IS NULL
UNION ALL SELECT 'U7-sales-line', l."Id" FROM "SalesInvoiceLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" AND x."ProductId" = l."ProductId" WHERE x."Id" IS NULL
UNION ALL SELECT 'U7-sales-return-line', l."Id" FROM "SalesReturnLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" AND x."ProductId" = l."ProductId" WHERE x."Id" IS NULL
UNION ALL SELECT 'U7-adjustment', l."Id" FROM "StockAdjustments" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" AND x."ProductId" = l."ProductId" WHERE x."Id" IS NULL
UNION ALL SELECT 'U7-movement', m."Id" FROM "StockMovements" m LEFT JOIN "ProductUnits" x ON x."Id" = m."EnteredProductUnitId" AND x."ProductId" = m."ProductId"
    WHERE m."EnteredProductUnitId" IS NOT NULL AND x."Id" IS NULL
UNION ALL SELECT 'U7-price', s."Id" FROM "SellingPrices" s LEFT JOIN "ProductUnits" x ON x."Id" = s."ProductUnitId" WHERE x."Id" IS NULL;

-- U8 line arithmetic, TOLERANT form: Entered qty x factor snapshot = base qty after rounding to the storage scale numeric(18,3).
--    Diagnostic only: U9 (exact) implies U8, so a row that fails U9 but passes U8 is a precision drift, one failing both is a real mismatch.
--    (AddPurchaseUnitSnapshots P3 / AddSalesUnitSnapshots P2 / AddStockAdjustmentUnitSnapshots; CK_*_BaseQty_Arithmetic).
SELECT 'U8-purchase-line' AS chk, "Id" FROM "PurchaseOrderLines" WHERE round("Qty" * "ConversionFactorToBase", 3) <> "BaseQty"
UNION ALL SELECT 'U8-purchase-return-line', "Id" FROM "PurchaseReturnLines" WHERE round("Qty" * "ConversionFactorToBase", 3) <> "BaseQty"
UNION ALL SELECT 'U8-sales-line', "Id" FROM "SalesInvoiceLines" WHERE round("Qty" * "ConversionFactorToBase", 3) <> "BaseQty"
UNION ALL SELECT 'U8-sales-return-line', "Id" FROM "SalesReturnLines" WHERE round("Qty" * "ConversionFactorToBase", 3) <> "BaseQty"
UNION ALL SELECT 'U8-adjustment', "Id" FROM "StockAdjustments" WHERE round("EnteredQuantity" * "ConversionFactorToBase", 3) <> "QuantityBaseUnits"
UNION ALL SELECT 'U8-transfer-line', "Id" FROM "StockTransferLines" WHERE round("RequestedQty" * "ConversionFactorToBase", 3) <> "RequestedBaseQty"
UNION ALL SELECT 'U8-movement', "Id" FROM "StockMovements"
    WHERE "EnteredQuantity" IS NOT NULL AND round("EnteredQuantity" * "ConversionFactorSnapshot", 3) <> abs("QuantityBaseUnits");

-- U9 line arithmetic, STRICT form: the product is exactly representable at 3 dp (StockQuantity.EnsureStorable: a base quantity is never rounded).
--    Equivalent to the validated CK_*_BaseQty_Arithmetic constraints; a row here means a base quantity was silently rounded.
SELECT 'U9-purchase-line' AS chk, "Id" FROM "PurchaseOrderLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty"
UNION ALL SELECT 'U9-purchase-return-line', "Id" FROM "PurchaseReturnLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty"
UNION ALL SELECT 'U9-sales-line', "Id" FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty"
UNION ALL SELECT 'U9-sales-return-line', "Id" FROM "SalesReturnLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty"
UNION ALL SELECT 'U9-adjustment', "Id" FROM "StockAdjustments" WHERE "EnteredQuantity" * "ConversionFactorToBase" <> "QuantityBaseUnits"
UNION ALL SELECT 'U9-transfer-line', "Id" FROM "StockTransferLines" WHERE "RequestedQty" * "ConversionFactorToBase" <> "RequestedBaseQty"
UNION ALL SELECT 'U9-movement', "Id" FROM "StockMovements"
    WHERE "EnteredQuantity" IS NOT NULL AND "EnteredQuantity" * "ConversionFactorSnapshot" <> abs("QuantityBaseUnits");

-- U10 snapshot columns are populated and consistent with the ProductUnit they point at. The columns are NOT NULL after the migrations, so a NULL
--     cannot occur; what can drift is the content: empty unit code (CK_*_UnitCode_NotEmpty), non-positive factor/quantity, or a snapshot that differs
--     from its ProductUnit (rule 8: a referenced ProductUnit is immutable, so UnitId and factor must still equal the snapshot).
SELECT 'U10-purchase-line' AS chk, l."Id" FROM "PurchaseOrderLines" l JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId"
WHERE l."UnitId" <> x."UnitId" OR l."ConversionFactorToBase" <> x."ConversionFactorToBase" OR l."ConversionFactorToBase" <= 0 OR l."Qty" <= 0 OR btrim(l."UnitCode") = ''
UNION ALL SELECT 'U10-purchase-return-line', l."Id" FROM "PurchaseReturnLines" l JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId"
WHERE l."UnitId" <> x."UnitId" OR l."ConversionFactorToBase" <> x."ConversionFactorToBase" OR l."ConversionFactorToBase" <= 0 OR l."Qty" <= 0 OR btrim(l."UnitCode") = ''
UNION ALL SELECT 'U10-sales-line', l."Id" FROM "SalesInvoiceLines" l JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId"
WHERE l."UnitId" <> x."UnitId" OR l."ConversionFactorToBase" <> x."ConversionFactorToBase" OR l."ConversionFactorToBase" <= 0 OR l."Qty" <= 0 OR btrim(l."UnitCode") = ''
UNION ALL SELECT 'U10-sales-return-line', l."Id" FROM "SalesReturnLines" l JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId"
WHERE l."UnitId" <> x."UnitId" OR l."ConversionFactorToBase" <> x."ConversionFactorToBase" OR l."ConversionFactorToBase" <= 0 OR l."Qty" <= 0 OR btrim(l."UnitCode") = ''
UNION ALL SELECT 'U10-adjustment', l."Id" FROM "StockAdjustments" l JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId"
WHERE l."UnitId" <> x."UnitId" OR l."ConversionFactorToBase" <> x."ConversionFactorToBase" OR l."ConversionFactorToBase" <= 0 OR l."EnteredQuantity" <= 0 OR btrim(l."UnitCode") = ''
UNION ALL SELECT 'U10-movement', m."Id" FROM "StockMovements" m JOIN "ProductUnits" x ON x."Id" = m."EnteredProductUnitId"
WHERE m."ConversionFactorSnapshot" IS DISTINCT FROM x."ConversionFactorToBase" OR m."EnteredUnitCode" IS NULL OR btrim(m."EnteredUnitCode") = '' OR m."EnteredQuantity" <= 0;

-- U11 movement entered-unit fields are all-or-none (CK_StockMovements_EnteredUnit_AllOrNone; legacy movements keep all NULL, AddMovementTypesAndReasonCode V4/V6);
--     an EnteredQuantity without a unit, or a unit without a factor, is a partial snapshot.
SELECT 'U11' AS chk, "Id" FROM "StockMovements"
WHERE ("EnteredProductUnitId" IS NULL) <> ("EnteredUnitCode" IS NULL) OR ("EnteredProductUnitId" IS NULL) <> ("ConversionFactorSnapshot" IS NULL)
   OR ("EnteredQuantity" IS NOT NULL AND "EnteredProductUnitId" IS NULL);

-- U12 SellingPrices: at most one open price (EffectiveTo IS NULL) per ProductUnit (PricingService closes the current price when it adds a new one),
--     a price is not negative and a closed window does not end before it starts. Orphans: U7-price.
--     U12-open assumes no future-dated (scheduled) prices; revisit it if scheduled prices are introduced.
SELECT 'U12-open' AS chk, "ProductUnitId" AS id FROM "SellingPrices" WHERE "EffectiveTo" IS NULL GROUP BY "ProductUnitId" HAVING COUNT(*) > 1
UNION ALL SELECT 'U12-price', "Id" FROM "SellingPrices" WHERE "Price" < 0 OR ("EffectiveTo" IS NOT NULL AND "EffectiveTo" < "EffectiveFrom");
