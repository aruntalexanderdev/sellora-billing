# Claude Code — Sellora Phase 09: End-to-End Regression and Release

## Task
Perform full security, inventory, accounting and deployment readiness verification.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 09 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Run .NET build/tests, Angular build/tests, PostgreSQL integration tests and migration dry runs on representative database copies.
2. Test multi-branch isolation, shipment concurrency, partial fulfillment, multi-vehicle multi-stop routing, idempotency and damaged goods.
3. Test financial balanced ledger, reversal, role delegation, audit completeness and authorization failures.
4. Review query performance, indexes, N+1, report filters and API pagination.
5. Produce rollback plan, migration backup procedure, deployment checklist and release notes; do not deploy without approval.

## Acceptance criteria
- [ ] All phase acceptance criteria pass.
- [ ] No destructive migration or deployment occurs without explicit approval.
- [ ] Rollback and monitoring plans documented.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 10 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
