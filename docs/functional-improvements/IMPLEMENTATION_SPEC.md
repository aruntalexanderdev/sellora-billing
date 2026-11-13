# Sellora Billing — Approved Functional Improvements Implementation Specification

**Version:** 1.0 | **Baseline:** Approved Requirements Review v1.4 | **Status:** Approved for phased planning and implementation; deployment requires separate authorization.

## Purpose and non-negotiable decisions

- Stack: existing .NET 10 Web API, EF Core, PostgreSQL, Angular 22 standalone components and SCSS. Preserve existing solution layout and contracts unless evidence supports a minimal change.
- Company Owner/Super Admin governs company; branch admins manage only delegated branch users/roles, never company governance or branch allocations.
- **Active Branch Only** for operational screens. Multi-branch users select exactly one authorized branch; company-wide views require explicit company permissions.
- Company-owned warehouses require explicit location permissions. Transfers are a scoped exception allowing destination users to inspect their own consignment, not unrelated branch data.
- Cash and bank accounts: funding after creation requires a declared source (owner capital, loan, cash deposit, customer receipt, approved other); support bank/cash transfers with balanced ledger entries.
- Product Category and Subcategory masters: company scoped; dependent selection in product creation/edit and stock/product filtering.
- Sales: quantity > 0, price >= 0, discounts >= 0 and within limits; authoritative server-side totals.
- Transfer requests: approval makes requests ready for shipment, **does not reserve stock**.
- Stock reservation: **during shipment allocation/loading**; source inventory deducted at dispatch, not request approval.
- Shipments: group multiple approved requests; support multiple destinations, consignments and vehicles. Consignments are destination-specific and linked to request lines.
- Transport modes: road, air, sea, rail, courier, hand carry. Via: own company fleet (select multiple vehicles) or external provider (enter details). Mode-specific references; do not require road registration for all modes.
- Transit: dedicated Current, Incoming, Outgoing, Completed and Discrepancies views. Authorized destination user can verify and receive consignment.
- Receipt approval: **configurable by company**, direct authorized posting or separate supervisor approval; optional separation of duties. Accepted stock posted only after authorization.
- Partial receipts and missing/damaged/rejected stock require traceable disposition and reconciliation.

## Existing repository assumptions — verify, do not presume

Expected structure: backend/src/Sellora.Api (controllers, contracts, authorization); Sellora.Domain (entities/interfaces); Sellora.Infrastructure (EF DbContext, migrations, services); backend/tests/Sellora.Tests; frontend/src/app (core, shared, features). Existing FIFO batch costing, product unit conversions, stock movements, returns, roles and company/branch restructuring may be partially implemented. Inspect source, migrations and tests to establish current state; this document does not claim a source audit.

## Domain boundaries

1. **Identity and scope:** company, branch, warehouse, user allocation history, role assignments and effective permissions. Server-authoritative scope provider; authorization on list/detail/create/update/export/report.
2. **Inventory:** products, base-unit quantities, batches, stock positions, reservations, stock movement ledger. No negative available stock; transaction and concurrency safety.
3. **Transfer requests:** request header and lines, approvals, state machine, quantities and outstanding demand. Approved is not dispatched.
4. **Shipment:** shipment header, selected request-line allocations, transport mode/via, carrier, vehicles/legs, dispatch information. Do not confuse shipment state with request state.
5. **Consignment:** one destination per consignment, assigned transport leg/vehicle, line snapshots and receiving history. A shipment can include multiple consignments, a request can be fulfilled by multiple consignments over time.
6. **Receipt:** verification events, receipt lines, accepted/damaged/rejected/missing quantities, company approval policy snapshot, authorized posting and discrepancy disposition.
7. **Cash/bank:** account, source-typed funding transaction, transfer, balanced ledger entries, reversal and statement.
8. **Product classification:** company-scoped category/subcategory and product reference; validate parent-child integrity.

## Quantities and inventory invariants

- Store authoritative stock in base units; snapshot entered unit, factor, price/cost and relevant batch identifiers in transactional lines.
- Available = on-hand minus active reservations and other unavailable quantities; clarify quarantine and other stock states in current schema.
- Allocation <= available and cumulative allocation/dispatch <= approved outstanding request quantity.
- Loading <= allocation; dispatch <= confirmed loaded quantity; receive cumulative accounted quantity <= dispatched quantity.
- On dispatch, atomically release/consume reservation, reduce source on-hand and increase in-transit quantity. On authorized receipt, decrease in-transit and increase destination accepted stock; quarantine damaged goods, and keep rejected/missing quantities tracked until disposition.
- Use idempotency keys, database transactions and row locks/concurrency tokens for allocation, dispatch, receipt and financial postings. Unique constraints must prevent duplicate posting.
- Multi-source shipments: initially constrain a shipment to one source loading location; require explicit design review for multiple pickups. Multiple destinations and multiple vehicles are supported from initial implementation.

## State machines (proposed; map to existing states)

- Request: Draft → PendingApproval → Approved/ReadyForShipment → PartiallyFulfilled/Fulfilled → Closed. Rejected/Cancelled where legal.
- Shipment: Draft → Planning → Loading → ReadyToDispatch → Dispatched/InTransit → PartiallyDelivered → Completed. Cancellation before dispatch; post-dispatch changes through auditable corrective operations.
- Consignment: Planned → Loaded → Dispatched → AwaitingReceipt → UnderVerification → PartiallyReceived/Received → Reconciled.
- Receipt: Draft → Verified → PendingApproval (if configured) → Posted or RejectedForCorrection. Never mutate a posted receipt silently.
- Distinguish partial fulfillment, partial physical receipt and discrepancy closure. Do not mark completed while quantities remain unresolved.

## Authorization matrix

| Action | Company authorized user | Branch admin | Branch staff |
|---|---|---|---|
| Company settings and branch allocations | With explicit permission | No | No |
| Manage branch users/roles | Authorized company scope | Active branch, delegated roles only | No |
| View stock/report | Company scope if permitted | Active branch | Active branch, permitted operations |
| Request/approve transfer | Permission + relevant source/destination scope | Permission + active branch | Permission + active branch |
| Plan/load/dispatch | Permission + source location access | If delegated | If delegated |
| View/receive incoming consignment | Company authorized scope | Active destination branch | Active destination branch if permitted |
| Approve receipt | Configured role + location scope | If delegated | If explicitly permitted |
| Post cash/bank movement | Authorized account scope | If delegated | If explicitly permitted |

Never trust client-supplied company/branch IDs. Validate location scope on every action. A shared shipment must not leak other destinations' consignments to branch users.

## API and application contract guidance

Use versioned endpoints matching existing conventions; illustrative resources: `/api/v1/stock-transfer-requests`, `/api/v1/shipments`, `/api/v1/consignments/{id}/receipts`, `/api/v1/transit`, `/api/v1/account-transfers`, `/api/v1/bank-fundings`, `/api/v1/categories`, `/api/v1/subcategories`. Do not introduce routes until existing route conventions are audited. Use DTOs, input validation, consistent ProblemDetails errors, pagination and explicit authorization. Keep DbContext out of controllers. Angular should use typed services, reusable forms and centralized active-branch context; invalidate cached queries on branch changes.

## Migration and compatibility requirements

- Inventory and financial movement history is immutable; no destructive migration or silent balance rewrite.
- Inventory current quantities must reconcile to movement history before and after migration.
- Backfill existing transfers to compatible legacy states or create adapters; do not fabricate dispatch/receipt history.
- Check current category masters and account/ledger schema before adding new tables.
- Introduce nullable fields and backfills before adding strict constraints when existing records require them.
- Plan rollback, migration dry run, database backup and staged rollout. Avoid auto-migrating production at API startup without an approved deployment strategy.

## Non-functional acceptance

- No cross-company/branch data leakage through direct IDs, joins, aggregates, exports or reports.
- Atomic, idempotent posting and concurrent stock correctness under PostgreSQL integration tests.
- Auditable user, time, location, before/after business state and reason for sensitive actions.
- No unnecessary rewrites, duplicate entities, mock production data or premature full logistics/route optimization subsystem.
- Optimize reads with scoped queries, indexes, projection, pagination and `AsNoTracking` where suitable.

## Implementation governance

Execute phases in order; before each phase inspect the relevant source and migrations, produce file-level plan and risks, and wait for approval to implement. After implementation run focused tests and build, summarize modified files, migration risks, results and remaining issues. Do not automatically advance to the next phase or deploy. See `PHASE_PLAN.md` and `prompts/`.

## Decisions (owner-approved 2026-11-12)

Repository context: the multi-company programme (arch Phases 0–11) and the unit-conversion programme (Phases 0–8) are already delivered. These phases extend that code; they do not rebuild it.

| ID | Decision |
|---|---|
| FI-D1 | **Document precedence.** For the nine phases in this package, this spec and `PHASE_PLAN.md` take precedence. `docs/architecture/Sellora-Multi-Company-Branch-Architecture-Claude.md` remains the reference for the foundations (company, branch, stock locations, scoped RBAC); `docs/analysis/unit-conversion-restructuring-analysis.md` remains authoritative for units and the stock ledger. Raise any conflict before implementing it. |
| FI-D2 | **Transfer model, Option A.** The existing `StockTransfer` becomes the transfer request. Shipment and Consignment are added on top for allocation, dispatch and receipt. No migration of existing transfers: legacy transfers already Dispatched/PartiallyReceived complete through the existing receive and resolve flow. Reuse the Phase 9 transit and receipt logic where possible. |
| FI-D3 | **No direct transfers.** New transfers can no longer go straight from Approved to Dispatched; every dispatch goes through a Shipment. |
| FI-D4 | **Reservations per product and location.** New `StockReservations` table (company, source location, product, base qty, shipment allocation line, status Active/Consumed/Released, audit) plus a `StockItems.ReservedQuantity` column kept in step under the existing row locks. Available = `QuantityOnHand − ReservedQuantity`. Batches stay FIFO, chosen at dispatch. |
| FI-D5 | **Reservation lifecycle.** Allocate/increase creates or extends; reduce/cancel releases; dispatch consumes exactly once in the same transaction as the stock deduction (unique constraint + idempotency key). No automatic expiry; old reservations are listed in a report and released only with a dedicated permission. Reconciliation: `ReservedQuantity = Σ Active reservations` and `ReservedQuantity ≤ QuantityOnHand`. |
| FI-D6 | **Reserved stock is a hard block for sales.** POS and sales cannot use reserved stock; only a planner with permission can release a reservation. |
| FI-D7 | **Shared availability check.** Phase 02 builds one availability check used by sales, POS, adjustments, purchase returns, van loading and dispatch, with reserved quantity treated as zero. Phase 04 turns reservations on in that one place. |
| FI-D8 | **Phase 01 is audit plus gap fixes**, not a rebuild. Existing pieces: user branch allocations, governance, scoped RBAC, the server-validated `X-Branch-Id` header and the Angular branch picker. |
| FI-D9 | **Phase order:** 01 → 08 → 07 → 02 → 03 → 04 → 05 → 06 → 09. Phase numbers stay as in `PHASE_PLAN.md`; only the order of execution changes. |
| FI-D10 | **Versions.** Do not change framework or package versions without the owner's approval. |
| FI-D11 | **Phase 01 – Active Branch Only reads.** Branch query filters, `StockVisibility` and the dashboard read the *active* branch only (header, else primary, else company default branch — Q3a). Writes keep validating against the allowed set. Company-wide reads are explicit: `?scope=company` on `[CompanyScopeRead]` endpoints (reports, accounts reports, dashboard, stock overview), honoured only when the endpoint's permission is held company-level, else 403 `company_scope_denied`. The UI drops "All branches" and offers a "Company view" toggle driven by `companyPermissions` from auth/me. Dashboard company view requires company-level `Reports.Sales`. |
| FI-D12 | **Phase 01 – shared company accounts (Q1a).** Balances of company-level financial accounts (BranchId NULL) are returned only to readers holding the accounts permission company-level; otherwise balance fields are null. **Amended 2026-11-12:** also requires a membership covering every branch — a member limited to some branches gets null figures even with a company role; list/report totals exclude hidden rows. |
| FI-D13 | **Phase 01 – delegated roles (Q2a).** No `IsDelegable` flag; the existing subset rule (assign only roles whose codes the actor holds at that scope) is the delegation rule. No migration in Phase 01. |
| FI-D14 | **Phase 01 – user administration.** A non-company-wide actor may edit/disable/reset-password only users whose active allocations all lie within the actor's branches and who hold no company-scope role (else 404); these actions write `AuditEvent`s. **Clarified after review:** the target must have an AssignedBranches membership with at least one active allocation, all allocations and branch roles inside the actor's branches; company-wide members and users without allocations are company administration. |
| FI-D15 | **Phase 01 – stock-location list (owner-approved 2026-11-12).** `GET /stock-locations` serves pickers and lists the allowed set, so its `branchId` filter is checked against the allowed set (inactive branches included with `includeInactive`); it also accepts `?scope=company`. Company-level admins can therefore list an inactive branch's locations. |
| FI-D16 | **Phase 08 – classification masters (owner-approved 2026-11-12).** Two tables `ProductCategories` and `ProductSubcategories` (company-owned; Code ≤20 required, unique per company / per category, auto-suggested from the name, editable; Name ≤60 unique case-insensitive in the same scope; SortOrder; IsActive). `Products.CategoryId`/`SubcategoryId` nullable; composite FK (CompanyId, CategoryId, SubcategoryId) enforces the parent-child match; subcategory optional (Q3). Deactivate only, never delete; inactive masters cannot be newly assigned but an unchanged one survives a product edit. Permission `Inventory.Categories` (company-only) to manage; reads open to any company member (Q5). |
| FI-D17 | **Phase 08 – migration.** Additive: backfill one category per company per distinct trimmed legacy `Products.Category` (case-insensitive; most-used spelling wins; generated codes with -2.. suffixes); no invented subcategories. Legacy `Products.Category` is kept and mirrored with the category name for this release; dropped in Phase 09 (Q2). |
| FI-D18 | **Phase 08 – filter scope (Q4).** `categoryId`/`subcategoryId` filters on product-level screens only: products list, stock overview, current-stock report, POS catalog. Document-level purchase/sales report filters are deferred. |
| FI-D19 | **Phase 07 – funding (owner-approved 2026-11-13).** New ledger type `Funding` (In, names an account) with `FundingSource` OwnerCapital/Loan/CashDeposit/CustomerReceipt/Other, `SourceReference`, `Counterparty`; CustomerReceipt requires a customer and a reference and is an unallocated receipt (no receivable/revenue effect, Q1a); Other requires a reason and is flagged in reports (Q3a). Permission `CashBank.Fund` (company-only). Cash-flow reports show funding under its own source lines, never as revenue. |
| FI-D20 | **Phase 07 – overdraft and posting (Q2a).** `FinancialAccounts.OverdraftLimit` (default 0; cash always 0). Transfers and reversals that would take an account below `-OverdraftLimit` are rejected (400). Payments are not checked in this phase. Every posting runs in one DB transaction with per-account row locks taken in Id order; transfer/funding/reversal accept `Idempotency-Key`. Balances stay derived, never stored. |
| FI-D21 | **Phase 07 – reversal (Q5a).** `POST /financial-transactions/{id}/reverse` with a mandatory reason, permission `CashBank.Reverse` (company-only). Allowed for Funding, Transfer (both legs together) and OpeningBalance; posts opposite entries of type `Reversal` linked by unique `ReversalOfTransactionId`; originals stay Active; reconciled entries are blocked; payment-sourced entries use their documents' flows. Audit events for funding, transfer and reversal. |
| FI-D22 | **Phase 07 – source-account rule (Q4a).** The source (debited) account of a transfer must be an account of the active branch, or a company-level account only for members covering every branch (consistent with FI-D12); the destination may be any account the user may access. Funding targets follow the same rule. Account statement `GET /financial-accounts/{id}/statement?from&to` (opening, rows with running balance, closing; FI-D12 hidden accounts 403). |
| FI-D23 | **Phase 02 – shared availability (owner-approved 2026-11-13).** One `StockAvailability` check, run under the existing stock-row locks, used by sales/POS, adjustment decreases, purchase returns (named batch) and transfer dispatch (incl. van loading): available = on hand − reserved per location/product (and per batch where named). Reserved is 0 until Phase 04 enables it in that single place. |
| FI-D24 | **Phase 02 – money and pricing rules.** Q1a: each sale line's taxable value, tax and total are rounded to the base currency precision (AwayFromZero); invoice totals are sums of rounded lines; paid/due compare against the rounded grand total; history is not recomputed. Q2a: a free item is price 0 via `Sales.PriceOverride` (no new flag), shown with a "Free" badge. Q3a: discounts stay amounts on the server; the POS may offer a 0–100 % entry that converts to an amount. Transfer line quantities must be > 0 on create/edit (already enforced); on dispatch a line may be 0 ("not shipped this time", existing partial-dispatch behaviour) but never negative, and at least one line must be > 0. |
| FI-D25 | **Phase 02 – DB guard (Q4a).** `CK_StockItems_QuantityOnHand_NonNegative`; the migration refuses (with a listing) if negative rows exist rather than changing data. |
