# Claude Code — Sellora Phase 05: Dispatch, Transit and Receiving

## Task
Implement dispatch, in-transit inventory and destination-side receiving.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 05 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Dispatch consumes reservations exactly once and posts source-out/transit-in movements in one transaction.
2. Expose Current, Incoming, Outgoing, Completed and Discrepancies transit views, filtered by active branch and consignment destination.
3. Allow destination user to verify consignment lines and record accepted, damaged, rejected, missing quantities.
4. Support partial receipts and multiple receipt events with idempotency keys and row-level concurrency checks.
5. Keep accepted stock unavailable until authorized receipt posting; damaged/quarantine and rejected stock must not become sellable.
6. Maintain separate shipment and consignment status machines; multi-stop shipment stays open while other consignments remain outstanding.

## Acceptance criteria
- [ ] Source stock deducted once on dispatch, destination stock added once after posting.
- [ ] Unauthorized branch cannot inspect unrelated consignments within a multi-stop shipment.
- [ ] Partial receipt, repeated API call, damaged/rejected/missing cases tested.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 06 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
