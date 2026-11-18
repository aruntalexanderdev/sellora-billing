# Database release runbook

How database changes (EF migrations + `backend/db` scripts) reach dev, claude-dev and prod. Design and decisions:
`docs/features/db-release-automation.md` (D1-D27). Commands assume macOS/Linux with `az`, `gh` and `psql`. Never paste secrets into tickets or chat.

## 1. Normal release

| Environment | Workflow (run manually, on its branch) | Database steps |
|---|---|---|
| claude-dev | Deploy claude-dev (`claude_dev`) | `migrate-db` runs automatically, then the API deploy |
| dev | Deploy dev (`dev`) | `run_migrations` is off until a claude-dev run is green (D17); then like claude-dev |
| prod | Deploy prod (`main` only) | CI -> `db-plan` -> `approve` -> `migrate-db` (with backup) -> `deploy-api` -> `deploy-web` |

`migrate-db` always runs before the API deploy; a failure blocks the deploy (D7). Startup auto-migrate is off on Azure
(`Database__ApplyMigrationsOnStartup=false`).

## 2. Approving a prod release

1. Actions -> the Deploy prod run -> job **Plan database release (prod)** -> Summary. Read:
   - **Pending EF migrations**: expected for this release? (Compare with the PR list.)
   - **Scripts to RUN**: new/changed/retried data scripts and validations. Anything unexpected -> reject.
   - **Errors**: any error means the release is invalid; the job already failed and the approval never starts.
2. Download artifact `migration-sql` (idempotent SQL of all EF migrations) and skim destructive statements (`DROP`, `ALTER ... TYPE`, table rewrites).
3. Open the **Approve release (prod)** job, review the deployments prompt, approve or reject. Reject = nothing is migrated or deployed.
4. After approval, `migrate-db` creates the backup first. Its summary lists the backup name and the **restore timestamp (UTC)** - keep both in the release ticket.

A retry ("Re-run failed jobs" or the Database releases page) does not ask for approval again and applies the same package (D27).

## 3. When `migrate-db` fails

Open the job summary and the Database releases page (`/platform/db-releases`) to find the failing script and its error.

| Cause | Action |
|---|---|
| Script bug, script **never succeeded in any environment** | Fix the script in a PR labelled `db-script-fix` (D10/D16), merge, then start a **new deploy** (prod: new Deploy prod run → new plan + approval). Do **not** use Retry / Re-run failed jobs: they re-apply the old release package, including the broken script. |
| Script bug, script **succeeded somewhere** | Never edit it. Add a new, later-dated script that corrects the result, then start a new deploy. |
| Data problem (bad rows, constraint violations) | Fix the data with the operator runbooks/cleanup scripts in `scripts/sql/`, then retry (same package, so Retry is correct here). |
| Transient (network, lock timeout exit 3, connection exit 4, firewall propagation) | Retry: Database releases page -> "Retry migration", or Actions -> run -> **Re-run failed jobs**. |
| Validation block (prod validations block, D27) | Investigate the rows: `SELECT "ErrorMessage","SqlState","StartedAt" FROM ops."DbScriptAttempts" WHERE "Status" <> 'Succeeded' ORDER BY "StartedAt" DESC LIMIT 20;` Fix the data or the validation, then retry. Do not weaken a validation to get green. |

Exit codes: 1 script failed, 2 invalid release, 3 lock timeout, 4 unexpected. If a failed script left partial changes, check whether it ran in a
transaction (default) - then nothing is partial. `transaction: none` scripts must contain exactly **one statement**, because earlier statements are not rolled back.

## 4. Restore and rollback

Database (choose by what you know). Use the backup / timestamp of **attempt 1** of the run: a retry takes a new backup *after* a
partial migration, so later attempts' backups may already contain changes.
- **On-demand backup** `pre-migrate-<run_id>-<attempt>` (named in the migrate-db summary): restore it from the Azure portal
  (server → Backup and restore → select the backup → Restore to a new server). The CLI restore below takes a timestamp, not a
  backup name (verify against your `az` version before relying on a named-backup CLI restore).
- **Point-in-time restore** to the recorded UTC restore timestamp:
  ```bash
  az postgres flexible-server restore --resource-group <rg> --name <NEW-server-name> \
    --source-server <server> --restore-time 2026-01-31T09:15:00Z
  ```
  Restore always creates a **new server**. Check the data, then point the API at it: update the connection string setting on the App Service
  and the `SELLORA_DB_MIGRATOR_CONNECTION` secret + `AZURE_POSTGRES_SERVER` variable of the `prod` environment. Data written after the restore point is lost; decide with the business first.

Application: redeploy the previous good run's `api-publish` artifact (Actions -> previous successful run -> download `api-publish`, deploy it with
`az webapp deploy --resource-group <rg> --name <app> --src-path <zip> --type zip`), or re-run the previous release workflow on its commit. The database
must be compatible with that build; migrations are additive first (see skill `ef-core-migrations`), otherwise restore the database too.

## 5. Stuck and edge cases

- **Advisory lock held** (a run died, or another migrator is running): the migrator waits `--lock-wait` seconds, then exits 3. Check who holds it:
  ```sql
  SELECT l.pid, a.state, a.query_start, a.application_name, a.client_addr
  FROM pg_locks l JOIN pg_stat_activity a USING (pid)
  WHERE l.locktype = 'advisory' AND l.classid = 5457228 AND l.objid = 1279541761;
  ```
  If the holder is a finished/dead run, `SELECT pg_terminate_backend(<pid>);` then retry. Never terminate a run that is still applying scripts.
- **Leftover firewall rules** `gh-migrate-*` (a cancelled run may skip cleanup):
  ```bash
  az postgres flexible-server firewall-rule list -g <rg> -n <server> --query "[?starts_with(name,'gh-migrate-')].name" -o tsv
  az postgres flexible-server firewall-rule delete -g <rg> -n <server> --rule-name <rule> --yes
  ```
- **Interrupted run**: the next migrator run marks the old `Running` journal row as Failed automatically; just run the release again.
- A cancelled or failed `db-plan` changes nothing (read-only).

## 6. Credentials

- **Rotate the `sellora_migrator` password**: generate a new one, run as server admin
  `psql "$ADMIN_DATABASE_URL" -v ON_ERROR_STOP=1 -v password='<new>' -v owner_role=<API login> -f scripts/sql/create-migrator-role.sql`
  (idempotent), then update the environment secret `SELLORA_DB_MIGRATOR_CONNECTION` (GitHub -> Settings -> Environments -> `prod`).
- **Rotate the GitHub App private key** (page retry, D20): GitHub -> Settings -> Developer settings -> GitHub Apps -> generate a new key, update
  `GitHubApp__PrivateKey` (Key Vault secret or App Service setting), restart the app, then delete the old key.

## 7. First-time setup checklist (per environment)

- [ ] Migrator login created (`create-migrator-role.sql`) - spec "Owner actions before the first claude-dev run", steps 1-2.
- [ ] Environment secrets/variables set (names are listed in the header of `.github/workflows/_deploy-template.yml`; prod also in `deploy-prod.yml`).
- [ ] Deploy identity roles: Website Contributor on the App Service; Contributor on the Postgres server (firewall + backup) - spec step 3.
- [ ] `Database__ApplyMigrationsOnStartup=false` on the App Service - spec step 4.
- [ ] Page retry: GitHub App configured - spec "Owner actions for page retry".
- [ ] prod only: environment `prod` restricted to branch `main` and **without** reviewers; environment `prod-approval` with required reviewers, branch rule `main` and no secrets (D24).
- [ ] prod only: first run checked end to end (plan summary, approval, backup name in summary, health check).
