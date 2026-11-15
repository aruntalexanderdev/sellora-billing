# Claude Code — Sellora Phase 07: Cash and Bank Account Operations

## Task
Add existing-bank funding and cash/bank transfers with source accounting.

## Mandatory context
Read `IMPLEMENTATION_SPEC.md` and `PHASE_PLAN.md` (or the equivalent approved documents in the repository) and existing `CLAUDE.md` instructions. Treat this prompt as scoped to Phase 07 only. First inspect the actual code, migrations and tests. Do not assume entities/endpoints are absent or that a prior phase is complete.

## Phase scope
1. Inspect existing bank/cash accounts, opening balances, payments and ledgers to avoid duplicates.
2. Implement funding source types: owner capital, loan, cash deposit, customer receipt, approved other; validate account mapping.
3. Support bank-to-cash, cash-to-bank, bank-to-bank and cash-to-cash transfers where authorized.
4. Use balanced postings in one database transaction; never directly overwrite balances.
5. Enforce positive amounts, available balance/overdraft policy, account ownership and active-branch permissions.
6. Provide reversal, reference, audit, account statements and reconciliation tests.

## Acceptance criteria
- [ ] Existing bank account accepts later funding with traceable source.
- [ ] Transfer changes both accounts atomically and never creates fictitious revenue.
- [ ] Unauthorized users cannot transfer funds from other branches.

## Required workflow
1. **PLAN ONLY FIRST:** Identify existing implementation, exact files, data model changes, authorization boundaries, migration strategy, tests and risks. Keep the plan concise; do not write code yet.
2. Wait for explicit user approval of this phase's plan.
3. Implement only approved scope using existing .NET/Angular/PostgreSQL architecture. Do not rewrite unrelated modules or add mock production data.
4. Validate server-side scope, concurrency, idempotency and auditability where applicable. Use EF Core migrations only after examining existing history.
5. Run targeted tests, relevant integration tests and builds. Report commands and outcomes honestly.
6. Return modified-file summary, migration/rollback notes, known issues and a checklist of acceptance results. **STOP.** Do not begin Phase 08 or deploy without approval.

## Efficiency constraints
Read targeted files, not the whole repository indiscriminately. Reuse existing services/entities and conventions. Avoid redundant agents, documents, abstraction layers or dependencies. Keep changes small and reviewable.
