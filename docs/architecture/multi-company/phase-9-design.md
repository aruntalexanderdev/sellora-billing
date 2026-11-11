# Phase 9 - Stock transfer lifecycle (= unit-analysis 6b, ADR-02/03/13) - file-level design

Base: `analyse` @ 4bc5d25 (Phase 8b delivered, not released; last migration `20261110114731_AddStockLocationDimension`). Size L. Paths relative to `backend/src` unless noted. **Release: 8b and 9 ship together** (Phase 8 O1 = A).

## 1. Scope

**Delivers**
- StockTransfers document: header, lines, per-batch dispatch rows, receipts/resolutions.
- State machine: Draft → Requested → Approved/Rejected → Dispatched → PartiallyReceived → Completed; Cancelled before dispatch.
- Dispatch source → Transit; partial receipts Transit → destination; batch identity and batch cost preserved.
- Damaged/short quantities resolved by traceable write-off or return-to-source.
- Idempotent writes, Phase 8 D4 lock order, TRF number series, StockTransfers.* permissions, audit events.
- `HasOpenTransfers` real; Transit hidden from branch-scoped stock sums; TransferOut/TransferIn movement types.
- RestrictedUserApiTests PO re-tag replaced by a real transfer.

**Excludes**: van loading/settlement UX and vehicle returns (Phase 10; vehicles are accepted as backend endpoints only); reservation on approval; approval thresholds; repack; report restructuring (analysis Phase 7); Angular (Phase 11).

## 2. Verified facts (code is source of truth)

**Posting primitives**
- `StockPosting.Receive` handles a new batch only (Infrastructure/Services/StockPosting.cs:18-35).
- `IssueFromBatchAsync` removes value at batch cost via `RemoveValue` (StockPosting.cs:50-57, :100-114; clamps at 0, :106).
- `RestoreAsync` leaves average cost unchanged (StockPosting.cs:61-78).
- No primitive moves a balance between locations while leaving the batch total alone.
- `BatchAllocator.ConsumeFifoAsync` always decrements `PurchaseBatches.RemainingBaseQty` (BatchAllocator.cs:56-60), so it cannot be reused unchanged for a move.
- Sales COGS comes from batch cost (SalesService.cs:651-661).

**Locks, idempotency, numbering**
- Idempotency = advisory lock per (company, user, scope, key) (StockLocks.cs:100-115); scope ≤ 30 chars (IdempotencyKeyConfiguration.cs:12); header name Api/Validation/IdempotencyHeader.cs:9.
- Location KEY SHARE StockLocks.cs:167-178; one ordered ensure+lock over (location, product) pairs StockLocks.cs:195-249.
- Template StockAdjustmentService.cs: replay :47-53, product KEY SHARE :58, resolve → lock → re-resolve :92-97, number :103, one movement per batch :171-199.
- Series constants DocumentNumbers.cs:10-18 and the in-memory fallback switch DocumentNumbers.cs:~160-167 (a new series goes in both); format per company+branch (DocumentNumbers.cs:31-41).

**Locations**
- Resolver refuses Transit (StockLocationRules.cs:42); branch fit StockLocationRules.cs:59-83; `PostingKind` = Sale/Purchase/Adjustment (StockLocationRules.cs:10).
- Exactly one always-active Transit per company (StockLocationConfiguration.cs:24, :39); default tenant …0004 (DefaultTenant.cs:17); new companies get one (PlatformService.cs:262).
- `HasOpenTransfers` hard-coded false (StockLocationUsageService.cs:24); `InUse` = stock OR open transfers (IStockLocationUsage.cs:10-12). Callers: VehicleService.cs:81, :112; WarehouseService.cs:109; BranchService.cs:185.

**Ledger**
- No Transfer movement types (StockMovementType.cs:3-13). Type ≤ 20, ReferenceType ≤ 40, ReferenceNo ≤ 30 (StockConfigurations.cs:12-15).
- `CK_StockMovements_Type_Sign` / `CK_StockMovements_ReasonCode` at StockConfigurations.cs:59-62.

**Scoping and permissions**
- Branch filter covers the 5 headers + Expenses/FinancialAccounts on a single BranchId (SelloraDbContext.cs:94-103).
- Handler evaluates at the working branch and narrows to permitted branches (PermissionAuthorizationHandler.cs:22-44).
- `StockVisibility` treats every BranchId-NULL, non-assigned-vehicle location as company-level and visible to all (StockVisibility.cs:58-62), so Transit is currently summed into branch users' stock.
- Branch-eligible codes PermissionCatalog.cs:16-24. New permission rows: migration insert with md5 ids (20261110093408_AddScopedRoles.cs:200-211), plus `scripts/sql/seed-permissions.sql` and its drift test; DbSeeder fills from the catalogue (DbSeeder.cs:102).

**Other**
- `AuditLog.Append` takes an `AccessActor` (AuditLog.cs:13-31); action codes in `AuditActions` (Domain/Entities/AuditEvent.cs).
- ProductUnit immutability sources (ProductUnitUsage.cs:35-41) do not include transfer lines; the comment at :13 already expects them.
- Movement reads already expose ReferenceType/No (StockController.cs:112, ReportsController.cs:155); adjustment detail filters on ReferenceType "StockAdjustment" (StockAdjustmentsController.cs:131) — no collision.
- Re-tag to replace: tests/Sellora.IntegrationTests/Api/RestrictedUserApiTests.cs:482-492.

## 3. Decisions

**D1 Schema (ADR-02).** All tables ICompanyOwned; composite (CompanyId, X) FKs, Restrict, navigation-less to locations (phase-1 §8 rule). Numeric: entered/base qty (18,3), factor (28,10).
- **StockTransfers**: Id, CompanyId, TransferNo varchar(30) unique (CompanyId, TransferNo); BranchId NOT NULL (creating working branch, number-series owner); SourceLocationId, DestinationLocationId, TransitLocationId (snapshot of company Transit); SourceBranchId?, DestinationBranchId? (effective branch = location.BranchId ?? Vehicle.AssignedBranchId; stable while open because D9 blocks reassignment); Status (string enum), Notes; actor/time pairs Created, Submitted, Approved, Rejected (+Reason), Dispatched, Completed, Cancelled (+Reason). CHECKs: Source ≠ Destination; Source/Destination ≠ Transit; status-consistent actor columns.
- **StockTransferLines**: Id, TransferId, LineNo, ProductId; request snapshot ProductUnitId, UnitId, UnitCode, ConversionFactorToBase, RequestedQty, RequestedBaseQty; dispatch snapshot (nullable until dispatch) DispatchProductUnitId, DispatchUnitCode, DispatchFactor, DispatchedQty, DispatchedBaseQty; base counters ReceivedBaseQty, DamagedBaseQty, WrittenOffBaseQty, ReturnedBaseQty. CHECKs: Qty × factor = BaseQty; Dispatched ≤ Requested (base); counters sum ≤ Dispatched. Same-product composite FK to ProductUnits.
- **StockTransferLineBatches**: PK (LineId, PurchaseBatchId); CompanyId; DispatchedBaseQty; UnitCost numeric(18,6) snapshot; the same four counters. Per-transfer batch identity (the shared Transit balance cannot be the source of truth for one transfer).
- **StockTransferReceipts** (Id, TransferId, Sequence, Kind ∈ {Receipt, Resolution}, At, ByUserId, Notes/Reason) and **StockTransferReceiptLines** (ReceiptId, TransferLineId, unit snapshot, AcceptedQty/BaseQty, DamagedQty/BaseQty, WrittenOffBaseQty, ReturnedBaseQty). Kind CHECK: Receipt = accepted/damaged only; Resolution = written-off/returned only.

**D2 State machine** (service transition table; anything else is 400 field `status`).

| From | To | Operation |
|---|---|---|
| Draft | Requested | submit |
| Draft / Requested / Approved | Cancelled | cancel (reason) |
| Requested | Approved / Rejected | approve / reject (terminal) |
| Approved | Dispatched | dispatch (exactly once) |
| Dispatched / PartiallyReceived | PartiallyReceived / Completed | receipt |
| Dispatched / PartiallyReceived | Completed | resolve |

No cancel after dispatch (return-to-source is the undo). "Received" folded into Completed. Draft editable (PUT replaces lines); create with `submit=true` goes straight to Requested.

**D3 Quantities.**
- Dispatch is one event per transfer: per line 0 ≤ dispatched ≤ requested, in any active unit of the product (own snapshot); at least one line > 0. The undispatched remainder is closed at dispatch ("not shipped"; no backorder).
- Receipts may be many: per line accepted + damaged ≤ outstanding (= Dispatched − Received − Damaged − WrittenOff − Returned). Excess is refused (O3). Auto-complete when every line's outstanding = 0.
- Resolve closes all remaining outstanding per line as WriteOff (loss) or ReturnToSource.
- Entered quantities: `EnsureEnteredDecimals` → `EnsureStorable` → `EnsureWithinBaseDecimals` (StockLocks.cs:19-55). Base never rounded.

**D4 Postings** (ADR-02: one signed row per location; ReferenceType "StockTransfer", ReferenceId = transfer id, ReferenceNo = TransferNo; one movement per (line, batch); EnteredQuantity only when one batch carries the whole line, 6a-D2).

| Event | Movements |
|---|---|
| Dispatch | TransferOut −q @source, TransferIn +q @Transit |
| Receipt, accepted | TransferOut −q @Transit, TransferIn +q @destination |
| Receipt, damaged | Adjustment −q @Transit, ReasonCode Damage |
| Resolve WriteOff | Adjustment −q @Transit, ReasonCode Loss |
| Resolve ReturnToSource | TransferOut −q @Transit, TransferIn +q @source |

Damaged goods never enter destination saleable stock (O2). New enum values TransferOut/TransferIn; sign CHECK extended (TransferIn > 0, TransferOut < 0).

**D5 Batch identity and cost.**
- `BatchAllocator.PlanFifoAsync` (non-mutating plan over balances at (location, product)); `ConsumeFifoAsync` = plan + apply, unchanged behaviour.
- `StockPosting.MoveBatchAsync(fromItem, toItem, batch, qty)`: balance(batch, from) −= q, balance(batch, to) += q (upsert); **PurchaseBatches.RemainingBaseQty unchanged** (ADR-12 dual-write holds); source `RemoveValue(q, batch.UnitCost)`; target moving average at batch cost (Receive formula).
- `StockPosting.MoveFifoAsync` = PlanFifo + MoveBatch; used by dispatch at the source.
- Receipt and return draw from the line's own LineBatches outstanding in dispatch order, never Transit-FIFO (concurrent transfers of the same product cannot swap batches).
- Write-offs use `IssueFromBatchAsync` at Transit (balance and batch total drop).
- Company value Σ QOH·Avg conserved by dispatch, receipt and return; destination average reflects the batches it holds (§13.4 row 5). StockPosting stays the only writer (source-scan test).

**D6 Locks and idempotency** (consistent with Phase 8 D4). Dispatch / receipt / resolve:
1. Idempotency advisory lock.
2. Transfer header FOR UPDATE (`StockLocks.LockTransferAsync`; never taken after stock locks).
3. Reload header, check transition.
4. Products FOR KEY SHARE (id order).
5. Locations FOR KEY SHARE {endpoint, Transit} in id order, re-check IsActive.
6. `EnsureAndLockStockItemsAsync` over {(endpoint, p), (Transit, p)} in one ordered statement.
7. Apply, save, commit.

Create/edit: products KEY SHARE → endpoint locations KEY SHARE + IsActive re-check → TRF number; no stock locks. Scopes: `stock-transfer`, `stock-transfer-dispatch`, `stock-transfer-receipt` (ResourceId = receipt id), `stock-transfer-resolve`; replay returns the stored resource. Without a key, header lock + state/outstanding checks still prevent double dispatch / over-receipt (400). Approve/reject/submit/cancel: header FOR UPDATE + transition check; idempotent by state.

**D7 Branch scoping (arch 13.2).**
- New Branch named filter on StockTransfers: `Allowed == null || Allowed.Contains(BranchId) || (SourceBranchId != null && Allowed.Contains(SourceBranchId)) || (DestinationBranchId != null && Allowed.Contains(DestinationBranchId))`. Children unfiltered.
- Per-action fit via extracted `StockLocationRules.EnsureFitsAsync(location, workingBranch, companyLevel)` (same messages): Create — source or destination fits, or company-level StockTransfers.Create; Approve/Reject/Dispatch — source fits; Receive — destination fits; Cancel — either fits; Resolve — company-level only.
- Endpoints active, non-Transit, different, same company (composite FKs + company filter).
- `StockVisibility`: Transit visible only when the read permission is company-level; branch readers see in-transit quantity on the transfer document. Transit never sellable (test added).

**D8 Permissions (ADR-13)**, module Inventory: StockTransfers.View, .Create, .Approve, .Dispatch, .Receive, .Cancel (branch-eligible); StockTransfers.Resolve (company-only). No role backfill; Administrator holds them implicitly (O5). Approver may be the requester (O1).

**D9 Usage.** `HasOpenTransfers` = EXISTS StockTransfers (Branch filter ignored, Company kept) with Source or Destination = location and Status ∉ {Completed, Cancelled, Rejected}, after the existing location FOR UPDATE. Blocks deactivating warehouse/vehicle/branch store and reassigning a vehicle while a transfer is open.

**D10 Numbering, audit, unit lock.** `DocumentSeries.Transfer = "TRF"` on the creating branch (+ in-memory fallback). `AuditActions` stock_transfer.created / submitted / approved / rejected / cancelled / dispatched / received / resolved in the same transaction; `AuditLog` overload taking (userId, correlationId). ProductUnitUsage adds StockTransferLines (ProductUnitId, DispatchProductUnitId) and StockTransferReceiptLines, bypassing the Branch filter.

## 4. Design table

| Layer | File | Change |
|---|---|---|
| Domain | Entities/StockTransfer.cs, StockTransferLine.cs, StockTransferLineBatch.cs, StockTransferReceipt.cs, StockTransferReceiptLine.cs, StockTransferStatus.cs, StockTransferReceiptKind.cs (new) | D1 |
| Domain | Entities/StockMovementType.cs; Entities/AuditEvent.cs | TransferOut/TransferIn; D10 |
| Domain | Stock/StockTransferInputs.cs, Abstractions/IStockTransferService.cs (new); Permissions/PermissionCatalog.cs | inputs; D8 |
| Infra | Data/Configurations/StockTransferConfigurations.cs (new), StockConfigurations.cs:61-62, SelloraDbContext (DbSets + filter) | D1, D4, D7 |
| Infra | Data/Migrations/<ts>_AddStockTransfers (+Designer, snapshot); scripts/sql/seed-permissions.sql | §5 |
| Infra | Services/StockPosting.cs, BatchAllocator.cs, StockLocks.cs, StockLocationRules.cs, DocumentNumbers.cs, AuditLog.cs, ProductUnitUsage.cs, StockLocationUsageService.cs | D5, D6, D7, D9, D10 |
| Infra | Services/StockTransferService.cs (new); Api/Program.cs DI | D2–D6 |
| Api | Controllers/StockTransfersController.cs (new), Contracts/StockTransferContracts.cs, StockVisibility.cs | §6, D7 |
| Tests | tests/** | §7 |

## 5. Migration `AddStockTransfers` (additive, one transaction; header carries P/V blocks)

**Guards (RAISE)**: G1 every company has exactly one Transit location; G2 no StockItems / StockMovements / BatchLocationBalances rows at any Transit location; G3 model at the AddStockLocationDimension head.
**Profiling (NOTICE)**: P1 companies / Transit locations; P2 Permissions rows.

**Steps**
1. Create the 5 tables with PK, FKs, CHECKs, indexes: IX_StockTransfers_CompanyId_Status; filtered IX on SourceLocationId / DestinationLocationId (usage check); IX on SourceBranchId / DestinationBranchId; unique (CompanyId, TransferNo). Names ≤ 63 chars, explicit short FK names.
2. Rebuild `CK_StockMovements_Type_Sign` with TransferIn > 0, TransferOut < 0.
3. Insert the 7 permission rows (`md5('perm:'||Code)::uuid`, ON CONFLICT ("Code") DO NOTHING), text matching the catalogue and seed-permissions.sql.

No backfill.

**Validation** (release SQL + test assertions after every flow)
- V1 per (location, product): QOH = Σ movements (incl. Transit).
- V2 per (location, product): QOH = Σ BatchLocationBalances.
- V3 Σ balances per batch = PurchaseBatches.RemainingBaseQty.
- V4 Transit holds exactly the in-transit quantity:
```sql
SELECT COALESCE(t."PurchaseBatchId", o."PurchaseBatchId") FROM
 (SELECT bl."PurchaseBatchId", bl."RemainingBaseQty" q FROM "BatchLocationBalances" bl JOIN "StockLocations" l ON l."Id"=bl."StockLocationId" AND l."LocationType"='Transit') t
 FULL JOIN (SELECT "PurchaseBatchId", SUM("DispatchedBaseQty"-"ReceivedBaseQty"-"DamagedBaseQty"-"WrittenOffBaseQty"-"ReturnedBaseQty") q FROM "StockTransferLineBatches" GROUP BY 1) o USING ("PurchaseBatchId")
 WHERE COALESCE(t.q,0) <> COALESCE(o.q,0);
```
- V5 line counters = Σ LineBatches = Σ receipt lines.
- V6 Completed ⇒ outstanding = 0; Status ∉ {Dispatched, PartiallyReceived, Completed} ⇒ no LineBatches and no movements.
- V7 per transfer: Σ 'StockTransfer' movements = −(Σ Damaged + WrittenOff) base.
- V8 no 'StockTransfer' movement at a location other than that transfer's source, destination or Transit.

**Down** refuses if any StockTransfers row or TransferIn/TransferOut movement exists; else removes the tables, restores the old CHECK, deletes the 7 permission rows and their RolePermissions.

**Tests**: Up on empty and on the 8a head with 2 companies; G1/G2 refusal; Down guard; Down→Up; idempotent script.

## 6. API/DTO and Phase 11 carry-over

`/api/v1/stock-transfers`:
- GET list (View): status, sourceLocationId, destinationLocationId, direction=incoming|outgoing, date range, paging; rows with from/to labels, branch labels, outstanding base qty.
- GET /{id} (View): header + actors; lines with request/dispatch snapshots and counters; per-batch rows (batch label, cost); receipts.
- POST (Create, Idempotency-Key) {sourceLocationId, destinationLocationId, notes, submit, lines[{productId, productUnitId, quantity}]}; PUT /{id} (Create, Draft only); POST /{id}/submit (Create).
- POST /{id}/approve, /{id}/reject {reason} (Approve); POST /{id}/cancel {reason} (Cancel).
- POST /{id}/dispatch (Dispatch, key) {lines[{lineId, productUnitId, quantity}]}.
- POST /{id}/receipts (Receive, key) {notes, lines[{lineId, productUnitId, acceptedQuantity, damagedQuantity}]}.
- POST /{id}/resolve (Resolve, key) {reason, lines[{lineId, action: WriteOff|ReturnToSource}]}.

Errors: 400 ProblemDetails with field names (`lines[i].quantity`, `stockLocationId`, `status`); hidden transfer 404. Movement DTOs unchanged in shape (new Type values, ReferenceType "StockTransfer").

**Phase 11 carry-over**: transfer list/detail/create/approve/dispatch/receive (variance)/resolve screens (lazy); TransferIn/TransferOut labels and movement → transfer link in stock.models.ts; Transit display; field-error mapping; Vitest specs.

## 7. Work packages (max 2 parallel; agents never share files)

- **WP1 database-engineer**: D1 entities/enums, StockMovementType, configurations, DbContext (DbSets + Branch filter), migration + Designer + snapshot, PermissionCatalog codes, seed-permissions.sql. AC: build green; no pending model changes; G/P/V/Down blocks; permission drift test green.
- **WP2 backend-engineer (after WP1)**: StockPosting/BatchAllocator (D5), StockLocks (LockTransferAsync + scopes), StockLocationRules.EnsureFitsAsync (no behaviour change for existing callers), StockTransferService, IStockTransferService + inputs, DocumentNumbers TRF, AuditLog/AuditActions, ProductUnitUsage, StockLocationUsageService, Program.cs DI. AC: source-scan test still shows StockPosting/BatchAllocator as only writers; existing suites green.
- **WP3 backend-engineer #2 (parallel once WP2 publishes interface + inputs)**: StockTransfersController, Contracts, StockVisibility Transit rule. AC: filters on roots before projection; branch reader sees only transfers of its source/destination branch; Transit absent from branch-scoped sums.
- **WP4 test-engineer**: unit (transitions; snapshots incl. Half-Carton = 50 pc, base never rounded, non-storable rejected; partial dispatch; partial receipts; damaged; excess refused; resolve WriteOff/ReturnToSource; cancel after dispatch refused; ProductUnit locked by Draft line; fit/permission matrix incl. Resolve company-only). PostgreSQL: analysis §13.4 across two branches (A 125 / B 50, B1 cost preserved, company 172) with V1–V8 after every step; concurrency (double dispatch with/without key, two receivers over-receiving, opposite-direction transfers of the same product/batch — no deadlock, dispatch vs source sale — no negative stock, dispatch vs location deactivation); usage hook blocks; Transit cannot be sold; cross-company id → 400/404; purchase cancel / purchase-return limits after the batch moved; migration tests (§5). **Replace RestrictedUserApiTests.cs:482-492** with purchase at y → transfer y→x → sale at x; remove the UPDATE and TODO. AC: `scripts/verify.sh all` green.

Then code-reviewer, then lead verify.

## 8. Risks / release notes

- **R1 Joint release with 8b.** Deploy per D10: snapshot → idempotent script → V-block → deploy → smoke. Additive migration; the CHECK rebuild scans StockMovements once (validation only, no rewrite).
- **R2** Source average shifts when a transfer leaves at batch cost; `RemoveValue` clamps at 0 (StockPosting.cs:106), so company value can drift by rounding. Test tolerance 6 dp.
- **R3** The Transit StockItems row per product is a hot lock for concurrent transfers of the same product. Accepted (short transactions).
- **R4** Vehicle effective-branch snapshot is correct only because D9 blocks reassignment while open. Never relax D9 without revisiting D7.
- **R5** InMemory exercises neither locks nor FKs; Postgres tests are the evidence.
- **R6** Transit rows appear in stock detail `locations[]` for company-level readers; document this.

## 9. Owner questions (with recommendation)

- **O1 Approval policy**: always required? may the requester approve their own transfer? Rec: always required; self-approval allowed when the user holds Approve at the source. Thresholds later (arch 12.3).
- **O2 Damaged on arrival**: write off from Transit (never enters destination stock) or receive then adjust? Rec: write off from Transit as Adjustment/Damage referencing the transfer.
- **O3 Excess**: refuse on receipt; extra goods via a separate Correction adjustment at the destination? Rec: yes.
- **O4 Shortages**: Resolve (write-off or return-to-source) company-level only? Rec: yes.
- **O5 Default grants**: new codes only to Administrator; owners grant the rest via roles? Rec: yes.
- **O6 Endpoint types**: allow all non-Transit pairs now, including vehicles (van UX in Phase 10)? Rec: yes.
- **O7 Reservation**: none on approval; availability checked only at dispatch? Rec: yes.
- **O8 Cost basis**: move at batch cost rather than source average? Rec: batch cost.

## 10. Deviations

- "Received" folded into "Completed"; no cancel after dispatch (return-to-source is the undo).
- Damage/loss are Adjustment movements with ReasonCode on the transfer, not new movement types.
- Transfer header carries BranchId plus Source/DestinationBranchId snapshots with a three-way Branch filter (not the single-BranchId pattern).
- Transit excluded from branch-scoped StockVisibility (changes the Phase 8 "company-level visible to all" rule for this one type).
- Arch 10.2 source/destination movement pair stays replaced by two signed rows (ADR-02).

## Owner approval (2026-11-11)

Design approved, deviations accepted. All recommendations accepted: O1 approval always required, self-approval allowed with Approve at the source (thresholds later); O2 damaged-on-arrival written off from Transit (Adjustment/Damage); O3 excess refused on receipt, extra via a Correction adjustment at the destination; O4 Resolve (write-off / return-to-source) company-level only; O5 new codes granted only to Administrator; O6 all non-Transit endpoint pairs incl. vehicles; O7 no reservation, availability checked at dispatch; O8 transfers move at batch cost.

## Delivery record (2026-11-11)

Status: **Phase 9 delivered — review "Approve with fixes" (no Blocker/Major), fixes applied.** Verify: 1528 unit / 1020 PostgreSQL integration / 192 Vitest, 0 warnings. Migration `20261111040145_AddStockTransfers` verified on PostgreSQL (Up on empty and on the previous head with two companies; guards G0–G3; Down guard; Down → Up; idempotent script). Release **together with 8b** (Phase 8 O1 = A).

**Delivered**
- Schema (D1): StockTransfers, StockTransferLines, StockTransferLineBatches, StockTransferReceipts, StockTransferReceiptLines; TransferIn/TransferOut with sign CHECK; three-way Branch filter; 7 permissions (6 branch-eligible, Resolve company-only); `scripts/sql/stock-transfers-validation.sql` (V1–V8).
- Service (D2–D10): state machine with state-idempotent submit/approve/reject/cancel; dispatch source → Transit and receipts/resolve per line's own batches at batch cost; RemainingBaseQty unchanged by moves; damage/loss as Adjustment at Transit; idempotency scopes for create/dispatch/receipt/resolve; lock order idempotency → header → products → locations → stock rows; TRF numbering; audit events; HasOpenTransfers real; transfer lines lock product units.
- API: 11 endpoints under `/api/v1/stock-transfers`; Transit visible only to company-level readers who are company-wide members.
- Tests: lifecycle matrix, quantities/snapshots, permission/fit matrix, §13.4 walkthrough on PostgreSQL with V1–V8 after every step (A 125 / B 50, B1 cost preserved, company 172), races (double dispatch, over-receipt, opposite directions, dispatch vs sale, vs deactivation), API, migration. RestrictedUserApiTests BatchLabels now uses a real transfer.

**Fixes during delivery**
- `UpdateDraftAsync` always failed (new lines with preset ids were tracked as Modified) → added explicitly.
- Branch allocation: a company-level grant no longer opens locations of branches outside an AssignedBranches member's allocation (fit bypass, Resolve needs both endpoints accessible); Transit visibility additionally requires a company-wide membership.

**Deviations / notes**
- Company-level transfer permission (company-wide member) may act on any location, including another branch's vehicle (sales still never may).
- Dispatch input lists every line (0 = not shipped); list rows show line counts, not a cross-product quantity.
- An idempotent replay returns the stored resource before permission/fit re-checks (key is per user + company; graph still Branch-filtered); if the caller's scope was revoked meanwhile, the answer is 400 on `IdempotencyKey`.
- Release note: average cost is rounded to 6 dp at each Transit hop, so company value (Σ QOH × AvgCost) can drift by rounding; quantities are exact (V1–V8). Do not assert exact value conservation.

**Phase 11 carry-over:** transfer list/detail/create/approve/dispatch/receive (variance)/resolve screens; TransferIn/TransferOut labels and movement → transfer link; Transit display; field-error mapping; Vitest specs.
