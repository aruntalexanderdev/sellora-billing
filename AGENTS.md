# Sellora Billing — Agent Instructions

Billing and business management for retail/SMB/trading: purchasing, sales/POS, inventory with FIFO batch costing,
multi-currency accounts, expenses, reports. Modular monolith — do not introduce microservices.

## Stack
- Backend: .NET 10, ASP.NET Core Web API, EF Core 10 + Npgsql, PostgreSQL, JWT + permission-based RBAC, xUnit.
- Frontend: Angular 22 (standalone components, signals, reactive forms), TypeScript, RxJS, Vitest.
- CI: push to `dev` → builds and deploys the API to Azure App Service (`.github/workflows/dev_sellora-billing-dev-api.yml`).
- Branch flow: work branch (from `analyse_copy`) → PR to `analyse_copy` → PR `analyse_copy` → `claude_dev`, whose merge deploys claude-dev. No direct pushes to `analyse_copy` or `claude_dev`; merge commits only. Details in `CLAUDE.md`, Git.

## Layout
- `backend/src/Sellora.Domain` — entities (`Entities/`), module models and `I*Service` interfaces (`Abstractions/`, `Sales/`, ...), `Permissions/PermissionCatalog.cs`.
- `backend/src/Sellora.Infrastructure` — `Data/` (DbContext, `Configurations/`, `Migrations/`, `DbSeeder`), `Services/` (business logic).
- `backend/src/Sellora.Api` — `Controllers/`, `Contracts/` (DTOs), `Authorization/`, `ErrorHandling/`, `Validation/`.
- `backend/tests/Sellora.Tests` — one folder per module.
- `frontend/src/app` — `core/` (auth, idempotency, guards), `shared/ui/` + `shared/styles/`, `layout/`, `features/<module>/`.
- `docs/analysis/` — architecture analyses; `unit-conversion-restructuring-analysis.md` is the source of truth for the active unit/stock restructuring (read only the cited sections).
- `docs/features/<slug>.md` — one spec per feature outside that programme (requirements, DB design, API contract, status).

## Commands
- Verify: `scripts/verify.sh [unit|backend|frontend|all]` (`unit` needs no Docker; integration tests use Testcontainers).
- Add migration: `dotnet ef migrations add <Name> --project backend/src/Sellora.Infrastructure --startup-project backend/src/Sellora.Api`
- Review migration SQL: `dotnet ef migrations script <From> <To> --project ... --startup-project ...`

## Non-negotiables
- Backend enforces authorization (`[Authorize(Policy = "Module.Action")]`); Angular guards are UX only.
- Money and quantities are `decimal` everywhere, never float/double. Stock state is always in base units; document lines snapshot unit + conversion factor.
- Stock, batch and money changes happen in one DB transaction (`StockLocks`) with ledger rows; history is never mutated.
- Migrations: additive → backfill → enforce → destructive; never rely on startup auto-migrate in production.
- Inspect existing code before editing; follow the nearest existing pattern; don't invent business rules — ask.
- No destructive DB operations, no pushes to `main`, no force-push, no production deploy without explicit approval.
- Don't create extra Markdown files or long code comments. Update existing docs when behaviour changes.
