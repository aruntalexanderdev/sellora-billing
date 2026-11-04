# Sellora Billing — Claude working guide

Production-grade billing and inventory platform (target: hypermarkets / retail chains).
Backend: .NET 10, ASP.NET Core Web API, EF Core 10 + Npgsql, PostgreSQL. Frontend: Angular 22 (standalone, signals, reactive forms), Vitest.

## Repository map

| Path | Contents |
|---|---|
| `backend/src/Sellora.Domain` | Entities, enums, service interfaces, input models. No EF references. |
| `backend/src/Sellora.Infrastructure` | `SelloraDbContext`, `Data/Configurations/*`, `Data/Migrations/*`, `Services/*` (write logic), `DbSeeder` |
| `backend/src/Sellora.Api` | Controllers (also hold read/report queries), `Contracts/*` DTO records, RBAC policies |
| `backend/tests/Sellora.Tests` | xUnit, EF InMemory (no locks/constraints exercised) |
| `frontend/src/app` | `core/`, `layout/`, `shared/ui/`, `features/<area>/<feature>/` |
| `docs/analysis/` | Architecture analyses — **source of truth for the unit/stock restructuring** |

Commands: `dotnet build backend/Sellora.slnx`, `dotnet test backend/Sellora.slnx`, `npm --prefix frontend run build`, `npm --prefix frontend test`.

## Active programme: Unit conversion & stock lifecycle restructuring

Read `docs/analysis/unit-conversion-restructuring-analysis.md` before any work on units, products, purchases, sales, stock, pricing or reports. All recommendations in §27 are **accepted** (D1–D10).

Phase order (§25): 0 Quality gates → 1 Unit Master → 2 Product unit hierarchy → 3 Opening stock/adjustments → 4 Purchase → 5 Sales/POS → 6a Ledger enrichment → B Multi-location → 6b Transfers/damage → 7 Reports → 8 Release (per phase).
Only the phase the user has explicitly approved may be implemented.

## Team model

The main session is the **Lead Architect / Delivery Lead**. It plans, delegates, integrates and verifies; it does not skip gates. Specialists live in `.claude/agents/`, shared knowledge in `.claude/skills/`. Subagents cannot spawn subagents, so all delegation flows from the lead.

| Agent | Writes code? | Owns |
|---|---|---|
| `solution-architect` | No | Phase design, cross-module consistency, ADR-style decisions, conflict resolution input |
| `database-engineer` | Yes | Entities' persistence, EF configurations, migrations, backfill + validation SQL |
| `backend-engineer` | Yes | Domain logic, services, controllers, DTOs, permissions |
| `frontend-engineer` | Yes | Angular features, models, services, shared utils/pipes |
| `test-engineer` | Yes (tests only) | xUnit service/unit tests, Postgres integration tests, Vitest specs |
| `devops-release-engineer` | Yes (CI/config only) | GitHub workflows, migration scripts, rollback, auto-migrate gating |
| `code-reviewer` | No | Independent review against invariants before the lead reports done |

Delivery workflow: use the `deliver-phase` skill (`/deliver-phase <phase>`).

## Non-negotiable rules

- Stock state is **always base units**. Conversions happen once, at document-line build time. See skill `stock-ledger-invariants`.
- Transaction lines snapshot entered qty, product unit, unit code and conversion factor; history is never recomputed.
- A `ProductUnit` referenced by history is immutable — deactivate and add a new one.
- Never silently round a base quantity; reject (`StockQuantity.EnsureStorable`).
- No cross-product quantity sums; aggregate value only.
- Migrations: additive first, destructive last, with backfill + validation SQL. Never rely on startup auto-migrate for production (D10).
- Follow existing conventions (PascalCase quoted tables, Guid ids, string enums, `DomainValidationException(field, message)` → 400 ProblemDetails).
- Do not commit, push or open PRs unless the user asks. Never include model identifiers in commits or code.
