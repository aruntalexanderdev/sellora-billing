---
name: ef-core-migrations
description: How Sellora creates, backfills, validates and releases EF Core migrations on PostgreSQL — naming, Id-preserving renames, additive-then-destructive sequencing, backfill SQL, validation queries, rollback and the no-production-auto-migrate rule. Load for any schema or data migration work.
user-invocable: false
---

# EF Core migration workflow

## Conventions (existing, verify in `backend/src/Sellora.Infrastructure/Data/`)
- Migrations: `Data/Migrations/yyyyMMddHHmmss_PascalCaseFeature.cs` + Designer + `SelloraDbContextModelSnapshot.cs`.
- Tables: PascalCase plural, quoted identifiers; constraints `PK_`, `FK_<Table>_<Ref>_<Col>`, `IX_<Table>_<Cols>`, `CK_<Table>_<Rule>`.
- Ids: `uuid`, app-generated. Enums: `HasConversion<string>()`.
- Configuration per aggregate in `Data/Configurations/*Configuration(s).cs`, loaded by `ApplyConfigurationsFromAssembly`.
- Data backfills: `migrationBuilder.Sql(...)` inside the migration (precedent: `AddPurchaseBatchTraceability`, `AddMultiCurrency`).

## Commands (run from repo root)
```bash
dotnet ef migrations add <Name> --project backend/src/Sellora.Infrastructure --startup-project backend/src/Sellora.Api
```
```bash
dotnet ef migrations script <From> <To> --idempotent --project backend/src/Sellora.Infrastructure --startup-project backend/src/Sellora.Api
```
Never run `dotnet ef database update` against a shared or remote database.

## Sequencing rules
1. **Additive** (nullable columns, new tables) → 2. **Backfill** (SQL) → 3. **Enforce** (NOT NULL, unique, CHECK, FK) → 4. **Destructive** (drop old) only in a later release.
- Renames that keep data: `RenameTable`, `RenameColumn`, rename PK/FK/index names explicitly. **Preserve Ids** of rows referenced by history (`ProductSellingModes` → `ProductUnits`).
- Widening `numeric` rewrites the table — note it in the release report.
- Every `Down()` must be meaningful; if a backfill is not reversible, state it and rely on the pre-deploy snapshot.
- Constraints that SQL cannot express (cycles, category rules) belong to the service layer.

## Planned migration set (analysis §17)
M1 `ExtendUnitMaster` · M2 `IntroduceProductUnits` · M3 `BackfillProductUnits` · M4 `EnforceProductUnitConstraints` · M5 `RenameSellingModeReferences` · M6 `AddTransactionUnitSnapshots` · M7 `WidenCostPrecision` · M8 `AddMovementTypesAndReasonCode` · Phase B `AddLocations…`.

## Validation (ship with every data migration)
- Row counts before/after for every touched table (e.g. `SellingPrices`).
- FK integrity on renamed columns.
- Domain checks: one base `ProductUnit` per product; stock invariants from `stock-ledger-invariants`.
- Profiling queries before writing mapping SQL (labels, factors > 3 dp, products without factor-1 mode).

## Release (D10)
- `Program.cs` startup `MigrateAsync()` is allowed only when `Database:ApplyMigrationsOnStartup=true` (dev/local).
- Production: snapshot/branch the database → apply idempotent script → run validation SQL → deploy app → smoke test. Rollback = restore snapshot + previous app build.
