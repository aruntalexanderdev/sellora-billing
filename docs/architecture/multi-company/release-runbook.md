# Release runbook: unit-analysis Phases 1-7 and multi-company programme (Phase H, 1-11)

Baseline: branch `analyse`; nothing in either programme (unit-analysis Phases 1-7, multi-company H, 1-11) has been released. Release model is D10: snapshot -> reviewed idempotent script -> validation SQL -> deploy app -> smoke. Never rely on startup auto-migrate for production.
All `psql` commands use `"$DATABASE_URL"` (a libpq URI or key/value string supplied from your shell or a secret store; never paste it into tickets or logs).

## 1. Scope and release order

| Step | Content | Deploy action |
|---|---|---|
| 1 | Backend Phase H-10 including migrations (last: `20261111040145_AddStockTransfers`). Phases 8b and 9 ship together (phase-8-design.md "8b delivered", phase-9-design.md R1); Phase 10 (no migration) ships with or after them | idempotent script, then API deploy |
| 1b | Unit-analysis Phase 7 (reports precision and packaging breakdown; commits 2b8951a..940a9c5): no migration, additive optional DTO fields, ships with the API in step 1 and is visible with the frontend 11b step | API deploy (with step 1) |
| 2 | Validation SQL (`scripts/sql/unit-restructuring-validation.sql`, `scripts/sql/stock-reconciliation.sql`, `scripts/sql/stock-transfers-validation.sql` V1-V8, `scripts/sql/van-operations-validation.sql` R1-R5) | psql, zero rows |
| 3 | Seed + platform admin (`scripts/sql/seed-permissions.sql`, `scripts/sql/grant-platform-admin.sql`) | psql |
| 4 | Frontend 11a, in the same window as the backend that carries D14 (status endpoints `POST /branches\|warehouses\|vehicles/{id}/status` now REQUIRE `reason`, 1-500 chars, missing -> 400 field `reason`). Old SWA against new API breaks activate/deactivate; new SWA against old API is not supported (needs `/auth/me` companies, governance, `permissionsAnyBranch`) | SWA workflow |
| 5 | Frontend 11b (transfers, returns, reports per location, etc.) after 11a is verified | SWA workflow |

Do not deploy 8b alone. 11a must never reach users before step 1 is live.

## 2. Pre-flight

1. Snapshot: Azure Database for PostgreSQL Flexible Server on-demand backup / point-in-time restore point (or a branch/copy server). Record the timestamp and restore target name. Take it in step 3.2: after the maintenance window starts and writes are stopped (step 3.1), immediately before the migration script runs (Phase 2 B2 rewrites the five header tables; the old API is incompatible with B2/B3 schema, so stop writes: phase-2-design.md section 8 R-2).
2. [ ] Auto-migrate off for the target. Note: the dev App Services run as environment `Production`, so with the flag set to true there `Program.cs:132` logs the D10 warning on every start; expected for dev, never for production. `backend/src/Sellora.Api/appsettings.json:13` now ships `Database:ApplyMigrationsOnStartup: false` (Gap 1 resolved). OWNER ACTION BEFORE the next push to `dev` or `claude_dev`: if the dev apps (`sellora-billing-dev-api`, `sellora-billing-dev-api-claude`) should keep auto-migrating and seeding, set App Service setting `Database__ApplyMigrationsOnStartup=true` on them first; otherwise the next deploy will not migrate or seed. Production sets `Database__ApplyMigrationsOnStartup=false` explicitly anyway, BEFORE deploying; with it false the API logs "migrations and seed data were not applied on startup" (`Program.cs:149`) and `DbSeeder` does not run, hence `seed-permissions.sql` in section 3.
3. Find the last applied migration on the target, then list what is new:
   - `psql "$DATABASE_URL" -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 3;'`
   - Reference: `git ls-tree --name-only origin/main backend/src/Sellora.Infrastructure/Data/Migrations/ | grep -v 'Designer\|Snapshot'` (origin/main ends at `20260920114635_AddIdempotencyKeysAndCancelledBy`; origin/claude_dev ends at `20261104105655_ExtendUnitMaster`).
   - `git log --oneline -- backend/src/Sellora.Infrastructure/Data/Migrations` for the commits.
   Migrations after `20260920114635` in `backend/src/Sellora.Infrastructure/Data/Migrations/`: `ExtendUnitMaster`, `IntroduceProductUnits`, `AddStockAdjustmentUnitSnapshots`, `AddPurchaseUnitSnapshots`, `WidenCostPrecision` (table rewrite, plan a window), `AddSalesUnitSnapshots`, `AddMovementTypesAndReasonCode` (20261104-20261109), `AddCompaniesAndMemberships`, `AddCompanyIdToCompanyTables`, `ScopeUniquesAndCompositeKeysToCompany`, `AddDocumentSequences`, `AddBranches`, `AddBranchIdToDocuments`, `ScopeDocumentSequencesToBranches`, `AddStockLocations`, `AddWarehousesAndVehicles`, `AddUserBranchAssignments`, `AddPlatformAdminAndGovernance`, `AddScopedRoles`, `AddStockLocationDimension`, `AddStockTransfers`. Anything the target already has is skipped by the idempotent script.
4. Generate the script (from the repo root, at the exact commit being deployed; do NOT run `dotnet ef database update` against a shared DB):
   - `dotnet ef migrations script <LAST_APPLIED> --idempotent --project backend/src/Sellora.Infrastructure --startup-project backend/src/Sellora.Api -o release-migrations.sql` (omit `<LAST_APPLIED>` for a full script; use `0` only on an empty DB). Needs a resolvable `ConnectionStrings:SelloraDb` and `Jwt` section at design time (see `backend/src/Sellora.Api/appsettings.Development.json.example`); a dummy local value is enough because the script command does not connect. Verified 2026-11-11: `dotnet tool install --global dotnet-ef` (if missing; add `~/.dotnet/tools` to PATH), then `ConnectionStrings__SelloraDb="Host=localhost;Database=x;Username=x;Password=x" dotnet ef migrations script --idempotent --project backend/src/Sellora.Infrastructure --startup-project backend/src/Sellora.Api -o release-migrations.sql` builds and writes the full idempotent script (~8,100 lines).
   - Keep `release-migrations.sql` outside the repo (it is an artefact; never commit it). Have a second person review it.
5. Unit profiling on a restored production copy (old schema, before any migration): `psql "$DATABASE_URL" -f scripts/sql/unit-restructuring-profile.sql`; record the results in the rehearsal record (unit-analysis section 22.1). Findings (labels, factors above 3 dp, products without a factor-1 mode) must be resolved or accepted before the script is generated.
6. Production-copy checks (read-only, run on a restored copy first): P-blocks in phase-1-design.md section 3 (P0-P4; `BusinessSettings` must hold at most one row), phase-5-design.md (P1 users per role/membership, P2 documents per branch), phase-6-design.md (P1 Active Administrators per company: the earliest becomes Owner, owner confirms), phase-7-design.md (P1 AssignedBranches users losing role management). Record owner sign-off on the Phase 5 O2 outcome: after release every non-Administrator user is restricted to the Main branch; owners/accountants that are not the system Administrator become branch-only until granted CompanyWide.
7. Review guards. Several migrations RAISE EXCEPTION inside the transaction and roll back; read the message, do not edit the script to bypass:
   - Company/units migrations: V1/V2/V6 count and orphan checks (`AddCompanyIdToCompanyTables`), plus the `ExtendUnitMaster`/`IntroduceProductUnits`/snapshot migrations' own asserts. Fix data (duplicate or non-numeric document numbers, multiple BusinessSettings rows) on the live DB, re-snapshot, re-generate.
   - G3 (`AddBranchIdToDocuments`): a company without a default branch. G4 (`ScopeDocumentSequencesToBranches`): conflicting branch-level counter row.
   - G5 (`AddStockLocations`): company without exactly one default branch. G6 (`AddWarehousesAndVehicles`): orphan warehouse/vehicle-type location.
   - Phase 5 G1-G3 (every company has one active default branch; no membership already AssignedBranches; every company keeps an Active Administrator member). Phase 6 G1/G2 (no `Company.Governance.%` permission rows yet; every company has an owner candidate).
   - Phase 8 G1/G2 (each stock-owning company has one default branch with a BranchStore; per-product stock invariants clean) and Phase 9 G0-G3 (one Transit location per company; no stock at Transit).
   Handling: abort, restore nothing (the transaction already rolled back), fix the offending data or escalate to the database-engineer, and re-run from step 2.1. Never disable a guard.

## 3. Deploy backend

1. Stop traffic to the API (App Service stop) or announce the window.
2. Snapshot (section 2.1). Apply: `psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f release-migrations.sql`. (If a lock timeout is hit, retry; the script is idempotent.)
3. Validation SQL, in this order; every query MUST return zero rows. psql exits 0 even when a query returns rows, so the check is the output itself: with `-t -A` a healthy file prints nothing (the files contain no meta-commands), and any output means STOP.
   - `for f in unit-restructuring-validation stock-reconciliation stock-transfers-validation van-operations-validation; do psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -t -A -f "scripts/sql/$f.sql" > "validation-$f.txt" || exit 1; test ! -s "validation-$f.txt" || { echo "STOP: $f returned rows"; cat "validation-$f.txt"; exit 1; }; done`
   - Files: `unit-restructuring-validation.sql` (U1-U12), `stock-reconciliation.sql` (S1-S8, C1-C7), `stock-transfers-validation.sql` (V1-V8), `van-operations-validation.sql` (R1-R5). Keep the `validation-*.txt` files outside the repo.
   Each migration also asserts its own V-block in-transaction; those results are part of the script run.
   Non-empty output: do not deploy; capture the rows (ids only), go to section 5 (DB restore) unless the cause is understood and fixable by the owner of the data.
4. `psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/sql/seed-permissions.sql` (idempotent, grants nothing; the Administrator role receives all codes through `PermissionResolver`). Rerun on every release.
5. First platform admin: `psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -v username=<operator> -v reason='Platform operator onboarding' -f scripts/sql/grant-platform-admin.sql`. This is the only way platform administrators are created. Choose a dedicated operator account that is NOT a company member (script warns on plain membership and refuses an Active company governance member or a Disabled user). Create that user first through the normal user flow if none exists; one run per operator; idempotent; writes a `platform.admin_granted` audit row.
6. App Service configuration (names only; values from your secret store): `ConnectionStrings__SelloraDb`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey` (base64), optional `Jwt__AccessTokenMinutes`, `Jwt__RefreshTokenDays`; `Database__ApplyMigrationsOnStartup=false`; `Cors__AllowedOrigins__0`, `__1`, ... (see section 4; empty list fails closed with a warning, `Program.cs:112`); `Platform__CompanyCreationEnabled` (keep false until verification and owner sign-off), `Platform__ReferenceDataEditableByCompanies` (keep false; see Gap 2), `Platform__SessionMinutes` (clamped 1-30); `Swagger__Enabled` (leave unset/false in production).
7. Deploy the API. Push-based deploys: `.github/workflows/dev_sellora-billing-dev-api.yml` (push to `dev`, `backend/**`, or manual workflow_dispatch; app `sellora-billing-dev-api`) and `.github/workflows/dev_sellora-billing-dev-api-claude.yml` (push to `claude_dev`; app `sellora-billing-dev-api-claude`). Both run restore, build, test, publish, deploy. The deploy is triggered by a push; per project rules pushes to `dev` are done only on the owner's instruction. Required GitHub secrets (names): `AZUREAPPSERVICE_CLIENTID_492AC88EFD674BFB80BF2238823AA7AD`, `AZUREAPPSERVICE_TENANTID_730DE1AEAA364A79BBC165C6BA253DDD`, `AZUREAPPSERVICE_SUBSCRIPTIONID_39EB3DF310524F6AB2C51C97DF685964`.
8. Backend smoke: `GET /api/v1/auth/me` after login returns companies, governance and `permissionsAnyBranch`; refresh token works (pre-release tokens with no company resolve to the default company); settings GET/PUT; create a purchase and a sale at Main; document numbers show the branch series; transfer create -> approve -> dispatch -> receive via API; one sales return and one purchase return; re-run all four validation SQL files (zero rows).

## 4. Frontend

1. CORS first (Q9 Option B, direct API calls with CORS; `phase-0-audit-and-plan.md` Q9). Set `Cors__AllowedOrigins__0=https://<swa-host>` (`__1..n` for custom domain / preview hosts) on the App Service and restart the API. Allowed request headers (`Program.cs:96`): `Authorization`, `Content-Type`, `Idempotency-Key`, `X-Branch-Id`, `X-Correlation-Id`.
2. Build-time API URL: GitHub Environment or repository variable `SELLORA_API_BASE_URL` (variable, not a secret), consumed by the step "Set API base URL" in `.github/workflows/azure-static-web-apps-wonderful-bay-04d447700.yml:22-26`, which runs `node frontend/scripts/set-api-base-url.mjs`.
   - Unset or empty: relative `/api/v1` is kept (unchanged behaviour).
   - Set: must be `https://<host>` (no `/api/v1`, no trailing slash needed; a trailing `/api/v1` and slashes are stripped; quotes or non-https exit 1 and fail the deploy). Resulting value `https://<host>/api/v1`.
   - Local check: `SELLORA_API_BASE_URL=https://<host> node frontend/scripts/set-api-base-url.mjs` rewrites `frontend/src/environments/environment.ts` in the working tree; run `git checkout frontend/src/environments/environment.ts` afterwards.
   - SWA workflow trigger: push or PR to `claude_dev`. Required secret (name): `AZURE_STATIC_WEB_APPS_API_TOKEN_WONDERFUL_BAY_04D447700`.
3. Deploy 11a (after backend step 8 passes), smoke, then deploy 11b. CI (`.github/workflows/ci.yml`) already builds and tests the frontend (`npm run build`, `npx ng test --watch=false`; 1MB bundle budget; 11a initial about 402 kB).
4. Smoke list (use a CompanyWide Administrator and a branch-restricted user):
   - Login, refresh across two tabs, logout in one tab ends the other.
   - Company switch (if the user has >1 membership) and branch switch; "All branches" mode; permissions and nav change on switch without re-login; document-creating routes prompt for a branch in All-branches mode.
   - POS sale at the store, and at a van (location selector); purchase received into a warehouse; stock adjustment at a chosen location.
   - Transfer lifecycle: create, approve, dispatch (including a line dispatched as 0), receive (partial then resolve shortage); Transit rows appear in stock detail `locations[]`.
   - Sales return at the store and a return of a van sale; purchase return; cancel a sale at an inactive location is refused (expected).
   - Reports per location (stock overview, stock movement, dashboard); location filters.
   - Unit-analysis Phase 7: stock overview, stock detail and the current-stock report show base-unit decimals (for example a kg product with 3 dp, no silent rounding) and the packaging breakdown (for example "1 Full Carton, 2 Box, 2 Piece"); dashboard low stock shows decimals.
   - Admin screens: branches, warehouses, vehicles (status change prompts for a reason), stock locations (read-only), user access, roles, governance.
   - Platform console (separate login): companies, units, currencies; `Platform__CompanyCreationEnabled` remains false so company creation is refused.
   - Browser network tab: calls go to `https://<api-host>/api/v1/...`, OPTIONS preflight succeeds, responses carry `X-Correlation-Id`.

## 5. Rollback

| Situation | Action |
|---|---|
| Frontend only broken | Clear `SELLORA_API_BASE_URL` or redeploy the previous SWA build (re-run the workflow on the previous commit). If CORS was the failure, fix `Cors__AllowedOrigins__*` and restart. |
| Backend deployed, DB script applied, no business writes yet (smoke only) | Prefer restore. App-only rollback is unsafe once the new schema is live: old builds do not populate `BranchId` (Phase 2 B2/B3 incompatibility), old middleware fails AssignedBranches closed (Phase 5), users created with `RoleId NULL` break old auth (Phase 7). |
| Validation SQL non-empty, or smoke finds data damage | Restore the pre-script snapshot/point-in-time to a new server, repoint `ConnectionStrings__SelloraDb`, redeploy the previous API build (previous commit of the deploy workflow). Business writes made after the snapshot are lost: export them first if any exist. |
| Guard RAISE during script | Transaction rolled back; nothing to restore. Fix data, retry. |
| Down migrations | Down is a last resort for non-production rehearsal. Guarded Downs refuse (RAISE) when post-release data would be lost: company folds (D-G, multiple companies), branches (D-G2), `AddStockLocations` (D-G3), warehouses/vehicles (D-G4), assignments (Phase 5), governance/platform admins (Phase 6), scoped roles (Phase 7, any Branch-scope role or NULL `RoleId`), `AddStockLocationDimension` (Phase 8, refuses once more than one location holds stock), transfers. In production the answer is always snapshot restore. |
| Platform admin granted by mistake | `PlatformAuditEvents` is append-only (trigger); revoke via the platform console / governance flow, not by deleting rows. |

## 6. Known limitations and post-release notes

- Average cost is rounded to 6 dp at each Transit hop, so company value can drift by rounding; quantities stay exact (V1-V8). Do not assert exact value conservation (phase-9-design.md R2/release note).
- Dashboard low-stock compares the visible-scope quantity with the product-level `ReorderLevel`; a branch-only reader may see low-stock for stock held elsewhere. Per-location reorder levels are later (phase-8-design.md Known limitations).
- No van trip/settlement document (settlement report goes to unit-analysis Phase 7); van cash is a branch account plus account transfer (phase-10-design.md O1-O9).
- Return-goods classification (arch 13.3) is deferred (phase-10-design.md O4).
- Status-change audit events (`*.status_changed`) carry no correlation id (services have no HTTP context; phase-11-design.md carry-over).
- Behaviour changes to announce: cancelling a sale whose location is inactive or whose van was reassigned is refused; returns of van sales may restock the branch store; a purchase return at an inactive location is refused (phase-10-design.md).
- Visibility change (Phase 5): non-Administrator users see only their allocated branches (Main) after release; grant CompanyWide to accountants/owners via the access screens, after reviewing P1.
- Phase 9 dispatch closes the undispatched remainder ("not shipped"; no backorder). Transit rows show in stock detail `locations[]` for company-level readers.
- `Users.RoleId` is nullable and unused for authorization; the column is removed in a later release (phase-7-design.md D4).
- Document-creating routes (incl. transfer new/edit and the van workspace) carry `requiresBranch`; detail pages opt into 403 -> `/permission-denied` via `primaryLoad()` (done in 11b). Background requests stay toast-only by design.
- Company creation stays disabled by configuration until owner sign-off (phase-7-design.md D12).

## 7. When the URLs arrive (phase-11-design.md D9)

1. Set `SELLORA_API_BASE_URL=https://<app-service-host>` (no `/api/v1`) on the GitHub Environment (or repo) variables.
2. Set App Service `Cors__AllowedOrigins__0=https://<swa-host>` (plus `__1..n` for custom domains/previews) and restart the API.
3. Re-run the SWA workflow (`.github/workflows/azure-static-web-apps-wonderful-bay-04d447700.yml`; push to `claude_dev` or re-run the workflow run).
4. Smoke: requests go to `https://<app-service-host>/api/v1/...`, OPTIONS preflight succeeds, branch switch works.
5. Rollback: clear the variable and re-run the workflow.

## Gaps found while writing this runbook

1. Resolved: `backend/src/Sellora.Api/appsettings.json:13` now ships `false` (code default also false, `Program.cs:125`); local dev keeps auto-migrate via `appsettings.Development.json` (copy of the tracked `.example`, which sets true). Remaining owner action: set `Database__ApplyMigrationsOnStartup=true` on the dev App Services if they should keep migrating (section 2.2); production keeps it false.
2. phase-7-design.md D11 says `Platform:ReferenceDataEditableByCompanies` defaults to true "this release", but `PlatformOptions.cs` and `appsettings.json:18` default to false. With false, company Units/Currencies writes are refused (403 `reference_data_platform_managed`) and writes go through the platform console; 11a made company units/currencies read-only (consistent). Resolved: the owner decided Phase 7 Q3 = platform-managed (`false`) and Phase 11 O7 = writes only in the platform console, so `false` is the intended production value; the D11 "default true" wording in phase-7-design.md predates that answer.
3. No committed script lists migrations since "last released"; the baseline must be read from `__EFMigrationsHistory` on the target (no release tag exists in git).
4. The two API deploy workflows run on push (`dev`, `claude_dev`) with no manual approval gate and no migration step; migrations and SQL scripts are manual operator steps by design (D10). The `dev` workflow does not trigger on frontend-only changes (paths `backend/**`).
5. Resolved: standalone validation now exists for the unit restructuring and stock reconciliation (`scripts/sql/unit-restructuring-validation.sql`, `scripts/sql/stock-reconciliation.sql`) plus Phase 9 and Phase 10 (`scripts/sql/stock-transfers-validation.sql`, `scripts/sql/van-operations-validation.sql`). The Phase 5-8 multi-company V-blocks still live only inside the migrations.

## Rehearsal record

| Field | Value |
|---|---|
| Date | 2026-11-12 (local rehearsal, automated) |
| Target | Local PostgreSQL via Testcontainers: `backend/tests/Sellora.IntegrationTests/Migrations/ReleaseRehearsalTests.cs`, baseline `20260920114635_AddIdempotencyKeysAndCancelledBy` (= origin/main) with a legacy retail seed (`TestData/LegacyRetailSeed.cs`: 5 products / 11 selling modes incl. one with no factor-1 mode, purchases, purchase return, FIFO sales, sales return, opening stock, 13 movements). Production-copy rehearsal: OWNER ACTION, still required |
| Script (path, line count, first/last migration) | `dotnet ef migrations script 20260920114635_AddIdempotencyKeysAndCancelledBy --idempotent` (outside the repo): about 6,065 lines, `20261104105655_ExtendUnitMaster` .. `20261111040145_AddStockTransfers`. Run 1 about 0.9 s, run 2 (re-apply) is a no-op |
| Profile results (`unit-restructuring-profile.sql`) | P9*/P10 zero rows; P2 lists the one product without a factor-1 mode (gets a new base row, as designed). Prod copy: TBD |
| Validation results (four SQL files) | All zero rows (U1-U12, S1-S8/C1-C7, V1-V8, R1-R5); history unchanged (prices, line counts, base quantities, QOH, totals); `seed-permissions.sql` idempotent. Prod copy: TBD |
| Smoke results | TBD (owner, after deploy) |
| Sign-off | TBD (owner) |
