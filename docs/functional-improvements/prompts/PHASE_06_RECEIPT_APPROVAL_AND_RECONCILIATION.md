# Claude Code — Sellora Phase 06: Receipt Approval and Reconciliation

## Task
Implement company-configurable receipt authorization and discrepancy closure.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 06 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Model company receipt policy: direct posting by authorized role or supervisor approval; separation-of-duties option.
2. Snapshot policy/version on receipt initiation so later setting changes do not silently alter pending approvals.
3. Validate verification totals and disposition: accepted, damaged, rejected, missing and outstanding quantities.
4. Post accepted stock to destination only after authorization; quarantine damaged stock; track rejected/missing until approved disposition.
5. Implement return-to-source, redirection, loss/write-off or approved correction through auditable movements as appropriate.
6. Prevent completion until all quantities are received or formally reconciled; add audit and approval tests.

## Acceptance criteria
- [ ] Direct and supervisor modes both enforce permissions.
- [ ] No unaccounted units silently disappear at completion.
- [ ] Reconciliation is auditable and repeat-safe.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 07 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
