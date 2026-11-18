# DB release automation

status: design-approved
<!-- draft → requirements-approved → design-approved → db-done → backend-done → frontend-done → reviewed → done -->
size: L

Review page (plan, workflow, UI prototype, answered questions): https://claude.ai/artifact/FyLELaZd9qLahrk2KvU1YQ
Owner answers 2026-11-18: all recommendations Q1–Q17 accepted. Q10 resolved (nothing to baseline, see Decisions D11).
Current step: **Delivery steps 2–4 implemented and reviewed on `feature/db-release-automation`.** Waiting for the owner actions below (claude-dev pipeline run = POC step 5, GitHub App). Step 5 (prod) not started.

## Problem
The deploy pipeline (`.github/workflows/_deploy-template.yml`) generates the idempotent EF migration script, publishes it as the
`migration-sql` artifact, and stops ("apply it manually"). Files in `scripts/sql/` are also run by hand. Nothing records what ran,
in which environment, or whether it failed, so scripts are regularly missed after a deploy.

## Scope
**In:**
- `Sellora.DbMigrator` console (`backend/tools/Sellora.DbMigrator`) that applies versioned SQL scripts and records every attempt.
- Journal tables in schema `ops`: `"DbReleaseRuns"`, `"DbScripts"`, `"DbScriptAttempts"` (created by the migrator, not EF).
- Script folders `backend/db/scripts` (one-time data), `backend/db/repeatable` (`R__`), `backend/db/validation` (`V__`), each file with a header.
- `migrate-db` job in `_deploy-template.yml` between test and API deploy: EF migration bundle → migrator. API deploy blocked on failure.
- CI checks on PRs: naming, applied scripts unchanged, scripts run on a throwaway Postgres.
- Read-only API for the platform console + `/platform/db-releases` page (platform admins only).
- Retry from the page (delivery step 4): starts the pipeline via `workflow_dispatch`, audited, reason required.
- Turn off `Database__ApplyMigrationsOnStartup` on every Azure environment once the job works (Q4).

**Out:**
- Any screen that runs free-text SQL. Ad-hoc queries use pgAdmin / Azure Data Studio with a read-only role.
- Automating operator scripts with parameters (`grant-platform-admin.sql`, `delete-product.sql`): they stay runbooks.
- Automatic rollback of succeeded scripts. A fix is always a new forward script.
- Per-tenant databases (one database per environment, shared by all companies).

## Business rules
- R1 Order per run: EF migrations → data scripts (name order) → repeatable scripts (name order) → validation scripts (name order).
- R2 Only one migrator runs per database at a time (PostgreSQL advisory lock). A second run waits up to the lock wait, then exits.
- R3 Each data/repeatable script runs in its own transaction unless its header says `transaction: none`.
- R4 The run stops at the first failed data/repeatable script. Later scripts are not run; the API deploy is blocked (Q7).
- R5 A data script that Succeeded (or was Baselined) is final. If its file changes, the run fails **before executing anything**
  with "applied script modified". The fix is a new script.
- R6 A data script that Failed may be fixed in place only if it never succeeded in any environment (Q11). Within one database the
  migrator can only enforce "never succeeded here"; the cross-environment part is enforced by CI against the release branch history.
- R7 A repeatable script runs when it has never succeeded or its checksum differs from the last success.
- R8 A validation script is read-only (runs in a `READ ONLY` transaction) and runs every time. Any returned row is a violation.
  Header `on-violation: warn|block`; default warn in dev/claude-dev, block in prod (Q9).
- R9 Default script timeout 10 minutes, overridable with header `timeout:` (Q13). Default lock timeout 30 s, header `lock-timeout:`.
- R10 Naming (Q12): `yyyy.MM.dd.nnn__kebab-description.sql`, `R__kebab-description.sql`, `V__kebab-description.sql`.
- R11 Every attempt is recorded with checksum, duration, rows affected, SQLSTATE and error message. Error `Detail` is **not**
  stored because it can contain row values (customer data); message, hint, where, schema/table/column/constraint are stored.
  Validation sample rows (ids) are kept in the journal only; pipeline logs and the GitHub step summary show counts.
- R12 The API never runs DDL or scripts. Retry only re-runs scripts already in the deployed release package.

## Edge cases
- Script file contains psql meta-commands (`\if`, `\echo`, `:var`): rejected at load time with the line number.
- `CREATE INDEX CONCURRENTLY` cannot run in a transaction: needs `transaction: none`, and such a script must hold **exactly one
  statement** (Npgsql sends several as one implicit transaction; the failure message says so). One index per script.
- Validation script returns rows across several statements: violations are summed over all result sets.
- Runner killed mid-run: the advisory lock is released with the session; the run row stays `Running` and the next run marks
  stale `Running` rows as `Failed` ("interrupted").
- Empty script folders: the run succeeds with nothing to do.
- Journal schema missing (first run): created idempotently.

## Acceptance criteria
- AC1: Given a release with new data scripts, When the deploy pipeline runs, Then every script is applied in name order with no manual step and recorded `Succeeded`.
- AC2: Given a data script that fails, When the migrator runs, Then its transaction is rolled back, the attempt is recorded with SQLSTATE and message, later scripts are not run, the process exits non-zero and the API is not deployed.
- AC3: Given the failed script was fixed in place, When the migrator runs again, Then it resumes from that script and the attempt count increases.
- AC4: Given an applied data script whose file changed, When the migrator starts, Then it fails before executing any script with "applied script modified: <name>".
- AC5: Given two migrators start at the same time, When both run, Then only one executes scripts; the other waits and then finds nothing to do (or times out).
- AC6: Given a repeatable script, When it is unchanged Then it is skipped; When it changed Then it runs again.
- AC7: Given a validation script returns rows, When `on-violation: warn` Then the run succeeds with a Warning; When `block` Then the run fails.
- AC8: Given a platform admin opens `/platform/db-releases`, Then they see the latest run per environment, counts by status, each script with attempts, duration and last error. Non-platform users get 403.
- AC9: Given a failed run, When a platform admin presses Retry with a reason, Then the pipeline `migrate-db` job is started, the request is audited, and the page shows the new run.
- AC10: Given the job is live on an Azure environment, Then `Database__ApplyMigrationsOnStartup` is false there and the API does not migrate on startup.

## Impact
DB: new schema `ops` with 3 tables (owned by the migrator) · API: read endpoints + retry under platform console ·
UI: `/platform/db-releases` · Permissions: platform admin only (no company permission) · Reports: none ·
CI/CD: `ci.yml` checks, `_deploy-template.yml` `migrate-db` job, GitHub environment protection for prod.

## Open questions
- ~~OQ1~~ resolved → D14. POC step 5: which identity/connection string does the pipeline use for claude-dev (needs DDL rights), and may the job open the
  Postgres firewall to the runner IP for its duration?
- OQ2 GitHub token for page retry (step 4): fine-grained PAT vs GitHub App, and where it is stored (Key Vault / App Service setting).

## POC (timebox 2–3 days, local Postgres + claude-dev)
| # | Step | Proves | Result |
|---|---|---|---|
| 1 | Migrator against local Postgres with sample scripts: success, failure, repeatable, validation | Failure recorded, rollback, stop at failure | ✅ 23502 recorded with table/column, update rolled back (3 null rows remained), 003+ not run, exit 1 |
| 2 | Fix the failing script and re-run; modify an applied script | Retry; applied-script protection | ✅ Retry resumed at 002 (attempts = 2) and finished; edited 001 → "applied script modified", nothing executed, exit 2; changed `R__` re-ran, unchanged skipped |
| 3 | Two migrators at once | Advisory lock | ✅ B waited for A (4 s script), then found only validation to run; `--lock-wait 1` → exit 3 |
| 4 | EF migrations + SQL scripts in one run against the real Sellora schema | Order, one journal view, real validation scripts | ✅ Fresh DB: EF bundle applied 39 migrations, `R__seed-permissions` seeded 83 permissions, the 4 real validation scripts passed; second run skipped the seed |
| 5 | `migrate-db` job in the claude-dev pipeline reaching Azure Postgres | Network, credentials, deploy blocked on failure | ⏳ waits on OQ1 |
| 6 | Read the journal with SQL | Data is enough for the page | ✅ Runs, scripts, attempts (SQLSTATE, message, duration, rows, retry reason) cover every prototype field |

Also verified: `timeout: 1` on a 3 s script → 57014 recorded, exit 1; validation `warn` → Warning + exit 0, `block` → exit 1;
a run left `Running` is closed as Failed ("Interrupted") by the next run; bad file name → exit 2 before executing.
Unit tests: `backend/tests/Sellora.Tests/DbMigrator/ScriptPlannerTests.cs` (planning + header parsing, 17 cases).

### POC findings
- F1 **DbUp would add little.** It gives ordering and a success-only journal. We would have to replace its journal, add checksums,
  run-on-change, failure recording, read-only validation with row capture and the advisory lock ourselves. The POC runner does all
  of that in ~800 lines of plain Npgsql (already a dependency) with no extra package. Owner confirmed: D13 changed to "own runner on Npgsql".
- F2 Scripts removed from the package (replaced failed scripts, deleted validations) kept showing as Failed. Added status `Removed`
  (succeeded data scripts keep `Succeeded`: they are history).
- F3 The EF bundle has its own migration lock (EF 9+) and logs it once per migration; the migrator hides that noise and the normal
  "history table does not exist yet" error on a fresh database.
- F4 The bundle step is recorded as one `ef-migration-bundle` row; individual EF migrations are mirrored from `__EFMigrationsHistory`
  and linked to the run that applied them.
- F5 The bundle is ~110 MB self-contained (linux-x64). Build it once in the build job and pass it as an artifact.
- F6 Not covered yet: Postgres integration tests (Testcontainers) for rollback/lock/timeout — Docker was not available in the POC
  environment. Planned for delivery step 2.
Passes when a claude-dev deploy applies scripts with no manual step, a failure blocks the API deploy and is recorded with its error,
and a re-run after the fix succeeds.

## Delivery after the POC
1. Spec + DB design approved (this file → `design-approved`).
2. Migrator hardened, pipeline job for dev + claude-dev, CI checks, startup auto-migrate off. **Missed scripts stop here.**
3. Read API + platform page (view only, Q8).
4. Retry from the page, failure notifications (Q16).
5. Prod: approval gate, backup step, runbook.

## Database design
Draft (from the approved plan; confirmed or changed by the POC). Schema `ops`, PascalCase quoted names, `uuid` ids, string enums.

```
ops."DbReleaseRuns"   Id uuid PK, Environment text, CommitSha text, AppVersion text, Trigger text (Deploy|Retry),
                      TriggeredBy text, RetryReason text NULL, Status text (Running|Succeeded|Failed),
                      StartedAt timestamptz, CompletedAt timestamptz NULL, PipelineUrl text NULL, Summary text NULL
ops."DbScripts"       Id uuid PK, Name text UNIQUE, Kind text (EfMigration|Data|Repeatable|Validation), Description text NULL,
                      Checksum text NULL (sha256 of last success), Status text (Pending|Succeeded|Failed|Baselined|Passed|Warning|Removed),
                      AttemptCount int, LastAttemptAt timestamptz NULL, AppliedAt timestamptz NULL
ops."DbScriptAttempts" Id uuid PK, ScriptId uuid FK, ReleaseRunId uuid FK, Checksum text, Status text
                      (Succeeded|Failed|Passed|Warning), StartedAt timestamptz, DurationMs int, RowsAffected int NULL,
                      SqlState text NULL, ErrorMessage text NULL, Sql text NULL (D19: executed SQL, added in step 3)
```
EF migrations keep `__EFMigrationsHistory`; the migrator mirrors applied ones into `"DbScripts"` (Kind `EfMigration`).

## API contract
Final for delivery step 3 (view only; retry endpoint comes in step 4). Platform session (`[PlatformEndpoint]`,
policy `PlatformPolicies.Admin`); each API reads the journal of **its own** database, so the page shows one environment.

| Method | Route | Permission | Request | Response | Errors |
|---|---|---|---|---|---|
| GET | /api/v1/platform/db-releases | Platform admin | `?runs=20` (1–100) | `DbReleasesOverviewDto` | 401, 403 |
| GET | /api/v1/platform/db-releases/runs/{id} | Platform admin | — | `DbReleaseRunDetailDto` | 401, 403, 404 |
| GET | /api/v1/platform/db-releases/scripts/{id} | Platform admin | — | `DbScriptDetailDto` | 401, 403, 404 |

```
DbReleasesOverviewDto  { journalAvailable: bool, runs: DbReleaseRunDto[] (newest first), scripts: DbScriptDto[] }
DbReleaseRunDto        { id, environment, commitSha?, appVersion?, trigger: "Deploy"|"Retry", triggeredBy?, retryReason?,
                         status: "Running"|"Succeeded"|"Failed", startedAt, completedAt?, pipelineUrl?, summary? }
DbScriptDto            { id, name, kind: "EfMigration"|"Data"|"Repeatable"|"Validation", description?,
                         status: "Pending"|"Succeeded"|"Failed"|"Baselined"|"Passed"|"Warning"|"Removed",
                         attemptCount, lastAttemptAt?, appliedAt?, lastDurationMs?, lastSqlState?, lastError? }
DbScriptAttemptDto     { id, scriptId, scriptName, scriptKind, releaseRunId, status, startedAt, durationMs?, rowsAffected?,
                         sqlState?, errorMessage?, checksum? }
DbReleaseRunDetailDto  { run: DbReleaseRunDto, attempts: DbScriptAttemptDto[] (execution order) }
DbScriptDetailDto      { script: DbScriptDto, sql?: string (text of the latest attempt; null for EF migrations), attempts: DbScriptAttemptDto[] (newest first) }
```
`journalAvailable = false` (schema `ops` absent: the migrator never ran on this database) → empty lists, not an error.

**Step 4 additions (retry, D20–D23):**

| Method | Route | Permission | Request | Response | Errors |
|---|---|---|---|---|---|
| POST | /api/v1/platform/db-releases/retry | Platform admin | `{ environment, reason }` | 202 `DbReleaseRetryResponseDto` | 400 (validation), 409 `retry_not_allowed`, 502 `github_rejected`, 503 `retry_not_configured` |

```
DbReleasesOverviewDto  + retry: DbReleaseRetryInfoDto
DbReleaseRetryInfoDto  { configured: bool, canRetry: bool, unavailableReason?: string,
                         lastRequest?: { requestedBy: string, reason: string, requestedAt } }   // last request for the latest run
RetryDbReleaseRequest  { environment: string (must equal the latest run's environment), reason: string (10–500 chars) }
DbReleaseRetryResponseDto { runId (journal run being retried), pipelineUrl, requestedAt }
```
`canRetry` = configured AND the latest run is `Failed` AND its `pipelineUrl` is a run of the configured repository AND no retry was
requested for that run in the last 10 minutes. `unavailableReason` explains a false `canRetry` in one sentence. 400 field errors use
`environment` / `reason`; 409/502/503 are ProblemDetails with `extensions.code` and a human `title`.

Scripts are ordered EfMigration, Data, Repeatable, Validation, then by name. Dates are ISO 8601 UTC.

## Decisions
- D1 (Q1) dev + claude-dev now; prod later on Azure Database for PostgreSQL Flexible Server.
- D2 (Q2) POC runs the migrator on the GitHub runner with a firewall rule for the job only; move to an Azure Container Apps Job in the VNet later.
- D3 (Q3) EF migrations run in the same job via an EF migration bundle, before the SQL scripts.
- D4 (Q4) Startup auto-migrate off on all Azure environments once the job works; stays on for local development.
- D5 (Q5) dev/claude-dev migrate automatically; prod waits for GitHub environment approval.
- D6 (Q6) Rely on point-in-time restore, record the restore timestamp on the run; `pg_dump` only before prod releases with destructive scripts.
- D7 (Q7) A failed script blocks the API deploy.
- D8 (Q8) First version of the page is view only; retry via GitHub "Re-run failed jobs". Page retry in delivery step 4.
- D9 (Q9) Validation scripts choose warn/block in their header; default warn in dev, block in prod.
- D10 (Q11) A failed script may be fixed in place only if it never succeeded in any environment; otherwise add a new script.
- D11 (Q10) Nothing to baseline: of today's `scripts/sql` files, 5 are read-only checks, 1 is an idempotent seed, 2 are operator
  runbooks; EF migrations track themselves. `Baselined` status is kept for future manual applies.
- D12 (Q12) Date + sequence naming. (Q13) 10 min default timeout. (Q15) Platform admins only. (Q16) GitHub failure email + page status.
  (Q17) Page shows script SQL read only.
- D13 (Q14, revised after POC F1, owner confirmed 2026-11-18) Tool: own runner on Npgsql (`Sellora.DbMigrator`). No DbUp/Flyway dependency.
- D14 (OQ1) Pipeline login `sellora_migrator` (member of the role that owns the tables, `SET role` to it so ownership does not
  change), connection string in GitHub environment secret `SELLORA_DB_MIGRATOR_CONNECTION`. The job adds a firewall rule for the
  runner IP and removes it in an `always()` step; the deploy identity gets Contributor on the Postgres server. Prod later: Entra
  token login + Container Apps Job in the VNet (D2).
- D15 The migrator uses an unpooled connection: a pooled session would keep the advisory lock after a run (found by the lock test).
- D16 CI guard `scripts/check-db-scripts.sh` blocks edits to merged data scripts on PRs; label `db-script-fix` overrides it for a
  script that failed everywhere (D10). The migrator still refuses a modified succeeded script per database.
- D17 `migrate-db` is opt-in per caller (`run_migrations` input): on for claude-dev, off for dev until a claude-dev run is green.
- D18 Step 3 contract: three read endpoints (overview, run detail, script detail) instead of the four drafted; one call loads the page.
- D19 Q17 "show the SQL": the migrator stores the executed SQL on each attempt (`"DbScriptAttempts"."Sql"`, added with
  `ADD COLUMN IF NOT EXISTS`), so the page shows exactly what ran, even when the API version differs from the migrated release.
- D20 (OQ2) Page retry uses a **GitHub App** (owner decision 2026-11-18), repository permission **Actions: read & write** only,
  installed on this repository. Config section `GitHubApp`: `AppId`, `InstallationId`, `PrivateKey` (PEM; App Service setting or
  Key Vault reference, never in the repo), `Owner`, `Repository`. Not configured → page shows retry as unavailable (503 on POST).
- D21 Retry = GitHub "re-run failed jobs" of the **latest failed pipeline run** (`POST /repos/{o}/{r}/actions/runs/{id}/rerun-failed-jobs`),
  not `workflow_dispatch`: a dispatch would deploy the branch head, possibly a different release; a re-run keeps the same release
  package (R12). The run id comes from the journal's `PipelineUrl`, accepted only when it points at the configured repository.
  The migrator then records the run as `Retry` ("GitHub re-run, attempt N"); the platform user and reason go to the platform audit
  (`platform.db_release_retry_requested`, target `DbReleaseRun`). GitHub keeps re-runs possible for 30 days; older → 502 with GitHub's message.
- D22 One retry per run per 10 minutes (409), so double clicks and impatient repeats do not queue several re-runs.
- D23 Notifications (Q16): GitHub already emails the actor of a failed workflow and the page shows the red status; nothing extra is
  built in step 4.
- File moves (done in step 2): `seed-permissions.sql` → `backend/db/repeatable/R__seed-permissions.sql`; `stock-reconciliation.sql`,
  `stock-transfers-validation.sql`, `unit-restructuring-validation.sql`, `van-operations-validation.sql` → `backend/db/validation/V__*.sql`;
  `unit-restructuring-profile.sql`, `grant-platform-admin.sql`, `delete-product.sql` stay in `scripts/sql/`. Done in delivery step 2.
- File moves after merging `analyse_copy` (functional improvements, PR 23): `transit-reconciliation.sql`, `stock-reservations-reconciliation.sql`,
  `cash-bank-funding-validation.sql`, `stock-items-negative-check.sql` → `backend/db/validation/V__*.sql` (environment default);
  `cash-bank-negative-balances.sql` → `V__cash-bank-negative-balances.sql` with `-- sellora:on-violation warn` (informational: accounts below
  zero from before Phase 07 are expected, so it must never block a prod deploy). Test helpers and `docs/functional-improvements/RELEASE.md` point there.

## Deployment notes
### Delivery step 2 — what changed
- `backend/db/` is the release package: `repeatable/R__seed-permissions.sql`, `validation/V__*.sql` (4 files), `scripts/` (empty).
- `.github/workflows/_deploy-template.yml`: build job bundles EF migrations + publishes the migrator (`db-release` artifact);
  new `migrate-db` job; `deploy-api` runs only if `migrate-db` succeeded or was skipped. `deploy-claude-dev.yml` sets `run_migrations: true`.
- `.github/workflows/ci.yml`: `db-scripts` job on PRs. Integration test `Repository_release_package_applies_to_the_current_schema_and_reruns_cleanly`
  runs the real package against a migrated Postgres in CI (naming, psql meta-commands, validation rows all fail it).
- `scripts/sql/create-migrator-role.sql`: operator script for the login (idempotent).

### Code review (delivery step 2)
`code-reviewer`: 1 Blocker, 3 Major, 6 Minor — all fixed except Minor 9 (accepted: "Re-run all jobs" is also journaled as Retry).
Blocker: two validation helpers still returned the old `scripts/sql` path. Majors: `transaction: none` multi-statement behaviour
(now documented + tested), role-script grants (now to `owner_role`, owner checks added), journal row lost when rollback/reset or
cancel threw (now best-effort cleanup, journal writes ignore cancellation). Minors fixed: EF bundle connection via environment
instead of argv, no row values in logs/summary + HTML-escaped summary, `--plan` writes nothing, firewall rule cleanup always runs
+ connect retry for rule propagation, every unexpected exception exits 4.

### Delivery step 3 — read API + Database releases page
- API: `PlatformDbReleasesController` (3 GETs, raw parameterised SQL over the journal, platform admin only; route registry test now
  lists 15 platform routes). Contracts in `Contracts/DbReleaseContracts.cs`.
- UI: `/platform/db-releases` (`features/platform/platform-db-releases.*`): latest run card, filter tiles, script list (EF migrations
  collapsed), script panel with attempts + executed SQL, recent runs with per-run attempts, empty state when the journal is absent.
- Migrator: stores the executed SQL per attempt (D19). Existing journals get the column on the next migrator run; until then the
  page shows no SQL for older attempts.
- Review (`code-reviewer`): no Blocker; fixed the 2 Majors (403/401 codes asserted exactly; SQL text no longer loaded for every
  attempt) and Minors 3–4 (composition comment; run panel closable while loading, unique test ids). Minor 5 (https-only pipeline
  link) not done: Angular already sanitises `[href]` and the URL is written by the pipeline.
- Not run here (no Docker): `PlatformDbReleasesApiTests` and the migrator Testcontainers tests — CI runs them. The controller queries
  were executed through EF against a real Postgres journal.

### Delivery step 4 — retry from the page (GitHub App)
- Backend: `IGitHubActionsClient` / `GitHubActionsClient` (app JWT RS256 → installation token → `rerun-failed-jobs`; network errors,
  timeouts and refusals become 502, never 500; `ApiBaseUrl` must be https), `DbReleaseRetryService` (rules, audit), `POST /retry`,
  `overview.retry`. Audit actions `platform.db_release_retry_requested` / `platform.db_release_retry_failed` (no schema change).
- Concurrency: a transaction-scoped advisory lock serialises the window check and the "requested" audit row, which is committed
  **before** GitHub is called; a GitHub refusal appends "failed" and releases the window. Tested: 4 simultaneous requests → 1 re-run.
- UI: "Retry migration" on the failed latest-run card (and in the script panel only for a script that failed in that run), inline
  confirm with required reason (10–500), last request shown, unavailable reason shown when retry is not possible.
- Review (`code-reviewer`): no Blocker; both Majors fixed (retry race; unmapped network errors). Minors fixed: `\z` anchor + https-only
  base URL, missing actor claim → 403 instead of 500, script-panel button scoped to the latest run, DI comment. Accepted: overview
  runs 2–3 extra queries for retry info.
- Not run here (no Docker): `DbReleaseRetryTests` / `PlatformDbReleasesApiTests` under Testcontainers — the same retry scenarios
  passed against a local PostgreSQL 16 with the full EF schema.

### Owner actions for page retry (GitHub App, D20)
1. GitHub → Settings → Developer settings → GitHub Apps → **New GitHub App**: name e.g. "Sellora DB releases", homepage = repo URL,
   **Webhook: off**, Repository permissions: **Actions: Read and write** (Metadata read is automatic), nothing else.
   "Where can this app be installed": only this account.
2. Generate a **private key** (.pem), note the **App ID**. Install the app on `sellora-billing` only; the installation id is the
   number at the end of the installation's settings URL.
3. App Service `sellora-billing-dev-api-claude` → Configuration: `GitHubApp__AppId`, `GitHubApp__InstallationId`,
   `GitHubApp__Owner=aruntalexanderdev`, `GitHubApp__Repository=sellora-billing`, `GitHubApp__PrivateKey` — preferably a Key Vault
   reference (`@Microsoft.KeyVault(SecretUri=…)`); a plain setting also works (`\n` escapes are accepted). Restart the app.
4. Check: the page's failed run shows "Retry migration" instead of "not set up". Without these settings the page stays view only.

### Owner actions before the first claude-dev run (POC step 5)
1. Generate a password and run, as the server admin:
   `psql "$ADMIN_DATABASE_URL" -v ON_ERROR_STOP=1 -v password='<generated>' -v owner_role=<API login> -f scripts/sql/create-migrator-role.sql`
2. GitHub → Settings → Environments → `claude-dev`: secret `SELLORA_DB_MIGRATOR_CONNECTION`
   (`Host=<server>.postgres.database.azure.com;Database=<db>;Username=sellora_migrator;Password=<generated>;Ssl Mode=Require`),
   variables `AZURE_POSTGRES_SERVER`, `AZURE_POSTGRES_RESOURCE_GROUP`.
3. Azure: give the claude-dev deploy managed identity **Contributor** on the Postgres Flexible Server (firewall rules).
4. App Service `sellora-billing-dev-api-claude`: set `Database__ApplyMigrationsOnStartup=false` (D4). The repo default is already false.
5. Merge to `claude_dev` and run **Deploy claude-dev** manually. Expected: `migrate-db` green with a "Database scripts" job summary,
   then the API deploy. Note: the legacy push-triggered `dev_sellora-billing-dev-api-claude.yml` deploys without migrating; retire it
   as `deploy-claude-dev.yml` already plans.

### Running the migrator locally (macOS/Linux, any Postgres 16)
```bash
dotnet publish backend/tools/Sellora.DbMigrator -c Release -o /tmp/migrator
SELLORA_DB_CONNECTION="Host=localhost;Database=sellora;Username=postgres;Password=postgres" \
  dotnet /tmp/migrator/Sellora.DbMigrator.dll --scripts backend/db --environment local --plan
```
Options: `--ef-bundle <path>`, `--trigger Deploy|Retry --reason <text>`, `--validation-default warn|block`, `--lock-wait <s>`,
`--commit`, `--version`, `--triggered-by` (defaults from GitHub Actions variables). Exit codes 0 ok · 1 failed · 2 invalid release · 3 lock timeout · 4 unexpected (e.g. cannot connect).
