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

Commands: prefer `scripts/verify.sh [unit|backend|frontend|all]` (compact output; `unit` skips the Docker/Testcontainers integration tests). Raw: `dotnet build backend/Sellora.slnx`, `dotnet test backend/Sellora.slnx`, `npm --prefix frontend run build`, `npm --prefix frontend test`.

## Active programme: Unit conversion & stock lifecycle restructuring

Read `docs/analysis/unit-conversion-restructuring-analysis.md` before any work on units, products, purchases, sales, stock, pricing or reports. All recommendations in §27 are **accepted** (D1–D10).

Phase order (§25): 0 Quality gates → 1 Unit Master → 2 Product unit hierarchy → 3 Opening stock/adjustments → 4 Purchase → 5 Sales/POS → 6a Ledger enrichment → B Multi-location (≈ multi-company 1–3+8, ADR-03) → 6b Transfers/damage (≈ multi-company 9, ADR-03) → 7 Reports → 8 Release (per phase).
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
| `business-analyst` | Docs only | Requirements + acceptance criteria in `docs/features/<slug>.md` for work outside the restructuring programme |
| `owner-proxy` | No | Owner-away mode only: answers gate questions as the owner would (DECIDE / DEFER / ESCALATE) |

**Owner-away mode:** when the owner says they are offline, follow `.claude/rules/owner-away.md`. Gate questions go to `owner-proxy` instead of `AskUserQuestion`, and every verdict is logged in `docs/decisions/owner-proxy-log.md` for the owner to review. Phase starts, deploys, destructive changes and overrides of owner decisions are never delegated.

Entry points:

| Work | Entry point |
|---|---|
| Restructuring phase (0–8, B) | `/deliver-phase <phase>` |
| New feature outside the programme, size L | `/feature <slug>` |
| Any schema change outside a phase | `/db-change <description>` |
| Size S / M | Main session directly → `/verify` → `/review` (M) |

## Non-negotiable rules

- Stock state is **always base units**. Conversions happen once, at document-line build time. See skill `stock-ledger-invariants`.
- Transaction lines snapshot entered qty, product unit, unit code and conversion factor; history is never recomputed.
- A `ProductUnit` referenced by history is immutable — deactivate and add a new one.
- Never silently round a base quantity; reject (`StockQuantity.EnsureStorable`).
- No cross-product quantity sums; aggregate value only.
- Migrations: additive first, destructive last, with backfill + validation SQL. Never rely on startup auto-migrate for production (D10).
- Follow existing conventions (PascalCase quoted tables, Guid ids, string enums, `DomainValidationException(field, message)` → 400 ProblemDetails).
- Do not commit, push or open PRs unless the user asks. Never include model identifiers in commits or code.

## Task sizing

Size every request before starting and state it in one line:

| Size | Signal | Flow |
|---|---|---|
| S | bug fix, copy/UI tweak, add a field, ≤ ~5 files | Do it yourself → `/verify`. No subagents. |
| M | new endpoint + screen, one module, no schema redesign | Do it yourself → `/verify` → `/review`. |
| L | new module/workflow, multi-table schema, money/stock logic | `/feature` or `/deliver-phase`. |

If unsure, pick the smaller size and escalate.

## Token discipline (Claude Pro — 5-hour and weekly limits)

- `docs/analysis/unit-conversion-restructuring-analysis.md` is ~17k tokens. Never read it whole —
  `grep -n '^#' ` it, then Read only the sections the task cites (e.g. the §25 row, §27).
- Read the feature spec / phase design before exploring code; Grep/Glob first, Read only the ranges you need.
- Max 2 subagents at once; never spawn one for work you can finish in a few tool calls.
- Delegation briefs are self-contained (spec/section refs, files owned, done criteria) so agents don't rediscover context.
- Use `scripts/verify.sh` rather than raw build/test output; use `unit` mode while iterating, `all` before reporting done.
- Suggest `/clear` between unrelated tasks and `/compact` in long sessions; one phase per session is fine —
  `status:` in the feature spec (or the phase report) is how work resumes.

## Git

Branch model (owner decision):

| Branch | Role | How it changes |
|---|---|---|
| `analyse_copy` | Working (integration) branch | Only by pull request from a work branch |
| `claude_dev` | Deployment branch: a merge deploys API + frontend to the claude-dev environment (`.github/workflows/deploy-claude-dev.yml`) | Only by pull request from `analyse_copy` |
| `feature/<slug>`, `fix/<slug>`, `chore/<slug>` | One work branch per feature or bug, created from the latest `analyse_copy` | Commits and pushes |

Flow: branch from `analyse_copy` → work → `/verify` → PR to `analyse_copy` → CI green → merge → PR `analyse_copy` → `claude_dev` → CI green → merge (deploys).

- Never commit or push directly to `analyse_copy`, `claude_dev` or `main`, and never force-push (blocked by `.claude/hooks/guard-bash.sh`). When a session starts on `analyse_copy`, create the work branch before the first change.
- A PR into `claude_dev` comes only from `analyse_copy`, after the work PR was merged there (checked by the `Branch flow` workflow).
- Merge with a merge commit, never squash or rebase: `claude_dev` must keep the commits of `analyse_copy`.
- Creating and merging PRs happens when the user asks. Merging into `claude_dev` deploys: check that CI is green on the head commit first, and say that it deploys.
- `dev` and `main` are outside this flow. Pushing to `dev` deploys the API to Azure — only when the user asks.
- Subagents never commit or push; the main session does, when asked.



## Multi-Company and Multi-Branch Architecture Reference

**Mandatory reference:** Before implementing, modifying, or restructuring any functionality related to multi-company, multi-branch, inventory locations, warehouses, vehicles, user allocations, company governance, or scoped permissions, Claude Code MUST read:

`docs/architecture/Sellora-Multi-Company-Branch-Architecture-Claude.md`

### Implementation Rules

1. Treat this document as the approved architectural specification for multi-company and multi-branch functionality.
2. Follow its database design principles, business rules, authorization requirements, workflows, and implementation phases.
3. Preserve existing Sellora Billing functionality and existing data.
4. Do not perform broad rewrites or introduce unrelated architectural changes.
5. Do not skip phases or implement multiple phases without approval.
6. Complete the repository audit (Phase 0) before implementation.
7. After completing each phase:
   - Summarize the changes.
   - List modified and created files.
   - Report database migrations and API changes.
   - Report tests executed and their results.
   - Identify risks or unresolved issues.
   - Stop and wait for explicit approval before proceeding.

### Important

If existing code conflicts with the approved architecture, explain the conflict and propose a safe migration strategy before making changes.

Do not assume that reading this reference authorizes implementation of all phases. Start with Phase 0 only and wait for approval.

## Approved Sellora functional improvements
Before working on branch authorization, accounts, product classifications, stock transfer requests, shipments, consignments, transit or receiving, read:
- `docs/functional-improvements/IMPLEMENTATION_SPEC.md`
- `docs/functional-improvements/PHASE_PLAN.md`

Owner decisions (precedence, transfer model, reservations, phase order 01 → 08 → 07 → 02 → 03 → 04 → 05 → 06 → 09) are in the spec's **Decisions** section (FI-D1…D10); this package takes precedence for its nine phases.

Execute only the phase explicitly requested by the user using the corresponding `docs/functional-improvements/prompts/PHASE_*.md`. Start with a targeted source audit and PLAN ONLY. Wait for approval before code changes; after implementation, run tests, report results and stop. Never auto-advance phases or deploy without approval.
