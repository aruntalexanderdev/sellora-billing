# Phase 0 audit: backend and database (multi-company / multi-branch / multi-location)

Scope: read-only audit of `backend/` against `docs/architecture/Sellora-Multi-Company-Branch-Architecture-Claude.md`
(called "arch" below) §3–§10, §14, §16, §17, §19, and `docs/analysis/unit-conversion-restructuring-analysis.md`
(called "analysis") §13.4–13.6, §25 (Phase B, 6b), §27 D5. HEAD = `a7c1c4e` (Phase 6a). Working tree clean.
Paths are relative to `backend/src/` unless stated. "ASSUMPTION" marks things not verifiable from code.

---------------------------------------------------------------------------------------------------------------
## 1. Current-state backend architecture

### Layers
| Project | Role | Evidence |
|---|---|---|
| Sellora.Domain | Entities, enums, service interfaces (`Abstractions/I*Service.cs`), input models, pure validators (`Inventory/ProductUnitConfigurationValidator.cs`, `ProductUnitGraph.cs`), `PermissionCatalog`. No EF reference. | `Sellora.Domain/*` |
| Sellora.Infrastructure | `SelloraDbContext` (30 DbSets, `Data/SelloraDbContext.cs:8-45`), configurations loaded via `ApplyConfigurationsFromAssembly` (`:49`), migrations, write services, `DbSeeder`, JWT/password security. | `DependencyInjection.cs:18-32` |
| Sellora.Api | Controllers (inject `SelloraDbContext` directly in **every** controller; read/report queries live in controllers), `Contracts/*` DTO records, `Authorization/*` permission policies, `ErrorHandling/GlobalExceptionHandler.cs`. | e.g. `Controllers/SalesController.cs:19` |

- No repository layer, no MediatR/CQRS, no global query filters (`HasQueryFilter` not used anywhere), no `ICurrentUser`/tenant
  context abstraction: services receive `userId` as a method parameter (e.g. `SalesService.CreateSaleAsync(..., Guid createdByUserId, ...)`,
  `Infrastructure/Services/SalesService.cs:12`); controllers read `sub`/`NameIdentifier` claim inline
  (`Controllers/UsersController.cs:58`, `StockAdjustmentsController.cs:86`, `ExchangeRatesController.cs:126`).
- **Write logic in controllers (not services)** for masters and admin: Users (6 SaveChanges), Roles (4), Customers (3), Suppliers (3),
  Taxes (3), Currencies (3), ExchangeRates (2), ExpenseCategories (3), Settings (1), Products (1), FinancialAccounts (1). Stock/money
  documents (purchase, sale, returns, adjustments, pricing, products-units, expenses, accounts) go through services.
  This conflicts with arch §3.1 / §14 ("do not inject DbContext into controllers for business operations") — see §8 C9.

### Conventions (verified)
| Convention | Evidence |
|---|---|
| Guid ids, app-generated (`Guid.NewGuid()`), `uuid` | all entities; e.g. `SalesService.cs:39` |
| Quoted PascalCase plural tables, `PK_`/`FK_`/`IX_`/`CK_` names | configurations; e.g. `Configurations/StockConfigurations.cs:39-48` |
| String enums: `HasConversion<string>()` in DB, `JsonStringEnumConverter` in API | `StockConfigurations.cs:12,17`; `Api/Program.cs:19-20` |
| `DomainValidationException(field, message)` → 400 ValidationProblemDetails; unique/check/FK violation + `DbUpdateConcurrencyException` → 409 | `Domain/Abstractions/DomainValidationException.cs:10-30`; `Api/ErrorHandling/GlobalExceptionHandler.cs:23-40` |
| Composite same-product FKs via alternate key `ProductUnits(ProductId, Id)` | `Configurations/ProductConfiguration.cs:29`, `StockConfigurations.cs:27-31`, `PurchaseConfigurations.cs:126,166`, `SalesConfigurations.cs:115,157`, `StockAdjustmentConfiguration.cs:34` — **reusable precedent for `(CompanyId, Id)` composite FKs required by arch §7.4/§17.1** |
| Mostly `DeleteBehavior.Restrict`; Cascade only parent→lines | `Data/Configurations/*` |
| Transactions: `StockLocks.BeginAsync` (relational only) | `Services/StockLocks.cs:124-125` |
| Row locks: `FOR KEY SHARE` on Products for document creates; `FOR UPDATE` on Products for unit/price edits; `FOR UPDATE` on StockItems in ProductId order; insert-then-lock for first stock | `StockLocks.cs:143-153, 159-166, 182-228, 230-240` |
| Advisory locks: document numbers `pg_advisory_xact_lock(hashtext('sellora:docno:'+series))`; idempotency `hashtext('sellora:idem:{user}:{scope}:{key}')` | `StockLocks.cs:132-136`, `:100-113` |
| Idempotency: `IdempotencyKeys (UserId, Scope, Key)` unique, scopes purchase/sale/stock-adjustment only | `Domain/Entities/IdempotencyKey.cs`, `Configurations/IdempotencyKeyConfiguration.cs:14`, `StockLocks.cs:93-95` |
| Document numbers = `COUNT(*)+1` under advisory lock: `PUR-`, `PRT-`, `{InvoicePrefix}-`, `SRT-`, `ADJ-`; `EXP-` **without lock** | `PurchaseService.cs:636-647`, `SalesService.cs:505-518`, `StockAdjustmentService.cs:202-207`, `ExpenseService.cs:46` |
| Auditing fields: `CreatedByUserId` + `CreatedAt` on documents; `CancelledAt/CancelledByUserId` on PO/SI/Expense; **no `UpdatedAt/UpdatedBy` (except `Expense.UpdatedAt`), no audit-log table, no correlation id** | `Domain/Entities/PurchaseOrder.cs:49-53`, `Expense.cs:39-47` |
| Soft delete: `IsActive` deactivation flags on masters; **no `IsDeleted`**; only hard delete is unreferenced ProductUnits | `ProductService.cs:451-454` |
| Money/qty precision: base qty numeric(18,3), per-base cost numeric(18,6), factor numeric(28,10) | `StockConfigurations.cs:13,59-60` |
| Singletons: `BusinessSettings` ("one business identity per deployment") read with `.SingleAsync()` in **23 places** | `Domain/Entities/BusinessSettings.cs:3`; grep `BusinessSettings\.` in Services/Controllers |
| Startup migrate + seed gated by `Database:ApplyMigrationsOnStartup` (appsettings.json currently `true`) | `Api/Program.cs:88-104`; `Api/appsettings.json:10` |

---------------------------------------------------------------------------------------------------------------
## 2. Entity and relationship map

No `Company`, `Tenant`, `Branch`, `Store`, `Warehouse`, `Location`, `Vehicle` concept exists anywhere (grep-verified).
The only "business identity" is the singleton `BusinessSettings` (name, address, GSTIN, InvoicePrefix, BaseCurrencyId).

Proposed classification: **P** = platform/global reference, **C** = company-owned (needs CompanyId),
**B** = branch-operational (needs BranchId), **L** = location-level stock (needs StockLocationId). "inherit" = scope derives from parent FK.

| Entity / table | Key FKs | Unique constraints today | Target scope |
|---|---|---|---|
| Users | RoleId (required, single role) `UserConfiguration.cs:23-25` | Username, Email `UserConfiguration.cs:20-21` | **P** identity; membership via new UserCompanyMemberships |
| Roles | — | Name `RoleConfiguration.cs:14` | **C** (+ScopeType, nullable BranchId) |
| Permissions | — | Code `PermissionConfiguration.cs:16` | **P** catalogue (seeded from `PermissionCatalog`) |
| RolePermissions | (RoleId, PermissionId) PK `RolePermissionConfiguration.cs:11` | PK | inherit (Role) |
| RefreshTokens | UserId | TokenHash | **P** (bind to selected company context — see §3) |
| Units | — | Name, ShortCode `UnitConfiguration.cs:16-17` | DECISION: **P** reference (recommended; standard factors are universal) or C |
| Currencies | — | Code `CurrencyConfigurations.cs:15` | **P** reference |
| ExchangeRates | CurrencyId | (CurrencyId, EffectiveDate) `:29` | **C** (rate is relative to the company's base currency) |
| Taxes | — | Name `TaxConfiguration.cs:14` | **C** |
| BusinessSettings | BaseCurrencyId | none (singleton by convention) | becomes **Company** profile (1:1). GSTIN/InvoicePrefix possibly **B** (open question) |
| Suppliers | CurrencyId | none (Name indexed) | **C** |
| Customers (incl. walk-in) | — | none | **C** (one walk-in per company) |
| ExpenseCategories | — | Name | **C** |
| Products | BaseUnitId, TaxId | Sku `ProductConfiguration.cs:16` | **C** (D5: company-wide product master) |
| ProductUnits | ProductId, UnitId, InnerProductUnitId | (ProductId,UnitId) active; base/default partials; **Barcode global** `ProductConfiguration.cs:56-57` | **C** inherit; barcode uniqueness must become per company |
| SellingPrices | ProductUnitId, CreatedByUserId | none | **C** inherit (D5: optional per-location override later) |
| PurchaseOrders | SupplierId, CurrencyId, CreatedByUserId | PurchaseNo `PurchaseConfigurations.cs:35` | **C+B**, + receiving **StockLocationId** |
| PurchaseOrderLines | PurchaseOrderId, ProductId, (ProductId,ProductUnitId), UnitId, TaxId | — | inherit |
| PurchasePayments | PurchaseOrderId, ReversesPaymentId, PurchaseReturnId | ReversesPaymentId | inherit |
| PurchaseReturns | PurchaseOrderId | ReturnNo `:89` | **C+B**, + source **StockLocationId** |
| PurchaseReturnLines | PurchaseReturnId, PurchaseOrderLineId, ProductId, ProductUnitId | — | inherit |
| PurchaseBatches | PurchaseOrderLineId? / StockAdjustmentId?, ProductId | BatchSequence identity `:183,193` | **C**; remaining qty becomes per-location (§4) |
| SalesInvoices | CustomerId, CreatedByUserId | InvoiceNo `SalesConfigurations.cs:26` | **C+B**, + source **StockLocationId** |
| SalesInvoiceLines | SalesInvoiceId, ProductId, ProductUnitId, UnitId, TaxId | — | inherit |
| SalesInvoiceLineBatchConsumptions | SalesInvoiceLineId, PurchaseBatchId | — | inherit (location = invoice location) |
| SalesPayments | SalesInvoiceId, ReversesPaymentId, SalesReturnId | ReversesPaymentId | inherit |
| SalesReturns | SalesInvoiceId | ReturnNo `:78` | **C+B**, + **StockLocationId** (restock location) |
| SalesReturnLines | SalesReturnId, SalesInvoiceLineId, ProductId, ProductUnitId | — | inherit |
| StockAdjustments | ProductId, ProductUnitId, UnitId, UserId | AdjustmentNo `StockAdjustmentConfiguration.cs:25` | **C+B**, + **StockLocationId** |
| StockItems | PK = ProductId `StockConfigurations.cs:58` | PK | **L**: re-key to (StockLocationId, ProductId) |
| StockMovements | ProductId, UserId, PurchaseBatchId?, (ProductId,EnteredProductUnitId) | — | **C+L** |
| Expenses | CategoryId, CurrencyId, CreatedByUserId | ExpenseNo `ExpenseConfigurations.cs:34` | **C**, BranchId optional (open question) |
| FinancialAccounts (cash drawer / bank) | CreatedByUserId | Name `FinancialAccountConfiguration.cs:15` | **C**, BranchId optional (cash drawers are typically per branch) |
| FinancialTransactions | PurchasePaymentId?/SalesPaymentId?/ExpenseId?, AccountId? | one per source payment | **C** (branch via source/account) |
| IdempotencyKeys | UserId (no FK) | (UserId, Scope, Key) | **C** add CompanyId to key |

Relationships to note: `User → Role` is N:1 and required — a user has exactly one role globally (`User.cs:13-14`). Every
document references a `User` via CreatedByUserId; that FK stays (platform identity) and does not need to change.

---------------------------------------------------------------------------------------------------------------
## 3. Authentication and authorization

### What exists
- **JWT** HS256, issuer/audience/lifetime validated, 30s skew (`Api/Program.cs:47-62`). Access token 15 min, refresh 14 days
  (`Infrastructure/Security/JwtOptions.cs:10-11`). Refresh tokens hashed (SHA-256), rotated on refresh (`Services/AuthService.cs:40-60`).
- **Claims**: `sub`, email, name, `role_name`, plus one `permission` claim per code (`Security/JwtTokenService.cs:20-27`,
  `Domain/Auth/AuthClaimTypes.cs:5-6`). **No company/branch/membership claim.**
- **Permission resolution**: `PermissionResolver.Resolve(role)` — system role → whole catalogue; else role's permissions
  (`Domain/Auth/AuthModels.cs:48-58`). Called at login/refresh/me (`AuthService.cs:75`, `AuthController.cs:68`).
- **Catalogue**: 57 codes, `Module.Action` style (`Users.View`, `Inventory.Adjust`, `Sales.PriceOverride`, `Reports.Profit`…),
  seeded read-only (`Domain/Permissions/PermissionCatalog.cs:12-96`; seeder `Data/DbSeeder.cs:36-75`).
- **Policies**: dynamic `PermissionPolicyProvider` turns any catalogue code into a policy (`Api/Authorization/PermissionPolicyProvider.cs:14,26-38`);
  `PermissionAuthorizationHandler` only checks the claim is present (`PermissionAuthorizationHandler.cs:8-19`). Registered at
  `Program.cs:64-66`. **No FallbackPolicy** — protection relies on attributes, guarded by a reflection test
  (`tests/Sellora.Tests/Api/EndpointAuthorizationTests.cs`, allow-list of signed-in-only endpoints incl. `DashboardController.Get`).
- **Roles**: single system role "Administrator" (`IsSystem`, all permissions, not editable/disable-able; `RolesController.cs:85,156`;
  seeded `DbSeeder.cs:103-119`) + custom roles. One seeded admin user `admin@selloramart.in` (`DbSeeder.cs:16,121-141`).

### Defects found (exist today, independent of the new spec)
1. **Inactive roles still grant permissions.** `PermissionResolver.Resolve` and login/refresh never check `Role.IsActive`
   (`AuthModels.cs:50-58`, `AuthService.cs:15-27, 44-50`). `Roles.Disable` therefore has no security effect.
2. **Privilege escalation via user edit.** `UsersController` create/update accept any existing `RoleId`, including the system
   Administrator role and inactive roles, and including the caller's own account (`UsersController.cs:137-152, 180-191`). A holder of
   `Users.Edit` can make themself Administrator. Violates arch §7.6.
3. **Privilege escalation via role edit.** A holder of `Roles.Edit` can add any permission to any non-system role, including the
   role they hold (`RolesController.cs:76-110`); no "may only delegate what you hold" rule.
4. Permissions are baked into the access token; revocation/disable takes effect only after ≤15 min (stateless JWT). Arch §6.5 step 9
   requires "recalculate effective access and invalidate cached authorization data".
5. `DashboardController` is `[Authorize]` only (`DashboardController.cs:14`) yet returns stock value and low-stock data (`:71-90`).

### Gaps vs arch §5–§7
| Arch requirement | Current | Gap |
|---|---|---|
| Platform Super Admin separate from company governance (§5.1) | "Administrator" = all permissions, platform and business mixed | Split: platform role/flag outside company RBAC; Administrator becomes a per-company system role |
| Governance members + individually assigned governance permissions (§5.2–5.4) | none | New `CompanyGovernanceMembers` + `GovernanceMemberPermissions`; `Company.*` keys added to catalogue (none overlap existing codes; existing `Users.*`/`Roles.*` are close to `Company.Users.*`/`Company.Roles.*` — dedupe decision required, §8 C6) |
| Explicit company membership with AccessScope (§6.1) | none | `UserCompanyMemberships` |
| Historical branch allocations, one active primary (§6.2–6.3) | none | `UserBranchAssignments` with partial unique indexes |
| Company- and branch-scoped roles, unique within scope (§7.1–7.3) | global roles, `Roles.Name` globally unique | Add CompanyId/ScopeType/BranchId; replace unique index with two partial uniques |
| Multiple role assignments per user, scope-checked (§7.4) | `Users.RoleId` single required FK | `UserRoleAssignments(MembershipId, RoleId, CompanyId, BranchId?)` with composite FKs |
| Effective permission evaluation per company/branch (§7.5) | flat claim check | Resource-based `IAuthorizationService` / service-layer `IAccessContext` evaluating (company, branch) |
| Resource-based authorization, never trust client CompanyId/BranchId/LocationId (§14) | no ids exist to trust | Company context must come from a validated token claim + membership row, not a header alone |
| Audit (§16) | none | `AuditEvents` table + writer |

---------------------------------------------------------------------------------------------------------------
## 4. Inventory

### Current model
- `StockItems` PK = `ProductId` (`Domain/Entities/StockItem.cs:11`, `StockConfigurations.cs:58`): `QuantityOnHand` numeric(18,3), `AverageCost` numeric(18,6).
- `PurchaseBatches`: one per purchase line or per adjustment increase; `PurchasedBaseQty`, `RemainingBaseQty`, `UnitCost`, global
  identity `BatchSequence` → `BATCH-0007` (`PurchaseBatch.cs:28-44`; `PurchaseConfigurations.cs:183-193`; CHECK 0 ≤ remaining ≤ purchased `:186`).
  All stock lives in batches (invariant QOH = Σ RemainingBaseQty; verified by `tests/Sellora.IntegrationTests/Stock/StockLedgerReconciliationTests.cs`, `TestData/StockInvariants.cs`).
- `StockMovements`: signed `QuantityBaseUnits`, `Type` (8 values incl. PurchaseCancellation, OpeningStock; `StockMovementType.cs`),
  `PurchaseBatchId?`, `ReferenceType/Id/No`, `UserId`, `OccurredAt`, `ReasonCode`, entered-unit snapshot (`StockMovement.cs:8-52`),
  CHECKs incl. arithmetic and type/sign (`StockConfigurations.cs:37-48`). Indexes `(ProductId, OccurredAt)`, `PurchaseBatchId`, `(ReferenceType, ReferenceId)` (`:33-35`).
- `SalesInvoiceLineBatchConsumptions`: per line × batch `QtyBaseUnits`, `ReturnedQtyBaseUnits`, `UnitCost` (`SalesInvoiceLineBatchConsumption.cs`).
- Locks: `EnsureAndLockStockItemsAsync` (insert ON CONFLICT (ProductId) DO NOTHING, then FOR UPDATE ORDER BY ProductId) — the only StockItem creator (`StockLocks.cs:182-228`);
  `LockStockItemsAsync` (`:230-240`); product KEY SHARE before StockItems (`:143-153`).
- FIFO: `BatchAllocator.ConsumeFifoAsync(db, productId, baseQty, …)` filters `ProductId && RemainingBaseQty > 0`, orders `CreatedAt, BatchSequence`
  (`Services/BatchAllocator.cs:25-58`). Used by sales (`SalesService.cs:658`) and adjustment decreases (`StockAdjustmentService.cs:131`).
- Writers: purchase create (`PurchaseService.cs:746-779`), purchase return from the line's own batch (`:447-505`), purchase cancel requires batch untouched (`:549-612`, check `:571`),
  sale (`SalesService.cs:633-652`), sale return into the exact consumed batches (`:383, 415-420`), sale cancel (`:447-500`), adjustment ±/opening (`StockAdjustmentService.cs:89-172`).

### What changes when a location dimension is added (analysis §13.5 + arch §8, §10, Phase 3/8)
| Element | Change |
|---|---|
| New `StockLocations` | Id, CompanyId, BranchId?, LocationType (BranchStore, CompanyWarehouse, BranchWarehouse, Vehicle, Transit), Code (unique per company), Name, IsActive, audit fields. Alternate key `(CompanyId, Id)` for composite FKs; CHECK on type/branch consistency. |
| `StockItems` | **PK change** `ProductId` → `(StockLocationId, ProductId)` (+ `CompanyId` denormalised, composite FK `(CompanyId, StockLocationId)` → StockLocations). Backfill: every row → default location. Drop PK, add column, backfill, add new PK in one migration (table is small: ≤ #products rows). `ReorderLevel` per location is a later option (Product.ReorderLevel stays as default). |
| Batch balances | `PurchaseBatches.RemainingBaseQty` is a single global number; a transfer of part of B1 to Branch B needs per-location remainder. Recommended: new `BatchLocationBalances(PurchaseBatchId, StockLocationId, RemainingBaseQty, CompanyId)` PK (PurchaseBatchId, StockLocationId); backfill 1 row per batch with RemainingBaseQty > 0 at the default location; keep `PurchaseBatches.RemainingBaseQty` as the company-wide total for one release (dual-write, validated) then deprecate. Alternative (split batch per location on transfer) loses the "same batch, same cost" trace in analysis §13.4 row 5 — not recommended. Add `PurchaseBatches.OriginStockLocationId`. |
| `StockMovements` | add `StockLocationId` (NOT NULL after backfill) + `CompanyId`; index `(StockLocationId, ProductId, OccurredAt)`. Keep **one signed row per location** (TransferOut at source, TransferIn at destination, linked by reference) — not a source/destination pair in one row — so `Σ movements per (location, product) = QOH` stays a simple GROUP BY. |
| Consumptions | add `StockLocationId` (or derive from invoice); returns must restore into the same (batch, location). |
| Locks | `EnsureAndLockStockItemsAsync(db, locationId, productIds)`: ON CONFLICT (StockLocationId, ProductId); ORDER BY (StockLocationId, ProductId) — for transfers lock both locations' rows in one ordered statement to avoid deadlock. Product KEY SHARE unchanged. |
| FIFO | `BatchAllocator.ConsumeFifoAsync(db, locationId, productId, …)` over `BatchLocationBalances` join `PurchaseBatches` for ordering; decrements both the location balance and (during dual-write) the batch total. |
| Availability | `SalesService.cs:72-84` reads one StockItem per product → must read the (location, product) row. |
| Reads | every `db.StockItems.Where(s => s.ProductId == p.Id).Select(...).FirstOrDefault()` correlated subquery becomes **silently wrong** (arbitrary row) once >1 location exists: `StockController.cs:29-30,88`, `ReportsController.cs:33-34`, `DashboardController.cs:71,80`, `SalesController.cs:71-73`, `PricingController.cs:40`. Must become location-filtered or `SUM` per product (value only across products). |
| Invariants | reconciliation SQL gains `StockLocationId` in GROUP BY; plus Σ BatchLocationBalances per batch = PurchaseBatches.RemainingBaseQty (during dual-write). |

### Conflict: arch §10.1 balance key `CompanyId + StockLocationId + ProductId + BatchId + UnitId`
- Our accepted invariant (CLAUDE.md, skill `stock-ledger-invariants`, analysis D1/§5): stock state is **always in base units**; conversions happen once at line build and are snapshotted.
  A `UnitId` in the balance key would create one balance row per (product, unit) → the same physical stock in two rows, double counting or
  requiring cross-unit netting; it re-introduces the sealed/loose packaging-state model explicitly rejected (D4).
- **Recommendation (ADR):** keep balances base-unit only. Satisfy §10.1's intent ("product and unit traceability") by:
  (a) the unit of every balance is implicitly `Product.BaseUnitId`, which is immutable once stock history exists (`ProductService.UpdateProductAsync`, rule 9);
  (b) every movement and document line already carries entered `ProductUnitId`/`UnitCode`/`ConversionFactor` snapshots (`StockMovement.cs:41-52`).
  Implement the key as two tables instead of one nullable-BatchId table: `StockItems(StockLocationId, ProductId)` (aggregate + lock anchor + AverageCost)
  and `BatchLocationBalances(PurchaseBatchId, StockLocationId)`; `CompanyId` denormalised on both with composite FK. Because every unit of stock
  is in a batch, BatchId is never null → `NULLS NOT DISTINCT` is not needed (tests run PG 17, `tests/Sellora.IntegrationTests/Infrastructure/PostgresFixture.cs:21`; production PG version is unknown — ASSUMPTION/open question, only matters if a nullable key is adopted).
- Record as a deviation ADR per arch §3.1 last bullet.

---------------------------------------------------------------------------------------------------------------
## 5. Workflow impact map

| Workflow | Today | Change for company / branch / location |
|---|---|---|
| Purchase create | `PurchaseService.CreatePurchaseAsync` `:12-104`: idempotency → supplier/currency → number → KEY SHARE → EnsureAndLock StockItems → lines → batches/movements/QOH | Header gets CompanyId, BranchId, `ReceivingStockLocationId` (validated: active, same company, actor allowed for that branch/location). Supplier/currency/product/tax must belong to the company. StockItems/batch balances at that location. Number series per company (or branch). |
| Purchase return | `CreateReturnAsync` `:356-547`, returns from the line's batch (`:447,482`) | Return from a chosen location that holds the batch (`BatchLocationBalances`); default = purchase receiving location. Lock (location, product). |
| Purchase cancel | `CancelPurchaseAsync` `:549-624`: refused unless every batch untouched (`:571`) | "Untouched" must mean all remainder still at the receiving location (no transfers/consumption anywhere). |
| Sales / POS | `CreateSaleAsync` `SalesService.cs:12-109`, availability per product `:72-84`, FIFO `:654+` | Header gets CompanyId, BranchId, `SourceStockLocationId` (POS = the branch store location from user's context). Availability, FIFO and QOH per location. Price lookup stays company-wide (D5) with optional per-location override later. Walk-in customer per company. |
| Sales return | `CreateReturnAsync` `:273-445`, restores exact consumed batches `:383` | Restore to the invoice's source location (default) — returning at a different branch is a business decision (open question). |
| Sale cancel | `CancelSaleAsync` `:447-503` | Restore to the invoice's location. |
| Adjustments / opening stock | `StockAdjustmentService.CreateAdjustmentAsync` `:26-200` | Requires `StockLocationId`; opening stock per location; FIFO per location. |
| Pricing | `PricingService.SetPriceAsync` `:10-…` locks Product FOR UPDATE; `SellingPrices` per ProductUnit | Company-scoped via product. Per-location override is a later, separate table (D5). Average cost shown in pricing grid (`PricingController.cs:40`) must pick a location or a weighted company average. |
| Products / units | `ProductService` (SKU uniqueness `:522`, barcode uniqueness `:359-369` + index `IX_ProductUnits_Barcode` name constant `:27`) | Product CompanyId; SKU/barcode unique per company; Units decision (P vs C). |
| Expenses / cash & bank / financial transactions | services + controllers; `FinancialAccounts.Name` global unique | CompanyId; optional BranchId on accounts and expenses; base currency per company. |
| Reports / dashboard | Queries in `ReportsController.cs`, `AccountsReportsController.cs`, `DashboardController.cs` with no scope predicate | Every query gets `CompanyId` predicate + optional branch/location filter; stock reports per location or summed per product (never across products). |
| Document numbering | `COUNT(*)+1` + global advisory key per series (`StockLocks.cs:132-136`) | Count must be filtered by the scope and advisory key must include the scope (`sellora:docno:{companyId}[:{branchId}]:{series}`), else two companies share a sequence and the unique index (global) collides. Recommend a `DocumentSequences(CompanyId, BranchId?, Series, NextValue)` row locked FOR UPDATE instead of COUNT. `ExpenseService.cs:46` lacks the lock today (bug). Uniques become `(CompanyId, InvoiceNo)` etc. GST invoice series per GSTIN/branch/financial year = open question. |
| Settings | `SettingsController.cs:17-48` singleton `BusinessSettings` | Becomes company profile; 23 `.SingleAsync()` call sites must become "settings of the current company" (helper) — they will throw once a second company row exists. |
| Seeder | `DbSeeder.SeedAsync` seeds admin, Administrator role, BusinessSettings, demo suppliers/products/customers whenever startup-migrate is on (`DbSeeder.cs:19-34`) | Seed default company/branch/location + memberships; demo data must not be seeded into real tenants. |
| Idempotency | `(UserId, Scope, Key)` | add CompanyId to unique key and advisory key; returns/cancellations remain non-idempotent (protected by state checks) — acceptable, note for transfers (dispatch/receive must be idempotent per arch §10.3). |

---------------------------------------------------------------------------------------------------------------
## 6. Migration risk assessment

### Tables needing scope columns (all nullable → backfill → NOT NULL + FK, in separate steps)
- **CompanyId**: Roles, Taxes, ExchangeRates, Suppliers, Customers, ExpenseCategories, Expenses, FinancialAccounts, FinancialTransactions, Products,
  ProductUnits (denormalised for barcode uniqueness; composite FK (CompanyId, ProductId) → Products(CompanyId, Id)), PurchaseOrders, PurchaseReturns,
  PurchaseBatches, SalesInvoices, SalesReturns, StockAdjustments, StockItems, StockMovements, IdempotencyKeys, (Units — only if decided C).
  BusinessSettings → becomes/joins `Companies` (1:1, CompanyId PK/FK).
- **BranchId**: PurchaseOrders, PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments; optional on Expenses, FinancialAccounts.
- **StockLocationId**: StockItems (PK), StockMovements, PurchaseOrders (receiving), PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments,
  SalesInvoiceLineBatchConsumptions, new BatchLocationBalances, PurchaseBatches.OriginStockLocationId.
- Lines/payments inherit through parent FKs (no column) unless a query pattern needs it.

### Backfill (arch §17.3)
1. Insert one `Companies` row from the `BusinessSettings` singleton (fixed, documented Guid; name = BusinessName). 2. One default Branch
("Main"), 3. one BranchStore `StockLocation` for it. 4. `UPDATE … SET CompanyId = <default>` per table; documents also `BranchId`/`StockLocationId`.
5. `UserCompanyMemberships`: every existing user → default company, **AccessScope = CompanyWide** (the only truthful mapping; do not fabricate
branch allocations — arch §17.3 last para). 6. Existing roles → company-scoped roles of the default company; `Users.RoleId` → one `UserRoleAssignments` row each;
keep `Users.RoleId` until a later destructive migration. 7. `BatchLocationBalances` from `PurchaseBatches.RemainingBaseQty > 0`.
Validation SQL: per-table `COUNT(*) WHERE CompanyId IS NULL` = 0; row counts unchanged; stock reconciliation per (location, product); Σ batch-location = batch remaining;
cross-company FK checks (document CompanyId = supplier/customer/product CompanyId).

### Uniqueness that must become scoped
| Today | Target |
|---|---|
| `Products.Sku` | `(CompanyId, Sku)` |
| `IX_ProductUnits_Barcode` (filtered active) | `(CompanyId, Barcode)` filtered — rename impacts constant `ProductService.cs:27` |
| `Roles.Name` | partial uniques `(CompanyId, lower(Name)) WHERE ScopeType='Company'` and `(CompanyId, BranchId, lower(Name)) WHERE ScopeType='Branch'` |
| `Taxes.Name`, `ExpenseCategories.Name`, `FinancialAccounts.Name` | `(CompanyId, Name)` |
| `ExchangeRates (CurrencyId, EffectiveDate)` | `(CompanyId, CurrencyId, EffectiveDate)` |
| `PurchaseNo`, `InvoiceNo`, `ReturnNo` ×2, `AdjustmentNo`, `ExpenseNo` | `(CompanyId[, BranchId], No)` |
| `IdempotencyKeys (UserId, Scope, Key)` | `(CompanyId, UserId, Scope, Key)` |
| `Users.Username`, `Users.Email`, `Permissions.Code`, `Currencies.Code`, `RefreshTokens.TokenHash`, `PurchaseBatches.BatchSequence` | stay global |
| `Units.Name/ShortCode` | global if Units = P (recommended) |

### Index impact
Add `(CompanyId, <date>)` on document headers used by reports; `(StockLocationId, ProductId, OccurredAt)` on StockMovements; `(CompanyId, Name)` lookups.
Existing `(ProductId, OccurredAt)` stays. Every `CompanyId` FK needs an index (EF adds one per FK; review to avoid redundant single-column indexes where a composite leads with CompanyId).

### Data volume / locking
ASSUMPTION: single-store deployment, low-to-moderate volume (no prod data accessible). Largest tables: StockMovements, SalesInvoiceLines, consumptions,
FinancialTransactions. `ADD COLUMN NULL` is metadata-only in PG; the backfill `UPDATE` rewrites rows (bloat + long lock on big tables) — batch it or run in a
maintenance window; `SET NOT NULL` scans (use `CHECK … NOT VALID` + `VALIDATE` if volume is large). StockItems PK swap takes ACCESS EXCLUSIVE briefly (small table).
Advisory-lock and FOR UPDATE semantics are unchanged in kind.

### Rollback
Additive steps have meaningful `Down()`s (drop columns/tables). PK swap on StockItems is reversible only while exactly one location exists per product
(Down must refuse if > 1 location row per product). Destructive steps (drop `Users.RoleId`, drop global uniques, retire `PurchaseBatches.RemainingBaseQty`)
go in a later release. Per D10: snapshot → idempotent script → validation SQL → deploy; rollback = restore snapshot + previous build. Startup auto-migrate is
still ON in base `appsettings.json:10` — must be off for the environment that receives these migrations (Program.cs:82-87 comment).

---------------------------------------------------------------------------------------------------------------
## 7. Test inventory and coverage gaps

### Existing
- `tests/Sellora.Tests` (xUnit, EF InMemory — no locks/constraints): services (purchase, sales, returns, adjustments, pricing, product units,
  units, expenses, accounts, multi-currency), controller-level tests instantiating controllers with fake `ClaimsPrincipal`
  (`Api/SalesEndpointsTests.cs:33`, `Api/StockAdjustmentsControllerTests.cs:53-56`), auth (`Auth/AuthServiceTests.cs`, `PermissionResolverTests.cs`, `PermissionSeederTests.cs`),
  authorization handler (`Authorization/PermissionAuthorizationHandlerTests.cs`), reflection guard that every endpoint has a policy (`Api/EndpointAuthorizationTests.cs`).
- `tests/Sellora.IntegrationTests` (Testcontainers `postgres:17-alpine`, `Infrastructure/PostgresFixture.cs:21`): concurrency (`Concurrency/StockLockConcurrencyTests.cs`,
  `FirstStockConcurrencyTests.cs`, return/product-unit/unit lock tests, `Infrastructure/LockRace.cs`), DB constraints (`Constraints/*`), migration tests with
  legacy-data backfill assertions per migration (`Migrations/*MigrationTests.cs`) and idempotent-script test (`Migrations/UnitRestructuringIdempotentScriptTests.cs`),
  ledger reconciliation (`Stock/StockLedgerReconciliationTests.cs`, `TestData/StockInvariants.cs`). CI runs `dotnet test` on the whole solution (`.github/workflows/ci.yml:59-60`).

### Gaps relevant to this initiative
1. No HTTP-pipeline tests (no `WebApplicationFactory`): authentication, 401/403, policy provider wiring and resource-based authorization are never exercised end to end.
   Cross-company isolation tests (arch §18.2) need either WebApplicationFactory + Postgres or service-level tests with an access-context abstraction.
2. No test that an inactive role loses permissions (defect §3.1), none for self-escalation via `Users.Edit`/`Roles.Edit` (defects §3.2–3.3).
3. No tests for `UsersController`/`RolesController` business rules at all (writes live in controllers).
4. Concurrency tests key on product only — need (location, product) lock ordering, transfer deadlock-freedom (two locations, opposite directions), duplicate dispatch/receive.
5. No migration test for a default-company/branch/location backfill (precedent harness exists — reuse `Migrations/*MigrationTests.cs` pattern).
6. Reports/dashboard: only `Reports/CurrentStockReportTests.cs` and `AccountsReportsTests.cs` — nothing that would catch the `FirstOrDefault()` per-product StockItem reads becoming wrong with multiple locations.
7. Document numbering under concurrency is untested (and `EXP-` has no lock).
8. InMemory cannot verify composite FKs / partial unique indexes — all new scope constraints need Postgres constraint tests (`Constraints/*` pattern).

---------------------------------------------------------------------------------------------------------------
## 8. Conflicts between existing code/decisions and the spec, with migration strategies

| # | Conflict | Proposed safe strategy |
|---|---|---|
| C1 | Arch §10.1 puts `UnitId` (and nullable `BatchId`) in the balance key; accepted invariant D1/D4: balances are base-unit only. | ADR: base-unit balances; unit traceability via immutable `Product.BaseUnitId` + line/movement snapshots; two tables `StockItems(Location, Product)` + `BatchLocationBalances(Batch, Location)`; no nullable key. (§4) |
| C2 | Arch §10 conceptual `StockMovements` + `StockMovementItems` header/items and source+destination per movement; existing ledger is one signed row per product per document line with `ReferenceType/Id`. | Keep existing ledger shape (already the auditable source, invariant-tested). Add `StockLocationId` + `CompanyId`; transfers write TransferOut/TransferIn pair referencing a `StockTransfers` document (that document carries source/destination). Record as ADR. |
| C3 | Programme order (CLAUDE.md, analysis §25): next is **Phase B multi-location** then 6b transfers. Arch §19 orders Company (1) → Branch (2) → Location (3) → Warehouse/Vehicle (4) → Membership (5) → Governance (6) → RBAC (7) → Location inventory (8) → Transfers (9). | Treat arch phases 1–3 as prerequisites of analysis Phase B; map analysis Phase B ≈ arch Phase 3 + 8, analysis 6b ≈ arch 9. Update the roadmap so both documents agree before Phase 1 starts (lead decision). |
| C4 | D5 ("global product units and prices, optional per-location overrides later") was written for one business; arch §4.1 makes company the tenant boundary. | Read D5's "global" as **company-wide**: Products/ProductUnits/SellingPrices get CompanyId; no per-branch product units; per-location price override deferred. Units: recommend platform reference data (no CompanyId) — needs product-owner confirmation. |
| C5 | Single `Users.RoleId` (required) vs multiple scoped `UserRoleAssignments`. | Additive: create memberships + assignments, backfill from RoleId, switch `PermissionResolver` to assignments within the token's company/branch context, keep RoleId nullable for one release, drop later. |
| C6 | Existing catalogue `Users.*`, `Roles.*`, `Masters.*` overlap arch §5.3 `Company.Users.*`, `Company.Roles.*`, `Company.Settings.Manage`. Arch §5.3 says reuse existing naming and avoid near-duplicates. | Keep existing operational codes; add only genuinely new keys (`Company.Governance.*`, `Company.Users.ManageBranchAccess`/`TransferAccess`, `Company.Branches.*`, `Company.Warehouses.*`, `Company.Vehicles.*`, `StockLocations.*`, `StockTransfers.*`). Keep governance permissions in a **separate** catalogue/table so they cannot be put on operational roles. Permission codes are strings in JWT and Angular — renames are breaking. |
| C7 | "Administrator" system role = all permissions, effectively platform super admin + company owner in one. | Introduce platform-admin as a separate flag/table outside company RBAC (no implicit business-data access); convert Administrator to a per-company protected system role; seed admin user as owner-governance member of the default company with explicit permissions. |
| C8 | `BusinessSettings` singleton + 23 `.SingleAsync()` reads. | Phase 1: add `Companies`, migrate settings into it (or add CompanyId PK/FK to BusinessSettings), introduce one `CompanySettings` accessor resolving by current company; replace call sites before a second company can be created (block company creation until then). |
| C9 | Arch §3.1/§14: no DbContext in controllers for business operations; current master/admin writes are in controllers and every controller injects DbContext for reads. CLAUDE.md documents controllers holding read/report queries as the convention. | Do not mass-refactor. New governance/membership/role/location writes go into services (needed anyway for authoritative scope checks). Existing reads stay in controllers but must apply the company/branch predicate through a shared helper. Move Users/Roles writes into services in arch Phase 5/7 when they are rewritten. Record ADR. |
| C10 | No tenant filter mechanism. Arch §14.1 requires tenant predicates in DB queries; §16 allows RLS only as defence in depth. | Options: EF global query filters on `CompanyId` driven by a scoped `ICompanyContext` (low effort, risk: `IgnoreQueryFilters` and raw SQL in `StockLocks` bypass it) vs explicit predicates everywhere. Recommend global query filters + explicit checks in raw-SQL lock helpers + an isolation test per controller. ADR needed. |
| C11 | JWT carries flat permissions, 15-min staleness; arch §6.5 requires access invalidation on change. | Add `company_id` (selected context) claim; resolve effective permissions server-side per request from memberships/assignments (short in-memory cache keyed by user+company with version stamp bumped on change), keep JWT claims only as a hint for UI; or add a `SecurityStamp` on membership checked per request. |
| C12 | Count-based numbering with global advisory key; global unique on numbers; `EXP-` without lock. | Phase 1: scope the advisory key and count by CompanyId and switch uniques to (CompanyId, No); fix EXP lock. Consider `DocumentSequences` table when branch-level series are decided. |
| C13 | Existing privilege-escalation defects (§3 defects 1–3) contradict arch §7.6. | Fix early (can be a size-S hardening before Phase 1): check `Role.IsActive` in resolver; forbid assigning system/inactive roles without a dedicated permission; forbid self role change; restrict permission grants to those the actor holds. |
| C14 | Startup auto-migrate + demo seeding still on in base config. | Must be off before running the default-company backfill in any shared environment (D10, arch §17.4); seeder must not create demo suppliers/products in new companies. |

### Open questions for the product owner / lead
1. Are Units platform-wide reference data or company-owned? (Recommend platform.) Taxes: company-owned?
2. Document number series: per company or per branch? Per financial year? GSTIN per branch?
3. Sales returns at a branch other than the selling branch: allowed? Restock location?
4. Do warehouses sell directly, or only BranchStore/Vehicle locations? (Drives "authorized source location" rules.)
5. Production PostgreSQL major version (only matters if a nullable-key uniqueness is used).
6. Should expenses and cash/bank accounts be branch-scoped?
7. Existing users: confirm "all CompanyWide in default company" as the documented migration assumption.
