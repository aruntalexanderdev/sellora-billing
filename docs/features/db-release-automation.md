# DB release automation

status: requirements-approved
<!-- draft → requirements-approved → design-approved → db-done → backend-done → frontend-done → reviewed → done -->
size: L

Review page (plan, workflow, UI prototype, answered questions): https://claude.ai/artifact/FyLELaZd9qLahrk2KvU1YQ
Owner answers 2026-11-18: all recommendations Q1–Q17 accepted. Q10 resolved (nothing to baseline, see Decisions D11).
Current step: **POC done locally (steps 1–4, 6); step 5 waits on OQ1.** Next gate: confirm D13 + approve the design → `design-approved`.

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
- R12 The API never runs DDL or scripts. Retry only re-runs scripts already in the deployed release package.

## Edge cases
- Script file contains psql meta-commands (`\if`, `\echo`, `:var`): rejected at load time with the line number.
- `CREATE INDEX CONCURRENTLY` cannot run in a transaction: needs `transaction: none`.
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
- OQ1 POC step 5: which identity/connection string does the pipeline use for claude-dev (needs DDL rights), and may the job open the
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
  of that in ~800 lines of plain Npgsql (already a dependency) with no extra package. Proposed: change D13 to "own runner on Npgsql".
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
                      Checksum text NULL (sha256 of last success), Status text (Pending|Succeeded|Failed|Baselined|Passed|Warning),
                      AttemptCount int, LastAttemptAt timestamptz NULL, AppliedAt timestamptz NULL
ops."DbScriptAttempts" Id uuid PK, ScriptId uuid FK, ReleaseRunId uuid FK, Checksum text, Status text
                      (Succeeded|Failed|Passed|Warning), StartedAt timestamptz, DurationMs int, RowsAffected int NULL,
                      SqlState text NULL, ErrorMessage text NULL
```
EF migrations keep `__EFMigrationsHistory`; the migrator mirrors applied ones into `"DbScripts"` (Kind `EfMigration`).

## API contract
Draft, finalised in the design phase.
| Method | Route | Permission | Request | Response | Errors |
|---|---|---|---|---|---|
| GET | /api/v1/platform/db-releases/runs?take= | Platform admin | — | `DbReleaseRunDto[]` | 401, 403 |
| GET | /api/v1/platform/db-releases/runs/{id} | Platform admin | — | `DbReleaseRunDetailDto` (scripts + attempts of that run) | 401, 403, 404 |
| GET | /api/v1/platform/db-releases/scripts | Platform admin | — | `DbScriptDto[]` (current state) | 401, 403 |
| GET | /api/v1/platform/db-releases/scripts/{id}/attempts | Platform admin | — | `DbScriptAttemptDto[]` | 401, 403, 404 |
| POST | /api/v1/platform/db-releases/retry | Platform admin | `{ environment, reason }` | 202 `{ pipelineUrl }` | 400, 401, 403, 409 (run in progress) |

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
- D13 (Q14) Tool: DbUp with a custom journal. **POC proposes changing this to an own runner on Npgsql (F1) — awaiting owner confirmation.**
- File moves: `seed-permissions.sql` → `backend/db/repeatable/R__seed-permissions.sql`; `stock-reconciliation.sql`,
  `stock-transfers-validation.sql`, `unit-restructuring-validation.sql`, `van-operations-validation.sql` → `backend/db/validation/V__*.sql`;
  `unit-restructuring-profile.sql`, `grant-platform-admin.sql`, `delete-product.sql` stay in `scripts/sql/`. Done in delivery step 2.

## Deployment notes
Filled in during delivery. Run the POC migrator locally (macOS/Linux, any Postgres 16):
```bash
dotnet publish backend/tools/Sellora.DbMigrator -c Release -o /tmp/migrator
SELLORA_DB_CONNECTION="Host=localhost;Database=sellora;Username=postgres;Password=postgres" \
  dotnet /tmp/migrator/Sellora.DbMigrator.dll --scripts backend/db --environment local --plan
```
Options: `--ef-bundle <path>`, `--trigger Deploy|Retry --reason <text>`, `--validation-default warn|block`, `--lock-wait <s>`,
`--commit`, `--version`, `--triggered-by` (defaults from GitHub Actions variables). Exit codes 0 ok · 1 failed · 2 invalid release · 3 lock timeout.
