---
name: feature
description: Run an L-sized Sellora feature that is outside the unit-restructuring programme through approval-gated phases (requirements → design → DB → backend → frontend → tests/review → verification).
argument-hint: <feature-slug> [short description]
disable-model-invocation: true
---
Run the Sellora feature pipeline for: $ARGUMENTS
(Restructuring phases use `/deliver-phase` instead.)

Spec: `docs/features/<slug>.md` (create from `_template.md`). The `status:` line is the source of truth — read it first and
resume from the current phase. Never skip a gate. After each phase update `status:`, then STOP with a short summary.

0. **Size check.** If this is really S/M, say so and propose the lighter flow.
1. **Requirements** — `business-analyst`. Present acceptance criteria + open questions.
   Gate: user approves → `status: requirements-approved`.
2. **Design** — `solution-architect` fills "Database design" and "API contract" (skip DB if no schema impact).
   Gate: user approves → `status: design-approved`.
3. **Database** — `database-engineer` (follows `ef-core-migrations`) → `status: db-done`.
4. **Backend** — `backend-engineer` against the approved contract → `status: backend-done`.
   `frontend-engineer` may scaffold in parallel after step 2 — no HTTP wiring until the contract is final.
5. **Frontend** — `frontend-engineer` → `status: frontend-done`.
6. **Tests + review** — `test-engineer` and `code-reviewer` in parallel. Fix Blocker/Major findings via the owning agent,
   re-run `scripts/verify.sh` → `status: reviewed`.
7. **Final verification** (you) — check every acceptance criterion with evidence, `scripts/verify.sh all`, completion report
   + deployment notes (migrations, new permissions, config). Ask before committing → `status: done`.

Each brief: spec path + sections, owned files, done criteria, "report ≤ 15 lines". Max 2 agents at once.
After each agent, `git diff --stat` to confirm it stayed inside its owned files.
