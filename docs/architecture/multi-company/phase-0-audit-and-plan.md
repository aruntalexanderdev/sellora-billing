# Multi-company / multi-branch / multi-location — Phase 0: repository audit and implementation plan

| Property | Value |
|---|---|
| Spec | `docs/architecture/Sellora-Multi-Company-Branch-Architecture-Claude.md` ("arch") |
| Related | `docs/analysis/unit-conversion-restructuring-analysis.md` ("analysis") §13.4–13.6, §25 Phase B / 6b, §27 D5 |
| Baseline | branch `analyse`, commit `a7c1c4e` (unit restructuring Phases 0–6a complete) |
| Status | **Phase 0 approved (2026-11-09).** ADR-01…13 accepted; answers in §10. Phase H and 1–4 delivered (see the phase-1…4 design delivery records); next: Phase 5 (on approval). |
| Appendix A | `phase-0-appendix-a-backend-database-audit.md` — backend/DB current state, entity map, auth, inventory, workflow impact, migration risk, tests, conflicts C1–C14 (file:line evidence) |
| Appendix B | `phase-0-appendix-b-frontend-ci-audit.md` — Angular current state, screen impact map, frontend tests, CI/CD and deployment |

Decisions below are **proposed** until approved. Items marked *PO* need a product-owner answer.

---

## 1. Current-state summary

| Area | Current state | Consequence |
|---|---|---|
| Organisation | No Company, Branch, Warehouse, Location or Vehicle concept. One business identity: singleton `BusinessSettings`, read with `.SingleAsync()` in 23 places. | Every business table is implicitly single-tenant; adding a 2nd company row breaks those 23 reads. |
| Layering | Domain / Infrastructure (services, EF) / Api. Stock and money documents go through services; master/admin writes and all read/report queries live in controllers with `SelloraDbContext`. No repository, no query filters, no current-user/tenant abstraction. | Tenant scoping has no single choke point today. |
| Identity & RBAC | JWT (15 min) with flat `permission` claims; one required global `Users.RoleId`; system role "Administrator" = all permissions; 57-code catalogue; dynamic policy provider. | No scope dimension at all. |
| Security defects (verified) | (1) Inactive roles still grant permissions (`PermissionResolver` ignores `Role.IsActive`). (2) `Users.Edit` can assign any role incl. Administrator, incl. to self (`UsersController` create/update). (3) `Roles.Edit` can add any permission to any non-system role incl. own. (4) `DashboardController` is sign-in-only but returns stock value. | Conflicts with arch §7.6 today, independent of multi-company. |
| Inventory | Stock in base units (D1). `StockItems` PK = `ProductId`; all stock lives in `PurchaseBatches.RemainingBaseQty`; signed `StockMovements` ledger with unit snapshots, reason codes, reference index, CHECKs; product-ordered row locks; FIFO `BatchAllocator`. Reconciliation invariant tested on Postgres. | Strong base for adding a location dimension; 5 read paths use `StockItems…FirstOrDefault()` per product and become silently wrong with >1 location. |
| Numbering | `COUNT(*)+1` under a global advisory lock per series; `EXP-` has no lock; numbers globally unique. | Must be company-scoped (and possibly branch/GSTIN/FY-scoped). |
| Frontend | Angular 22, signals, no context store, no selector, tenant name hard-coded; flat cached permissions in localStorage; `/auth/me` never called; one interceptor, no 403 handling, no single-flight refresh; ~85 routes with `permissionGuard`; ~20 eager routes. | Phase 11 needs a context service, context-aware `hasPermission`, interceptor work and lazy features. |
| Tests | 750 xUnit (InMemory), 281 Testcontainers PG17 (locks, constraints, migrations, reconciliation), 192 Vitest. No HTTP-pipeline (WebApplicationFactory) tests; no tests for auth plumbing on the frontend. | Cross-company isolation and 403 tests need a new harness. |
| CI/CD | `ci.yml` build+test on every push. Push to `dev` / `claude_dev` deploys the API to Azure App Service with **startup auto-migrate on** (dev apps run as `Production`). SWA frontend deploys from `claude_dev`. No migration step, no staging slot, no prod pipeline. | Tenant backfill migrations must not run via auto-migrate on a shared database. |

## 2. Entity and relationship map (target scope)

Full table with FKs and current unique constraints: Appendix A §2. Summary:

| Scope | Tables |
|---|---|
| **Platform** (no CompanyId) | Users (identity), Permissions (catalogue), RefreshTokens, Currencies, Units *(PO — recommended platform)* |
| **Company** (CompanyId) | Companies (absorbs BusinessSettings), Roles (+ScopeType, BranchId?), Taxes, ExchangeRates, Suppliers, Customers, ExpenseCategories, Expenses, FinancialAccounts, FinancialTransactions, Products, ProductUnits (denormalised for barcode uniqueness), SellingPrices (via unit), PurchaseBatches, IdempotencyKeys |
| **Company + Branch** (CompanyId, BranchId) | PurchaseOrders, PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments; optional BranchId on Expenses, FinancialAccounts *(PO)* |
| **Location** (StockLocationId) | StockItems (new PK `(StockLocationId, ProductId)`), new BatchLocationBalances, StockMovements, document headers (receiving / source / restock location), SalesInvoiceLineBatchConsumptions |
| **New** | Companies, Branches, StockLocations, Warehouses, Vehicles, UserCompanyMemberships, UserBranchAssignments, CompanyGovernanceMembers, GovernanceMemberPermissions, UserRoleAssignments, StockTransfers(+Lines), DocumentSequences, AuditEvents |

Lines/payments inherit scope from their header FK; no column unless a query needs it.

## 3. Workflow impact map

Detail per workflow: Appendix A §5 (backend) and Appendix B §2 (screens). Headline impacts:

| Workflow | Impact |
|---|---|
| Purchase / purchase return / cancel | Header gets Company, Branch, receiving location; masters must be same-company; batches get per-location balances; "untouched batch" cancel rule becomes location-aware. |
| Sales / POS / returns / cancel | Header gets Company, Branch, source location (POS = branch store or vehicle); availability and FIFO per location; return location rule *(PO: cross-branch returns?)*. |
| Adjustments / opening stock | Location required. |
| Pricing | Company-wide (D5 reinterpreted); per-location override deferred. Avg-cost display needs a location or weighted company average. |
| Reports / dashboard | Every query gets a company predicate and optional branch/location filter; never sum quantities across products. |
| Settings / invoices | Company profile replaces singleton; invoice prefix / GSTIN possibly per branch *(PO)*. |
| Users / roles | Single role → memberships + branch allocations + scoped role assignments; escalation guards. |
| Seeder | Default company/branch/location + memberships; demo data must never seed into real tenants. |

## 4. Migration risk assessment

| Risk | Likelihood / impact | Mitigation |
|---|---|---|
| Backfill `UPDATE` of CompanyId on large tables (StockMovements, sales lines, consumptions, financial transactions) rewrites rows | M / M | Nullable add (metadata-only) → batched backfill → `CHECK … NOT VALID` + `VALIDATE` → NOT NULL; time on a prod-size copy. |
| `StockItems` PK swap | L / M (small table, brief ACCESS EXCLUSIVE) | Single migration; Down refuses if >1 location row per product. |
| Global uniques (SKU, barcode, role name, document numbers, idempotency) collide across companies | H / H once company #2 exists | Re-scope uniques to `(CompanyId, …)` in the phase that introduces CompanyId; block company creation until done. |
| 23 `BusinessSettings.SingleAsync()` sites throw with 2 companies | H / H | Company-settings accessor in Phase 1; company creation disabled until all call sites use it. |
| Tenant leak through a query without company predicate | M / H | Global query filters + explicit predicates in raw SQL lock helpers + per-controller isolation tests (ADR-05). |
| Auto-migrate applies a half-finished phase migration to dev DB(s); two dev APIs possibly share one DB | M / H | Release gate R1 (§7) before Phase 1 migrations. |
| Stale JWT permissions after revocation (≤15 min) | H / M | Server-side effective-permission evaluation with version stamp (ADR-04). |
| Fabricated branch allocations | — | Existing users → `CompanyWide` membership of the default company; no branch allocation invented (arch §17.3). |
| Production PostgreSQL version unknown | L | Only matters for `NULLS NOT DISTINCT`; ADR-01 avoids nullable keys. *(PO/ops: confirm.)* |

Rollback: additive steps have real `Down()`s; destructive steps (drop `Users.RoleId`, global uniques, retire `PurchaseBatches.RemainingBaseQty` total) ship in a later release after validation (D10: snapshot → idempotent script → validation SQL → deploy).

## 5. Decisions (proposed ADRs — need approval)

| # | Decision | Rationale / deviation |
|---|---|---|
| **ADR-01** | **Balances stay in base units; no `UnitId` in the balance key.** Implement arch §10.1 as two tables: `StockItems(StockLocationId, ProductId)` (aggregate, lock anchor, avg cost) and `BatchLocationBalances(PurchaseBatchId, StockLocationId)`; CompanyId denormalised with composite FK. | **Deviation from arch §10.1.** A unit in the key would split one physical stock into per-unit rows (double counting; re-introduces rejected D4). Unit traceability is already satisfied by immutable `Product.BaseUnitId` + unit snapshots on every line and movement. All stock is in batches → BatchId never null → no `NULLS NOT DISTINCT` needed. |
| **ADR-02** | **Keep the existing one-signed-row-per-location ledger**; add `CompanyId`, `StockLocationId`. Transfers write TransferOut + TransferIn rows referencing a `StockTransfers` document (which holds source/destination). No `StockMovementItems` header/items split. | **Deviation from arch §10 conceptual entities.** Keeps `Σ movements per (location, product) = QOH` a plain GROUP BY and the existing reconciliation tests valid. |
| **ADR-03** | **Arch §19 phase sequence supersedes analysis Phase B / 6b.** Mapping: analysis B ≈ arch 1–3 (+4) + 8; analysis 6b ≈ arch 9 (+ damage/repack); analysis 7 (reports) runs after arch 8; analysis 8 (release) applies per phase. | Arch requires company/branch before locations. Analysis §25 and CLAUDE.md phase list to be updated to point here once approved. |
| **ADR-04** | **Context resolution:** active company is a **token claim** (`company_id`), re-issued by `POST auth/switch-company` after membership check; active branch is a per-request `X-Branch-Id` header **validated server-side** against the membership/allocation. Effective permissions are computed server-side per (user, company, branch) with a short cache invalidated by a membership/role version stamp; JWT permission claims become a UI hint only. | Arch §14: never trust client-supplied ids; §6.5: revocation must be effective. Company switch is rare (token re-issue acceptable); branch switch is frequent (POS) so a validated header avoids token churn. Needs SWA/CORS check (§8 Q9). |
| **ADR-05** | **Tenant filtering:** EF global query filters on `CompanyId` from a scoped `ICompanyContext`, plus explicit company predicates in raw-SQL helpers (`StockLocks`, numbering), plus an isolation test per controller/service. RLS not used (optional defence-in-depth later). | Lowest-effort single choke point given no repository layer; known bypasses (raw SQL, `IgnoreQueryFilters`) covered by tests and review checklist. |
| **ADR-06** | **D5 reinterpreted:** "global" products, product units and prices mean **company-wide**. No per-branch product-unit configuration. Per-location price override deferred. Units and Currencies are platform reference data *(PO confirm)*; Taxes, ExchangeRates company-owned. | Arch §4.1 makes the company the tenant boundary. |
| **ADR-07** | `BusinessSettings` becomes the company profile (`Companies` 1:1, or `BusinessSettings.CompanyId` PK/FK); one company-settings accessor replaces the 23 singleton reads. Company creation stays disabled until done. | Prevents runtime failures when company #2 appears. |
| **ADR-08** | Document numbers via a `DocumentSequences(CompanyId, BranchId?, Series, FinancialYear?, NextValue)` row locked `FOR UPDATE`, replacing `COUNT(*)+1`; uniques become `(CompanyId[, BranchId], No)`. Fix missing `EXP-` lock. Series granularity *(PO)*. | Count-based numbering is wrong once scoped and racy for EXP today. |
| **ADR-09** | **No mass refactor of controller-side writes/reads.** New writes (companies, branches, locations, memberships, governance, roles, transfers) go into services. Existing reads stay in controllers but use the shared scope predicate. Users/Roles writes move to services when rewritten in arch Phases 5/7. | Arch §3.1 vs documented CLAUDE.md convention; avoids a risky unrelated rewrite. |
| **ADR-10** | **Platform admin separated:** a platform-admin flag/table outside company RBAC with no implicit business-data access. "Administrator" becomes a protected per-company system role. Seeded admin becomes owner-governance member of the default company with explicit permissions. | Arch §5.1 / §7.6. |
| **ADR-11** | Default company/branch/location are created with **fixed, documented Guids** by migration backfill SQL (not by the startup seeder). Existing users → `CompanyWide` membership; existing roles → company roles of the default company; `Users.RoleId` → one `UserRoleAssignments` row, column kept nullable for one release. | Arch §17.3; idempotent and reproducible in every environment. |
| **ADR-12** | `BatchLocationBalances` introduced with a **dual-write release**: `PurchaseBatches.RemainingBaseQty` kept as the company-wide total and validated (`Σ location balances = batch remaining`) before retirement. | Safe rollback window for the largest stock change. |
| **ADR-13** | Permission codes stay stable strings; scope is a separate dimension. Only genuinely new codes are added (`Company.Branches.*`, `Company.Warehouses.*`, `Company.Vehicles.*`, `Company.Users.ManageBranchAccess`, `StockLocations.*`, `StockTransfers.*`). Governance permissions live in a **separate catalogue/table** and can never be put on an operational role. | Arch §5.3 "avoid near-duplicates"; codes are shared contract with Angular. |

## 6. Implementation plan

Every phase follows arch §20 (approval per phase, completion report, stop). Sizes use CLAUDE.md task sizing.

| Phase | Scope (this repo) | Key DB changes | Key API / UI | Exit evidence | Size |
|---|---|---|---|---|---|
| **H — Hardening (pre-req, recommended first)** | Fix verified defects: inactive role grants nothing; cannot assign system/inactive roles or change own role without dedicated permission; can only grant permissions the actor holds; dashboard behind a permission; `EXP-` numbering lock. Release gate R1 (§7). | none | 400/403 on escalation attempts | Escalation + inactive-role tests; CI migration-script job | S–M |
| **1 — Company foundation** | `Companies` from BusinessSettings; `ICompanyContext` + `company_id` claim; global query filters; CompanyId on company tables; scoped uniques; company-settings accessor; `DocumentSequences`; WebApplicationFactory + PG isolation harness. | Companies; CompanyId on ~20 tables (nullable → backfill → enforce); unique re-scoping | `auth/me` returns memberships; `auth/switch-company` | Company isolation tests per endpoint; existing suites green; V-queries 0 rows | L–XL (may split 1a schema/backfill, 1b context/filters) |
| **2 — Branch foundation** | `Branches` (composite FK to company), default branch, activation; BranchId on document headers; branch context header + validation. | Branches; BranchId on 5 headers | branches API; header context | Branch-belongs-to-company constraint tests; regression | M–L |
| **3 — Stock location foundation** | `StockLocations` (types, ownership CHECKs, `(CompanyId, Id)` AK); default branch-store location. No posting change yet. | StockLocations | locations API (read/admin) | Every internal location valid; consistency constraint tests | M |
| **4 — Warehouses & vehicles** | Company/branch warehouses and vehicles linked to locations; reassignment blocked while stock exists (check wired fully in Phase 8). | Warehouses, Vehicles | CRUD APIs | Ownership model tests; reassignment guard tests | M |
| **5 — Membership & branch allocation** | `UserCompanyMemberships` (AccessScope), historical `UserBranchAssignments` (one active primary), allocation management service, audit events, access-cache invalidation. | 2 tables + partial uniques; `AuditEvents` | memberships/allocations API | Multi-branch allocation, history, revocation-effective tests | L |
| **6 — Platform separation & governance** | Platform admin split; governance members + individual governance permissions; sensitive-operation protection. | 2–3 tables | governance API | Self-escalation and isolation tests | L |
| **7 — Scoped RBAC** | Roles get ScopeType/BranchId; `UserRoleAssignments`; effective permission evaluation (ADR-04); delegation limits; `Users.RoleId` deprecated. | Roles columns, assignments | role/assignment API | Branch 1 ≠ Branch 2 roles; no cross-company; no self-grant | L |
| **8 — Location-based inventory** (= analysis Phase B core) | `StockItems` PK swap; `BatchLocationBalances` (dual-write); StockLocationId on movements/headers/consumptions; locks + FIFO per (location, product); availability per location; fix 5 `FirstOrDefault()` reads; reconciliation per location. | PK change, new table, columns | `locationId` params on stock/sales/purchase/report endpoints | Single-location behaviour identical; per-location reconciliation; concurrency tests on (location, product) | XL (split 8a schema+backfill, 8b posting) |
| **9 — Transfer lifecycle** (= analysis 6b) | Request/approve/dispatch/transit/partial receipt/variance/cancel; idempotent dispatch/receive; transit location. | StockTransfers + lines (unit snapshots) | stock-transfers API | Analysis §13.4 walkthrough across two branches; no double debit/credit | L |
| **10 — Purchase/sales/returns/van ops** | Location-aware receiving/issue/returns; vehicle loading, van sales, vehicle returns. | minor | POS location, van flows | E2E regression; financial consistency | L |
| **11 — Angular integration** | Context service + selector, context-aware `hasPermission`, single-flight refresh, 403/revoked handling, correlation id, admin/governance/allocation/role/location/transfer screens (lazy), runbook. | — | — | §18.3 specs; full regression; prod bundle within budget | L |

Analysis Phase 7 (reports: breakdown, entered-unit columns, location filter) follows arch Phase 8. Frontend work may be pulled forward per phase (e.g. company/branch admin screens with Phases 1–2) if preferred; arch places it in Phase 11.

## 7. Release prerequisites (before any Phase 1 migration reaches a shared database)

| # | Prerequisite | Owner |
|---|---|---|
| R1 | `ApplyMigrationsOnStartup=false` in base `appsettings.json`; enable explicitly per dev App Service only if wanted; give dev apps a non-`Production` environment name; fail startup if true in real production. | devops |
| R2 | CI job: `dotnet ef migrations has-pending-model-changes` + idempotent script artifact. | devops |
| R3 | Gated migration step: snapshot → idempotent script → validation SQL → deploy; backend before frontend. | devops |
| R4 | Confirm whether `dev` and `claude_dev` APIs share a database. | owner |
| R5 | Production-size data copy available to time backfills. | owner |

## 8. Open questions

| # | Question | Recommendation |
|---|---|---|
| Q1 *PO* | Units: platform reference data or company-owned? | Platform. |
| Q2 *PO* | Document number series: per company, per branch, per GSTIN, per financial year? | Per company + branch, FY reset optional; decide before Phase 1 (ADR-08). |
| Q3 *PO* | GSTIN / invoice prefix per company or per branch? | Per branch where a branch has its own GST registration; else company. |
| Q4 *PO* | Sales returns at a branch other than the selling branch? Restock location? | Not in first release; restock to the invoice's location. |
| Q5 *PO* | Can warehouses sell directly, or only branch-store and vehicle locations? | Only branch-store and vehicle. |
| Q6 *PO* | Expenses and cash/bank accounts branch-scoped? | Optional BranchId (cash drawers per branch). |
| Q7 *PO* | Confirm migration assumption: all existing users become `CompanyWide` members of the default company, no branch allocations invented. | Yes. |
| Q8 ops | Production PostgreSQL major version. | Needed only if a nullable unique key is introduced. |
| Q9 ops | How does the SWA frontend reach the API (linked backend vs CORS)? Affects the `X-Branch-Id` header (ADR-04). | Confirm in Azure before Phase 2. |
| Q10 | Approve ADR-01…13, the phase sequence (ADR-03) and starting with Phase H. | — |

## 9. Exit criteria check (arch §19 Phase 0)

| Criterion | Status |
|---|---|
| Current behaviour understood | Done — §1, Appendices A/B with file:line evidence |
| Existing data model documented | Done — §2, Appendix A §2 |
| Risks and assumptions listed | Done — §4, §8, Appendix A §6, Appendix B §6 |
| Implementation plan reviewed and approved | Approved by the owner, 2026-11-09 |

## 10. Owner decisions (2026-11-09)

All ADR-01…13 approved. Answers to §8:

| # | Answer | Consequence for the plan |
|---|---|---|
| Q1 | Units are **platform** reference data. | Units get no CompanyId (ADR-06 confirmed). |
| Q2 | Number series **per company + branch**; financial-year reset optional. | `DocumentSequences(CompanyId, BranchId, Series, FinancialYear?)` (ADR-08), introduced in Phase 1, branch key populated from Phase 2. |
| Q3 | GSTIN / invoice prefix **per branch** where the branch has its own GST registration, else company. | Branch gets optional GSTIN + invoice prefix overriding the company's (Phase 2). |
| Q4 | Sales returns **restock to the invoice's location**. | No cross-branch restock in the first release. |
| Q5 | Only **branch-store and vehicle** locations sell. | Warehouses are receive/transfer-only; enforced from Phase 8/10. |
| Q6 | Expenses and cash/bank accounts get an **optional BranchId** (cash drawers per branch). | Phase 2. |
| Q7 | Confirmed, plus **user-branch mapping drives the working branch at login**. | See login rules below. Existing users → mapped to the default "Main" branch as primary; Administrator stays company-wide. |
| Q8 | Use the recommendation. | No nullable unique key needed (ADR-01); confirm prod PG version before Phase 8 as a deployment check. |
| Q9 | **Option B — direct API calls with CORS.** | Phase 2: CORS origins from configuration (`Cors:AllowedOrigins`), `X-Branch-Id` allowed header, production `apiBaseUrl` = absolute App Service URL. Note: today's code uses a relative `/api/v1` and allows only `localhost:4200`, so the SWA and App Service URLs per environment are needed by Phase 2. |
| R1 | **Leave dev auto-migrate as is** (risk accepted for dev). | Base `appsettings.json` unchanged. Must be switched off for any production environment before its first deployment (D10). |
| R4 | `dev` and `claude_dev` APIs use **separate databases**. | No cross-branch migration collision; closed. |

**Login / working-branch rules (Q7):**

| User | Behaviour |
|---|---|
| Mapped to one branch | Works only in that branch; selected automatically; no selector. |
| Mapped to several branches | Starts in the primary branch; can switch among mapped branches from the header. |
| No branch mapping (company-wide: owner, admin, accountant) | May choose any branch; reports may show "All branches". |

### Phase H — delivered rules

| # | Rule | Where |
|---|---|---|
| H1 | Only Administrators assign the Administrator role and edit, disable or reset the password of Administrator accounts (403 otherwise). | `UsersController`, `Authorization/AccessManagementActor.cs` |
| H2 | Nobody changes their own role or disables themselves; with H1 this guarantees at least one active Administrator remains. | `UsersController` |
| H3 | Nobody grants a permission they do not hold — role create, edit (added codes only), duplicate (incl. duplicating Administrator) **and assigning a role to a user** (its permissions must all be held). Caller permissions are read from the database, not the token; a disabled caller holds nothing. | `RolesController`, `UsersController` |
| H3b | All user/role management writes run in one transaction under a single advisory lock, with the caller re-loaded after the lock — two Administrators disabling/demoting each other concurrently cannot both succeed; deactivation cannot race an assignment. | `Authorization/AccessManagementActor.cs` (`AccessManagementScope`) |
| H4 | A role with active users cannot be deactivated; an inactive role grants no permissions; inactive roles cannot be newly assigned. | `RolesController`, `PermissionResolver`, `UsersController` |
| H5 | Already implemented: dashboard sections are gated by `Sales.View` / `Purchase.View` / `Inventory.View` (the audit's defect 5 was a false positive). | `DashboardController` (no change) |
| H6 | `EXP-` numbering takes the per-series advisory lock inside a transaction. | `ExpenseService` |
| H7 | Already implemented: `MigrationTests` asserts no pending model changes and runs in CI. | (no change) |
