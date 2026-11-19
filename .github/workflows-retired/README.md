# Retired workflows

GitHub Actions only runs files in `.github/workflows/`. Files here are kept for reference and never run.

| File | Did | Replaced by | Retired |
|---|---|---|---|
| `dev_sellora-billing-dev-api-claude.yml` | Push to `claude_dev` (`backend/**`) → build, test, deploy API `sellora-billing-dev-api-claude`. No database migrations. | `deploy-claude-dev.yml` (`migrate-db` before `deploy-api`) | 2026-11-19 |
| `azure-static-web-apps-wonderful-bay-04d447700.yml` | Push to `claude_dev` → deploy the frontend to Static Web Apps; PR preview environments for PRs into `claude_dev`. | `deploy-claude-dev.yml` (`deploy-web`). PR previews are not replaced. | 2026-11-19 |

Do not move these back into `.github/workflows/`: the API workflow deploys without migrating (startup migration is off on
claude-dev), and either file would deploy alongside Deploy claude-dev on every push.
Background: `docs/features/db-release-automation.md`, `docs/operations/database-release-runbook.md`.
