# Phase 3: Stock location foundation (file-level design, DRAFT for owner approval)
Baseline `analyse` @ ad3171b. Sources: arch §8, §9, §10.1, §13.1, §17, §19 Ph3/Ph8; plan §5 ADR-01/02/11/12/13, §10 Q5; phase-1 §8 (INNER JOIN rule); phase-2 D1-D3, D8, D9, §12. Size M-L, one migration, one release.
Verified facts (backend/src): `DefaultTenant` reserves "location ...0003" `Sellora.Domain/Companies/DefaultTenant.cs:5`; Branch AK + composite-FK target `Data/Configurations/BranchConfiguration.cs:13`, code varchar(6) `:15`; navigation-less composite FK pattern `StockAdjustmentConfiguration.cs:28-30`; ICompanyOwned gets FK→Companies + "Company" filter + CompanyId stamping automatically `Data/SelloraDbContext.cs:76-79,87,125-138`; BranchService create in one tx `Services/BranchService.cs:27,69-72`, status `:120-139` (default branch cannot deactivate `:126`); policies auto-derived from the catalogue `Sellora.Api/Authorization/PermissionPolicyProvider.cs:14`; catalogue Branch codes `Sellora.Domain/Permissions/PermissionCatalog.cs:31-35`; seeder `Data/DbSeeder.cs:30,65-71`; DI `Sellora.Api/Program.cs:93`; migration template `Data/Migrations/20261109105800_AddBranches.cs:8-39,124-145,151-163`; T1 test asserts "nothing else has a BranchId" `tests/Sellora.Tests/Infrastructure/CompanyQueryFilterTests.cs:80-100`; migration constants `tests/Sellora.IntegrationTests/TestData/CompanyMigrations.cs:24-30`.

## 1. Scope
Delivers: `StockLocations` table + entity; type/branch CHECKs; AK (CompanyId, Id); exactly one BranchStore per branch; exactly one Transit per company; backfill (store per existing branch, transit per company); BranchService keeps the store in step (create/rename/status); read-only locations API; `StockLocations.View`; pure domain capability rules (sell/receive) for Phase 8/10.
Excludes: any change to StockItems, StockMovements, PurchaseBatches, StockLocks, posting, documents (Phase 8); BatchLocationBalances (Phase 8, ADR-12); Warehouses/Vehicles tables and creation of warehouse/vehicle locations (Phase 4); transfers (Phase 9); "Branch" query filter / allocation scoping (Phase 5); Angular (Phase 11); company creation (Phase 6).

## 2. Decisions
D1 Entity `StockLocation : ICompanyOwned`: Id uuid PK; CompanyId; BranchId uuid NULL; LocationType varchar(20) string enum; Code varchar(20) (upper-case `^[A-Z0-9-]{1,20}$`, service-level, immutable); Name varchar(150); IsActive; CreatedAt, CreatedByUserId?, UpdatedAt?, UpdatedByUserId? (FK→Users Restrict, as Branch). No UnitId, no balances, no IsDefault (ADR-01; the default location = store of the default branch).
D2 Constraints: `AK_StockLocations_CompanyId_Id` (Phase 8 composite FK target for StockItems/BatchLocationBalances/StockMovements/headers); `FK_StockLocations_Branches_CompanyId_BranchId` composite, navigation-less, Restrict, MATCH SIMPLE (NULL branch skips); `IX_StockLocations_CompanyId_Code` UNIQUE; `IX_StockLocations_CompanyId_BranchId` (FK index); `IX_StockLocations_BranchId_BranchStore` UNIQUE (BranchId) WHERE LocationType='BranchStore'; `IX_StockLocations_CompanyId_Transit` UNIQUE (CompanyId) WHERE LocationType='Transit'; CHECKs `CK_StockLocations_LocationType` IN (5 values), `CK_StockLocations_Type_Branch` `("LocationType" IN ('BranchStore','BranchWarehouse')) = ("BranchId" IS NOT NULL)`, `CK_StockLocations_Transit_Active` (`"LocationType" <> 'Transit' OR "IsActive"`), `CK_StockLocations_Id_NotEmpty`. No delete path anywhere; Phase 8 FKs from stock tables make "locations with stock cannot be deleted" structural.
D3 Type rules: BranchStore, BranchWarehouse → BranchId required. CompanyWarehouse, Transit → NULL. Vehicle → NULL: the location is company-owned; branch assignment lives on `Vehicles.AssignedBranchId` (Phase 4, arch §9.2), so reassignment never re-owns stock. Phase 4 adds `Warehouses.StockLocationId` / `Vehicles.StockLocationId` UNIQUE FKs (subtype tables, arch §8 last para) - no polymorphic FK here.
D4 Capabilities are derived from type (no columns): `StockLocationTypeRules` in Domain: `RequiresBranch`, `CanSell` = BranchStore|Vehicle (Q5), `CanReceivePurchase` = BranchStore|CompanyWarehouse|BranchWarehouse (arch §13.1), `IsSystemManaged` = BranchStore|Transit (not editable via locations API). Pure functions, unit-tested now, enforced in Phase 8/10.
D5 Reserved Ids (ADR-11): `DefaultTenant.MainStoreLocationId = ...0003`, new `DefaultTenant.TransitLocationId = ...0004`; other companies `md5('location:store:' || BranchId)::uuid` / `md5('location:transit:' || CompanyId)::uuid`. Store Code = branch Code (≤6 chars, unique per company already), Name = branch Name, IsActive = branch IsActive. Transit Code 'TRANSIT' (7 chars, cannot collide with a ≤6-char branch code), Name 'In transit'.
D6 Transit now, one per company, always active: arch Ph3 "company-level locations where needed" + §8 attribution; per-transfer attribution comes from movement → StockTransfers reference (ADR-02, Phase 9). Avoids a Phase 9 backfill.
D7 BranchService hooks (same transaction/SaveChanges): CreateAsync adds the store (and rejects a branch code already used by any location of the company → 400 `Code`; no-op until Phase 4 adds free-coded locations); UpdateAsync mirrors Name; SetStatusAsync mirrors IsActive (deactivate and reactivate). Default branch cannot deactivate → Main store always active. Phase 8 adds "branch whose store holds stock cannot be deactivated".
D8 API read-only in Phase 3 (all existing types are system-managed). Permission `StockLocations.View` (module "Inventory"), Administrator via PermissionResolver, no grants to other roles (phase-2 D9 precedent). Create/Edit/Disable arrive with their owners in Phase 4 (`Company.Warehouses.*`, `Company.Vehicles.*`).
D9 Branch columns in DTOs via id→branch dictionary (phase-1 §8 / phase-2 D3 rule), never a navigation chain.
D10 Phase 8 pre-build = AK (D2), one store per branch (deterministic working location), reserved Ids (legacy stock/movements map to the default branch store ...0003, phase-2 O-4), capability rules (D4). Must NOT: add LocationId anywhere else, a sellable column, IsDefault, UnitId, or touch locks/posting.

## 3. Files per layer
Domain: `Entities/StockLocation.cs` (new, D1); `Inventory/StockLocationType.cs` (new enum + `StockLocationTypeRules`, D4) ASSUMPTION folder name, else `Entities/`; `Companies/DefaultTenant.cs` (+2 Ids, fix comment); `Permissions/PermissionCatalog.cs` (+`StockLocations.View`) [WP2].
Infrastructure: `Data/Configurations/StockLocationConfiguration.cs` (new, D2, `HasConversion<string>()`); `Data/SelloraDbContext.cs` (`DbSet<StockLocation> StockLocations`); `Data/Migrations/<ts>_AddStockLocations.cs` + Designer + snapshot; `Data/DbSeeder.cs` (`SeedDefaultLocationsAsync` after `SeedDefaultBranchAsync`, InMemory only, idempotent) [WP1]; `Services/BranchService.cs` (D7) [WP2].
Api [WP2]: `Controllers/StockLocationsController.cs` (new); `Contracts/StockLocationContracts.cs` (new).

## 4. Migration `<ts>_AddStockLocations` (ts > 20261109105822; header layout as AddBranches.cs:8-39; one tx)
Guard G5 (Up, before insert): any company without exactly one default branch → RAISE (V4 of B1 re-checked).
Additive: create table + D2 constraints/indexes; review generated migration for redundant per-FK indexes.
Backfill (`ON CONFLICT DO NOTHING`, runs on empty DBs too):
```sql
INSERT INTO "StockLocations" ("Id","CompanyId","BranchId","LocationType","Code","Name","IsActive","CreatedAt")
SELECT CASE WHEN b."Id"='0c0a1f00-0000-4000-8000-000000000002' THEN '0c0a1f00-0000-4000-8000-000000000003'::uuid
            ELSE md5('location:store:'||b."Id"::text)::uuid END,
       b."CompanyId", b."Id", 'BranchStore', b."Code", b."Name", b."IsActive", now()
FROM "Branches" b ON CONFLICT DO NOTHING;
INSERT INTO "StockLocations" ("Id","CompanyId","BranchId","LocationType","Code","Name","IsActive","CreatedAt")
SELECT CASE WHEN c."Id"='0c0a1f00-0000-4000-8000-000000000001' THEN '0c0a1f00-0000-4000-8000-000000000004'::uuid
            ELSE md5('location:transit:'||c."Id"::text)::uuid END,
       c."Id", NULL, 'Transit', 'TRANSIT', 'In transit', true, now()
FROM "Companies" c ON CONFLICT DO NOTHING;
```
In-tx asserts (RAISE): V1-V3 below non-empty; default company present ⇒ ...0003 (BranchId ...0002) and ...0004 exist.
V-block (all must return no rows):
- V1 `SELECT b."Id" FROM "Branches" b WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."BranchId"=b."Id" AND l."LocationType"='BranchStore' AND l."CompanyId"=b."CompanyId" AND l."IsActive"=b."IsActive" AND l."Code"=b."Code") <> 1;`
- V2 `SELECT c."Id" FROM "Companies" c WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId"=c."Id" AND l."LocationType"='Transit' AND l."IsActive") <> 1;`
- V3 `SELECT l."Id" FROM "StockLocations" l JOIN "Branches" b ON b."Id"=l."BranchId" WHERE b."CompanyId"<>l."CompanyId";`
- V4 `SELECT "Id" FROM "StockLocations" WHERE "LocationType" NOT IN ('BranchStore','Transit');` (Phase 3 creates nothing else)
- V5 regression: stock reconciliation SQL (skill stock-ledger-invariants) unchanged vs P-block; P-block: counts of Branches, Companies (expect 1/1 in prod).
Down: guard D-G3 RAISE if any location of another type exists (Phase 4 data; restore snapshot instead); then remove the table. All rows are derived ⇒ reversible (name/status mirror rebuilt by Up). Release: new table only, instant; old API ignores it. Production must add the `StockLocations.View` permission row (seeding runs only where startup seeding runs, phase-2 §12).

## 5. API / DTO contract
- `GET api/v1/stock-locations?type=&branchId=&includeInactive=false` [StockLocations.View] → `StockLocationDto[]` ordered BranchStore of default branch first, then type, Code.
- `GET api/v1/stock-locations/{id}` [StockLocations.View] → 404 for foreign/unknown.
- `StockLocationDto(Guid Id, string Code, string Name, string LocationType, Guid? BranchId, string? BranchCode, string? BranchName, bool IsActive, bool CanSell, bool CanReceivePurchase, bool IsSystemManaged)`.
- `branchId` validated like phase-2 D8: exists in company (inactive allowed) + `IBranchContext.CanAccess`, else 403 `branch_access_denied`; invalid `type` → 400 `type`. Phase 5 narrows the list to allowed branches + company-level locations. No write endpoints; Branch contracts unchanged.

## 6. Work packages (WP2 ∥ WP3 after WP1; no file overlap)
| WP | Owner | Files | Acceptance |
|---|---|---|---|
| WP1 | database-engineer | Domain `Entities/StockLocation.cs`, `StockLocationType.cs` (+rules), `DefaultTenant.cs`; Infrastructure `Data/**` (config, DbContext, migration+Designer+snapshot, DbSeeder) | build green; pending-model-changes clean; Up on empty DB → ...0003 + ...0004; G5/D-G3 guards; constraint/index names per D2; header P/V blocks; no redundant FK index |
| WP2 | backend-engineer | Domain `PermissionCatalog.cs`; Infrastructure `Services/BranchService.cs`; Api `Controllers/StockLocationsController.cs`, `Contracts/StockLocationContracts.cs` | D7-D9 + §5 implemented; no change under Services other than BranchService; no raw SQL; contract handed to lead |
| WP3 | test-engineer | `tests/**` only: `CompanyMigrations.cs` (+B4 const, ids), `Migrations/AddStockLocationsMigrationTests.cs`, `Constraints/StockLocationConstraintTests.cs`, `Sellora.Tests/Inventory/StockLocationTypeRulesTests.cs`, `Branches/BranchServiceTests.cs` additions, `Api/StockLocationsApiTests.cs`, `IsolationCases.cs`, `EndpointAuthorizationTests.cs`, `CompanyQueryFilterTests.cs` (T1 update), `TestData/BranchBuilder.cs` (optional store helper) | T1-T7 pass; `scripts/verify.sh all` green after WP2 lands; no existing test weakened |

## 7. Tests
T1 classification: StockLocation CompanyOwned, BranchId optional (update `CompanyQueryFilterTests.cs:80-100`, which today forbids other BranchId carriers).
T2 rules (pure): CanSell/CanReceivePurchase/RequiresBranch/IsSystemManaged for all 5 types.
T3 BranchService (InMemory + PG): create → store (code/name/active, same tx; PG: failed store insert leaves no branch); rename mirrors; deactivate/reactivate mirrors; code used by a location → 400; default branch store stays active.
T4 constraints (PG): each type with/without BranchId; unknown type string; branch of another company (composite FK); 2nd store per branch; 2nd Transit; inactive Transit; duplicate code per company vs same code in another company; empty Id.
T5 migration (PG): legacy fixture through B3 (+extra active and inactive branch, second company) → Up → V1-V4; reserved and md5 Ids; G5 raise rolls back; D-G3 raise with a Vehicle row; Down → Up; idempotent script twice.
T6 API: list filters (type, branchId, includeInactive), branch columns, capability flags; detail 404 foreign; `branchId` of company B → 403; endpoint policy = StockLocations.View; isolation cases for both routes.
T7 regression: Concurrency/*, StockLedgerReconciliationTests, MigrationTests, UnitRestructuringIdempotentScriptTests unchanged and green.

## 8. Risks
R1 Phase 6 company creation must insert Main + store + Transit (record in Phase 6 design). R2 InMemory fixtures that add branches via `BranchBuilder` have no store; Phase 3 code never assumes one, Phase 8 must (builder helper in WP3). R3 Permission row in production (§4 release). R4 Phase 4 free-coded locations may clash with later branch codes: guarded by D7 check. R5 Store name mirrors branch name; separate store naming would need an edit endpoint (Phase 4).

## 9. Owner questions
O-1 Vehicle locations never carry BranchId (assignment on Vehicles, D3)? Rec: yes. O-2 Create the per-company Transit now (D6) rather than in Phase 9? Rec: yes.

## 10. Deviations
Permission View-only (lead suggested View/Manage): nothing is manageable in Phase 3; write codes come with Phase 4 owners. Transit created in Phase 3 (plan placed it in Phase 9). New reserved Id ...0004. Capabilities derived from type, not stored. No conflict with unit-analysis D1-D10 or ADR-01/02 (no stock shape change).

## Owner approval (2026-11-10)

Design approved, deviations accepted (View-only permission in Phase 3; Transit pulled forward; reserved Id …0004). O-1 yes: Vehicle locations never carry BranchId (assignment on Vehicles in Phase 4). O-2 yes: one Transit location per company created now.

## Delivery record (2026-11-10)

Status: **delivered; review "Approve with fixes" — fixes applied.** Verify: 895 unit / 533 Postgres integration / 192 Vitest, 0 warnings.

| Item | Outcome |
|---|---|
| Review Major 1 | BranchService rename/status now require the branch's store (fail loudly on missing store instead of silently drifting). Test fixtures create a store for every branch, as production does. |
| Review Major 2 | Location list hides branch-owned locations of inaccessible branches (company-level locations always visible); detail returns 404 for them. Effective once Phase 5 restricts members to assigned branches. |
| Minors | Phase 4 requirement recorded: free-coded warehouse/vehicle locations must pre-check `(CompanyId, Code)` (branch codes and `TRANSIT`) and return 400 on `Code`, not a database violation. Down leaves the `StockLocations.View` permission row (harmless). Release: production must add that permission row after deploy (seeding runs only with startup migration). |
| Housekeeping | Test builder `BranchBuilder.Main` → `MainBranch` (CS0028), one xUnit2012 assertion style fix. |
