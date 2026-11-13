-- Hard-delete ONE product that has NO transaction history (e.g. created by mistake / duplicate / test entry).
-- Removes the product and everything that only exists because of it: SellingPrices -> ProductUnits (leaf first) -> StockItems -> Products.
--
-- It REFUSES (no change) when the product appears in any document or in the stock ledger: purchase lines, purchase returns,
-- sales lines, sales returns, stock adjustments (incl. opening stock), transfers, movements, batches, or non-zero stock.
-- History is never deleted: invoices, returns and the ledger must stay intact (stock-ledger-invariants; "history is never recomputed").
-- For a product WITH history, deactivate it instead (Products."IsActive" = false via the product edit screen / PUT /api/products/{id}).
--
-- Safety:
--   * the SKU must match the id (guards against pasting the wrong id);
--   * DRY RUN by default: everything runs inside a transaction and is ROLLED BACK unless -v apply=1 is passed;
--   * the whole thing is one transaction with ON_ERROR_STOP, so any unexpected FK (Restrict) aborts with no change.
--
-- Usage (dry run, then real):
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -v product_id=<guid> -v sku=<SKU> -f scripts/sql/delete-product.sql
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -v product_id=<guid> -v sku=<SKU> -v apply=1 -f scripts/sql/delete-product.sql
-- Find the id:  SELECT "Id", "CompanyId", "Sku", "Name", "IsActive" FROM "Products" WHERE "Sku" = '<SKU>';

\if :{?product_id}
\else
  \echo 'usage: psql -v ON_ERROR_STOP=1 -v product_id=<guid> -v sku=<SKU> [-v apply=1] -f scripts/sql/delete-product.sql'
  \quit
\endif
\if :{?sku}
\else
  \echo 'sku is required (must match the product id)'
  \quit
\endif

BEGIN;

-- psql variables are not expanded inside DO bodies: pass them through transaction-local settings.
SELECT set_config('sellora.product_id', :'product_id', true) AS product_id,
       set_config('sellora.sku', :'sku', true) AS sku;

DO $$
DECLARE
    pid      uuid := current_setting('sellora.product_id')::uuid;
    sku_in   text := current_setting('sellora.sku');
    p        record;
    blockers text := '';
    n        bigint;
    deleted  int;
    total    int := 0;
BEGIN
    SELECT "Id", "CompanyId", "Sku", "Name" INTO p FROM "Products" WHERE "Id" = pid FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Product % not found', pid;
    END IF;
    IF p."Sku" <> sku_in THEN
        RAISE EXCEPTION 'SKU mismatch: product % has SKU "%", not "%"', pid, p."Sku", sku_in;
    END IF;

    -- ---- History checks: any row here means the product is part of business records -> refuse. ----
    SELECT COUNT(*) INTO n FROM "PurchaseOrderLines"        WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  PurchaseOrderLines: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "PurchaseReturnLines"       WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  PurchaseReturnLines: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "SalesInvoiceLines"         WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  SalesInvoiceLines: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "SalesReturnLines"          WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  SalesReturnLines: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "StockAdjustments"          WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  StockAdjustments (incl. opening stock): %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "StockTransferLines"        WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  StockTransferLines: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "StockTransferReceiptLines" WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  StockTransferReceiptLines: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "StockMovements"            WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  StockMovements: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "PurchaseBatches"           WHERE "ProductId" = pid; IF n > 0 THEN blockers := blockers || format(E'\n  PurchaseBatches: %s', n); END IF;
    SELECT COUNT(*) INTO n FROM "StockItems"                WHERE "ProductId" = pid AND "QuantityOnHand" <> 0; IF n > 0 THEN blockers := blockers || format(E'\n  StockItems with non-zero quantity: %s', n); END IF;

    IF blockers <> '' THEN
        RAISE EXCEPTION 'Product "%" (%) has transaction history and cannot be deleted. Deactivate it instead.%', p."Sku", pid, blockers;
    END IF;

    -- ---- Delete (children first). ----
    DELETE FROM "SellingPrices" sp USING "ProductUnits" u
     WHERE sp."ProductUnitId" = u."Id" AND u."ProductId" = pid;
    GET DIAGNOSTICS deleted = ROW_COUNT; RAISE NOTICE 'SellingPrices deleted: %', deleted;

    -- ProductUnits reference each other (InnerProductUnitId, ON DELETE RESTRICT): delete leaves first, level by level.
    LOOP
        DELETE FROM "ProductUnits" pu
         WHERE pu."ProductId" = pid
           AND NOT EXISTS (SELECT 1 FROM "ProductUnits" c WHERE c."ProductId" = pid AND c."InnerProductUnitId" = pu."Id");
        GET DIAGNOSTICS deleted = ROW_COUNT;
        EXIT WHEN deleted = 0;
        total := total + deleted;
    END LOOP;
    IF EXISTS (SELECT 1 FROM "ProductUnits" WHERE "ProductId" = pid) THEN
        RAISE EXCEPTION 'ProductUnits of % could not be fully removed (cyclic hierarchy?)', pid;
    END IF;
    RAISE NOTICE 'ProductUnits deleted: %', total;

    DELETE FROM "StockItems" WHERE "ProductId" = pid;   -- zero-quantity rows only (checked above)
    GET DIAGNOSTICS deleted = ROW_COUNT; RAISE NOTICE 'StockItems deleted: %', deleted;

    DELETE FROM "Products" WHERE "Id" = pid;            -- any FK not covered above raises here and rolls everything back
    RAISE NOTICE 'Product "%" (%) of company % deleted.', p."Sku", pid, p."CompanyId";
END
$$;

\if :{?apply}
  COMMIT;
  \echo 'APPLIED: product deleted.'
\else
  ROLLBACK;
  \echo 'DRY RUN: rolled back. Re-run with -v apply=1 to delete.'
\endif
