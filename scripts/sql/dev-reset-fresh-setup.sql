-- DEV/TEST ONLY: returns the business data of a database to a "fresh setup" state: no units, no products, no stock, no purchases,
-- no sales, no accounts movements. It goes further than dev-reset-transactions.sql (transactions only): the masters of the Inventory,
-- Purchases, Sales and Accounts modules and the Unit master are removed too. Operator runbook, run by hand with psql whenever needed;
-- it is NOT a migration and must never be placed in backend/db (the deploy pipeline would run it).
--
-- Removed (ALL companies; Units are global, so this cannot be limited to one company):
--   Units        Units
--   Inventory    Products, ProductUnits, SellingPrices, ProductCategories, ProductSubcategories, StockItems, PurchaseBatches,
--                BatchLocationBalances, StockMovements, StockAdjustments (incl. opening stock), StockReservations, StockTransfers (+ lines,
--                batches, receipts), Shipments (+ vehicles), Consignments (+ lines, receipts, dispositions)
--   Purchases    PurchaseOrders (+ lines), PurchaseReturns (+ lines), PurchasePayments, Suppliers
--   Sales        SalesInvoices (+ lines, batch consumptions), SalesReturns (+ lines), SalesPayments, Customers
--   Accounts     FinancialTransactions, Expenses, ExpenseCategories, FinancialAccounts
--   System       IdempotencyKeys (request replay cache: its stored responses point at removed documents)
-- Put back, because a newly provisioned company has them:
--   * the walk-in customer is never removed (POS defaults to it and no API can create one);
--   * one empty "Main Cash" account per company (as PlatformService creates when a company is provisioned).
-- Reset: every document number series (INV, PUR, ...) starts again at 1.
-- Kept: companies, branches, stock locations, warehouses, vehicles, users, roles, permissions, governance, business settings, taxes,
--   currencies, exchange rates, countries, states, sessions (refresh tokens), audit history, migrations and the ops release journal.
--
-- keep=<list> keeps some masters (comma separated, no spaces): customers, suppliers, categories (product categories and subcategories),
--   expense_categories, accounts (financial accounts; their balance is computed from transactions, so it reads 0 afterwards).
--
-- Guards, all required:
--   1. The database must be opted in ONCE by its owner (production never is); the same switch as dev-reset-transactions.sql:
--        ALTER DATABASE <db> SET sellora.allow_dev_reset = 'on';      (takes effect for new connections)
--   2. -v confirm=<database name> must name the database you are connected to.
--   3. Nothing is changed without -v apply=1: the default run is a read-only PREVIEW (row counts only, no locks on the tables).
-- Usage:
--   psql "$PGCONN" -v ON_ERROR_STOP=1 -v confirm=<db> -f scripts/sql/dev-reset-fresh-setup.sql                                (preview)
--   psql "$PGCONN" -v ON_ERROR_STOP=1 -v confirm=<db> -v apply=1 -f scripts/sql/dev-reset-fresh-setup.sql                     (reset)
--   psql "$PGCONN" -v ON_ERROR_STOP=1 -v confirm=<db> -v keep=customers,suppliers -v apply=1 -f scripts/sql/dev-reset-fresh-setup.sql
--
-- Single transaction: either everything is removed or nothing is. The tables are emptied with one TRUNCATE without CASCADE, after a
-- check that no table outside the list references them: when a newer migration adds such a table the script stops (no change)
-- instead of silently wiping or orphaning it - add the table to the list below. TRUNCATE locks the tables briefly; if the API holds
-- them for more than 15 seconds the script fails with no change (stop the API and run it again).
--
-- After the reset: with Database:ApplyMigrationsOnStartup=true (local development) the next API start runs DbSeeder, which puts its
-- demo masters back into the default company (9 units, 9 products, categories, suppliers, customers, expense categories). Start the
-- API with Database__ApplyMigrationsOnStartup=false to keep the database empty.
\if :{?confirm}
\else
  \echo 'usage: psql -v ON_ERROR_STOP=1 -v confirm=<database-name> [-v keep=customers,suppliers,categories,expense_categories,accounts] [-v apply=1] -f scripts/sql/dev-reset-fresh-setup.sql'
  \quit
\endif
\if :{?apply}
\else
  \set apply 0
\endif
\if :{?keep}
\else
  \set keep ''
\endif

SELECT current_database() <> :'confirm' AS wrong_db,
       coalesce(current_setting('sellora.allow_dev_reset', true), '') <> 'on' AS not_opted_in \gset
\if :wrong_db
  \echo 'fresh-setup: confirm does not match the connected database; nothing changed.'
  \quit
\endif
\if :not_opted_in
  \echo 'fresh-setup: this database is not opted in (ALTER DATABASE <db> SET sellora.allow_dev_reset = ''on''); nothing changed.'
  \quit
\endif

SELECT k::text AS keep_list, k @> '{customers}' AS keep_customers, k @> '{accounts}' AS keep_accounts,
       NOT (k <@ '{customers,suppliers,categories,expense_categories,accounts}') AS bad_keep
FROM (SELECT array_remove(string_to_array(replace(lower(:'keep'), ' ', ''), ','), '') AS k) parsed \gset
\if :bad_keep
  \echo 'fresh-setup: unknown value in keep (allowed: customers, suppliers, categories, expense_categories, accounts); nothing changed.'
  \quit
\endif

BEGIN;

-- The reset list. keep_token: the keep=<...> value that leaves the table alone. via: truncate = emptied; delete = see note.
CREATE TEMP TABLE reset_plan (name text PRIMARY KEY, area text NOT NULL, via text NOT NULL, note text NOT NULL) ON COMMIT DROP;
INSERT INTO reset_plan (area, name, via, note)
SELECT v.area, v.name, v.via, v.note
FROM (VALUES
    ('1 Units',     'Units',                             NULL::text,           'truncate', 'remove all'),
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
WHERE v.keep_token IS NULL OR v.keep_token <> ALL (:'keep_list'::text[]);

-- Fail closed: every listed table must exist, and no table outside the list may reference a listed one.
SELECT count(*) > 0 AS missing_tables, coalesce(string_agg(name, ', ' ORDER BY name), '') AS missing_list
FROM reset_plan WHERE to_regclass(format('%I', name)) IS NULL \gset
\if :missing_tables
  ROLLBACK;
  \echo 'fresh-setup: tables missing in this database (its schema does not match this script):' :missing_list
  \echo 'fresh-setup: nothing changed.'
  \quit
\endif

SELECT count(*) > 0 AS has_blockers, coalesce(string_agg(DISTINCT c.relname || ' -> ' || p.relname, ', '), '') AS blocker_list
FROM pg_constraint con
JOIN pg_class c ON c.oid = con.conrelid
JOIN pg_class p ON p.oid = con.confrelid
WHERE con.contype = 'f'
  AND con.confrelid IN (SELECT to_regclass(format('%I', name))::oid FROM reset_plan)
  AND con.conrelid NOT IN (SELECT to_regclass(format('%I', name))::oid FROM reset_plan) \gset
\if :has_blockers
  ROLLBACK;
  \echo 'fresh-setup: tables outside the reset list reference tables in it (referencing -> referenced):' :blocker_list
  \echo 'fresh-setup: add them to the list in this script (a newer migration created them); nothing changed.'
  \quit
\endif

PREPARE reset_report AS
SELECT p.area AS "Area", p.name AS "Table",
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I', p.name), false, true, '')))[1]::text::bigint AS "Rows",
       p.note AS "Action"
FROM reset_plan p ORDER BY p.area, p.name;

\echo 'Rows before reset:'
EXECUTE reset_report;

\if :apply
  -- Do not hang behind a busy API: give up (no change) when the table locks are not granted in time.
  SET LOCAL lock_timeout = '15s';

  -- Who the re-created "Main Cash" belongs to: the creator of the company's oldest account, else its earliest active member.
  CREATE TEMP TABLE reset_main_cash ON COMMIT DROP AS
  SELECT c."Id" AS company_id,
         coalesce((SELECT a."CreatedByUserId" FROM "FinancialAccounts" a WHERE a."CompanyId" = c."Id" ORDER BY a."CreatedAt", a."Id" LIMIT 1),
                  (SELECT m."UserId" FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active'
                   ORDER BY m."CreatedAt", m."Id" LIMIT 1)) AS user_id
  FROM "Companies" c;

  -- One TRUNCATE over exactly the listed tables (no CASCADE): PostgreSQL refuses it if any other table still references them.
  SELECT string_agg(format('%I', name), ', ' ORDER BY name) AS truncate_list FROM reset_plan WHERE via = 'truncate' \gset
  TRUNCATE :truncate_list;

  \if :keep_customers
  \else
    DELETE FROM "Customers" WHERE NOT "IsWalkIn";
  \endif

  \if :keep_accounts
  \else
    INSERT INTO "FinancialAccounts" ("Id", "CompanyId", "BranchId", "Name", "Kind", "Reference", "OverdraftLimit", "IsActive", "CreatedByUserId", "CreatedAt")
    SELECT gen_random_uuid(), m.company_id, NULL, 'Main Cash', 'Cash', NULL, 0, true, m.user_id, now()
    FROM reset_main_cash m
    WHERE m.user_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM "FinancialAccounts" a WHERE a."CompanyId" = m.company_id);
  \endif

  UPDATE "DocumentSequences" SET "NextValue" = 1, "UpdatedAt" = now();

  INSERT INTO "PlatformAuditEvents" ("Id", "OccurredAt", "ActorUserId", "Action", "TargetType", "TargetId", "CompanyId", "Reason", "Before", "After", "CorrelationId")
  VALUES (gen_random_uuid(), now(), NULL, 'ops.dev_reset_fresh_setup', 'Database', '00000000-0000-0000-0000-000000000000', NULL,
          'Development reset: units, inventory, purchases, sales and accounts removed, document numbers reset', NULL,
          jsonb_build_object('database', current_database(), 'via', 'scripts/sql/dev-reset-fresh-setup.sql', 'kept', :'keep_list'), 'ops-script');

  \echo 'Rows after reset:'
  EXECUTE reset_report;

  COMMIT;
  \echo 'fresh-setup: done. Units, inventory, purchases, sales and accounts removed; document numbers reset; setup and users kept.'
  \echo 'fresh-setup: restart the API. With Database:ApplyMigrationsOnStartup=true it seeds its demo masters into the default company again.'
\else
  ROLLBACK;
  \echo 'fresh-setup: PREVIEW - nothing changed. The rows above would be removed; run again with -v apply=1 to reset.'
\endif
