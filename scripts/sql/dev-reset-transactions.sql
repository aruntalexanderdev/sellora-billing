-- DEV/TEST ONLY: wipes every transaction (inventory, purchases, sales & billing, payments, accounts & bank movements, expenses)
-- and resets document numbers, so a development database can be tested from a clean state. Operator runbook, run by hand with
-- psql; never place it in backend/db (the deploy pipeline would run it).
--
-- Kept (master data and setup): companies, branches, locations, warehouses, vehicles, users, roles, permissions, governance,
-- units, taxes, currencies, exchange rates, business settings, products, product units, categories, selling prices,
-- customers, suppliers, financial accounts (balances are computed from transactions, so they read 0 afterwards),
-- expense categories, audit history, migrations and the ops release journal.
-- Removed (all companies): stock items/batches/balances/movements/adjustments/reservations, transfers, shipments, consignments,
-- purchase orders/returns/payments, sales invoices/returns/payments, expenses, financial transactions, idempotency keys.
-- Reset: every document number series (INV, PUR, ...) starts again at 1.
--
-- Two guards, both required:
--   1. The database must be opted in ONCE by its owner (production never is):
--        ALTER DATABASE <db> SET sellora.allow_dev_reset = 'on';      (takes effect for new connections)
--   2. -v confirm=<database name> must name the database you are connected to.
-- Usage:
--   psql "$PGCONN" -v ON_ERROR_STOP=1 -v confirm=<db> -v dry_run=1 -f scripts/sql/dev-reset-transactions.sql   (preview, rolls back)
--   psql "$PGCONN" -v ON_ERROR_STOP=1 -v confirm=<db> -f scripts/sql/dev-reset-transactions.sql                 (reset)
-- Single transaction: either everything is removed or nothing is. TRUNCATE without CASCADE: if a newer migration adds a table that
-- references one of these tables and is not listed here, the script fails (no change) instead of silently wiping or orphaning it.
-- TRUNCATE locks the tables briefly; running API requests on them wait until the script commits.
\if :{?confirm}
\else
  \echo 'usage: psql -v ON_ERROR_STOP=1 -v confirm=<database-name> [-v dry_run=1] -f scripts/sql/dev-reset-transactions.sql'
  \quit
\endif
\if :{?dry_run}
\else
  \set dry_run 0
\endif

SELECT current_database() <> :'confirm' AS wrong_db,
       coalesce(current_setting('sellora.allow_dev_reset', true), '') <> 'on' AS not_opted_in \gset
\if :wrong_db
  \echo 'dev-reset: confirm does not match the connected database; nothing changed.'
  \quit
\endif
\if :not_opted_in
  \echo 'dev-reset: this database is not opted in (ALTER DATABASE <db> SET sellora.allow_dev_reset = ''on''); nothing changed.'
  \quit
\endif

BEGIN;

CREATE TEMP TABLE dev_reset_tables (name text PRIMARY KEY) ON COMMIT DROP;
INSERT INTO dev_reset_tables (name) VALUES
    -- inventory
    ('StockItems'), ('PurchaseBatches'), ('BatchLocationBalances'), ('StockMovements'), ('StockAdjustments'), ('StockReservations'),
    ('StockTransfers'), ('StockTransferLines'), ('StockTransferLineBatches'), ('StockTransferReceipts'), ('StockTransferReceiptLines'),
    ('Shipments'), ('ShipmentVehicles'),
    ('Consignments'), ('ConsignmentLines'), ('ConsignmentReceipts'), ('ConsignmentReceiptLines'),
    ('ConsignmentDispositions'), ('ConsignmentDispositionLines'),
    -- purchases
    ('PurchaseOrders'), ('PurchaseOrderLines'), ('PurchaseReturns'), ('PurchaseReturnLines'), ('PurchasePayments'),
    -- sales & billing
    ('SalesInvoices'), ('SalesInvoiceLines'), ('SalesInvoiceLineBatchConsumptions'), ('SalesReturns'), ('SalesReturnLines'), ('SalesPayments'),
    -- accounts & bank
    ('FinancialTransactions'), ('Expenses'),
    -- request replay cache (its stored responses point at removed documents)
    ('IdempotencyKeys');

\echo 'Rows before reset:'
SELECT t.name AS "Table",
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I', t.name), false, true, '')))[1]::text::bigint AS "Rows"
FROM dev_reset_tables t ORDER BY 1;

-- One TRUNCATE over exactly the listed tables (no CASCADE): PostgreSQL refuses it if any other table still references them.
DO $reset$
BEGIN
    EXECUTE 'TRUNCATE ' || (SELECT string_agg(format('%I', name), ', ' ORDER BY name) FROM dev_reset_tables);
END
$reset$;

UPDATE "DocumentSequences" SET "NextValue" = 1, "UpdatedAt" = now();

INSERT INTO "PlatformAuditEvents" ("Id", "OccurredAt", "ActorUserId", "Action", "TargetType", "TargetId", "CompanyId", "Reason", "Before", "After", "CorrelationId")
VALUES (gen_random_uuid(), now(), NULL, 'ops.dev_reset_transactions', 'Database', '00000000-0000-0000-0000-000000000000', NULL,
        'Development reset: all transactions removed, document numbers reset', NULL,
        jsonb_build_object('database', current_database(), 'via', 'scripts/sql/dev-reset-transactions.sql'), 'ops-script');

\if :dry_run
  \echo 'dev-reset: DRY RUN - rolling back, nothing changed.'
  ROLLBACK;
\else
  COMMIT;
  \echo 'dev-reset: done. All transactions removed and document numbers reset; master data kept.'
\endif
