# Claude Code — Sellora Phase 01: Branch Authorization and Active Branch

## Task
Implement branch-scoped authorization, user administration, and Active Branch Only context.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 01 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Inspect existing company, branch, warehouse, user, role, permission models and authorization handlers.
2. Define effective access for user-company-branch-role; company-owned warehouse access must be explicit.
3. Implement active branch validation server-side; reject unauthorized branch IDs and cross-tenant references.
4. Restrict branch admins to delegated branch roles; block self-escalation and branch allocation changes.
5. Scope stock overview, dashboard, reports, exports, details, sales, purchases, accounts and returns; prevent cross-branch data leakage.
6. Persist historical allocations and audit changes; clear Angular state and query caches on branch switch.

## Acceptance criteria
- [ ] Branch A user cannot read Branch B via list, detail, export or crafted API requests.
- [ ] Multi-branch user can switch only among assigned branches; each query respects active branch.
- [ ] Company-level authorized reporting works without weakening branch-level checks.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 02 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
