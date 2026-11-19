# Sellora Billing — Phase Plan and Acceptance Criteria

Each phase is a separate Claude Code task. Stop after planning for approval, then stop again after implementation for review. Do not deploy automatically.

## Phase 01 — Branch Authorization and Active Branch

**Goal:** Implement branch-scoped authorization, user administration, and Active Branch Only context.

**Implementation tasks**
- Inspect existing company, branch, warehouse, user, role, permission models and authorization handlers.
- Define effective access for user-company-branch-role; company-owned warehouse access must be explicit.
- Implement active branch validation server-side; reject unauthorized branch IDs and cross-tenant references.
- Restrict branch admins to delegated branch roles; block self-escalation and branch allocation changes.
- Scope stock overview, dashboard, reports, exports, details, sales, purchases, accounts and returns; prevent cross-branch data leakage.
- Persist historical allocations and audit changes; clear Angular state and query caches on branch switch.

**Acceptance criteria**
- [ ] Branch A user cannot read Branch B via list, detail, export or crafted API requests.
- [ ] Multi-branch user can switch only among assigned branches; each query respects active branch.
- [ ] Company-level authorized reporting works without weakening branch-level checks.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 01b — Branch-Admin User Administration (follow-up to Phase 01, FI-D44/FI-D45)

**Goal:** Branch administrators create and manage users and roles of their active branch only.

**Implementation tasks**
- Add `Branch.Users.View/Create/Edit/Disable/ResetPassword` (branch-eligible); user endpoints accept company `Users.*` OR the branch code at the active branch.
- Branch-path user creation: server-forced AssignedBranches + [active branch] + primary; initial branch role of that branch only, codes held by the creator; audited.
- Branch administration codes grantable only through the company path (FI-D45); peer branch admins and self are not targets.
- Roles screen: branch-only actor's role branch is the active branch (locked). Users screen: branch mode hides scope/branch fields and company roles.

**Acceptance criteria**
- [ ] Branch A admin creates a user who is allocated to Branch A only, with a Branch A role; crafted company/branch ids are ignored or rejected.
- [ ] Branch A admin cannot see, edit, disable or reset Branch B users, company-role users, peer branch admins or itself (404).
- [ ] Branch A admin cannot grant a company role, a code it does not hold, or any branch administration code; cannot change allocations.
- [ ] Company admin behaviour unchanged; every branch-path action is audited.

**Gate:** Plan approved before code; backend + frontend tests pass; code review before reporting done.

## Phase 02 — Sales and Transfer Validation

**Goal:** Fix invalid sales values and insufficient-stock transfer defects before the new workflow.

**Implementation tasks**
- Review DTO validation, UI forms, discount/tax calculations, unit conversions and posting services.
- Reject sales quantity <= 0, negative prices/discounts, discount percent > 100 and discounts exceeding eligible line values.
- Recompute monetary totals server-side with decimal precision and explicit free-item policy.
- Enforce available stock checks at the exact location and batch, accounting for reservations.
- Protect concurrent inventory operations with PostgreSQL transactions and locking/concurrency strategy; test race conditions.

**Acceptance criteria**
- [ ] Negative values rejected through UI and API.
- [ ] Concurrent attempts cannot over-dispatch or drive stock negative.
- [ ] Returns use dedicated workflows, not negative sales lines.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 03 — Transfer Requests and Approvals

**Goal:** Introduce stock transfer requests, authorization, approval and outstanding quantities.

**Implementation tasks**
- Map existing transfers and movements; design migration without replacing history.
- Model request header/lines, source/destination, approval decisions, status history and audit.
- Support draft, submit, approve, reject, ready for shipment, partially fulfilled, fulfilled, closed/cancelled transitions.
- Approval must not reserve or deduct stock.
- Track requested, allocated, dispatched and remaining quantities per line, with invariants across shipments.
- Expose scoped APIs and Angular screens with authorization.

**Acceptance criteria**
- [ ] Approved request enters ready-for-shipment queue without changing stock.
- [ ] Request cannot be over-allocated or over-fulfilled.
- [ ] Branch users see only authorized requests and fields.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 04 — Shipment Planning, Fleet and Consignments

**Goal:** Combine multiple approved requests into shipments, plan transport, allocate and load stock.

**Implementation tasks**
- Model shipment, transport mode, own/external provider, transport references, shipment vehicle assignments, consignments and line-to-request allocations.
- Support road, air, sea, rail, courier, hand carry with mode-specific reference validation.
- Own fleet supports multiple vehicles selected from scoped vehicle master; external carrier permits entered details.
- Allow multiple destinations and destination-specific consignments; define vehicle/leg assignment per consignment.
- Initially require one source loading location per shipment; reject incompatible requests unless multi-pickup design is explicitly approved.
- Reserve stock atomically during allocation/loading; track requested, allocated, loaded and dispatched snapshots and units/batches.
- Release reservation on cancellation or reduction; forbid allocation beyond available quantity or approved request balance.

**Acceptance criteria**
- [ ] Multiple requests can be grouped while destination consignments remain isolated.
- [ ] Reservations prevent overselling and are released on cancellation.
- [ ] Multiple vehicles and external transport details are auditable.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 05 — Dispatch, Transit and Receiving

**Goal:** Implement dispatch, in-transit inventory and destination-side receiving.

**Implementation tasks**
- Dispatch consumes reservations exactly once and posts source-out/transit-in movements in one transaction.
- Expose Current, Incoming, Outgoing, Completed and Discrepancies transit views, filtered by active branch and consignment destination.
- Allow destination user to verify consignment lines and record accepted, damaged, rejected, missing quantities.
- Support partial receipts and multiple receipt events with idempotency keys and row-level concurrency checks.
- Keep accepted stock unavailable until authorized receipt posting; damaged/quarantine and rejected stock must not become sellable.
- Maintain separate shipment and consignment status machines; multi-stop shipment stays open while other consignments remain outstanding.

**Acceptance criteria**
- [ ] Source stock deducted once on dispatch, destination stock added once after posting.
- [ ] Unauthorized branch cannot inspect unrelated consignments within a multi-stop shipment.
- [ ] Partial receipt, repeated API call, damaged/rejected/missing cases tested.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 06 — Receipt Approval and Reconciliation

**Goal:** Implement company-configurable receipt authorization and discrepancy closure.

**Implementation tasks**
- Model company receipt policy: direct posting by authorized role or supervisor approval; separation-of-duties option.
- Snapshot policy/version on receipt initiation so later setting changes do not silently alter pending approvals.
- Validate verification totals and disposition: accepted, damaged, rejected, missing and outstanding quantities.
- Post accepted stock to destination only after authorization; quarantine damaged stock; track rejected/missing until approved disposition.
- Implement return-to-source, redirection, loss/write-off or approved correction through auditable movements as appropriate.
- Prevent completion until all quantities are received or formally reconciled; add audit and approval tests.

**Acceptance criteria**
- [ ] Direct and supervisor modes both enforce permissions.
- [ ] No unaccounted units silently disappear at completion.
- [ ] Reconciliation is auditable and repeat-safe.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 07 — Cash and Bank Account Operations

**Goal:** Add existing-bank funding and cash/bank transfers with source accounting.

**Implementation tasks**
- Inspect existing bank/cash accounts, opening balances, payments and ledgers to avoid duplicates.
- Implement funding source types: owner capital, loan, cash deposit, customer receipt, approved other; validate account mapping.
- Support bank-to-cash, cash-to-bank, bank-to-bank and cash-to-cash transfers where authorized.
- Use balanced postings in one database transaction; never directly overwrite balances.
- Enforce positive amounts, available balance/overdraft policy, account ownership and active-branch permissions.
- Provide reversal, reference, audit, account statements and reconciliation tests.

**Acceptance criteria**
- [ ] Existing bank account accepts later funding with traceable source.
- [ ] Transfer changes both accounts atomically and never creates fictitious revenue.
- [ ] Unauthorized users cannot transfer funds from other branches.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 08 — Category and Subcategory Masters

**Goal:** Implement company-scoped product classifications without duplicating existing schema.

**Implementation tasks**
- Inspect existing category entities and migrations before introducing or extending tables.
- Model category and subcategory codes, names, parent, status and sort order; enforce company-scoped uniqueness.
- Add dependent selectors to product create/edit; validate parent-child match server-side.
- Expose filters in products, stock overview and relevant purchasing/sales/report screens.
- Preserve legacy classifications and existing products during migration; deactivate in-use masters rather than hard delete.

**Acceptance criteria**
- [ ] Product classification persists and is validated by API.
- [ ] Existing products survive migration.
- [ ] Branch users see authorized company catalog, not other companies.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

## Phase 09 — End-to-End Regression and Release

**Goal:** Perform full security, inventory, accounting and deployment readiness verification.

**Implementation tasks**
- Run .NET build/tests, Angular build/tests, PostgreSQL integration tests and migration dry runs on representative database copies.
- Test multi-branch isolation, shipment concurrency, partial fulfillment, multi-vehicle multi-stop routing, idempotency and damaged goods.
- Test financial balanced ledger, reversal, role delegation, audit completeness and authorization failures.
- Review query performance, indexes, N+1, report filters and API pagination.
- Produce rollback plan, migration backup procedure, deployment checklist and release notes; do not deploy without approval.

**Acceptance criteria**
- [ ] All phase acceptance criteria pass.
- [ ] No destructive migration or deployment occurs without explicit approval.
- [ ] Rollback and monitoring plans documented.

**Gate:** Source audit, migration impact and API/UI changes reviewed; focused tests pass before proceeding.

