# Phase 2 — Branch foundation: file-level design

| Property | Value |
|---|---|
| Baseline | branch `analyse`, HEAD `fd5854c` (Phase 1 delivered, `phase-1-design.md` §8) |
| Sources | arch §4.2, §6.1–6.3, §14, §17, §19 Phase 2; plan §5 ADR-04/05/08/09/11/13, §6 row 2, §10 Q2/Q3/Q4/Q6/Q7/Q9; Phase 1 design D1–D6, §5, §8 |
| Status | DRAFT for owner approval |
| Size | L (multi-table schema + numbering + context resolution); one checkpoint, one release |

Verified facts (path:line, backend paths relative to `backend/src`):
- Context seam: `SelloraDbContext` primary ctor `(options, ICompanyContext)` `Sellora.Infrastructure/Data/SelloraDbContext.cs:9`, `CurrentCompanyId` instance member `:16`, inherited-child filters `:89-95`; design-time factory passes `CompanyContext.None` `Data/SelloraDbContextFactory.cs:18`; DI `DependencyInjection.cs:18-21`.
- `CompanyContext` set-once holder `Data/CompanyContext.cs:15-50`; `CompanyContextMiddleware` one membership query, 401 `company_context_invalid` `Sellora.Api/Authorization/CompanyContextMiddleware.cs:22-57`; registered `Program.cs:137-139`.
- Numbering: `DocumentNumbers.NextAsync(db, series, ct)` company-level only (`BranchId IS NULL`) `Services/DocumentNumbers.cs:28-90`; series constants `:10-18`; callers `PurchaseService.cs:638,643`, `SalesService.cs:507,512-513` (company `InvoicePrefix`), `StockAdjustmentService.cs:204`, `ExpenseService.cs:45`. `DocumentSequence.BranchId` already nullable, "FK added in Phase 2" `Sellora.Domain/Entities/DocumentSequence.cs:10-11`; partial uniques from Phase 1 M4 (`Migrations/20261109085910_AddDocumentSequences.cs:9-13`).
- Doc-number uniques are `(CompanyId, No)`: `PurchaseConfigurations.cs:36,93`, `SalesConfigurations.cs:27,82`, `StockAdjustmentConfiguration.cs:25`, `ExpenseConfigurations.cs:35`. AKs `(CompanyId, Id)` exist on PurchaseOrders `PurchaseConfigurations.cs:35`, SalesInvoices `SalesConfigurations.cs:26`, FinancialAccounts `FinancialAccountConfiguration.cs:15`.
- No date indexes on `PurchaseDate`/`InvoiceDate`/`ReturnDate` (grep of `Data/Configurations`); `IX_StockAdjustments_CreatedAt` `StockAdjustmentConfiguration.cs:26`, `IX_Expenses_ExpenseDate` `ExpenseConfigurations.cs:36`.
- Account usage choke point `FinancialAccountRules.EnsureUsableAsync(db, accountId, ct)` `Services/FinancialAccountRules.cs:11`, called at `PurchaseService.cs:33,140,279,538`, `SalesService.cs:33,122,205,436`, `ExpenseService.cs:37`.
- Administrator (system role) resolves to the whole catalogue `Sellora.Domain/Auth/AuthModels.cs:83-86`; catalogue `Sellora.Domain/Permissions/PermissionCatalog.cs`; seeder adds missing codes `Data/DbSeeder.cs:63-84` → new codes reach Administrator with no grant step, other roles get none.
- CORS hard-coded `WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod().AllowCredentials()` `Sellora.Api/Program.cs:69-76`, `UseCors` `:133`. Frontend never sends cookies (no `withCredentials` in `frontend/src/app/core`); custom headers today: `Authorization`, `Idempotency-Key` (`frontend/src/app/core/idempotency.ts:13-14`).
- Frontend prod `apiBaseUrl: '/api/v1'` `frontend/src/environments/environment.ts:3`; dev absolute `environment.development.ts:3`; SWA workflow `api_location: ""` (no managed API) `.github/workflows/azure-static-web-apps-wonderful-bay-04d447700.yml:31-33`.
- Unique/check/FK violations already map to 409 `ErrorHandling/GlobalExceptionHandler.cs:34-39`.
- Status routes convention `POST {id}/status` `SuppliersController.cs:99`, `FinancialAccountsController.cs:93`.
- No invoice print uses GSTIN today (grep: only settings and supplier screens).

------------------------------------------------------------------------
## 1. Scope

Delivers: `Branches` (company-owned, `AK (CompanyId, Id)`), one default "Main" branch per company (default company → reserved Guid `…0002`); branch CRUD + activate/deactivate service and API; per-branch optional GSTIN + invoice prefix override (Q3); `BranchId NOT NULL` on PurchaseOrders, PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments (backfilled to Main); optional `BranchId` on Expenses, FinancialAccounts (Q6, no backfill); composite same-company FKs `(CompanyId, BranchId) → Branches(CompanyId, Id)`; DocumentSequences branch FK + per-branch series (Q2); `X-Branch-Id` header validated server-side (ADR-04) with an interim rule Phase 5 only tightens; `IBranchContext` on the DbContext; optional `branchId` filter on document lists/reports; `(CompanyId, BranchId, <date>)` indexes; CORS from configuration (Q9); new permission codes (ADR-13).

Excludes: user-branch allocations, primary branch, AssignedBranches scope (Phase 5); "Branch" EF query filter (Phase 5, D5); stock locations / per-branch stock (Phases 3/8 — stock stays company-wide); cross-branch returns (Q4: not in first release); FinancialYear series reset (deferred, Phase 1 D5); branch picker / context service in Angular (Phase 11); GSTIN snapshot on invoice rows (O-3); company creation (Phase 6).

------------------------------------------------------------------------
## 2. Decisions

**D1 — Branch entity.** Table `"Branches"`: `Id uuid PK`, `CompanyId uuid NOT NULL` FK→Companies (Restrict), `Code varchar(6) NOT NULL` (`^[A-Z0-9]{1,6}$`, upper-cased, **immutable after create** — it is the numbering segment, D6), `Name varchar(150) NOT NULL`, `AddressLine`, `City`, `State`, `Phone`, `Email` (lengths mirror `BusinessSettingsConfiguration`), `Gstin varchar(20) NULL` (same validation as settings, `MasterContracts.cs:37`), `InvoicePrefix varchar(10) NULL`, `IsDefault bool NOT NULL`, `IsActive bool NOT NULL`, `CreatedAt`, `CreatedByUserId uuid NULL` FK→Users, `UpdatedAt NULL`, `UpdatedByUserId NULL`.
Constraints: `AK_Branches_CompanyId_Id`; `IX_Branches_CompanyId_Code` UNIQUE; `IX_Branches_CompanyId_Name` UNIQUE; `IX_Branches_CompanyId_IsDefault` UNIQUE `WHERE "IsDefault"` (one default per company); `CK_Branches_Default_Active` `"IsActive" OR NOT "IsDefault"`; `CK_Branches_Id_NotEmpty`. `Branch : ICompanyOwned` → gets the `"Company"` filter + SaveChanges stamping automatically (Phase 1 D3).
Rules (service): default branch cannot be deactivated; no "change default" operation in Phase 2 (one default for life of Phase 2; Phase 6 company creation must insert its Main); deactivation allowed with history; inactive branch accepts no new branch documents (purchases, sales, returns, adjustments) but payments/refunds/reversals on its existing documents stay allowed (collecting dues after closure).

**D2 — Default "Main".** `DefaultTenant.BranchId = 0c0a1f00-0000-4000-8000-000000000002`, `DefaultTenant.BranchCode = "MAIN"`. Migration B1 inserts one Main per existing company (default company → reserved Guid; any other → `md5('branch:' || "Id")::uuid`), `Name 'Main'`, `IsDefault true`, address/GSTIN/prefix NULL (company profile is the fallback — not copied, so there is one source of truth until someone edits the branch). Inserted on empty databases too (FK target for seed/tests, as Phase 1 did for the company). `DbSeeder` gets `SeedDefaultBranchAsync` for non-migrated (InMemory) databases.

**D3 — BranchId placement.** NOT NULL on the 5 stock/trading headers (backfill = company's default branch; arch §17.3 step 5 — documented default mapping, nothing fabricated: the business was single-office). Nullable on Expenses/FinancialAccounts, **no backfill** (NULL = company-level; tagging existing rows to Main would be invented data). Lines, payments, batches, movements inherit through their header (no column). Composite FKs only (no extra single-column FK), navigation-less, mirroring Phase 1 (`PurchaseConfigurations.cs:40-41`). Reason navigation-less: Phase 1 §8 rule — a filtered required `Branch` navigation reached through an optional navigation (e.g. `FinancialTransaction.SalesPayment → SalesInvoice → Branch`) would become an INNER JOIN and drop rows; DTOs resolve branch code/name via a per-request id→name dictionary (branches are few).

**D4 — O2 numbering: keep `(CompanyId, No)` uniques; per-branch counters with a branch-distinct number shape.** Rejected: `(CompanyId, BranchId, No)`. Rationale: (a) GST Rule 46(b) requires the invoice serial to be unique per GSTIN per financial year (ASSUMPTION — owner/accountant to confirm wording); branches without their own registration share the company GSTIN, so identical numbers in two branches would be non-compliant — company-wide uniqueness is required anyway for them; (b) keeps the Phase 1 uniques (no index swap on 6 tables), and lookup/search by number stays unambiguous; (c) a stricter unique is a safe backstop (409 via `GlobalExceptionHandler.cs:35`). So distinct numbers are guaranteed by shape:
- Default branch: unchanged `{SERIES}-{n:D4}` / `{InvoicePrefix}-{n:D4}` — existing numbering continues exactly (counter rows moved to Main, D6).
- Non-default branch: `{SERIES}-{Code}-{n:D4}` (PUR, PRT, SRT, ADJ). Invoices: `{Branch.InvoicePrefix}-{n:D4}` if the branch has its own prefix, else `{CompanyPrefix}-{Code}-{n:D4}`.
- Service rules (BranchService, under a `Companies` row lock `FOR UPDATE` shared with the settings PUT): an own `InvoicePrefix` must differ from the company prefix and from every other branch's own prefix, and must not prefix any existing invoice number of another branch (`InvoiceNo LIKE prefix || '-%'`); GST length: worst-case invoice number (5-digit counter) ≤ 16 chars, else 400 on `invoicePrefix`/`code`. `SettingsController` PUT rejects a company prefix equal to a branch's own prefix (same lock).
- EXP stays one **company-level** series (`BranchId NULL`) even when an expense is branch-tagged — avoids `EXP-0001` collisions between the company series and Main; expenses are not GST invoices.

**D5 — No "Branch" query filter in Phase 2; explicit predicates.** Every member is CompanyWide in Phase 2 (Phase 1 §7 O3), so a filter would only add joins and risk the §8 INNER JOIN gotcha with no protection gained; Q7 needs "All branches" for company-wide users and returns need cross-branch reads. Phase 2 adds the seam instead: `IBranchContext.AllowedBranchIds` (null = all) on the DbContext, and T1's entity classification gains a `BranchScoped` column (5 headers mandatory, 2 optional). Phase 5 adds a named filter `"Branch"` = `AllowedBranchIds == null || AllowedBranchIds.Contains(e.BranchId)` without touching call sites.

**D6 — Branch context resolution (ADR-04) and interim rule.**
- `Sellora.Domain/Abstractions/IBranchContext.cs`: `Guid? RequestedBranchId` (validated header value or null), `IReadOnlySet<Guid>? AllowedBranchIds` (null = every branch of the company), `bool CanAccess(Guid branchId)`.
- `Sellora.Infrastructure/Data/BranchContext.cs` (scoped, set-once like `CompanyContext`), `BranchContext.None` (AllowedBranchIds = empty: grants nothing) and `BranchContext.Unrestricted(Guid? requested = null)` for tests/seeder.
- `SelloraDbContext(options, ICompanyContext, IBranchContext)` — required (fail closed, house style D3); `public IBranchContext BranchContext { get; }`. Factory passes `BranchContext.None`.
- `CompanyContextMiddleware` (extended, same file — one membership query): select `AccessScope` instead of `Any`; `CompanyWide` → AllowedBranchIds = null; `AssignedBranches` (cannot exist yet — Phase 1 service rejects it) → empty set (fail closed; Phase 5 loads assignments). Then, only if `X-Branch-Id` is present: malformed → 400 `branch_header_invalid`; else one query `Branches` (filtered) `Id = h AND IsActive` + `CanAccess(h)` → else 403 `branch_access_denied` (identical for foreign / unknown / inactive — arch §14.2 no existence leak). Applies to every authenticated request incl. GETs.
- Header absent (interim): working branch = company default branch, provided `CanAccess(default)`. Phase 5 tightens to "primary allocation, else 403 `branch_required`" for AssignedBranches users only — CompanyWide behaviour unchanged. So existing clients keep working with **no frontend change**.
- Writes: `Services/BranchRules.cs` `ResolveWorkingBranchAsync(db, ct)` → `RequestedBranchId ?? default`, must exist, be active and `CanAccess` → else `DomainValidationException("branchId", …)`. Services read the context from `db.BranchContext` (no service ctor/input-record churn; tests set it on the DbContext). Controllers do not pass branch ids from bodies for header documents (arch §14: never trust client BranchId).

**D7 — Document creation and referenced documents.**
- Purchase, sale, adjustment: `BranchId = ResolveWorkingBranchAsync`.
- Purchase/Sales return (Q4): `BranchId = original.BranchId` (restock where sold/received). If `RequestedBranchId` is set and differs → 400 `branchId` "Returns must be recorded at the branch of the original document."; original branch must be active and `CanAccess` (Phase 5 tightening point). Header-less return of a non-Main invoice is therefore allowed for company-wide users (they may act in any branch, Q7).
- Payments/refunds/reversals/cancellations act on the document's branch; check `CanAccess(doc.BranchId)` (no-op in Phase 2), no active check.
- `FinancialAccountRules.EnsureUsableAsync(db, accountId, Guid? branchId, ct)`: account with `BranchId` set may only be used for a document of that branch; company-level accounts usable everywhere; expense with NULL branch may use any account. No existing account has a branch, so behaviour is unchanged after migration.
- Expenses/FinancialAccounts: optional `branchId` in create/update request bodies (explicit tagging, not stamped from the header — keeps today's company-level default); validated exists + `CanAccess` (+ active on create).

**D8 — Reads, filters, indexes.** Optional `[FromQuery] Guid? branchId` on: `PurchasesController.List :22`, `PurchaseReturnsController.List :22`, `SalesController.List :91`, `SalesReturnsController.List :21`, stock-adjustments list, `ExpensesController.List :18`, `FinancialAccountsController` list, `ReportsController.Purchases :152`, `Sales :186`, `ProfitSummary :219`; validated by `IBranchContext.CanAccess` + exists in company (inactive allowed for history) → 403 `branch_access_denied`. Absent = all branches (Q7). Stock reports (`current-stock`, `stock-movement`, `StockController`) stay company-wide until Phase 8 (stock is not per branch). Dashboard unchanged. ProfitSummary with `branchId`: expenses with NULL branch excluded (ASSUMPTION — confirm in O-2). DTOs: list/detail of the 5 headers + expenses + accounts gain `BranchId`, `BranchCode`, `BranchName` (additive). Sales invoice detail also gains `SellerGstin` = branch.Gstin ?? company Gstin (current value; see O-3).
Indexes (B2): `IX_PurchaseOrders_CompanyId_BranchId_PurchaseDate`, `IX_PurchaseReturns_CompanyId_BranchId_ReturnDate`, `IX_SalesInvoices_CompanyId_BranchId_InvoiceDate`, `IX_SalesReturns_CompanyId_BranchId_ReturnDate`, `IX_StockAdjustments_CompanyId_BranchId_CreatedAt`; these also serve the composite FKs. Expenses/FinancialAccounts: `IX_<T>_CompanyId_BranchId` (FK index only; small tables). Suppress EF's redundant per-FK index (review generated migration).

**D9 — Permissions (ADR-13).** New `PermissionCatalog` entries, module "Administration", mirroring the existing View/Create/Edit/Disable convention (`PermissionCatalog.cs:43-46`): `Company.Branches.View`, `Company.Branches.Create`, `Company.Branches.Edit`, `Company.Branches.Disable`. Administrator gets them via `PermissionResolver` (`AuthModels.cs:83-86`); no other role is granted (no `GrantNewPermissionToExistingHolders` call); Angular reads the catalogue from `GET permissions`, so the roles screen shows them without code change. Using a branch (header, filters, `GET branches/accessible`) needs no permission — branch is a data scope, not an action.

**D10 — CORS (Q9).** `Program.cs`: policy built from `Cors:AllowedOrigins` (string array), `WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Branch-Id")`, `AllowAnyMethod`, `SetPreflightMaxAge(10 min)` (POS sends a custom header ⇒ preflight), **no `AllowCredentials`** (bearer tokens in headers, refresh token in body; ASSUMPTION verified by grep). `appsettings.json` default `["http://localhost:4200"]`; environments override with `Cors__AllowedOrigins__0..n` (placeholders `<SWA-PROD-URL>`, `<SWA-DEV-URL>` in the release note). Empty list → no cross-origin access + startup warning (fail closed). Policy renamed `Frontend`.
Frontend (WP5, **conditional on owner URLs**): `environment.ts` `apiBaseUrl: '__SELLORA_API_BASE_URL__/api/v1'`, substituted in the SWA workflow from repo variable `SELLORA_API_BASE_URL` with a step that fails the build if unset (devops, one workflow file). Until URLs are supplied, nothing changes in the frontend and the current production routing keeps working. The app sends no `X-Branch-Id` in Phase 2 (D6 default) — picker in Phase 11.

------------------------------------------------------------------------
## 3. Design table per layer

### Domain (`backend/src/Sellora.Domain`)
| File | Change | Reason |
|---|---|---|
| `Entities/Branch.cs` (new) | D1 fields, `: ICompanyOwned` | arch §4.2 |
| `Companies/DefaultTenant.cs` | `BranchId …0002`, `BranchCode "MAIN"` | ADR-11 |
| `Entities/{PurchaseOrder,PurchaseReturn,SalesInvoice,SalesReturn,StockAdjustment}.cs` | `Guid BranchId` | D3 |
| `Entities/{Expense,FinancialAccount}.cs` | `Guid? BranchId` | Q6 |
| `Abstractions/IBranchContext.cs` (new) | D6 | ADR-04 |
| `Abstractions/IBranchService.cs` (new), `Companies/BranchModels.cs` (new) | `CreateAsync/UpdateAsync/SetStatusAsync`, `CreateBranchInput`, `UpdateBranchInput` | ADR-09 (new writes in services) |
| `Permissions/PermissionCatalog.cs` | 4 codes | D9 |
| `Expenses/ExpenseModels.cs`, `Accounts/AccountModels.cs` | optional `BranchId` (init property, non-positional to avoid test churn) | D7 |

### Infrastructure (`backend/src/Sellora.Infrastructure`)
| File | Change | Reason |
|---|---|---|
| `Data/BranchContext.cs` (new) | scoped impl, `Set`, `None`, `Unrestricted` | D6 |
| `Data/SelloraDbContext.cs` | 3rd ctor param + property; `DbSet<Branch>` | D6 |
| `Data/SelloraDbContextFactory.cs:18` | pass `BranchContext.None` | design-time |
| `Data/Configurations/BranchConfiguration.cs` (new) | D1 | |
| `Data/Configurations/{Purchase,Sales}Configurations.cs`, `StockAdjustmentConfiguration.cs`, `ExpenseConfigurations.cs`, `FinancialAccountConfiguration.cs` | composite FK `FK_<T>_Branches_CompanyId_BranchId` + D8 indexes | D3/D8 |
| `Data/Configurations/CompanyConfigurations.cs` (DocumentSequence) | `FK_DocumentSequences_Branches_CompanyId_BranchId` (nullable, MATCH SIMPLE) | Q2 |
| `Data/Migrations/*` B1–B3 + Designers + snapshot | §4 | |
| `Data/DbSeeder.cs` | `SeedDefaultBranchAsync` | D2 |
| `DependencyInjection.cs:18-21` | `AddScoped<BranchContext>()`, `AddScoped<IBranchContext>(…)` | D6 |
| `Services/BranchRules.cs` (new) | `ResolveWorkingBranchAsync`, `EnsureCanUseAsync(db, branchId, requireActive, ct)`, `GetDefaultBranchAsync` | D6/D7 |
| `Services/BranchService.cs` (new) | D1/D4 rules, `Companies` row lock | |
| `Services/DocumentNumbers.cs` | `NextAsync(db, series, Guid? branchId, ct)` (insert-if-missing on the branch partial unique, `UPDATE … RETURNING` with `"BranchId" = @branch` or `IS NULL`); `DocumentNumberFormat.Format(series, branch, companyPrefix, n)`; InMemory `ExistingMaxAsync` per branch | D4 |
| `Services/PurchaseService.cs`, `SalesService.cs`, `StockAdjustmentService.cs` | stamp BranchId (D7), branch numbering, return same-branch rule, account branch rule | D4/D7 |
| `Services/ExpenseService.cs`, `FinancialAccountService.cs`, `FinancialAccountRules.cs:11` | optional branch, account branch rule | D7 |

### Api (`backend/src/Sellora.Api`)
| File | Change |
|---|---|
| `Authorization/CompanyContextMiddleware.cs` | AccessScope + header validation, sets `BranchContext`; codes `branch_header_invalid` (400), `branch_access_denied` (403) |
| `Program.cs:69-76,133` | D10 CORS |
| `appsettings.json` | `"Cors": { "AllowedOrigins": ["http://localhost:4200"] }` |
| `Controllers/BranchesController.cs` (new) | §5 endpoints |
| `Controllers/{Purchases,PurchaseReturns,Sales,SalesReturns,StockAdjustments,Expenses,FinancialAccounts,Reports}Controller.cs` | `branchId` filter, branch columns in DTO projections (dictionary lookup, D3) |
| `Controllers/SettingsController.cs` | prefix-collision check under the Companies lock (D4) |
| `Contracts/BranchContracts.cs` (new), `Contracts/*` of the touched controllers | DTO additions |

### Frontend — WP5 only (D10): `frontend/src/environments/environment.ts`; devops: SWA workflow substitution step.

------------------------------------------------------------------------
## 4. Migrations (mirror the Phase 1 header/P/V/guard layout of `Migrations/20261109085910_AddDocumentSequences.cs:7-50`; one transaction each)

| # | Name | Content |
|---|---|---|
| B1 | `AddBranches` | Create `Branches` (D1). Insert Main per company (D2), `ON CONFLICT DO NOTHING`. Assert: every company has exactly one default active branch; default company's Main Id = `…0002`. |
| B2 | `AddBranchIdToDocuments` | **Guard G3**: any company without a default branch → RAISE. Add `"BranchId" uuid NULL` to the 5 headers + Expenses + FinancialAccounts (metadata-only). Backfill 5 headers: `UPDATE "<T>" t SET "BranchId" = b."Id" FROM "Branches" b WHERE b."CompanyId" = t."CompanyId" AND b."IsDefault" AND t."BranchId" IS NULL`. In-tx asserts: no NULL BranchId in the 5; row counts = pre-counts (temp table `ON COMMIT DROP`). SET NOT NULL (5). Composite FKs (7) + D8 indexes. |
| B3 | `ScopeDocumentSequencesToBranches` | Composite FK on DocumentSequences. **Guard G4**: a branch-level row already exists for (company, default branch, series) → RAISE. Move counters: `UPDATE "DocumentSequences" s SET "BranchId" = b."Id", "UpdatedAt" = now() FROM "Branches" b WHERE b."CompanyId" = s."CompanyId" AND b."IsDefault" AND s."BranchId" IS NULL AND s."Series" IN ('PUR','PRT','INV','SRT','ADJ')` (EXP stays company-level). Assert: NextValue per (company, series) identical before/after; no NULL-branch row for the 5 series. |

P-block (prod copy, read-only): P0 row counts of the 7 tables + DocumentSequences; P1 `SELECT "CompanyId", "Series", "BranchId", "NextValue" FROM "DocumentSequences" ORDER BY 1,2` (expect BranchId NULL everywhere); P2 companies count (expect 1); P3 timing of B2 on the R5 copy (SalesInvoices backfill + 2 index builds dominate).

V-block (after apply; V1 = P0, others must return no rows):
- V2 `SELECT 'SalesInvoices', "Id" FROM "SalesInvoices" WHERE "BranchId" IS NULL OR "BranchId" <> '0c0a1f00-0000-4000-8000-000000000002'` UNION ALL the other 4 headers.
- V3 same-company: `SELECT i."Id" FROM "SalesInvoices" i JOIN "Branches" b ON b."Id" = i."BranchId" WHERE b."CompanyId" <> i."CompanyId"` (+6 tables; FK makes this empty — evidence for arch §19 exit "branches cannot be linked to another company").
- V4 `SELECT "CompanyId" FROM "Companies" c WHERE (SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault" AND b."IsActive") <> 1`.
- V5 `SELECT * FROM "DocumentSequences" WHERE "BranchId" IS NULL AND "Series" <> 'EXP'`; and NextValue per series equals P1.
- V6 `SELECT COUNT(*) FROM "Expenses" WHERE "BranchId" IS NOT NULL` = 0 and same for FinancialAccounts (no invented tags).
- V7 post-deploy smoke: next PUR/INV/SRT/PRT/ADJ/EXP number = previous max+1 in the unchanged format.

Down (each starts with guard **D-G2**: more than one branch in any company, or any BranchId other than the company's default → RAISE): B3 moves the 5 counters back to `BranchId NULL`, removes the FK; B2 removes indexes/FKs/columns (optional Main tags on Expenses/FinancialAccounts are lost — derived from nothing, acceptable; stated in header); B1 removes Branches. Backfill reversible (derived).

Release note: old API + new schema is incompatible (inserts without BranchId fail; old numbering writes a company-level counter) → stop API, apply B1–B3 (idempotent script), V-block, deploy API, smoke, then (if WP5) SWA. Rollback = snapshot restore + previous build. Index builds lock writes on the 5 headers for their duration (maintenance window; time on R5).

------------------------------------------------------------------------
## 5. API / DTO contract

- `GET api/v1/branches?includeInactive=` [Company.Branches.View] → `BranchDto[]` (`Id, Code, Name, AddressLine, City, State, Phone, Email, Gstin, InvoicePrefix, EffectiveInvoicePrefix, IsDefault, IsActive`).
- `GET api/v1/branches/{id}` [View] → 404 for foreign/unknown.
- `POST api/v1/branches` [Create] `CreateBranchRequest(Code, Name, address…, Gstin?, InvoicePrefix?)` → 201.
- `PUT api/v1/branches/{id}` [Edit] `UpdateBranchRequest(Name, address…, Gstin?, InvoicePrefix?)` (Code not editable).
- `POST api/v1/branches/{id}/status` [Disable] `{ isActive }` — default branch → 400.
- `GET api/v1/branches/accessible` [Authorize] → `[{ id, code, name, isDefault }]` active branches the caller may work in (Phase 2: all; Phase 5: allocations, Phase 11 picker source).
- Header `X-Branch-Id` (optional). Errors: `branch_header_invalid` 400, `branch_access_denied` 403, validation `branchId` 400.
- Additive DTO fields per D8; request bodies of Expense and FinancialAccount create/update gain optional `branchId`. No breaking change; unchanged SWA keeps working.

------------------------------------------------------------------------
## 6. Work packages (max 2 parallel; no file overlap)

| WP | Owner | Depends | Files owned | Acceptance criteria |
|---|---|---|---|---|
| WP1 | database-engineer | approval | Domain: `Branch.cs`, `DefaultTenant.cs`, BranchId on 7 entities, `IBranchContext.cs`; Infrastructure `Data/**` (BranchContext, DbContext, factory, configurations, B1–B3, snapshot, DbSeeder), `DependencyInjection.cs` | build green; `has-pending-model-changes` clean; B1–B3 apply on empty DB → one Main `…0002`; headers carry P/V blocks; G3/G4/D-G2 guards; FK/index names per D3/D8; no redundant FK indexes |
| WP2 | test-engineer | WP1 | `tests/**/TestData/TestBranch.cs` (new; `Unrestricted`, Main seeding helper), the DbContext construction sites (~15 + `PostgresFixture.cs`, `CompanyMigrations.cs`), InMemory fixtures seeding Main, `Migrations/{AddBranches,AddBranchIdToDocuments,ScopeDocumentSequencesToBranches}MigrationTests.cs`, `CompanyQueryFilterTests.cs` (BranchScoped classification), `Constraints/BranchConstraintTests.cs` | `scripts/verify.sh backend` green, existing suites unchanged in intent; T1, T2, T9 pass |
| WP3 | backend-engineer | WP1 (∥ WP2) | Domain `IBranchService.cs`, `Companies/BranchModels.cs`, `PermissionCatalog.cs`, Expense/Account models; Infrastructure `Services/**`; Api `Program.cs`, `appsettings.json`, `Authorization/CompanyContextMiddleware.cs`, `Controllers/**`, `Contracts/**` | D4–D10 implemented; `IgnoreQueryFilters` sites unchanged (T4); every raw SQL on DocumentSequences carries Company + Branch predicates; contract §5 handed to lead |
| WP4 | test-engineer | WP3 | `tests/Sellora.Tests/Branches/**`, service tests additions, `tests/Sellora.IntegrationTests/Api/Branch*ApiTests.cs`, `IsolationCases.cs` (new routes), `Concurrency/DocumentNumberConcurrencyTests.cs`, `Api/CorsConfigurationTests.cs` | T3–T8, T10 pass; `scripts/verify.sh all` green |
| WP5 (conditional) | frontend-engineer (+ devops for the workflow step) | owner URLs (O-1) | `frontend/src/environments/environment.ts`; SWA workflow | prod build has absolute URL; build fails if variable unset; Vitest green |
| WP6 | code-reviewer | all | — | review vs ADR-04/05/08/11/13, D1–D10, migration checklist, §8 INNER JOIN rule |

------------------------------------------------------------------------
## 7. Tests

- T1 Classification: every entity Platform / CompanyOwned / Inherited **and** BranchScoped(required|optional|none); Branch is CompanyOwned; the 5 headers required, Expenses/FinancialAccounts optional.
- T2 BranchContext: set-once; `None` grants nothing (write resolution fails); DbContext from DI gets the request-scoped instance.
- T3 Branch service: create/edit/status; duplicate code/name per company → 400, same code in another company OK; Code immutable; default branch cannot be deactivated; prefix rules (equal company prefix, other branch prefix, used by another branch's invoices, >16 chars) → 400; settings PUT colliding with a branch prefix → 400.
- T4 Branch isolation (InMemory): A/B branches of one company — documents stamped with header branch; header-less → Main; inactive branch → 400; list `branchId` filter returns only that branch, absent → all; return of invoice at B with header A → 400, header-less → return.BranchId = B; account tagged B unusable for A documents, company-level account usable everywhere.
- T5 Numbering per branch (InMemory + PG): Main continues `INV-00nn`/`PUR-00nn`; branch `BLR` → `PUR-BLR-0001`, `INV-BLR-0001` or own-prefix `KAR-0001`; EXP company-level regardless of tag; PG concurrency: N parallel creates in two branches → contiguous per branch, no blocking across branches, rollback leaves no gap, first document of a new branch upserts its counter.
- T6 Header validation (WebApplicationFactory + PG): malformed → 400 `branch_header_invalid`; foreign-company / unknown / inactive branch → 403 `branch_access_denied` with identical bodies; valid → 2xx; branch of company B via header while in A → 403; query `branchId` of company B → 403.
- T7 Endpoint authorization: `EndpointAuthorizationTests` covers `BranchesController` (View/Create/Edit/Disable; `accessible` authenticated-only); Administrator sees the 4 new codes in `auth/me`; a non-admin role has none.
- T8 Company isolation extended (`IsolationCases.cs`): branch routes; company B branch never visible from A (list, detail 404, accessible).
- T9 Migrations (PG, per migration, mirroring `AddDocumentSequencesMigrationTests.cs`): legacy fixture → Up → V-block; G3/G4 raise and roll back; D-G2 raises with a second branch; Down restores previous schema; Down→Up; idempotent script twice; B3 NextValue preserved exactly; composite FK rejects a branch of another company (constraint test).
- T10 CORS: configured origin gets `Access-Control-Allow-Origin` and `X-Branch-Id`/`Idempotency-Key` in allowed headers on preflight; unlisted origin gets none; no `Allow-Credentials`; empty config → no CORS headers.
- Regression: existing `Concurrency/*`, `StockLedgerReconciliationTests`, `MigrationTests`, `UnitRestructuringIdempotentScriptTests` green.

------------------------------------------------------------------------
## 8. Risks / release notes

- R-1 Stock is company-wide until Phase 8: a sale at branch B consumes batches physically at Main. Phase 8 design must map pre-Phase-8 movements to the default branch location (or block non-default branch postings — O-4) so per-location reconciliation stays balanced.
- R-2 Maintenance window: old app incompatible with B2/B3 (§4 release note); index builds on SalesInvoices/PurchaseOrders time on R5.
- R-3 Invoice-number collisions from prefix churn are prevented by service checks; residual race-free backstop = `(CompanyId, InvoiceNo)` unique → 409.
- R-4 Fail-closed DbContext ctor change touches every test construction site again (mechanical, WP2).
- R-5 CORS: removing `AllowCredentials` and listing headers could break an undiscovered cookie/header use — T10 + smoke; production SWA URL switch (WP5) must follow the API CORS config, never precede it.
- R-6 `ExpenseService`/`ProfitSummary` branch semantics for NULL-branch expenses (O-2).

## 9. Open questions (each with recommendation)

- O-1 SWA and App Service URLs per environment (Q9) — needed for WP5 and `Cors:AllowedOrigins`. Rec: ship backend config now with localhost default; run WP5 when URLs arrive.
- O-2 Branch-filtered profit summary: exclude company-level (NULL-branch) expenses? Rec: yes, shown only under "All branches".
- O-3 Snapshot seller GSTIN/branch address on SalesInvoices at issue (GST printouts must not change when a branch's GSTIN is edited)? Rec: yes, but in Phase 10 with the invoice print work; Phase 2 exposes the current effective value.
- O-4 Allow stock postings at non-default branches before Phase 8 (shared stock)? Rec: allow; Phase 8 maps legacy movements to the default location (R-1).
- O-5 Confirm GST Rule 46(b) interpretation (≤16 chars, unique per GSTIN per FY) used for D4. Rec: accountant confirmation; design is safe either way (stricter).

## 10. Deviations from plan

- Permission codes View/Create/Edit/Disable instead of View/Manage (lead suggestion) — mirrors existing catalogue convention (`PermissionCatalog.cs:43-46`).
- O2 resolved as "keep `(CompanyId, No)`" with branch-coded number shapes rather than a unique swap (D4).
- EXP numbering stays company-level although expenses can be branch-tagged (D4) — refines Q2 "per company + branch" for non-GST documents.
- No "Branch" query filter in Phase 2 (D5); seam only.
- `IBranchContext` lives on `SelloraDbContext` (like `CompanyContext`), so services need no ctor/input changes; branch-scoped validation authority stays in services (arch §14).
- Frontend apiBaseUrl change is conditional on owner URLs (D10). No conflict with unit-analysis D1–D10 (no stock/ledger shape change).

## 11. Owner approval (2026-11-09)

Design approved, deviations accepted. O2 → D4 (company-wide uniques, per-branch counters, branch-distinct number shape). O-2 yes (branch-filtered profit excludes company-level expenses). O-3 yes (seller GSTIN snapshot on invoices in Phase 10; Phase 2 exposes the current value). O-4 yes (non-default branches may post stock before Phase 8; Phase 8 maps legacy movements to the default location). O-5 accountant confirmation pending (design is the stricter reading). O-1 URLs pending → WP5 deferred; backend CORS ships config-driven with the localhost default.

## 12. Delivery record (2026-11-09)

Status: **delivered; review "Approve with fixes" — fixes applied.** Verify: 870 unit / 486 Postgres integration / 192 Vitest, 0 warnings.

| Item | Outcome |
|---|---|
| Review Major 1 | Clearing a branch's own prefix now runs the derived-shape checks (`{CompanyPrefix}-{Code}`: ≤16-char worst case, collision with other branches' own prefixes and history) — shared helper for create/update/settings. |
| Review Major 2 | A company prefix change is rejected if another branch already holds numbers of the default-branch shape `P-<digits>` or a derived shape `P-{Code}-<digits>`. |
| Minors | History check made exact (`P-<digits>` only); purchase header update checks branch access; settings PUT validates the prefix only when it changes; `appsettings.json` diff limited to the Cors section. Accepted as designed: header validated on reads too (a deactivated branch header → 403); deactivation without the company lock (codes stay unique). |
| Behaviour note | A branch's counter belongs to (branch, series): changing a branch's invoice prefix continues the count (`INV-BLR-0001` → next `KAR-0002`). |
| Release | Seeder adds the 4 `Company.Branches.*` permissions only where startup seeding runs — production must run it (or insert the rows) after B1–B3. B2 rewrites the 5 header tables → maintenance window. Frontend absolute `apiBaseUrl` (WP5) pending the owner's URLs. |
