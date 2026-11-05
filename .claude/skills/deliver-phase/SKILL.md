---
name: deliver-phase
description: Lead Architect workflow to deliver one approved phase of the Sellora unit-conversion & stock restructuring using the specialist agents — design, delegate in dependency order, integrate, test, review, report. Invoke as /deliver-phase <phase id, e.g. 0, 1, 2, 6a, B>.
argument-hint: <phase>
disable-model-invocation: true
---

# Deliver phase $ARGUMENTS

You are the **Lead Architect / Delivery Lead**. Specialists: `solution-architect`, `database-engineer`, `backend-engineer`, `frontend-engineer`, `test-engineer`, `devops-release-engineer`, `code-reviewer` (`.claude/agents/`).

## 0. Gate
- Confirm the user explicitly approved phase **$ARGUMENTS**. If not, stop and ask.
- Read `docs/analysis/unit-conversion-restructuring-analysis.md` §25 row for the phase, its prerequisites, and §27 (all decisions accepted). Confirm prerequisite phases are merged.
- Working branch: the session's designated branch; never push or open a PR unless asked.

## 1. Design
Delegate to `solution-architect` with: phase id, acceptance criteria from §25/§26, relevant sections. Review its design; resolve conflicts; trim anything outside the phase. Share the agreed design with the user briefly before coding if it deviates from the analysis.

## 2. Delegate in dependency order
Typical order — skip agents with no work in this phase; run independent packages in parallel:

| Step | Agent | Typical package |
|---|---|---|
| a | devops-release-engineer | Phase 0 / release gating, CI changes |
| b | database-engineer | entities persistence, configurations, migrations, backfill/validation SQL |
| c | backend-engineer | services, validation, controllers, DTOs, permissions |
| d | frontend-engineer | models, services, components, utils/pipes (after DTOs are fixed) |
| e | test-engineer | unit/service/integration/API/Vitest tests; run suites |
| f | code-reviewer | independent review of full diff |

Each work package states: goal, files in scope, contracts it must honour (DTO shapes, migration names), acceptance criteria, and "report format: files changed, decisions, risks, follow-ups".

## 3. Integrate & verify (lead does this itself)
Run `scripts/verify.sh unit` while fixing, then `scripts/verify.sh all` (needs Docker for the integration tests) before reporting.
Check the diff is within phase scope. Re-dispatch fixes for any failure or reviewer Blocker/Major finding, then re-run.

## 4. Definition of done
- All phase acceptance criteria met with evidence (test names / outputs).
- Stock invariants and conversion rules respected (skills `stock-ledger-invariants`, `unit-conversion-rules`).
- Migrations: backfill + validation SQL + rollback note present.
- code-reviewer verdict Approve (or fixes applied and re-reviewed).

## 5. Report to user
Phase summary, files changed by area, migrations, API/DTO changes, test results (counts), reviewer verdict, release checklist, open items. Then stop — do not start the next phase without explicit approval.
