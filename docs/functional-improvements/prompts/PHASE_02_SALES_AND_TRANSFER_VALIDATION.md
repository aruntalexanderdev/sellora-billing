# Claude Code — Sellora Phase 02: Sales and Transfer Validation

## Task
Fix invalid sales values and insufficient-stock transfer defects before the new workflow.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 02 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Review DTO validation, UI forms, discount/tax calculations, unit conversions and posting services.
2. Reject sales quantity <= 0, negative prices/discounts, discount percent > 100 and discounts exceeding eligible line values.
3. Recompute monetary totals server-side with decimal precision and explicit free-item policy.
4. Enforce available stock checks at the exact location and batch, accounting for reservations.
5. Protect concurrent inventory operations with PostgreSQL transactions and locking/concurrency strategy; test race conditions.

## Acceptance criteria
- [ ] Negative values rejected through UI and API.
- [ ] Concurrent attempts cannot over-dispatch or drive stock negative.
- [ ] Returns use dedicated workflows, not negative sales lines.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 03 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
