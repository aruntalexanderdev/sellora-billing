# Claude Code — Sellora Phase 04: Shipment Planning, Fleet and Consignments

## Task
Combine multiple approved requests into shipments, plan transport, allocate and load stock.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 04 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Model shipment, transport mode, own/external provider, transport references, shipment vehicle assignments, consignments and line-to-request allocations.
2. Support road, air, sea, rail, courier, hand carry with mode-specific reference validation.
3. Own fleet supports multiple vehicles selected from scoped vehicle master; external carrier permits entered details.
4. Allow multiple destinations and destination-specific consignments; define vehicle/leg assignment per consignment.
5. Initially require one source loading location per shipment; reject incompatible requests unless multi-pickup design is explicitly approved.
6. Reserve stock atomically during allocation/loading; track requested, allocated, loaded and dispatched snapshots and units/batches.
7. Release reservation on cancellation or reduction; forbid allocation beyond available quantity or approved request balance.

## Acceptance criteria
- [ ] Multiple requests can be grouped while destination consignments remain isolated.
- [ ] Reservations prevent overselling and are released on cancellation.
- [ ] Multiple vehicles and external transport details are auditable.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 05 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
