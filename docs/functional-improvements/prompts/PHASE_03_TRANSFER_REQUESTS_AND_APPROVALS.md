# Claude Code — Sellora Phase 03: Transfer Requests and Approvals

## Task
Introduce stock transfer requests, authorization, approval and outstanding quantities.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 03 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Map existing transfers and movements; design migration without replacing history.
2. Model request header/lines, source/destination, approval decisions, status history and audit.
3. Support draft, submit, approve, reject, ready for shipment, partially fulfilled, fulfilled, closed/cancelled transitions.
4. Approval must not reserve or deduct stock.
5. Track requested, allocated, dispatched and remaining quantities per line, with invariants across shipments.
6. Expose scoped APIs and Angular screens with authorization.

## Acceptance criteria
- [ ] Approved request enters ready-for-shipment queue without changing stock.
- [ ] Request cannot be over-allocated or over-fulfilled.
- [ ] Branch users see only authorized requests and fields.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 04 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
