# Phase 10 - Location-aware purchase, sales, returns and van operations - file-level design

Base: `analyse` @ 7260ec3 (Phases H–9 delivered; 8b + 9 not yet released; last migration `20261111040145_AddStockTransfers`). Size M–L. **No migration.** Paths relative to `backend/src`. Angular → Phase 11.

## 1. Scope

**Already delivered by 8b/9 — no Phase 10 work (verified in §2)**
- §13.1 purchases receive into a branch store, branch warehouse or company warehouse (company warehouse needs the permission company-wide).
- §13.2 sales post at a validated location; only branch store or vehicle sells; another branch's vehicle always refused; a central vehicle needs `Sales.Create` company-wide. POS catalog / barcode lookup per location.
- §13.5 mostly: vehicle loading, unloading, vehicle-to-vehicle moves and in-transit damage/shortage are Phase 9 transfers (O6 allows every non-Transit pair); van sale = POST /sales with `stockLocationId` = vehicle; van stock via stock overview/detail and the stock-movement report with `stockLocationId`; van damage/loss = stock adjustment at the vehicle; vehicle reassignment/deactivation blocked while it holds stock or has open transfers.
- One posting service for every write path (`StockPosting`, source-scan test); purchase cancel refused once the batch has moved.

**Phase 10 delivers (remaining gap)**
- G1 Sales return / sale cancel: restock location must be active and still belong to the invoice's branch (Phase 8 D3 rule, never built).
- G2 Purchase return from the location that holds the goods now (arch §13.4), original purchase kept as reference.
- G3 Purchase detail: returnable quantity per location.
- G4 `stockLocationId` filter on sales, sales-return and purchase-return lists.
- G5 Idempotency-Key on sales and purchase returns (van connectivity).
- G6 End-to-end van regression test + release validation script.

**Excluded**: van trip/settlement document (O1); return-goods classification (O4); user-to-vehicle binding (O7); loading fast path (O9); settlement/van reports (unit-analysis Phase 7); Angular (Phase 11).

## 2. Verified facts

**Location resolution and fit**
- Resolver is Sale/Purchase/Adjustment only; refuses Transit and inactive locations (`StockLocationRules.cs:10`, `:42`).
- `EnsureFitsAsync` (`StockLocationRules.cs:66-105`): branch-owned location must belong to the working branch (`:83-86`); a vehicle must be assigned to the working branch, another branch's vehicle never fits (`:90-100`); any other company-level location needs the permission company-wide (`:102-104`).
- `CanSell` = BranchStore or Vehicle; `CanReceivePurchase` excludes Vehicle (`Domain/Inventory/StockLocationType.cs:21-26`).

**Sales return / sale cancel**
- Both restock at `invoice.StockLocationId` with no IsActive check and no check on the vehicle's current branch: return `SalesService.cs:329-331` (header `:347`, movement `:446`); cancel `SalesService.cs:479-480`.
- `LockLocationsKeyShareAsync` only locks, no re-check (`StockLocks.cs:187-198`).
- Only deactivation-race test covers new postings (`tests/Sellora.IntegrationTests/Concurrency/StockLocationPostingRaceTests.cs:286`).

**Purchase return**
- Always issues from `purchase.StockLocationId` (`PurchaseService.cs:407-410`, header `:423`); available = batch balance there (`:469-473`), so goods transferred elsewhere cannot be returned. Input has no location (`Domain/Purchasing/PurchaseModels.cs:51-57`). Purchase detail returnable uses the purchase location (`PurchasesController.cs:89`, `:239`).

**Posting primitives, branches**
- `StockPosting.RestoreAsync` creates the batch balance at any location if missing (`StockPosting.cs:57-74`); `IssueFromBatchAsync` needs an existing balance (`:46-53`); `BalanceAsync` per (batch, location) (`:134`).
- A return is recorded at the original document's branch (`BranchRules.cs:64-71`).

**Lists, money, numbering**
- Sales list filters by branch only (`SalesController.cs:139-150`); same for purchase returns (`PurchaseReturnsController.cs:22-24`); stock-movement report already filters by location (`ReportsController.cs:117-122`).
- Payment accounts validated against the invoice branch (`FinancialAccountRules.cs:22-25`, `SalesService.cs:42`); accounts have optional BranchId (`FinancialAccount.cs:22`); account transfers exist (`FinancialAccountService.cs:82`, `CashBank.Transfer` at `PermissionCatalog.cs:143`).
- Numbering per company + branch (`DocumentNumbers.cs:12-18`); a van invoice takes its branch's INV series.

**Idempotency, vehicles, permissions**
- Scopes `StockLocks.cs:93-99` (none for returns); only sale and purchase create use them (`SalesService.cs:28`, `PurchaseService.cs:21`).
- Vehicle reassign/deactivate guarded by `InUse` (`VehicleService.cs:78-85`, `:112`). Arch §9.2 forbids driver/HR entities.
- `Sales.Return`, `Purchase.Return` are branch-eligible (`PermissionCatalog.cs:18-19`); no new codes.

## 3. Decisions

**D1 Restock location for sales return and cancel (Q4; closes Phase 8 D3 gap).** New `StockLocationRules.ResolveRestockAsync(db, invoice, forCancel, ct)`, run before the locks and again after the location KEY SHARE lock. Target = invoice location if active and still the invoice branch's (branch-owned location of that branch, a vehicle currently assigned to it, or a central vehicle). Otherwise:
- Return, original a vehicle → invoice branch's store (O2). Header `StockLocationId` and movement record the actual location; goods return to the consumed batches via `RestoreAsync`.
- Return, any other location → 400 `stockLocationId` "The sale's stock location is inactive. Reactivate the location first."
- Cancel → always 400 (exact undo, no fallback).
Lock order unchanged: invoice header → products → location(s) → stock rows.

**D2 Purchase return from the current location (arch §13.4).** Optional `StockLocationId` on `CreatePurchaseReturnInput` (default = purchase location). `ResolveReturnSourceAsync`: active, non-Transit; fit against the purchase's branch via `EnsureFitsAsync` with company-level `Purchase.Return`. Another branch's location only with `Purchase.Return` company-wide AND that branch accessible (O3; mirrors Phase 9 Resolve). Per-line limit = `BalanceAsync(batch, location)`. Header `StockLocationId` = issuing location, `PurchaseOrderId` keeps the reference. Supplier credit, tax proration, refund account stay with the purchase branch. Lock order unchanged.

**D3 Returnable per location (reads).** `GET /purchases/{id}?stockLocationId` computes returnable there (default purchase location); hidden explicit location → 403 `branch_access_denied` (StockVisibility, Purchase.View). Lines gain `locationBalances[]` {stockLocationId, code, name, baseQty} over visible locations with balance > 0.

**D4 List filters.** Optional `stockLocationId` on GET sales, sales-returns, purchase-returns; narrows an already branch-filtered query.

**D5 Van money (Q6), no schema.** Van cash = a branch-tagged `FinancialAccount` ("Van KA01 cash"); payments/refunds validated against the invoice branch; end-of-day settlement = account transfer (`CashBank.Transfer`) to the branch cash account (O5).

**D6 Idempotency for returns (O8).** Scopes `sales-return`, `purchase-return`; `IdempotencyKey` on both return inputs; `Idempotency-Key` header in both controllers; replay semantics as sale create.

**D7 No new permissions, numbering series, movement types or migration.** Van sales use the branch INV series (O6). Van traceability = movements at the vehicle + transfers + invoices filtered by location (D4).

## 4. Design table

| Layer | File | Change |
|---|---|---|
| Domain | Purchasing/PurchaseModels.cs:51; Sales/SalesModels.cs:40 | +StockLocationId? (purchase return); +IdempotencyKey? on both return inputs |
| Infra | Services/StockLocationRules.cs | ResolveRestockAsync (D1), ResolveReturnSourceAsync (D2) |
| Infra | Services/SalesService.cs:293-470, :474-520 | D1 return + cancel; D6 |
| Infra | Services/PurchaseService.cs:371-555 | D2; D6 |
| Infra | Services/StockLocks.cs:93-99 | two idempotency scopes (≤ 30 chars) |
| Api | Controllers/PurchasesController.cs:63-91, :239-261 | D3 |
| Api | SalesController.cs:139; SalesReturnsController.cs:21, :92; PurchaseReturnsController.cs:22, :99 | D4; Idempotency-Key; request → input |
| Api | Contracts (purchase-return request, purchase detail line DTO) | +StockLocationId; +LocationBalances |
| SQL | scripts/sql/van-operations-validation.sql (new, read-only) | §5 |
| Tests | see WP3 | §7 |

## 5. Migration

None (the `SalesReturns` / `PurchaseReturns` header `StockLocationId` from Phase 8 already stores the restock / issuing location). Release validation `scripts/sql/van-operations-validation.sql`, run alongside `stock-transfers-validation.sql` V1–V8; every query returns no rows:
- R1 every `SaleReturn` movement is at its SalesReturns header location.
- R2 same for `PurchaseReturn`.
- R3 a sales return restocked away from the invoice location only when the invoice location is a vehicle.
- R4 no `SaleReturn` / `PurchaseReturn` movement at a Transit location.
- R5 no `SalesInvoiceLineBatchConsumptions` with `ReturnedQtyBaseUnits > QtyBaseUnits`.
Rollback = previous app build.

## 6. API/DTO and Phase 11 carry-over

Additive: POST /purchase-returns `stockLocationId?`; POST /sales-returns and /purchase-returns accept `Idempotency-Key`; GET /purchases/{id} `?stockLocationId` + `lines[].locationBalances[]`; GET /sales, /sales-returns, /purchase-returns `?stockLocationId`. Errors: 400 `stockLocationId`; 403 `branch_access_denied` on hidden read locations. Sales-return requests carry no location (server resolves, D1).

**Phase 11 carry-over**: van workspace (vehicle selector on POS, load/unload as transfer screens, vehicle stock view); location picker on purchase-return form driven by `locationBalances`; location filter on document lists; restock-location display on sales-return detail; `stockLocationId` field-error mapping; Vitest specs.

## 7. Work packages

- **WP1 backend-engineer**: Domain inputs; `StockLocationRules`; `SalesService`; `PurchaseService`; `StockLocks` scopes. AC: D1, D2, D6; lock order unchanged; re-check under the location lock; existing suites green.
- **WP0 database-engineer (parallel)**: `scripts/sql/van-operations-validation.sql` (R1–R5, read-only).
- **WP2 backend-engineer #2 (after WP1 inputs)**: PurchasesController, SalesController (list), SalesReturnsController, PurchaseReturnsController, Contracts. AC: D3, D4; filters on roots before projection; 403 hidden locations.
- **WP3 test-engineer**: unit `ReturnLocationRulesTests` (D1 matrix incl. cancel never falls back; D2 fit matrix incl. Transit and over-balance; idempotent replay of both returns). PostgreSQL E2E `VanOperationsEndToEndTests` with V1–V8 + R1–R5 after every step: purchase 100 at A store → transfer 40 to vehicle V (A) → van sale 30 into A "van cash" (A INV series; COGS = Σ consumptions × batch cost) → sales return 5 restocks at V, refund from van account → damage 2 at V → unload 13 to store (V = 0, store = 73) → settle cash by account transfer (van account = 0; PaidAmount = payments − refunds) → reassign V to B, return 1 more → restocks at A store → transfer 20 A → B store, purchase return 10 from B (refused for A-only user, allowed company-level, reference kept) → purchase cancel refused → company value within 6 dp. Concurrency: sales return vs vehicle reassign/deactivate; purchase return at B vs dispatch of the same batch at B. Isolation: cross-company `stockLocationId` → 400/404. AC: `scripts/verify.sh all` green.

Then code-reviewer, then lead verify.

## 8. Risks / release notes

- Ships after or with the 8b + 9 release; no migration; deploy app, run the V-block, smoke test.
- Behaviour change: cancelling a sale whose location is inactive or whose vehicle is now another branch's is refused (previously it silently restocked an inactive location).
- Behaviour change: returns of van sales may restock the branch store (O2); header and movement show the real location.
- Purchase return from another branch's location moves stock outside the branch that records the document; visible only to company-level readers of the purchase branch.
- InMemory exercises no locks; PostgreSQL tests are the evidence.
- Van loading under Phase 9 approval takes several calls; Phase 11 UI chains them with idempotency keys (O9).

## 9. Owner questions (with recommendation)

- **O1** Van trip/settlement document? Rec: not now; transfers + van invoices (location filter) + movement report by vehicle + account transfer cover it. Settlement summary report → unit-analysis Phase 7.
- **O2** Return of a van sale when the vehicle is inactive or reassigned: refuse, or restock to the invoice branch's store? Rec: restock to the store; non-vehicle inactive locations refused.
- **O3** Purchase return from another branch's location: allow with company-level `Purchase.Return` + access, or require a transfer back first? Rec: allow; branch-only users transfer back first.
- **O4** Saleable/damaged disposition on return lines? Rec: defer (needs a migration); record damage as a Damage adjustment.
- **O5** Van cash = branch-tagged account + account transfer for settlement, no vehicle link on accounts? Rec: yes.
- **O6** Van invoices share the branch INV series? Rec: yes.
- **O7** Restrict which users may sell from which vehicle? Rec: no in Phase 10 (arch §9.2 forbids driver entities).
- **O8** Idempotency-Key on returns? Rec: yes.
- **O9** Same-branch store ↔ own-vehicle loading without approval/Transit? Rec: no; keep Phase 9 O1, the UI chains the steps.

## 10. Deviations

- Arch §13.5 van operations delivered with existing transfer, sale and adjustment documents; no van-specific document.
- Arch §13.3 return classification deferred (O4).
- Q4 relaxed for vehicle origins only (O2).
- Phase 8 D3 "returns/cancels need an active location" was never built; implemented now (correction, not new scope).
- No new permission codes (ADR-13).

## Owner approval (2026-11-11)

Design approved, deviations accepted. O1–O9 all as recommended: no van trip document (settlement report → unit-analysis Phase 7); returns of van sales restock to the invoice branch's store when the vehicle is inactive or reassigned; purchase return from another branch's location with company-level Purchase.Return + access; return-goods classification deferred; van cash = branch account + account transfer; van invoices on the branch INV series; no user-to-vehicle restriction; Idempotency-Key on returns; loading keeps the Phase 9 approval flow.

## Delivery record (2026-11-11)

Status: **Phase 10 delivered — review "Approve with fixes" (no Blocker), fixes applied.** Verify: 1569 unit / 1033 PostgreSQL integration / 192 Vitest, 0 warnings (incl. analyzers). No migration. Release with (or after) 8b + 9: deploy app, run `scripts/sql/stock-transfers-validation.sql` (V1–V8) and `scripts/sql/van-operations-validation.sql` (R1–R5), smoke test.

**Delivered**
- D1: sales return restocks at the invoice location when it is active and still the invoice branch's; a van-sale return falls back to the invoice branch's active store when the van is inactive or reassigned; otherwise 400 on `stockLocationId`. Cancel never falls back. Both candidates are locked in id order and the target is re-resolved under the lock.
- D2: purchase return from `stockLocationId` (default: the purchase's location); active, non-Transit, fits the purchase branch; another branch's location only with company-level Purchase.Return and access to that branch; limit = batch balance at that location; credit, tax and refund stay with the purchase branch.
- D3: `GET /purchases/{id}?stockLocationId` and `lines[].locationBalances[]` (visible locations only).
- D4: `?stockLocationId` filter on sales, sales-return, purchase-return and purchase lists.
- D6: Idempotency-Key on both return POSTs (scopes `sales-return`, `purchase-return`).
- `scripts/sql/van-operations-validation.sql` (R1–R5).
- Tests: return-location rule matrices, API reads/filters/keys, van end-to-end on PostgreSQL (purchase → load van → van sale with van cash → return → damage → unload → cash settlement → reassign → return to store → transfer → cross-branch purchase return → cancel refused) with V1–V8 + R1–R5 after every step, races (return vs van reassign/deactivate, purchase return vs dispatch), isolation.

**Fixes during delivery**
- Review Major: an Idempotency-Key reused for a different sale or purchase returned the unrelated earlier return (no refund for the new one) → now 400 on `IdempotencyKey`.
- Distinct error message when a van has been reassigned (cancel), instead of "inactive".
- Three Phase 9 test-analyzer warnings fixed; the warning check now counts analyzer warnings too.

**Notes**
- The restock candidate list is read before locking; a branch store created in that window would be used without a lock (narrow, accepted).
- `GET /purchases/{id}?stockLocationId` changes only the returnable figure; the header `stockLocationId` remains the purchase's own location.
- Behaviour changes for release notes: cancelling a sale whose location is inactive or whose van was reassigned is refused; returns of van sales may restock the branch store; a purchase return at an inactive location is refused.
