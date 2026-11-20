-- DEV/TEST ONLY: returns the business data of a database to a "fresh setup" state: no units, no taxes, no products, no stock, no
-- purchases, no sales, no accounts movements. It goes further than dev-reset-transactions.sql (transactions only).
-- Plain SQL: it runs in DBeaver, pgAdmin, DataGrip or psql, against a local or a hosted (for example Azure) PostgreSQL database.
-- It is NOT a migration and must never be placed in backend/db (the deploy pipeline would run it). Run it by hand, whenever needed.
--
-- WHAT IT DOES (ALL companies; Units are global, so it cannot be limited to one company)
--   Removes        Masters    Units, Taxes
--                  Inventory  Products, ProductUnits, SellingPrices, ProductCategories, ProductSubcategories, StockItems,
--                             PurchaseBatches, BatchLocationBalances, StockMovements, StockAdjustments (incl. opening stock),
--                             StockReservations, StockTransfers (+ lines, batches, receipts), Shipments (+ vehicles),
--                             Consignments (+ lines, receipts, dispositions)
--                  Purchases  PurchaseOrders (+ lines), PurchaseReturns (+ lines), PurchasePayments
--                  Sales      SalesInvoices (+ lines, batch consumptions), SalesReturns (+ lines), SalesPayments
--                  Accounts   FinancialTransactions, Expenses, ExpenseCategories, FinancialAccounts
--                  System     IdempotencyKeys (request replay cache; its stored responses point at removed documents)
--   Keeps by default, removes only on request (v_wipe)   Customers, Suppliers. Their outstanding amounts are computed from the
--                  removed documents, so they read 0 afterwards.
--   Puts back      the walk-in customer is never removed, not even with v_wipe = 'customers' (POS defaults to it and no API can
--                  create one); one empty "Main Cash" account per company (as PlatformService creates for a new company).
--   Resets         every document number series (INV, PUR, ...) starts again at 1.
--   Never touches  companies, branches, stock locations, warehouses, vehicles, users, roles, permissions, governance, business
--                  settings, currencies, exchange rates, countries, states, sessions, audit history, migrations.
--
-- HOW TO RUN (DBeaver; other tools are the same idea)
--   1. Open a SQL editor on the database. Keep the editor in Auto-commit mode (the default).
--   2. Allow the reset for this session: type this line into the SAME editor tab and run it. It is deliberately not part of the script.
--          SET sellora.allow_dev_reset = 'on';
--      With a superuser login it can be allowed once per database instead (ALTER DATABASE "name" SET sellora.allow_dev_reset = 'on').
--      Hosted services such as Azure Database for PostgreSQL have no superuser login, so use the SET line there.
--   3. Paste this whole script below that line. Type the database name into v_confirm_database, leave v_apply = false and run the
--      WHOLE script (DBeaver "Execute SQL script", Alt+X). The result grid lists what would be removed. Nothing is changed.
--   4. Change v_apply to true and run the whole script again. The grid shows the rows before and after, and RESET DONE.
--   Do not save the edited values into the repository copy of this file: a test keeps the committed script disarmed.
--   psql: run the SET line and the edited file in one session, for example  psql "$CONN" -c "SET sellora.allow_dev_reset = 'on'" -f file.sql
--
-- SAFETY
--   * Three deliberate steps are needed before anything changes: the session switch, the typed database name, v_apply = true.
--   * The reset is ONE statement (the DO block), so it is all or nothing: if anything fails, nothing is changed.
--   * The tables are emptied with one TRUNCATE without CASCADE, after a check that no table outside the list references them. When a
--     newer migration adds such a table the script refuses (no change) instead of silently wiping or orphaning it: add the table to
--     the list below (the unit test FreshSetupResetScriptTests fails first).
--   * TRUNCATE locks the tables briefly. If another session holds them for more than 15 seconds the script fails with no change:
--     stop the API and run it again. The connected user must own the tables.
--   * In Manual-commit mode nothing is permanent, and the tables stay locked, until you press Commit.
--
-- AFTER THE RESET
--   An API that starts with the Database setting ApplyMigrationsOnStartup = true runs DbSeeder, which puts its demo masters back into
--   the default company (units, taxes, products, categories, suppliers, customers, expense categories). To keep the database empty
--   set the Database setting SeedDemoData to false (appsettings.Development.json, or the environment variable
--   Database__SeedDemoData=false on a hosted app); the API then still migrates and seeds system data, the walk-in customer and Main Cash.
--   The setting exists only in API builds that contain it: a hosted app running an older build ignores it until it is redeployed.

-- Result sheet of this session. If the reset statement below fails, the last query still shows this row.
CREATE TEMP TABLE IF NOT EXISTS sellora_reset_report (seq integer, area text, table_name text, via text, rows_before bigint, rows_after bigint, action text);
DELETE FROM pg_temp.sellora_reset_report;
INSERT INTO pg_temp.sellora_reset_report (seq, area, action)
VALUES (0, 'RESULT', 'NOT COMPLETED - the reset statement failed or did not run. Nothing was changed. See the error reported by your SQL tool.');

DO $$
DECLARE
    -- ============================== EDIT ONLY THESE FOUR VALUES ==============================
    v_confirm_database constant text    := 'TYPE-THE-DATABASE-NAME-HERE';  -- exactly the database you are connected to
    v_apply            constant boolean := false;  -- false = check only, nothing changes; true = reset
    v_wipe             constant text    := '';     -- also remove masters that are kept by default: '' or 'customers,suppliers'
    v_keep             constant text    := '';     -- keep masters that are removed by default: taxes,categories,expense_categories,accounts
    -- =========================================================================================
    v_k           text[];
    v_w           text[];
    v_kept        text[];
    v_kept_text   text;
    v_status      text;
    v_refused     boolean := true;
    v_text        text;
    v_count       bigint;
    v_row         record;
    v_company_ids uuid[];
    v_user_ids    uuid[];
BEGIN
    DELETE FROM pg_temp.sellora_reset_report;
    -- Do not hang behind a busy database: give up (no change) when a table lock is not granted in time. Covers the row counts too.
    PERFORM set_config('lock_timeout', '15s', true);
    <<work>>
    BEGIN
        -- Guard 1: the session (or the database) must be opted in.
        IF coalesce(current_setting('sellora.allow_dev_reset', true), '') <> 'on' THEN
            v_status := 'REFUSED - this session is not allowed to reset. Run the line  SET sellora.allow_dev_reset = ''on'';  in this same editor first. Nothing was changed.';
            EXIT work;
        END IF;
        -- Guard 2: the typed name must be the connected database.
        IF current_database() <> v_confirm_database THEN
            v_status := format('REFUSED - v_confirm_database is "%s" but you are connected to "%s". Nothing was changed.', v_confirm_database, current_database());
            EXIT work;
        END IF;
        -- Masters that stay: customers and suppliers unless v_wipe names them, plus whatever v_keep adds.
        v_k := array_remove(string_to_array(replace(lower(v_keep), ' ', ''), ','), '');
        v_w := array_remove(string_to_array(replace(lower(v_wipe), ' ', ''), ','), '');
        IF NOT (v_k <@ ARRAY['customers', 'suppliers', 'taxes', 'categories', 'expense_categories', 'accounts']) THEN
            v_status := 'REFUSED - unknown value in v_keep (allowed: taxes, categories, expense_categories, accounts, customers, suppliers). Nothing was changed.';
            EXIT work;
        END IF;
        IF NOT (v_w <@ ARRAY['customers', 'suppliers']) THEN
            v_status := 'REFUSED - unknown value in v_wipe (allowed: customers, suppliers). Nothing was changed.';
            EXIT work;
        END IF;
        IF v_k && v_w THEN
            v_status := 'REFUSED - the same value is in v_keep and in v_wipe. Nothing was changed.';
            EXIT work;
        END IF;
        v_kept := ARRAY(SELECT unnest(ARRAY['customers', 'suppliers'] || v_k) EXCEPT SELECT unnest(v_w) ORDER BY 1);
        v_kept_text := coalesce(nullif(array_to_string(v_kept, ', '), ''), 'none');
        -- The reset list. keep_token: the master group that leaves the table alone when it is kept. via: truncate = emptied; delete = see note.
        INSERT INTO pg_temp.sellora_reset_report (seq, area, table_name, via, action)
        SELECT row_number() OVER (ORDER BY v.area, v.name), v.area, v.name, v.via, v.note
        FROM (VALUES
            ('1 Masters',   'Units',                             cast(NULL AS text),   'truncate', 'remove all'),
            ('1 Masters',   'Taxes',                             'taxes',              'truncate', 'remove all'),
            ('2 Inventory', 'Products',                          NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ProductUnits',                      NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'SellingPrices',                     NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ProductCategories',                 'categories',         'truncate', 'remove all'),
            ('2 Inventory', 'ProductSubcategories',              'categories',         'truncate', 'remove all'),
            ('2 Inventory', 'StockItems',                        NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'PurchaseBatches',                   NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'BatchLocationBalances',             NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockMovements',                    NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockAdjustments',                  NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockReservations',                 NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockTransfers',                    NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockTransferLines',                NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockTransferLineBatches',          NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockTransferReceipts',             NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'StockTransferReceiptLines',         NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'Shipments',                         NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ShipmentVehicles',                  NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'Consignments',                      NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ConsignmentLines',                  NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ConsignmentReceipts',               NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ConsignmentReceiptLines',           NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ConsignmentDispositions',           NULL,                 'truncate', 'remove all'),
            ('2 Inventory', 'ConsignmentDispositionLines',       NULL,                 'truncate', 'remove all'),
            ('3 Purchases', 'PurchaseOrders',                    NULL,                 'truncate', 'remove all'),
            ('3 Purchases', 'PurchaseOrderLines',                NULL,                 'truncate', 'remove all'),
            ('3 Purchases', 'PurchaseReturns',                   NULL,                 'truncate', 'remove all'),
            ('3 Purchases', 'PurchaseReturnLines',               NULL,                 'truncate', 'remove all'),
            ('3 Purchases', 'PurchasePayments',                  NULL,                 'truncate', 'remove all'),
            ('3 Purchases', 'Suppliers',                         'suppliers',          'truncate', 'remove all'),
            ('4 Sales',     'SalesInvoices',                     NULL,                 'truncate', 'remove all'),
            ('4 Sales',     'SalesInvoiceLines',                 NULL,                 'truncate', 'remove all'),
            ('4 Sales',     'SalesInvoiceLineBatchConsumptions', NULL,                 'truncate', 'remove all'),
            ('4 Sales',     'SalesReturns',                      NULL,                 'truncate', 'remove all'),
            ('4 Sales',     'SalesReturnLines',                  NULL,                 'truncate', 'remove all'),
            ('4 Sales',     'SalesPayments',                     NULL,                 'truncate', 'remove all'),
            ('4 Sales',     'Customers',                         'customers',          'delete',   'remove all except the walk-in customer'),
            ('5 Accounts',  'FinancialTransactions',             NULL,                 'truncate', 'remove all'),
            ('5 Accounts',  'Expenses',                          NULL,                 'truncate', 'remove all'),
            ('5 Accounts',  'ExpenseCategories',                 'expense_categories', 'truncate', 'remove all'),
            ('5 Accounts',  'FinancialAccounts',                 'accounts',           'truncate', 'remove all, then one "Main Cash" per company'),
            ('6 System',    'IdempotencyKeys',                   NULL,                 'truncate', 'remove all')
        ) AS v (area, name, keep_token, via, note)
        WHERE v.keep_token IS NULL OR v.keep_token <> ALL (v_kept);
        -- Fail closed: every listed table must exist, and no table outside the list may reference a listed one.
        SELECT string_agg(r.table_name, ', ' ORDER BY r.table_name) INTO v_text
        FROM pg_temp.sellora_reset_report r WHERE r.seq > 0 AND to_regclass(format('%I', r.table_name)) IS NULL;
        IF v_text IS NOT NULL THEN
            v_status := format('REFUSED - tables missing in this database (its schema does not match this script): %s. Nothing was changed.', v_text);
            EXIT work;
        END IF;
        SELECT string_agg(DISTINCT c.relname || ' -> ' || p.relname, ', ') INTO v_text
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_class p ON p.oid = con.confrelid
        WHERE con.contype = 'f'
          AND con.confrelid IN (SELECT cast(to_regclass(format('%I', r.table_name)) AS oid) FROM pg_temp.sellora_reset_report r WHERE r.seq > 0)
          AND con.conrelid NOT IN (SELECT cast(to_regclass(format('%I', r.table_name)) AS oid) FROM pg_temp.sellora_reset_report r WHERE r.seq > 0);
        IF v_text IS NOT NULL THEN
            v_status := format('REFUSED - tables outside the reset list reference tables in it (referencing -> referenced): %s. Add them to the list in this script (a newer migration created them). Nothing was changed.', v_text);
            EXIT work;
        END IF;
        v_refused := false;
        FOR v_row IN SELECT r.seq, r.table_name FROM pg_temp.sellora_reset_report r WHERE r.seq > 0 ORDER BY r.seq LOOP
            EXECUTE format('SELECT count(*) FROM %I', v_row.table_name) INTO v_count;
            UPDATE pg_temp.sellora_reset_report SET rows_before = v_count WHERE seq = v_row.seq;
        END LOOP;
        IF NOT v_apply THEN
            v_status := format('CHECK ONLY - all checks passed and nothing was changed. The tables below would be emptied. Masters kept: %s. Set v_apply to true and run the whole script again to reset.', v_kept_text);
            EXIT work;
        END IF;
        -- Who the re-created "Main Cash" belongs to: the creator of the company's oldest account, else its earliest active member.
        SELECT array_agg(s.company_id), array_agg(s.user_id) INTO v_company_ids, v_user_ids
        FROM (SELECT c."Id" AS company_id,
                     coalesce((SELECT a."CreatedByUserId" FROM "FinancialAccounts" a WHERE a."CompanyId" = c."Id" ORDER BY a."CreatedAt", a."Id" LIMIT 1),
                              (SELECT m."UserId" FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active'
                               ORDER BY m."CreatedAt", m."Id" LIMIT 1)) AS user_id
              FROM "Companies" c) s
        WHERE s.user_id IS NOT NULL;
        -- One TRUNCATE over exactly the listed tables (no CASCADE): PostgreSQL refuses it if any other table still references them.
        SELECT string_agg(format('%I', r.table_name), ', ' ORDER BY r.table_name) INTO v_text
        FROM pg_temp.sellora_reset_report r WHERE r.seq > 0 AND r.via = 'truncate';
        EXECUTE 'TRUNCATE ' || v_text;
        IF NOT ('customers' = ANY (v_kept)) THEN
            DELETE FROM "Customers" WHERE NOT "IsWalkIn";
        END IF;
        IF NOT ('accounts' = ANY (v_kept)) THEN
            INSERT INTO "FinancialAccounts" ("Id", "CompanyId", "BranchId", "Name", "Kind", "Reference", "OverdraftLimit", "IsActive", "CreatedByUserId", "CreatedAt")
            SELECT gen_random_uuid(), m.company_id, NULL, 'Main Cash', 'Cash', NULL, 0, true, m.user_id, now()
            FROM unnest(v_company_ids, v_user_ids) AS m (company_id, user_id)
            WHERE NOT EXISTS (SELECT 1 FROM "FinancialAccounts" a WHERE a."CompanyId" = m.company_id);
        END IF;
        UPDATE "DocumentSequences" SET "NextValue" = 1, "UpdatedAt" = now();
        INSERT INTO "PlatformAuditEvents" ("Id", "OccurredAt", "ActorUserId", "Action", "TargetType", "TargetId", "CompanyId", "Reason", "Before", "After", "CorrelationId")
        VALUES (gen_random_uuid(), now(), NULL, 'ops.dev_reset_fresh_setup', 'Database', '00000000-0000-0000-0000-000000000000', NULL,
                'Development reset: units, taxes, inventory, purchases, sales and accounts removed, document numbers reset', NULL,
                jsonb_build_object('database', current_database(), 'via', 'scripts/sql/dev-reset-fresh-setup.sql', 'kept', v_kept_text), 'ops-script');
        FOR v_row IN SELECT r.seq, r.table_name FROM pg_temp.sellora_reset_report r WHERE r.seq > 0 ORDER BY r.seq LOOP
            EXECUTE format('SELECT count(*) FROM %I', v_row.table_name) INTO v_count;
            UPDATE pg_temp.sellora_reset_report SET rows_after = v_count WHERE seq = v_row.seq;
        END LOOP;
        v_status := format('RESET DONE - business data removed and document numbers reset; setup and users kept. Masters kept: %s. If your SQL tool is in Manual-commit mode, press Commit now.', v_kept_text);
    END work;
    IF v_refused THEN
        DELETE FROM pg_temp.sellora_reset_report WHERE seq > 0;
    END IF;
    INSERT INTO pg_temp.sellora_reset_report (seq, area, action) VALUES (0, 'RESULT', v_status);
    RAISE NOTICE '%', v_status;
END
$$;

SELECT area AS "Area", table_name AS "Table", rows_before AS "Rows before", rows_after AS "Rows after", action AS "Result / action"
FROM pg_temp.sellora_reset_report ORDER BY seq;
