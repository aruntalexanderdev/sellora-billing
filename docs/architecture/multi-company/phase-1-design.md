# Phase 1 — Company foundation: file-level design

| Property | Value |
|---|---|
| Baseline | branch `analyse`, HEAD `4503d78` (Phase H delivered) |
| Sources | `docs/architecture/multi-company/phase-0-audit-and-plan.md` (ADR-01..13, §6 row 1, §7, §10); Appendix A §2/§5/§6; arch §4.1, §6.1, §14, §17, §19 Phase 1 |
| Status | **APPROVED by owner 2026-11-09** (deviations accepted; O1 yes, O3 yes, O4 yes; O2 deferred to Phase 2). Lead addition L1 below. |
| Size | L–XL; delivered as two review checkpoints 1a (schema) and 1b (context/filters), **released together** |

Verified facts this design relies on (path:line):
- No query filters / tenant abstraction anywhere; `AddDbContext` (no pooling) `backend/src/Sellora.Infrastructure/DependencyInjection.cs:18`; no `IDesignTimeDbContextFactory`; no hosted/background services.
- `SelloraDbContext` single-arg ctor `Data/SelloraDbContext.cs:6`; 15 `new SelloraDbContext(...)` sites in tests (14 in `Sellora.Tests`, `PostgresFixture.CreateDbContext` `tests/Sellora.IntegrationTests/Infrastructure/PostgresFixture.cs:53-59`).
- EF Core 10.0.12 (`backend/Directory.Packages.props:7`) has **named** query filters: `HasQueryFilter(string, expr)` and `IgnoreQueryFilters(IReadOnlyCollection<string>)` (verified in the package XML docs).
- 23 `BusinessSettings` reads: controllers `AccountsReportsController.cs:386`, `FinancialAccountsController.cs:33`, `CurrenciesController.cs:83,93`, `ExpensesController.cs:61`, `SettingsController.cs:17,27`, `PurchasesController.cs:76`, `ExchangeRatesController.cs:48,113`, `PurchaseReturnsController.cs:67`, `FinancialTransactionsController.cs:69`; services `ExchangeRateService.cs:11`, `ExpenseService.cs:40`, `SalesService.cs:191,314,514`, `FinancialAccountService.cs:156`, `PurchaseService.cs:53,162,277,342,384`; seeder `DbSeeder.cs:171`. Nothing references `BusinessSettings.Id`.
- Raw SQL touching company data: `StockLocks.cs:151` (Products KEY SHARE), `:164` (Products FOR UPDATE), `:191` (INSERT StockItems … ON CONFLICT), `:195`, `:238` (StockItems FOR UPDATE), `:106` idempotency advisory key, `:135` doc-number advisory key; `PurchaseService.cs:182`, `SalesService.cs:158` (header FOR UPDATE). Units raw SQL (`UnitService.cs:161`, `ProductService.cs:493`) is platform.
- Numbering = `COUNT(*)+1`: `PurchaseService.cs:636-647`, `SalesService.cs:505-518` (prefix from settings `:514`), `StockAdjustmentService.cs:202-207`, `ExpenseService.cs:44-50`.
- Index-name constant `ProductService.cs:27` (`IX_ProductUnits_Barcode`) used at `:467`.
- Child roots queried directly (need a filter even without a column): `AccountsReportsController.cs:32,73` (payments), `StockController.cs:70` (consumptions), `ReportsController.cs:64,232`, `PricingController.cs:36,78`, `SalesController.cs:75`, `Purchase/SalesReturnsController.cs:44-45,63-65`.
- Platform in-use checks that must stay cross-company: `UnitService.cs:100-106`, `UnitsController.cs:93-94`, `CurrenciesController.cs:83`.
- Frontend: interceptor refreshes on any 401 (`frontend/src/app/core/auth/auth.interceptor.ts:19-31`); tenant name hard-coded `frontend/src/app/layout/sidebar/sidebar.html:5`.

---------------------------------------------------------------------------------------------------------------
## 1. Scope

Delivers: `Companies` (+ BusinessSettings as its 1:1 profile, ADR-07); minimal `UserCompanyMemberships` (pulled forward, see D1); `CompanyId` on 19 company-owned tables + BusinessSettings (nullable → backfill to the default company → NOT NULL + FK); company-scoped uniques; `(CompanyId, Id)` alternate keys + 13 composite same-company FKs; `company_id` JWT claim + per-request membership validation (`ICompanyContext`); named EF global query filter `"Company"` with fail-closed semantics and SaveChanges stamping; company predicates in raw SQL / advisory keys; settings accessor replacing the 23 singleton reads; `DocumentSequences` replacing `COUNT(*)+1`; `POST auth/switch-company`; WebApplicationFactory + Postgres isolation harness.

Excludes: Branches/BranchId on headers (Phase 2), locations (3), warehouses/vehicles (4), branch allocations / allocation management / audit events (5), platform admin + governance (6), Roles.CompanyId / ScopeType / UserRoleAssignments (7, see D4), any company create/edit-identity API, Angular context selector (11).

---------------------------------------------------------------------------------------------------------------
## 2. Design decisions (the 8 open points)

### D1 — Company resolution before Phase 5: option (a) minimal `UserCompanyMemberships` now
- Arch §6.1: "explicit active membership … do not infer". Option (b) (implicit membership) would be an inference baked into auth code and thrown away in Phase 5; (a) creates the real table with the arch §6.1 columns so Phase 5 only **adds** (UserBranchAssignments, management service, audit) — no reshaping.
- Not fabricated: arch §17.3 step 6 + ADR-11 + owner Q7 confirm "existing users → CompanyWide member of the default company". Branch-level mapping (Q7: non-admins → Main as primary) is **Phase 5** data; Phase 1 stores `AccessScope='CompanyWide'` for all because no branch exists — Phase 5 migration re-maps non-admin users to `AssignedBranches` + Main primary (record in Phase 5 design; open question O3).
- Table `"UserCompanyMemberships"`: `Id uuid PK`, `UserId uuid NOT NULL FK→Users (Restrict)`, `CompanyId uuid NOT NULL FK→Companies (Restrict)`, `Status varchar(20) NOT NULL` (string enum `MembershipStatus {Active, Suspended, Revoked}`), `AccessScope varchar(20) NOT NULL` (`CompanyAccessScope {CompanyWide, AssignedBranches}`), `CreatedAt timestamptz`, `CreatedByUserId uuid NULL FK→Users`, `UpdatedAt timestamptz NULL`, `UpdatedByUserId uuid NULL FK→Users`.
  `IX_UserCompanyMemberships_UserId_CompanyId` UNIQUE; `AK_UserCompanyMemberships_CompanyId_Id` (for Phase 5/7 composite FKs); `IX_UserCompanyMemberships_CompanyId`; CHECKs `CK_UserCompanyMemberships_Status`, `CK_UserCompanyMemberships_AccessScope` (value lists). `AssignedBranches` is rejected by the service in Phase 1 (no branches).
- Not query-filtered (it is the lookup that establishes the context); every read uses explicit `UserId`/`CompanyId` predicates.
- `UsersController` create (`UsersController.cs:117-158`) also inserts an Active/CompanyWide membership for the caller's current company in the same SaveChanges; list (`:20-46`) and detail (`:108-112`) are restricted to users with a membership in the current company (404 otherwise). Without this a new user could not log in and Users would be a cross-company listing.

### D2 — Claim issuance and validation
- Claim `company_id` (`AuthClaimTypes.CompanyId = "company_id"`) added by `JwtTokenService.CreateAccessToken(User user, Guid companyId, IReadOnlyCollection<string> permissionCodes)` (`JwtTokenService.cs:16-27`).
- `RefreshTokens` gets `CompanyId uuid NULL` FK→Companies (`FK_RefreshTokens_Companies_CompanyId`, Restrict) — the refresh token remembers the selected company so refresh never silently switches tenant. Table stays platform (no filter).
- Login (`AuthService.cs:11-38`): after credentials/status, resolve the company = the user's **active membership with an active company**, ordered by `CreatedAt, CompanyId` (deterministic; one row today). None → `LoginFailureReason.NoCompanyAccess` → 403 `code: no_company_access`.
- Refresh (`AuthService.cs:40-60`): company = token's `CompanyId`; if NULL (tokens issued before this release) → same default resolution as login. Membership/company no longer active → `RefreshResult.Failed` (401, existing `invalid_refresh_token`).
- `POST api/v1/auth/switch-company` `[Authorize]`, body `SwitchCompanyRequest(Guid CompanyId, string RefreshToken)`: refresh token must be active and belong to the caller; membership Active and company active; revokes the old refresh token, issues a new pair bound to the new company. Non-member → 403 `code: company_access_denied` (same response whether or not the company exists — arch §14.2 no existence leak).
- Per-request validation: `Sellora.Api/Authorization/CompanyContextMiddleware.cs`, registered between `UseAuthentication()` and `UseAuthorization()` (`Program.cs:134-135`). For authenticated principals only: parse `sub` + `company_id`; one query `UserCompanyMemberships ⋈ Users ⋈ Companies` (Active membership, user not Disabled, company IsActive) via the unique index; success → `CompanyContext.Set(companyId)`; missing/invalid claim or failed check → **401** problem `code: company_context_invalid`.
- Legacy access tokens (no claim, ≤15 min old) → 401 → existing interceptor refreshes (`auth.interceptor.ts:19-31`) → new token carries the claim. Graceful, zero frontend change, and no server-side "guess the company" path for access tokens (fail closed). Side benefit: a disabled user / revoked membership loses access on the next request instead of after ≤15 min (arch §6.5) — permission claims are unchanged (server-side permission evaluation remains Phase 7, ADR-04).

### D3 — Global query filter mechanics (fail closed)
- `Sellora.Domain/Abstractions/ICompanyContext.cs`: `Guid? CompanyId { get; }`, `Guid RequireCompanyId()` (throws `InvalidOperationException("No company context.")`).
- `Sellora.Infrastructure/Data/CompanyContext.cs` (scoped): `Set(Guid companyId)` — once per scope; a second `Set` with a different id throws (no mid-request tenant switch). `public static ICompanyContext None` for design-time/tests that must not see data.
- `SelloraDbContext(DbContextOptions<SelloraDbContext> options, ICompanyContext companyContext)`; `public ICompanyContext CompanyContext { get; }`; `private Guid CurrentCompanyId => CompanyContext.CompanyId ?? Guid.Empty;` — filters reference this **instance member** (EF parameterises it per context; the documented multi-tenant pattern; model cache unaffected). No company uses `Guid.Empty` (CHECK `CK_Companies_Id_NotEmpty`), so no context ⇒ every filtered query returns **zero rows**.
- Filters are registered in `SelloraDbContext.OnModelCreating` after `ApplyConfigurationsFromAssembly` (configurations have no context instance): named filter `"Company"` (constant `CompanyQueryFilter.Name`):
  - every entity implementing `Sellora.Domain/Abstractions/ICompanyOwned.cs` (`Guid CompanyId { get; set; }`): `e => e.CompanyId == CurrentCompanyId` (generic helper over the model's entity types);
  - inherited children via navigation: `PurchaseOrderLines`/`PurchasePayments` → `PurchaseOrder.CompanyId`; `PurchaseReturnLines` → `PurchaseReturn`; `SalesInvoiceLines`/`SalesPayments` → `SalesInvoice`; `SalesReturnLines` → `SalesReturn`; `SalesInvoiceLineBatchConsumptions` → `PurchaseBatch.CompanyId` (one join); `SellingPrices` → `ProductUnit.CompanyId` (denormalised, one join).
  - Not filtered (platform / auth): Users, Roles, RolePermissions, Permissions, RefreshTokens, Units, Currencies, Companies, UserCompanyMemberships.
- Writes: override `SaveChanges`/`SaveChangesAsync` → `ApplyCompanyOwnership()`: Added `ICompanyOwned` with `CompanyId == Guid.Empty` → stamp `RequireCompanyId()`; Added with a different CompanyId → throw; Modified with `CompanyId` changed → throw. (Throws are programming errors → 500.) `ExecuteUpdate/Delete` are queries → filtered.
- Raw SQL: explicit `"CompanyId" = {db.CompanyContext.RequireCompanyId()}` predicates (list in §3 Infrastructure); FromSql on filtered sets keeps EF composition as defence-in-depth. ASSUMPTION to verify on PG: EF wraps `… FOR UPDATE` as a sub-select and PG keeps the row locks/order — existing `Concurrency/*` tests are the check; fallback = `.IgnoreQueryFilters([CompanyQueryFilter.Name])` + the explicit predicate.
- Bypasses (only these, each commented and listed in the review checklist): `UnitService.cs:100-106`, `UnitsController.cs:93-94` (platform unit in-use flags/locks must see all companies), `CurrenciesController.cs:83` (a currency that is any company's base currency cannot be deactivated). Test T4 fails on any other `IgnoreQueryFilters` usage.
- Non-HTTP callers: startup seeder (`Program.cs:99-103`) resolves `CompanyContext` from its scope and `Set(DefaultTenant.CompanyId)` before `DbSeeder.SeedAsync`. Migrations/`dotnet ef`: filters are not evaluated; EF tools build the context from the app host's DI (ASSUMPTION: tools resolve in a scope — if not, add `SelloraDbContextFactory : IDesignTimeDbContextFactory` passing `CompanyContext.None`). Tests: `new SelloraDbContext(options, TestCompany.Context)` (default company) — InMemory supports filters; no pooling is introduced (pooling would require resetting the context per lease).

### D4 — Denormalised CompanyId vs inherit; composite FKs
- **Column (`ICompanyOwned`)** on 19 tables: Taxes, ExchangeRates, Suppliers, Customers, ExpenseCategories, Expenses, FinancialAccounts, FinancialTransactions, Products, ProductUnits, PurchaseOrders, PurchaseReturns, PurchaseBatches, SalesInvoices, SalesReturns, StockAdjustments, StockItems, StockMovements, IdempotencyKeys; plus BusinessSettings (`ICompanyOwned`, UNIQUE). Lines/payments/consumptions/SellingPrices inherit (navigation filter, D3).
- **Roles deferred to Phase 7 (deviation from plan §2 / Appendix A §6 list).** Roles are loaded by login before a company context exists (`AuthService.cs:15-17`), are bound to the platform `Users.RoleId`, and Phase 7 must add `ScopeType`/`BranchId` and swap `IX_Roles_Name` for two partial uniques anyway; adding CompanyId now means two unique swaps and a filter on a required navigation used by auth. With one company and creation disabled nothing leaks; listed as a known platform surface (O1).
- **AKs** `AK_<T>_CompanyId_Id` on Products, Taxes, Suppliers, Customers, PurchaseOrders, SalesInvoices, PurchaseBatches, ExpenseCategories, FinancialAccounts.
- **Composite FKs now (13)** — added in the same phase that introduces the column so the big tables are scanned once: `FK_ProductUnits_Products_CompanyId_ProductId`, `FK_Products_Taxes_CompanyId_TaxId`, `FK_StockItems_Products_CompanyId_ProductId`, `FK_PurchaseBatches_Products_CompanyId_ProductId`, `FK_StockMovements_Products_CompanyId_ProductId`, `FK_StockMovements_PurchaseBatches_CompanyId_PurchaseBatchId` (nullable, MATCH SIMPLE), `FK_StockAdjustments_Products_CompanyId_ProductId`, `FK_PurchaseOrders_Suppliers_CompanyId_SupplierId`, `FK_PurchaseReturns_PurchaseOrders_CompanyId_PurchaseOrderId`, `FK_SalesInvoices_Customers_CompanyId_CustomerId`, `FK_SalesReturns_SalesInvoices_CompanyId_SalesInvoiceId`, `FK_Expenses_ExpenseCategories_CompanyId_CategoryId`, `FK_FinancialTransactions_FinancialAccounts_CompanyId_AccountId` (nullable). Modelled navigation-less (`HasOne<Product>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ProductId }).HasPrincipalKey(p => new { p.CompanyId, p.Id })`) alongside the existing single-column FKs/navigations — precedent `StockConfigurations.cs:27-31`, `PurchaseConfigurations.cs:124-127`. ASSUMPTION: EF accepts a second, navigation-less relationship to the same principal; `has-pending-model-changes` + T5 verify.
- **Not now:** line-level (lines have no CompanyId — services resolve every referenced master through filtered sets, so another company's id is "not found"); Phase 2/3/8 add their own composite FKs (Branch, Location) when those columns arrive.
- Indexes: EF's per-FK `IX_<T>_CompanyId` is suppressed by convention where a unique/AK leads with CompanyId; review the generated migration for redundant single-column indexes. **No `(CompanyId, <date>)` report indexes in Phase 1** (CompanyId has one value; build `(CompanyId, BranchId, Date)` once in Phase 2).

### D5 — Migrations: four, each one transaction, guarded, with V-assertions and guarded Down
Default tenant constants `Sellora.Domain/Companies/DefaultTenant.cs`: `CompanyId = 0c0a1f00-0000-4000-8000-000000000001` (Phase 2 branch …0002 and Phase 3 location …0003 reserved). Documented in each migration header (ADR-11). Mirror the header/P-block/V-block/guard layout of `Migrations/20261109053202_AddMovementTypesAndReasonCode.cs:7-110`, its `DO $g$ … RAISE EXCEPTION` guards (`:121-141`) and in-tx assertions (`:240-276`).

| # | Name | Content |
|---|---|---|
| M1 | `AddCompaniesAndMemberships` | Create `Companies` (`Id uuid PK`, `Code varchar(20) NOT NULL` + `IX_Companies_Code` UNIQUE, `Name varchar(150) NOT NULL`, `IsActive bool NOT NULL`, `CreatedAt timestamptz NOT NULL`, `CK_Companies_Id_NotEmpty`). **Guard G1**: >1 BusinessSettings row → RAISE. Insert default company (`Code 'DEFAULT'`, `Name = COALESCE((SELECT "BusinessName" FROM "BusinessSettings"), 'Default Company')`, ON CONFLICT DO NOTHING) — inserted on empty databases too, so seeding and tests always have the FK target. `BusinessSettings.CompanyId` add nullable → backfill → NOT NULL, `IX_BusinessSettings_CompanyId` UNIQUE, `FK_BusinessSettings_Companies_CompanyId`. Create `UserCompanyMemberships` (D1) and backfill one Active/CompanyWide row per existing user (`CreatedAt = Users.CreatedAt`, `CreatedByUserId NULL` — not fabricated). `RefreshTokens.CompanyId` nullable + FK, no backfill. Assert: memberships = users; settings rows unchanged. |
| M2 | `AddCompanyIdToCompanyTables` | Per table (19): add `"CompanyId" uuid NULL` (metadata-only) → backfill `SET "CompanyId" = '<default>' WHERE "CompanyId" IS NULL` → in-tx assertions (no NULLs, row counts = pre-counts in an `ON COMMIT DROP` temp table, reconciliation sets unchanged as in M8) → SET NOT NULL → `FK_<T>_Companies_CompanyId` (Restrict) + index. |
| M3 | `ScopeUniquesAndCompositeKeysToCompany` | Replace: `IX_Products_Sku`→`IX_Products_CompanyId_Sku`; `IX_ProductUnits_Barcode`→`IX_ProductUnits_CompanyId_Barcode` (same filter `"Barcode" IS NOT NULL AND "IsActive"`); `IX_Taxes_Name`, `IX_ExpenseCategories_Name`, `IX_FinancialAccounts_Name` → `IX_<T>_CompanyId_Name`; `IX_ExchangeRates_CurrencyId_EffectiveDate`→`IX_ExchangeRates_CompanyId_CurrencyId_EffectiveDate`; `IX_PurchaseOrders_PurchaseNo`, `IX_PurchaseReturns_ReturnNo`, `IX_SalesInvoices_InvoiceNo`, `IX_SalesReturns_ReturnNo`, `IX_StockAdjustments_AdjustmentNo`, `IX_Expenses_ExpenseNo` → `IX_<T>_CompanyId_<No>`; `IX_IdempotencyKeys_UserId_Scope_Key`→`IX_IdempotencyKeys_CompanyId_UserId_Scope_Key`. Then the 9 AKs and 13 composite FKs (D4). Stay global: Users.Username/Email, Permissions.Code, Currencies.Code, Units.Name/ShortCode, RefreshTokens.TokenHash, PurchaseBatches.BatchSequence, Roles.Name (Phase 7). **Guard G2**: P4 orphan query non-empty → RAISE before adding constraints. |
| M4 | `AddDocumentSequences` | Create `DocumentSequences` (`Id uuid PK`, `CompanyId uuid NOT NULL FK`, `BranchId uuid NULL` — FK to Branches added in Phase 2, `Series varchar(10) NOT NULL`, `NextValue bigint NOT NULL`, `UpdatedAt timestamptz NOT NULL`, `CK_DocumentSequences_NextValue_Positive`, `CK_DocumentSequences_Series_NotEmpty`); uniques without nullable keys (Q8): `IX_DocumentSequences_CompanyId_Series` UNIQUE `WHERE "BranchId" IS NULL` and `IX_DocumentSequences_CompanyId_BranchId_Series` UNIQUE `WHERE "BranchId" IS NOT NULL`. Seed per company × series `PUR` (PurchaseOrders.PurchaseNo), `PRT` (PurchaseReturns), `INV` (SalesInvoices.InvoiceNo, any prefix), `SRT` (SalesReturns), `ADJ` (StockAdjustments), `EXP` (Expenses): `NextValue = GREATEST(COUNT(*), COALESCE(MAX(substring(no FROM '([0-9]+)$')::bigint), 0)) + 1`, deterministic `Id = md5('docseq:' || CompanyId || ':' || series)::uuid` (no `gen_random_uuid` dependency; Q8). Assert: every seeded NextValue > every existing numeric suffix of that series. |

- **FinancialYear: not added now.** Owner made FY reset optional (§10 Q2); adding `FinancialYear` later is a NOT NULL-with-default column on a tiny table + swapping two partial indexes — not disruptive. Adding it now would put an unused key column into the uniques.
- **Down** of every migration starts with **guard D-G**: `IF (SELECT COUNT(*) FROM "Companies") > 1 OR EXISTS (… any CompanyId <> default …) THEN RAISE` (cannot fold a second tenant back into global uniques). Then the exact reverse: M4 removes the sequences table; M3 removes FKs/AKs and recreates the global uniques (safe only with one company); M2 removes FKs/columns; M1 removes memberships, `RefreshTokens.CompanyId`, `BusinessSettings.CompanyId`, Companies. Backfill is reversible (derived value).
- One transaction per migration (Npgsql default). If R5 timing on a prod-size copy shows M2 too slow on StockMovements/FinancialTransactions, split M2 into M2a (add + batched backfill, `CHECK ("CompanyId" IS NOT NULL) NOT VALID`) and M2b (`VALIDATE`, SET NOT NULL, FK) — decide after timing, not before.

**P-block (header, read-only, prod copy before release):** P0 row counts of the 20 tables + Users; P1 `SELECT COUNT(*) FROM "BusinessSettings"` (must be ≤ 1); P2 document numbers whose suffix is not numeric, per series (informational — seeding falls back to COUNT); P3 the existing reconciliation queries (record unbalanced products, like M8 P4); P4 composite-pair orphans (must be empty), e.g. `SELECT pu."Id" FROM "ProductUnits" pu LEFT JOIN "Products" p ON p."Id" = pu."ProductId" WHERE p."Id" IS NULL` (single company ⇒ only true orphans can fail; existing simple FKs make this empty in practice).

**V-block (after apply; V1 = P0, the rest must return no rows):**
- V1 row counts unchanged; V2 `SELECT '<T>' FROM "<T>" WHERE "CompanyId" IS NULL OR "CompanyId" <> '<default>' LIMIT 1` UNION ALL over the 20 tables;
- V3 users without membership: `SELECT u."Id" FROM "Users" u WHERE NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."UserId" = u."Id" AND m."CompanyId" = '<default>' AND m."Status" = 'Active')`;
- V4 cross-company pairs: `SELECT pu."Id" FROM "ProductUnits" pu JOIN "Products" p ON p."Id" = pu."ProductId" WHERE pu."CompanyId" <> p."CompanyId"` and the 12 analogous document→master joins; children: `SELECT l."Id" FROM "SalesInvoiceLines" l JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId" JOIN "Products" p ON p."Id" = l."ProductId" WHERE p."CompanyId" <> i."CompanyId"` (+ PurchaseOrderLines, both return-line tables, consumptions→batch vs invoice);
- V5 sequences ahead of history: per series `NextValue <= MAX(numeric suffix)` returns nothing;
- V6 reconciliation queries from `stock-ledger-invariants` return exactly the P3 set;
- V7 post-deploy (after smoke test): new documents numbered max+1 per series; `SELECT COUNT(*) FROM "RefreshTokens" WHERE "CompanyId" IS NULL AND "CreatedAt" > <deploy time>` = 0.

### D6 — API/DTO changes (frontend-compatible)
- `GET/PUT api/v1/settings` (`SettingsController.cs:15-56`): **route and `BusinessSettingsDto`/`UpdateBusinessSettingsRequest` shape unchanged** (`Contracts/MasterContracts.cs:26-40`); add `Guid CompanyId` to the DTO (additive). Reads/writes the current company's row; PUT also sets `Companies.Name = BusinessName` (single edit point in Phase 1).
- `AuthenticatedUserSummary`/`UserSummaryDto` (`Domain/Auth/AuthModels.cs:5-13`, `Contracts/AuthContracts.cs:15`) gain `CompanyId`, `CompanyName`; `GET auth/me` additionally returns `Companies: [{ id, name }]` (active memberships) — plan §6 row 1 "auth/me returns memberships".
- New `POST auth/switch-company` (D2) → `AuthResponse`. New error codes: `no_company_access` (403 login), `company_access_denied` (403 switch), `company_context_invalid` (401 any request).
- Cross-company ids in requests: master/detail GET → 404 (filtered); create/update referencing another company's master → existing "not found" validation path (as today) — no new codes, no existence leak.
- Frontend (WP5, **optional**): `core/auth/auth.models.ts` add optional `companyId`, `companyName`; `layout/sidebar/sidebar.html:5` shows `companyName` (fallback to current text). No selector/context service (Phase 11). Backend is compatible with the unchanged SWA.

---------------------------------------------------------------------------------------------------------------
## 3. Design table per layer

### Domain (`backend/src/Sellora.Domain`)
| File | Change | Reason |
|---|---|---|
| `Entities/Company.cs` (new) | Id, Code, Name, IsActive, CreatedAt | ADR-07 identity |
| `Entities/UserCompanyMembership.cs`, `Entities/MembershipStatus.cs`, `Entities/CompanyAccessScope.cs` (new) | D1 | arch §6.1 |
| `Entities/DocumentSequence.cs` (new) | D5 M4 | ADR-08 |
| `Abstractions/ICompanyOwned.cs`, `Abstractions/ICompanyContext.cs` (new) | D3 | ADR-05 |
| `Companies/DefaultTenant.cs` (new) | fixed Guids | ADR-11 |
| 19 entities + `BusinessSettings.cs` | `: ICompanyOwned`, `public Guid CompanyId { get; set; }` | D4 |
| `Entities/RefreshToken.cs` | `Guid? CompanyId` | D2 |
| `Auth/AuthClaimTypes.cs` | `CompanyId = "company_id"` | D2 |
| `Auth/AuthModels.cs` | summary + `CompanyId`, `CompanyName`; `LoginFailureReason.NoCompanyAccess`; `SwitchCompanyResult` | D2/D6 |
| `Abstractions/IAuthService.cs`, `Abstractions/ITokenService.cs` | `SwitchCompanyAsync(Guid userId, Guid companyId, string refreshToken, CancellationToken ct)`; `CreateAccessToken(User, Guid companyId, IReadOnlyCollection<string>)` | D2 |
| `Abstractions/CompanySettingsSnapshot.cs` (new) | `record CompanySettingsSnapshot(Guid CompanyId, string BusinessName, string InvoicePrefix, Guid BaseCurrencyId, string BaseCurrencyCode, string BaseCurrencySymbol, int BaseCurrencyDecimalPrecision)` | ADR-07 |

### Infrastructure (`backend/src/Sellora.Infrastructure`)
| File | Change | Reason |
|---|---|---|
| `Data/CompanyContext.cs` (new) | scoped `ICompanyContext` impl, `Set`, `None`; `CompanyQueryFilter.Name` constant | D3 |
| `Data/SelloraDbContext.cs` | ctor + `CompanyContext` property; DbSets Companies, UserCompanyMemberships, DocumentSequences; filters (D3); `SaveChanges*` override `ApplyCompanyOwnership()` | D3 |
| `Data/Configurations/CompanyConfiguration.cs`, `UserCompanyMembershipConfiguration.cs`, `DocumentSequenceConfiguration.cs` (new) | D1/D5 | |
| Existing configurations: BusinessSettings, CurrencyConfigurations (ExchangeRates), Customer, ExpenseConfigurations, FinancialAccount, FinancialTransaction, IdempotencyKey, Product, Purchase, Sales, StockAdjustment, Stock, Supplier, Tax, RefreshToken | CompanyId FK, re-scoped uniques with explicit names, AKs, composite FKs | D4/M3 |
| `Data/Migrations/*` M1–M4 + Designers + snapshot | D5 | |
| `Data/DbSeeder.cs` | admin membership in default company if missing; settings seeded for current company; sync Companies.Name; seeding assumes context = default company (demo data never into another tenant) | ADR-11 |
| `DependencyInjection.cs` | `AddScoped<CompanyContext>()` + `AddScoped<ICompanyContext>(sp => sp.GetRequiredService<CompanyContext>())` | D3 |
| `Services/CompanySettings.cs` (new) | `public static class CompanySettings { Task<CompanySettingsSnapshot> GetCurrentCompanySettingsAsync(this SelloraDbContext db, CancellationToken ct); Task<Currency> GetBaseCurrencyAsync(this SelloraDbContext db, CancellationToken ct); }` — explicit `CompanyId == RequireCompanyId()` predicate (plus filter) | ADR-07; extension avoids ctor churn across ~750 unit tests (deviation: helper, not DI service) |
| `Services/DocumentNumbers.cs` (new) | `internal static Task<long> NextAsync(SelloraDbContext db, string series, CancellationToken ct)`: PG: insert-if-missing `ON CONFLICT ("CompanyId","Series") WHERE "BranchId" IS NULL DO NOTHING`, then `UPDATE "DocumentSequences" SET "NextValue" = "NextValue" + 1, "UpdatedAt" = now() WHERE "CompanyId" = @c AND "BranchId" IS NULL AND "Series" = @s RETURNING "NextValue" - 1` (row lock to tx end; rollback ⇒ no gap); InMemory: tracked-entity increment. `DocumentSeries` constants PUR/PRT/INV/SRT/ADJ/EXP. Format stays `{prefix}-{n:D4}` | ADR-08 |
| `Services/StockLocks.cs` | remove `LockDocumentNumberAsync` (:132-136); company predicate in `:151`, `:164`, `:195`, `:238`; INSERT at `:191` writes `"CompanyId"` from `p."CompanyId"` and filters `p."CompanyId" = {companyId}`; idempotency advisory key `sellora:idem:{companyId}:{userId}:{scope}:{key}` (`:106`) | ADR-05 |
| `Services/PurchaseService.cs`, `SalesService.cs`, `StockAdjustmentService.cs`, `ExpenseService.cs` | numbering → `DocumentNumbers.NextAsync`; settings → accessor (`SalesService.cs:514` prefix) | ADR-07/08 |
| `Services/ExchangeRateService.cs`, `FinancialAccountService.cs` | settings accessor | ADR-07 |
| `Services/ProductService.cs` | `BarcodeIndex = "IX_ProductUnits_CompanyId_Barcode"` (:27) | M3 |
| `Services/UnitService.cs` | `:100-106` `.IgnoreQueryFilters([CompanyQueryFilter.Name])` + comment | platform unit in use anywhere |
| `Services/AuthService.cs`, `Security/JwtTokenService.cs` | D2 | |

### Api (`backend/src/Sellora.Api`)
| File | Change |
|---|---|
| `Authorization/CompanyContextMiddleware.cs` (new) | D2 |
| `Program.cs` | middleware between `:134` and `:135`; seeder scope `Set(DefaultTenant.CompanyId)` (`:99-103`); `public partial class Program;` for WebApplicationFactory |
| `Controllers/AuthController.cs` | `me` additions, `switch-company`, `no_company_access` mapping (`:24-29`) |
| `Controllers/SettingsController.cs` | accessor, Companies.Name sync |
| `Controllers/{AccountsReports,FinancialAccounts,Currencies(:93),Expenses,Purchases,ExchangeRates,PurchaseReturns,FinancialTransactions}Controller.cs` | accessor |
| `Controllers/CurrenciesController.cs:83`, `UnitsController.cs:93-94` | documented `IgnoreQueryFilters` |
| `Controllers/UsersController.cs` | membership on create; list/detail scoped to current company (D1) |
| `Contracts/AuthContracts.cs`, `Contracts/MasterContracts.cs` | D6 |

### Frontend (optional, WP5)
`frontend/src/app/core/auth/auth.models.ts`, `layout/sidebar/sidebar.html` + `.ts`, matching spec.

---------------------------------------------------------------------------------------------------------------
## 4. Work packages

Checkpoint **1a** = WP1 + WP2 (review: schema, migrations, filter mechanics). Checkpoint **1b** = WP3 + WP4 (+WP5). Release once, after the 1b review (1a alone leaves the HTTP app without a company context). Max 2 agents in parallel: WP2 ∥ WP3 after WP1.

| WP | Owner | Depends | Files owned | Acceptance criteria |
|---|---|---|---|---|
| WP1 | database-engineer | — | Domain: new entities/enums, `ICompanyOwned`, `ICompanyContext`, `DefaultTenant`, CompanyId on 20 entities, `RefreshToken.CompanyId`; Infrastructure: `Data/**` (context, `CompanyContext`, configurations, migrations M1–M4, snapshot, `DbSeeder.cs`), `DependencyInjection.cs` | build green; `has-pending-model-changes` clean; M1–M4 apply on an empty DB and leave exactly one company + seeded admin membership after `DbSeeder`; headers carry P/V blocks; guards G1/G2 and Down guard present; filters + stamping per D3; nothing outside owned files |
| WP2 | test-engineer | WP1 | `tests/**/TestData/TestCompany.cs` (new), the 15 context-construction sites, `PostgresFixture.cs`, new `tests/Sellora.IntegrationTests/Migrations/{AddCompaniesAndMemberships,AddCompanyIdToCompanyTables,ScopeUniquesAndCompositeKeysToCompany,AddDocumentSequences}MigrationTests.cs`, `tests/Sellora.Tests/Infrastructure/CompanyQueryFilterTests.cs` | `scripts/verify.sh backend` green with existing suites unchanged in intent; T1, T2, T4 (skeleton), T5 pass |
| WP3 | backend-engineer | WP1 (parallel with WP2) | Infrastructure `Services/**`, `Security/JwtTokenService.cs`; Domain `Auth/**`, `Abstractions/IAuthService.cs`, `ITokenService.cs`, `CompanySettingsSnapshot.cs`; Api `Program.cs`, `Authorization/CompanyContextMiddleware.cs`, `Controllers/**`, `Contracts/**` | 0 `BusinessSettings` reads outside `CompanySettings.cs`/seeder/SettingsController (grep); 0 `CountAsync(ct) + 1` numbering; `IgnoreQueryFilters` only at the 3 listed sites; every raw SQL on company tables has a CompanyId predicate; API contract per D6 handed to lead before WP5 |
| WP4 | test-engineer | WP3 | `tests/Sellora.IntegrationTests/Api/**` (new WebApplicationFactory harness), `Concurrency/DocumentNumberConcurrencyTests.cs`, `tests/Sellora.Tests/Auth/*` additions, T3 service isolation tests, `backend/Directory.Packages.props` + IntegrationTests csproj (`Microsoft.AspNetCore.Mvc.Testing`) | T3, T6–T10 pass; `scripts/verify.sh all` green |
| WP5 (opt.) | frontend-engineer | WP3 contract | D6 frontend files | Vitest green; prod build within budget |
| WP6 | code-reviewer | all | — | review vs ADR-04/05/07/08/11, bypass list, migration checklist |

Test list:
- T1 Filter coverage (reflection): every model entity type is classified Platform / CompanyOwned / Inherited in one allow-list; every CompanyOwned and Inherited type has the `"Company"` filter; a new unclassified entity fails the test.
- T2 Fail-closed: no context ⇒ each filtered set returns 0 rows; SaveChanges of an Added company-owned entity throws; Added with another CompanyId throws; changing CompanyId throws; `CompanyContext.Set` twice with different ids throws.
- T3 Isolation (InMemory, per service): with companies A and B populated, every service read/write under A ignores B (purchase, sale, both returns, cancellations, adjustments, pricing, products incl. same SKU/barcode in both, expenses, accounts, exchange rates); referencing B's supplier/customer/product/account/category from A ⇒ validation failure.
- T4 Bypass guard: source scan — `IgnoreQueryFilters` only at the 3 approved sites; raw SQL strings on company tables contain `"CompanyId"`.
- T5 Migrations (PG, per migration, mirroring `AddMovementTypesAndReasonCodeMigrationTests.cs`): legacy fixture in the previous schema → Up → V-block → guards G1/G2 raise and roll back → Down guard raises with a 2nd company → Down restores the previous schema → Down then Up → idempotent script twice over a plain connection; M4 seeding with gaps, non-numeric numbers and two invoice prefixes; existing `MigrationTests` (pending model) and `UnitRestructuringIdempotentScriptTests` still green.
- T6 HTTP isolation (WebApplicationFactory<Program> + Testcontainers): company B + member user inserted by SQL with the **same** SKU, barcode, tax/category/account names and document numbers as A (proves scoped uniques); theory over every company-owned GET list/detail route: A never sees B ids, B detail ⇒ 404; create/update with B's master ids ⇒ 400/404; reflection test that every controller action is in the isolation table or the platform allow-list (mirrors `tests/Sellora.Tests/Api/EndpointAuthorizationTests.cs`).
- T7 Auth: login/refresh carry `company_id`; legacy refresh token (NULL CompanyId) ⇒ default membership; access token without claim ⇒ 401 `company_context_invalid`; signed token with a non-member `company_id` ⇒ 401; suspended membership / disabled user / inactive company ⇒ 401 on the next request and refresh fails; switch-company to member ⇒ new claim, to non-member ⇒ 403 identical for existing/non-existing ids; user created via `UsersController` can log in.
- T8 Numbering concurrency (PG): N parallel creates per series in A ⇒ distinct contiguous numbers; A and B in parallel each continue their own sequence without blocking each other; rolled-back create consumes no number; first document of a company without a sequence row (upsert path).
- T9 Locks: existing `Concurrency/*` and `StockLedgerReconciliationTests` green with company predicates; first-stock insert writes CompanyId; a product of company B cannot be locked/inserted from A.
- T10 Seeder: `SeedAsync` twice on a migrated DB ⇒ no duplicates, admin has exactly one membership (extend `MigrationTests.cs:28`).

---------------------------------------------------------------------------------------------------------------
## 5. Cross-phase consistency

| Later phase | What Phase 1 pre-builds | What Phase 1 deliberately leaves |
|---|---|---|
| 2 Branch | `DocumentSequences.BranchId` + partial uniques; branch Guid reserved in `DefaultTenant`; named filter lets a `"Branch"` filter be added/ignored separately | Doc-number uniques are `(CompanyId, No)`; if branch series reuse a prefix, Phase 2 must require distinct prefixes or swap to `(CompanyId, BranchId, No)` (O2) |
| 3/8 Location | AK pattern `(CompanyId, Id)`; StockItems/StockMovements/PurchaseBatches already carry CompanyId for the ADR-01 composite FKs | StockItems PK swap; StockMovements.StockLocationId (second backfill of StockMovements — accepted, time with R5) |
| 5 Membership | Table + arch §6.1 columns, AK for assignments | Q7 re-mapping of non-admin users to AssignedBranches + Main primary |
| 6 Platform | — | Units/Currencies writes are platform-wide but any permitted company user can edit them (O1) |
| 7 RBAC | — | Roles.CompanyId/ScopeType, `IX_Roles_Name`, scope of the `sellora:access-management` advisory lock |

---------------------------------------------------------------------------------------------------------------
## 6. Risks, release, open questions, deviations

Risks:
- R-a Backfill time/locks on StockMovements, FinancialTransactions, SalesInvoices (backfill rewrites rows; SET NOT NULL + 13 FK validations scan under ACCESS EXCLUSIVE). Volume ASSUMPTION small; time M2/M3 on the R5 copy; split per D5 if needed.
- R-b Dev auto-migrate (owner R1 accepted): first deploy to `dev`/`claude_dev` applies M1–M4 at startup, then seeds under the default company. Separate DBs (R4) ⇒ no collision. Production must have auto-migrate off (D10).
- R-c Forgotten predicate: mitigated by filter-by-default, T1/T4/T6 and the bypass list; residual risk = new raw SQL (review checklist item).
- R-d EF composition over `FOR UPDATE` raw SQL (ASSUMPTION, D3) — verified by the existing concurrency suite; fallback documented.
- R-e Every authenticated request costs one indexed membership query (no cache in Phase 1; Phase 7 adds the versioned access cache, ADR-04).
- R-f Within ≤15 min after deploy each open session gets one transparent 401→refresh; sessions whose refresh token is already invalid re-login (as today).

Release order (D10): snapshot/branch → P-block (P1, P4 empty) → idempotent script M1–M4 → V-block → deploy API → (optional SWA) → smoke: login, refresh, `auth/me`, settings GET/PUT, purchase, sale (invoice number = previous max+1), return, adjustment, expense (EXP number), report, logout → V7. Rollback: restore snapshot + previous build (Down guarded for single company only).

Open questions:
- O1 Units/Currencies/Users/Roles remain platform surfaces editable by company users until Phases 6/7 — acceptable while company creation is disabled? (recommend yes; no company-creation API until Phase 6/7.)
- O2 Branch document series: distinct prefixes per branch vs `(CompanyId, BranchId, No)` uniques — decide in Phase 2 design.
- O3 Confirm Phase 1 memberships are CompanyWide for all users and the Q7 branch mapping is applied in Phase 5.
- O4 Default company `Code` ('DEFAULT') and name source (BusinessName) — confirm.

Deviations from plan/analysis (justified above): Roles CompanyId deferred to Phase 7 (D4); memberships table pulled from Phase 5 in minimal form (D1); settings accessor as a DbContext extension rather than an injected service (§3); FinancialYear column omitted (D5); no report `(CompanyId, date)` indexes until Phase 2 (D4); RefreshTokens (platform) gains a nullable CompanyId (D2). No conflict with D1–D10 of the unit analysis (stock stays in base units; ledger shape unchanged).

## 7. Owner approval and lead additions (2026-11-09)

- Design approved; deviations in §6 accepted. O1 yes (platform surfaces until Phases 6/7; no company-creation API). O3 yes (all memberships CompanyWide in Phase 1; Q7 branch mapping in Phase 5). O4 yes (`DEFAULT` code, name from BusinessName). O2 decided in the Phase 2 design.
- **L1 (lead):** D1 scoping extends to every `UsersController` action that reads or changes another user — list, detail, update, status, reset-password — restricted to users with a membership in the current company (404 otherwise). Role assignment remains platform (Roles deferred, D4).
- WP5 (sidebar company name) not in scope unless requested.

## 8. Delivery record (2026-11-09)

Status: **delivered, review "Approve with fixes" — fixes applied.** Verify: 803 unit / 344 Postgres integration / 192 Vitest, 0 warnings.

| Item | Outcome |
|---|---|
| EF gotcha (found by WP4 T6) | With the company filter, a required navigation two hops after an optional one (`FinancialTransaction.PurchasePayment → PurchaseOrder → Supplier`) became a top-level INNER JOIN and dropped rows. Fixed in `FinancialTransactionsController` with id lookups. Model-wide scan: no other such chain; optional → required → column chains (`PurchaseBatch.PurchaseOrderLine.PurchaseOrder.PurchaseNo`) stay nested — guarded by `Api/MixedBatchDetailApiTests`. **Rule for later phases:** do not project `optional.required.required` chains under the filter; look up by id. |
| Review Major 1 | Users is platform-wide: a user who also belongs to another company cannot be edited, disabled or reset from this company (403). Test in `AccessManagementRulesTests`. |
| Review minors | Username/e-mail duplicate messages stay global (global uniqueness, accepted). Global `sellora:access-management` lock kept until access management is per-company (Phase 7). M2/M3 headers carry the maintenance-window release note. |
| Flaky test | `StockAdjustmentConstraintTests.Delete_ProductUnitReferencedByAdjustment` accepts either legitimately violated FK. |
