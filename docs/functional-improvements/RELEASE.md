# Sellora functional improvements: release runbook (Phase 09)

Status: **ready for release review. Not deployed.** Deployment needs the owner's explicit approval (FI-D43g, D10).
Scope: phases 01, 08, 07, 02, 03, 04, 05, 06 of `IMPLEMENTATION_SPEC.md` (decisions FI-D1 to FI-D43). Branch: `feature/functional-improvements`.

---

## 1. Release notes

### Functional changes
| Phase | What users get | Key rulings |
|---|---|---|
| 01 Branch authorization | An active branch in every request. Branch-scoped roles and user allocations are enforced server-side on reads and writes. A company view where the role is company-level. | FI-D1 to FI-D15 |
| 08 Categories | Category and subcategory masters replace the free-text product category, backfilled from existing products. Products link a category and subcategory. | FI-D16 to FI-D18 |
| 07 Cash and bank | Cash and bank accounts with funding, transfers between accounts, and reversal (append-only, audited). Branch-visible accounts. Validation SQL. | FI-D19 to FI-D22 |
| 02 Sales and transfer validation | Non-negative stock enforced (DB check), plus sales and transfer validation against branch scope. | FI-D23 to FI-D25 |
| 03 Transfer requests | Request, approve, fulfil (partial or full), close remaining, ready-for-shipment queue and history. | FI-D26 to FI-D28 |
| 04 Shipments | Shipment planning: own fleet or carrier, multiple vehicles and multi-stop consignments, allocation with stock reservations, loading. | FI-D29 to FI-D33 |
| 05 Dispatch and receiving | Dispatch into Transit. Consignment receipts with damaged, rejected and missing quantities, and posting. The van wizard runs the full chain. The legacy direct transfer dispatch is retired (400). | FI-D34 to FI-D38 |
| 06 Receipt approval and reconciliation | Per company, Direct or SupervisorApproval posting plus optional separation of duties. Reject for correction. Quarantine dispositions (write off, return to source, accept to destination). Consignments become Reconciled and shipments Completed. | FI-D39 to FI-D42 |
| 09 Release | Performance indexes, a `stockTransferId` filter on the shipments list (the van wizard resumes safely across users), the end-to-end regression and the release rehearsal. | FI-D43 |

### Behaviour changes operators must know
- **Legacy direct transfer dispatch** returns 400. Transfers move through shipments (FI-D38).
- **SupervisorApproval mode** needs at least one `StockTransfers.ApproveReceipt` holder per destination branch, or receipts wait in PendingApproval. The migration grants ApproveReceipt to every role that already holds `StockTransfers.Approve` (FI-D42f).
- **Damaged, rejected and missing goods stay in Transit** (never sellable) until a company-level `StockTransfers.Resolve` holder dispositions them (FI-D40).
- **Reject receipt** needs `StockTransfers.Receive` or `StockTransfers.ApproveReceipt`. A caller with neither gets 403.
- **New permissions** are seeded by the migrations for existing databases, and by `DbSeeder` for fresh ones. Review role assignments after the release (§6).

### Known limitations
- The legacy `Products.Category` text column is kept (read fallback and search). Dropping it is a separate destructive migration that needs explicit approval (FI-D43a).
- Ledger `EnteredQuantity` is null on multi-batch receipt and disposition draws. The consignment or receipt line keeps the entered-unit snapshot (FI-D43f).
- "Today" defaults (statement range, account opening date) use the **UTC** date, not branch-local time. A late-evening local entry can fall on the next UTC day.
- `GET /financial-accounts/options` lists active account names (company-level ones included) to any signed-in company member, for payment forms. Confirm this is acceptable for branch cashiers.
- The account statement has no maximum date range (unpaginated).
- Financial-ledger visibility is still decided per row through the source document (purchase order, sales invoice, expense, account), so very large ledgers may need a pre-computed visibility column. The list now evaluates it twice instead of 4 times, and the new covering indexes help. The cash-flow, cash/bank and payment-method reports still use the per-row predicate.
- Substring searches on document numbers (`%term%`) use sequential scans within a company. Add a `pg_trgm` index if volumes grow.

---

## 2. Database migrations (apply in this order)

Never by startup auto-migrate (D10; `Database:ApplyMigrationsOnStartup=false`). Two ways, depending on the environment:

- **Where the `migrate-db` pipeline job is enabled** (claude-dev first, then dev, prod later behind approval; see
  `docs/features/db-release-automation.md`): the deploy builds an EF migration bundle and `Sellora.DbMigrator` applies the
  pending migrations, then the `backend/db/` scripts, then every `backend/db/validation/V__*.sql`, and records each attempt in the
  `ops` journal (`/platform/db-releases`). A failure blocks the API deploy. No manual step.
- **Manual fallback** (an environment without the job): one reviewed idempotent script, as below.

| # | Migration | Phase | Type | Data backfill |
|---|---|---|---|---|
| 1 | `20261112114131_AddProductCategoryMasters` | 08 | additive | categories from legacy `Products.Category` |
| 2 | `20261113032716_AddCashBankFundingAndReversal` | 07 | additive | permissions |
| 3 | `20261113052229_AddStockItemsNonNegativeCheck` | 02 | constraint | none. **Fails if any StockItem is negative**: run the pre-check first |
| 4 | `20261115085816_AddTransferRequestFulfilment` | 03 | additive | none (new states apply to new requests) |
| 5 | `20261116033533_AddShipmentsConsignmentsAndReservations` | 04 | additive | permissions |
| 6 | `20261116081256_AddShipmentDispatchAndReceiving` | 05 | additive | deterministic ids for existing transfer batch rows |
| 7 | `20261117095458_AddReceiptApprovalAndDispositions` | 06 | additive | Received→Reconciled, Delivered→Completed; receipt policy snapshot; `StockTransfers.ApproveReceipt` |
| 8 | `20261117105841_AddReleaseReadinessIndexes` | 09 | indexes only (`CREATE INDEX IF NOT EXISTS`, so a DBA may pre-create them `CONCURRENTLY` under the same names). Replaces `IX_FinancialTransactions_CompanyId_AccountId` with a superset index | none |

Migrations before #1 (multi-company: locations, warehouses and vehicles, user assignments, governance, scoped roles, location dimension, stock transfers) may already be applied in the target. The script is idempotent and skips anything already in `__EFMigrationsHistory`.

Generate the script on a clean checkout of the release commit:
```bash
dotnet tool install --global dotnet-ef --version 10.0.12
```
```bash
dotnet ef migrations script --idempotent --project backend/src/Sellora.Infrastructure --startup-project backend/src/Sellora.Infrastructure -o release-fi.sql
```
Review `release-fi.sql` before it runs. Each migration's header comment lists its validation SQL.

### Dry run (done in CI and locally)
- `ReleaseRehearsalTests` takes a production-shaped legacy copy, generates the idempotent script and runs it **twice**. Then every validation script must return zero rows, and the applied history must equal the full migration list.
- **Repeat on a restored copy of the real target before applying:** restore the backup (§3) into a scratch database, apply `release-fi.sql`, and run the §5 validation scripts.

---

## 3. Backup procedure (before any script runs)

1. Announce a maintenance window. Stop write traffic: scale the API to 0, or point the front door at the maintenance page.
2. Take a full logical backup:
   ```bash
   pg_dump --format=custom --no-owner --file=sellora-pre-fi-$(date +%Y%m%d%H%M).dump "$SELLORA_DB_URL"
   ```
   On Azure Database for PostgreSQL, also note the point-in-time-restore timestamp. That is the fastest restore path.
3. Prove the backup restores:
   ```bash
   pg_restore --list sellora-pre-fi-*.dump | head
   ```
   Restoring it into the scratch database used for the §2 dry run proves it end to end.
4. Record the pre-release state:
   ```sql
   SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;
   ```
   Keep the output with the release ticket.

---

## 4. Deployment checklist

**Pre-flight**
- [ ] CI green on the release commit: backend build, the missing-migration check, unit and integration tests (Testcontainers), and the frontend build and tests.
- [ ] `scripts/verify.sh all` is green locally on the release commit.
- [ ] Owner approval recorded for this deployment.
- [ ] Backup taken and restore proven (§3).
- [ ] Pre-check queries return zero rows on production:
  - `backend/db/validation/V__stock-items-negative-check.sql` (migration #3 would fail otherwise);
  - `backend/db/validation/V__stock-reconciliation.sql`.
- [ ] `release-fi.sql` generated from the release commit and reviewed; dry-run on the restored copy is clean.

**Apply**
- [ ] Write traffic stopped.
- [ ] Run `psql "$SELLORA_DB_URL" -v ON_ERROR_STOP=1 -f release-fi.sql`. Each migration runs in its own transaction; on error, stop and go to §7.
- [ ] Run the §5 validation scripts. All must return zero rows.

**Deploy the application**
- [ ] Deploy the API build of the same commit, with `Database__ApplyMigrationsOnStartup=false`. Recommended: `Database__FailOnPendingMigrations=true`, so a schema and code mismatch refuses to start.
- [ ] `GET /health` returns 200 with `pendingMigrations: 0`. The startup log contains "Database schema is up to date".
- [ ] Deploy the frontend (Static Web App) from the same commit.
- [ ] Smoke test with a branch user and a company user, in order:
  1. Switch the active branch.
  2. List products and categories.
  3. Cash and bank: post, then reverse.
  4. Transfer request, then approve.
  5. Shipment: allocate, load, dispatch.
  6. Receive, then post (Direct mode).
  7. Resolve a damaged quantity.
  8. The transit view shows Reconciled.
- [ ] Re-open write traffic.

**Post-release (day 0 and day 1)**
- [ ] Re-run the §5 validation scripts after the first day of traffic.
- [ ] Review role grants for the new permissions (§6). Set the receipt policy per company if supervisor approval is wanted.

---

## 5. Validation scripts (each query must return zero rows)

| Script | Covers |
|---|---|
| `backend/db/validation/V__stock-reconciliation.sql` | ledger = on-hand per item, batches |
| `backend/db/validation/V__stock-items-negative-check.sql` | no negative stock |
| `backend/db/validation/V__stock-transfers-validation.sql` | transfer counters, batches = Transit, completion (V1 to V8) |
| `backend/db/validation/V__transit-reconciliation.sql` | reservations = dispatched, receipts = counters, Transit = open batches, dispositions (queries 1 to 9) |
| `backend/db/validation/V__stock-reservations-reconciliation.sql` | active reservations = `StockItems.ReservedQuantity` |
| `backend/db/validation/V__van-operations-validation.sql` | van flows |
| `backend/db/validation/V__cash-bank-funding-validation.sql`, `V__cash-bank-negative-balances.sql` (informational, `on-violation: warn`) | balanced ledger, reversals, no negative balances where disallowed |
| `backend/db/validation/V__unit-restructuring-validation.sql` | unit-conversion invariants |

With the `migrate-db` job these run on every deploy (read-only transaction; `on-violation` warn in dev/claude-dev, block in prod unless a script's
header says otherwise) and the result is on `/platform/db-releases`. By hand: `psql -v ON_ERROR_STOP=1 -f <script>`. All are read-only and safe on production.

---

## 6. Permissions introduced (review the grants after release)

New codes are seeded in `PermissionCatalog`, `seed-permissions.sql` and the migrations. Run `SELECT "Code" FROM "Permissions" ORDER BY 1;` and compare the result with `PermissionCatalog`.

Notable codes:
- `StockTransfers.ApproveReceipt`: granted to holders of `StockTransfers.Approve`.
- `StockTransfers.Resolve`: company-level only.
- Shipment planning, dispatch and vehicle codes.
- Cash and bank account and reversal codes.

---

## 7. Rollback plan

**Preferred: restore.** These migrations are not destructive, so a rollback is only needed if the release is abandoned.

1. Stop write traffic.
2. Redeploy the previous API and frontend build.
3. Pick a restore path:
   - **If no business data was written after the release:** restore the backup (§3), or use point-in-time restore to the recorded timestamp.
   - **If business data was written:** do not restore, because that loses data. Keep the new schema. The old code ignores the new tables and columns, but does not respect the new rules (see the warning below), so fix forward instead.

**EF Down migrations** (`dotnet ef migrations script <from> <to>`) exist for every migration, but only some guard against data loss:
- **Guarded:** #2 cash/bank, #4 transfer fulfilment, #5 shipments, #6 dispatch and receiving, #7 receipt approval. Each raises an exception, instead of deleting data, when that phase's data exists (for example dispositions, pending or rejected receipts, shipments or fundings).
- **Not guarded:**
  - #1 categories: Down drops the category masters and the products' category links. The legacy `Products.Category` text survives.
  - #3 non-negative check: Down only drops the constraint.
  - #8 indexes: Down only drops the indexes.

Use Down only on a database with no new-feature data. Otherwise restore from the backup.

> **Warning: old code on the new schema.** With the new schema and the old code, the old code cannot see the shipment, receipt and disposition rules. Damaged quantities in Transit, for example, would look like ordinary transit stock. Do not run old code against a database that has Phase 04 to 06 data.

**Decision owner:** the release owner. Trigger: any §5 query returns rows, `/health` stays 503, or a smoke step fails without a fix-forward under 1 hour.

---

## 8. Monitoring

- **`/health`** returns 503 while migrations are pending. Alert on non-200 from the App Service health check.
- **Startup log:** "Database schema is behind the application" is an error-level entry. Alert on it in Application Insights.
- **Error rate:** alert on 5xx above 1% over 5 minutes on `/api/v1/shipments`, `/consignments`, `/stock-transfers` and `/financial-*`. Npgsql `53300` (too many clients) means the connection pool or server limits need attention.
- **Scheduled reconciliation:** run the §5 scripts nightly (read-only) and alert on any returned row. The scripts that matter most are transit, reservations, stock-reconciliation and cash-bank.
- **Business signals:**
  - receipts in PendingApproval older than 24 hours (`SELECT count(*) FROM "ConsignmentReceipts" WHERE "Status"='PendingApproval' AND "VerifiedAt" < now()-interval '24 hours'`);
  - consignments Received but not Reconciled older than 7 days (open discrepancies);
  - active stock reservations older than 7 days.
- **Performance:** watch the p95 of the cash/bank list, account balances, the transit discrepancies view and the shipment list. The Phase 09 indexes target these.
