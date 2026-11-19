# Country and State Masters

status: design-approved
<!-- draft → requirements-approved → design-approved → db-done → backend-done → frontend-done → reviewed → done -->
size: L (two new tables + relinking four existing address owners)

## Problem

State is free text on `Branch.State` and `BusinessSettings.State`; Customer and Supplier carry no country/state at all.
Free text cannot drive GST place-of-supply, filters or reports reliably, and GCC operations need their own subdivisions.

## Scope
**In:**
- Global masters `Countries` and `States` (first-level subdivision: state / union territory / emirate / region / governorate).
- Seed: India (states + UTs with GST state codes) and the six GCC countries with their first-level subdivisions.
- Administration → **Countries**, **States** screens (list, create, edit, activate/deactivate); read-only via a `canEdit` signal when the platform manages reference data (D8).
- Platform console → **Countries**, **States** screens + endpoints (list, create, edit, status) and platform lookup endpoints (active countries; active states by country) for onboarding (D8, D10).
- Platform console → company creation (onboarding) form uses Country → State selects and stops writing free-text State (D10).
- Country → State selection on every address-owning page: **Branch, Business Settings, Customer, Supplier**.
- Backfill existing `Branch.State` / `BusinessSettings.State` text to `StateId`, then a GSTIN-prefix pass (D9, D11, D12) incl. Suppliers from their own GSTIN (D13), with unmatched / GSTIN-conflict reports.

**Out:**
- GST place-of-supply / IGST-vs-CGST+SGST logic on invoices (separate change; this feature only provides the data).
- Districts, cities, postal codes as masters.
- Dropping the legacy free-text `State` columns (later, destructive, separately approved).

## Business rules
- BR1 Masters are **global** (no `CompanyId`), like `Currency`. View: `Masters.View`; create/edit/deactivate: `Masters.Edit`, subject to the reference-data guard (D8): company writes only when the platform allows company edits (single-tenant); otherwise company screens are read-only and edits go through the Platform console.
- BR2 Country `Code` = ISO 3166-1 alpha-2, unique, uppercase. Name unique (case-insensitive).
- BR3 State `Code` unique per country (ISO 3166-2 suffix, e.g. `KL`, `DU`); Name unique per country (case-insensitive).
- BR4 `GstStateCode` (2 digits) only for India, unique within India when present.
- BR5 Never delete; deactivate. Inactive country/state cannot be newly selected; an unchanged existing link survives an edit.
- BR6 A state cannot be active under an inactive country; deactivating a country does not cascade-edit states but hides them from pickers.
- BR7 Selected `StateId` must belong to the selected `CountryId` (server-validated).
- BR8 Country/State on Branch, Business Settings, Customer, Supplier are optional (existing records may have none).

## Edge cases
- Existing free text that matches no state (typos, "Kerala " vs "KERALA", abbreviations) → left unlinked, listed by validation SQL; text kept.
- Text matching a state in more than one country → not auto-linked.
- Global edits affect every company → only `Masters.Edit` holders; edits audited.

## Acceptance criteria
- AC1 Given a fresh database, When migrations run, Then India (36 states/UTs with GST codes) and AE, SA, QA, KW, BH, OM with subdivisions exist and are active.
- AC2 Given `Masters.Edit`, When I add a state with a duplicate name in the same country, Then 400 on `name`.
- AC3 Given a branch with State "Kerala", When migration runs, Then its `StateId` = Kerala and `CountryId` = India.
- AC4 Given the Branch form, When I pick a country, Then the State dropdown lists only that country's active states.
- AC5 Given a state from another country posted with a country, Then 400 on `stateId`.
- AC6 Given an inactive state already on a customer, When the customer is edited without changing it, Then save succeeds.
- AC7 Given only `Masters.View`, Then screens are read-only and write endpoints return 403.
- AC8 (D8) Given the platform does not allow company edits, When a company `Masters.Edit` holder opens Countries/States, Then the screens are read-only (driven by a `canEdit` signal) and company write endpoints return 403; a platform administrator can create/edit/deactivate via the Platform console.
- AC9 (D9) Given a Branch with blank State text and an Indian GSTIN (strict format) of its own or, when its own is NULL, of the company, When the migration runs, Then StateId/CountryId come from the GSTIN prefix; given text 'Kerala' and GSTIN 33…, Then the row links to Kerala and appears in the GSTIN-conflict report.
- AC10 (D10) Given the Platform console company-creation form, When a company is created with Country/State, Then CountryId/StateId are stored and the free-text State is not written; an inactive state or a state of another country gives 400.

## Impact
DB: `Countries`, `States` (new); nullable `CountryId`, `StateId` on `Branches`, `BusinessSettings`, `Customers`, `Suppliers` (additive) ·
API: `/api/v1/countries`, `/api/v1/states` (+ `canEdit`), lookup endpoints; platform Countries/States endpoints + platform lookups; branch/settings/customer/supplier/onboarding DTOs gain `countryId`, `stateId` (+ names) ·
UI: 2 Administration screens + 2 Platform-console screens; Country/State selects in 5 forms (Branch, Business Settings, Customer, Supplier, onboarding); list columns where useful ·
Permissions: reuse `Masters.View` / `Masters.Edit` · Reports: none in this feature

## Open questions
_None. Earlier Q1/Q2 (spec draft) → D6/D7; design questions Q1/Q2/Q3 → D8/D9/D10; GSTIN refinements → D11–D13._

## Database design
Status: final design for approval (database-engineer, 2026-11-19). Applies owner decisions D8 (guard, Platform console scope), D9 (GSTIN fallback), D10 (onboarding), D11 (strict GSTIN), D12 (inherited company GSTIN only for blank text), D13 (supplier linking) and every architecture, seed and round-2 verifier finding. Design only: no code or migration exists yet. Migration name: `AddCountryStateMasters`.

### 1. Tables

Template: `CurrencyConfigurations.cs:7-17`. Both tables are global (no `ICompanyOwned`, no CompanyId, so tenant filters do not apply). New files `CountryConfiguration.cs`, `StateConfiguration.cs`; DbSets `Countries`, `States`. CHECKs are declared in the configuration as in `UnitConfiguration.cs:23-32`. Enums use `HasConversion<string>()`. No `CreatedByCompanyId` column (D8).

**`Countries`**

| Column | Type | Null | Notes |
|---|---|---|---|
| Id | uuid | no | PK `PK_Countries`; seeded rows use deterministic ids (section 2) |
| Code | varchar(2) | no | ISO alpha-2, upper case |
| Iso3Code | varchar(3) | yes | optional |
| Name | varchar(100) | no | |
| IsActive | boolean | no | no DB default (matches Currency; avoids the EF bool-sentinel trap) |
| IsSystem | boolean | no | no DB default; seeded rows true |

- `IX_Countries_Code` unique (EF-modelled, `HasIndex(...).IsUnique()`).
- `UX_Countries_Name_Lower` unique on `lower("Name")`: raw SQL in the migration, with a config comment pointing to it as at `ProductCategoryConfiguration.cs:18`. Naming rule: IX_ for EF-modelled indexes, UX_ only for raw `lower()` indexes.
- `CK_Countries_Code_Format`: `"Code" ~ '^[A-Z]{2}$'`. `CK_Countries_Iso3Code_Format`: `"Iso3Code" IS NULL OR "Iso3Code" ~ '^[A-Z]{3}$'`.

**`States`** (first-level subdivision)

| Column | Type | Null | Notes |
|---|---|---|---|
| Id | uuid | no | PK `PK_States` |
| CountryId | uuid | no | FK `FK_States_Countries_CountryId`, ON DELETE RESTRICT |
| Code | varchar(3) | no | ISO 3166-2 suffix, text (SA and BH codes are numeric with leading zeros) |
| Name | varchar(100) | no | |
| Kind | varchar(20) | no | string enum State, UnionTerritory, Emirate, Region, Governorate, Municipality; display only; no DB default (seed and service set it) |
| GstStateCode | varchar(2) | yes | India only |
| IsActive | boolean | no | no DB default |
| IsSystem | boolean | no | no DB default |

- `IX_States_CountryId_Code` unique (EF-modelled; also the ON CONFLICT target of the seed).
- `UX_States_CountryId_Name_Lower` unique on `("CountryId", lower("Name"))`: raw SQL.
- `IX_States_GstStateCode` unique, filtered `"GstStateCode" IS NOT NULL` (EF-modelled, precedent `RoleConfiguration.cs:28`).
- `AK_States_CountryId_Id`: alternate key `(CountryId, Id)`, target of the composite FKs in section 3.
- `CK_States_Code_Format` `"Code" ~ '^[A-Z0-9]{1,3}$'`; `CK_States_GstStateCode_Format` `"GstStateCode" IS NULL OR "GstStateCode" ~ '^[0-9]{2}$'`; `CK_States_Kind` Kind IN the six values.

**State.CountryId is immutable for EVERY row** (it is part of the alternate key; EF throws on change, which would be a 500). The update contract has no `countryId`; a service that receives a different value answers 400 `countryId` before touching EF. To "move" a state: deactivate it and create a new one.

**BR4 (GstStateCode India only)** cannot be a CHECK (cross-table). Enforced in the service and reported by `V__country-state-geo-rules.sql`. A global partial unique index is equivalent to "unique within India" while only IN states carry a code.

**Audit and guard (D8).** Same as Currencies: reuse the reference-data guard (`Platform:ReferenceDataEditableByCompanies`, the `CurrencyService` `EnsureMayWrite` pattern, `CurrencyService.cs:80-84`). Company `Masters.Edit` holders write only when the platform allows company edits (single-tenant); otherwise company screens are read-only and edits go through the Platform console (`PlatformReferenceDataController` pattern, `:16`). Edits are audited to `PlatformAuditEvents` (CompanyId is nullable, `PlatformAuditEvent.cs:20`; ActorUserId FK to Users) with new `PlatformAuditActions` / `PlatformAuditTargets` constants for Country and State. Actor design in section 6.

### 2. Ids and seed delivery

- Ids: `md5('sellora:country:' || Code)::uuid` and `md5('sellora:state:' || cc || '-' || Code)::uuid`. Same value as `DbSeeder.Md5Guid` (`DbSeeder.cs:253`, verified). FKs are resolved by Code in SQL, never by literal id.
- Delivery stays **inside the EF migration** (`migrationBuilder.Sql`, `ON CONFLICT DO NOTHING`), before the backfill, per the INR precedent (`AddMultiCurrency.cs:16-18,45`). No `R__` seed script (EF migrations run before repeatables, `db-release-automation.md:178`). The seed SQL and the backfill SQL live as frozen embedded resources next to the migration (`Data/Migrations/Sql/<timestamp>_AddCountryStateMasters.seed.sql` and `.backfill.sql`; csproj item, helper, SHA-256 pin test and `check-db-scripts.sh` extension in section 7); the migration never calls mutable app code. Tests replay the same resource text on probe rows.
- InMemory tests and dev do not run migrations: a shared static `GeoSeedData` (Infrastructure) holds the 7 countries, 85 states and the alias map (Appendix B), used by `DbSeeder` (lookup then insert, Md5 ids) and by tests. Its `Normalize` is the C# twin of `pg_temp.geo_norm` (section 7, same explicit escaped whitespace class without `\s`). `GeoSeedData.Aliases` is a list of `(alias, target)` pairs generated 1:1 from Appendix B, which is deduplicated after normalisation; the drift test asserts that no normalised alias appears twice or maps to two different states, and none equals a state name or ISO code. Demo data at `DbSeeder.cs:324-331`, `:403-421`, `:550-567` sets Country/State **per row**: IN-TN only for rows with a `33`-prefixed GSTIN or a Chennai/Tamil Nadu address; the USD supplier (`Global Ingredients Inc.`, Chicago, `DbSeeder.cs:408`) and the walk-in customer stay NULL.
- **Future additions** go in a dated data script `yyyy.MM.dd.nnn__add-<x>-states.sql` (additions only, existing scripts never edited, per `check-db-scripts.sh`). Use `ON CONFLICT DO NOTHING` without a conflict target, or pre-check name collisions, because a company-created row with the same name would otherwise violate `UX_..._Name_Lower` and block the deploy; report skipped rows. When subdivisions are added, `GeoSeedData` and Appendix A are updated in the same change; `V__country-state-seed-complete.sql` uses `count < expected`, so adding rows never fails the release, and its expected numbers are raised only when a removal-sensitive guarantee is wanted.
- **Drift test** (Postgres integration): runs the migrator first (EF migrations plus `backend/db/scripts` data scripts, as `MigrationRunnerTests` does), then `GeoSeedData` must equal the resulting rows (migration seed plus data-script additions: ids, codes, names, kinds, GST codes, IsSystem) and every Appendix B alias resolves as listed. The first future data-script addition therefore does not break it.
- **Source verification note:** iso.org and wikipedia were unreachable from the sandbox. The seed was verified by two independent agents against multiple secondary sources (Wikipedia mirrors, Statoids, OFBiz, GST code lists). Final primary-source confirmation (iso.org OBP for ISO 3166-1/-2, GST portal MasterCodes) is a **release-checklist item** before the production deploy. Wrong codes would be hard to fix later because ISO identity is locked on `IsSystem` rows.

### 3. Relink columns

Tables: `Branches`, `BusinessSettings`, `Customers`, `Suppliers`. Each gets nullable `CountryId uuid` and `StateId uuid` (BR8).

- `FK_<T>_Countries_CountryId` (CountryId) -> Countries(Id), RESTRICT.
- `FK_<T>_States_CountryId_StateId` composite (CountryId, StateId) -> States(CountryId, Id), RESTRICT. EF: `HasOne<State>().WithMany().HasForeignKey(x => new { x.CountryId, x.StateId }).HasPrincipalKey(s => new { s.CountryId, s.Id })` (precedent `ExpenseConfigurations.cs:39`, `RoleConfiguration.cs:22`). MATCH SIMPLE skips the check when StateId is NULL, so country-only rows are valid.
- `CK_<T>_State_Requires_Country`: `"StateId" IS NULL OR "CountryId" IS NOT NULL` (required: without it a StateId with NULL CountryId would skip the FK).
- `IX_<T>_CountryId_StateId` (prefix also serves the country FK).
- The composite FK makes BR7 impossible to violate even via raw SQL or backfill. The DB error is an opaque 23503, so the service validates first and returns 400 on `stateId` (AC5). Rows are never deleted (RESTRICT, BR5).
- Positional-record breakage is avoided by adding the new record parameters at the end, with defaults.

### 4. Legacy text

- `Branch.State` and `BusinessSettings.State` are kept (D6) and **frozen**: no new code writes them, including company onboarding (D10). The DTO returns them read-only as the hint while StateId is null.
- Customers and Suppliers have no State or Country text (`Customer.cs:16`, `Supplier.cs:11`): no text backfill. Suppliers have a `Gstin` (`Supplier.cs:12`) and are linked from their own strict-format GSTIN (D13, section 7); Customers have no GSTIN and are not backfilled.

### 5. History and snapshots

- No document stores an address or state (`SalesInvoices` and `PurchaseOrders` hold only CustomerId and SupplierId; seller GSTIN is read live, `SalesController.cs:222-227`). Impact on posted documents: none; the new columns appear on no document.
- Out of scope: the seller GSTIN snapshot (approved `phase-2-design.md:195,210`, never delivered) and any state snapshot. Constraint for the future place-of-supply change: snapshot seller and buyer StateId on the invoice at posting. Also fix the misleading comment at `frontend/src/app/features/sales/sales.models.ts:154` (frontend-engineer).

### 6. Write-path rules (service layer)

New `CountryService` / `StateService` patterned on `CurrencyService`, plus platform-console variants through `PlatformReferenceDataController`.

1. **Guard (D8):** `EnsureMayWrite(actor)` identical to Currencies. Company actor allowed only when `Platform:ReferenceDataEditableByCompanies` is true; otherwise 403. Platform actor always allowed.
2. **Audit actor never anonymous:** `ReferenceDataActor` (`ICurrencyService.cs:7-11`) already has `UserId`; only `CompanyId` is new. `UserId` comes from the `NameIdentifier`/`sub` claim; `CompanyId` = `ICompanyContext.RequireCompanyId()` (validated against membership by `CompanyContextMiddleware.cs:62-87`, never a raw claim); the platform actor has `CompanyId` NULL. The new services audit BOTH actor kinds: do NOT copy `CurrencyService.Audit`'s early return (`CurrencyService.cs:88`, `if (!actor.IsPlatform) return;`), which would skip company audits. They throw when `UserId` is missing, or when `CompanyId` is missing for a company actor. `PlatformAuditLog.Append` writes ActorUserId and CompanyId. Tests: section 10 (company edit records both ids; platform edit records ActorUserId with CompanyId NULL; missing ids throw and write nothing). Currency/Unit behaviour is not changed by this feature (follow-up for backend-engineer).
3. **IsSystem rows:** Code, CountryId and GstStateCode are immutable (400 on that field). Name and IsActive remain editable (subject to the guard). No delete anywhere.
4. **BR2/BR3 friendly errors:** duplicate Code or Name (case-insensitive, within country for states) -> 400 on `code` / `name` before the unique index fires (AC2).
5. **BR4:** GstStateCode accepted only when the state's country is IN; 2 digits; unique.
6. **BR5:** inactive country/state cannot be newly selected; an unchanged existing link survives an edit (pattern `SuppliersController.ValidateCurrencyAsync`, `:113-125`; AC6).
7. **BR6:** a state cannot be activated or created active under an inactive country (400 `isActive`). Deactivating a country does not cascade; pickers hide its states.
8. **BR7:** `stateId` must belong to `countryId` (400 `stateId`); `stateId` without `countryId` is rejected (400 `countryId`).
9. **State.CountryId immutable**, no `countryId` in the update contract (section 1).
10. **D10 onboarding:** `PlatformService` company creation (`PlatformService.cs:242-247`) and the platform form take `countryId` / `stateId` (Country -> State dropdowns, same BR5/BR7 validation), write them to the new company's BusinessSettings, and stop writing the free-text `State`.
11. **D7:** new Customer/Supplier default to the BusinessSettings country (UI default; server stores what is posted).
12. **Read-only company screens (D8) need a mandatory signal:** no endpoint exposes `ReferenceDataEditableByCompanies` today (the Currency precedent keeps screens editable and only maps the 403, `frontend/.../api-errors.ts:27`, which does NOT meet D8). The list responses of the company endpoints `/api/v1/countries` and `/api/v1/states` carry a mandatory `canEdit` = flag value for company callers (always true on the platform console); the frontend hides every write action when it is false and write endpoints answer 403.
13. **Platform console (D8 scope):** with the default `ReferenceDataEditableByCompanies=false` (`appsettings.json:19`) only the Platform console can write, so it gets Countries and States list/create/edit/status endpoints (`PlatformReferenceDataController` pattern) and screens (precedent: Currencies, `platform-layout.ts:17`), plus platform GET lookups for the D10 onboarding form (active countries; active states by country; a platform session has no company context, precedent `platform-company-form.ts:95`). Endpoint list: section 11.

### 7. Migration plan and exact backfill SQL

One migration, additive only. After `dotnet ef migrations add AddCountryStateMasters`, hand-reorder the generated steps: `Branches` and `BusinessSettings` last (lock rationale: section 9):

0. `migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';")` as the very first operation (valid inside the `DO $EF$` wrapper too; rationale and failure behaviour: section 9).
1. `CreateTable` Countries, States (PK, EF indexes, AK, CHECKs); raw `UX_Countries_Name_Lower`, `UX_States_CountryId_Name_Lower`.
2. Seed SQL (`.seed.sql`): 7 countries, 85 states (Appendix A).
3. `Customers` then `Suppliers`: `AddColumn` CountryId, StateId; index; FK; CK. Plain EF `AddForeignKey` / `AddCheckConstraint` (no raw `NOT VALID` SQL, so the model snapshot stays authoritative).
4. `Branches` then `BusinessSettings`: `AddColumn` CountryId, StateId.
5. Backfill SQL (`.backfill.sql`): ONE `migrationBuilder.Sql` call containing everything below (function, temp tables, Branches passes, BusinessSettings passes, Suppliers pass). A single call keeps the temp objects together; the text must not contain the tag `$EF$` (the function uses `$f$`). Never set `suppressTransaction` on this call: `ON COMMIT DROP` temp tables only exist inside a transaction.
6. Index, FK, CK on Branches and BusinessSettings (last).

**Embedded resources (new to this repo, no precedent).** The work package adds to `Sellora.Infrastructure.csproj` an `<EmbeddedResource Include="Data/Migrations/Sql/*.sql" />` item and a small helper (`MigrationSql.Read(name)`, `GetManifestResourceStream` by logical name). The files are frozen: a test pins the SHA-256 of each resource (section 10), and the repo-root `.gitattributes` gets `backend/src/Sellora.Infrastructure/Data/Migrations/Sql/*.sql text eol=lf` (the repo has none today), so a checkout with `core.autocrlf` cannot change the embedded bytes or the hash; the pin test also asserts the resources contain no `\r`; `scripts/check-db-scripts.sh` only guards `backend/db/scripts`, so it is extended to reject modifications (not additions) under `Data/Migrations/Sql`.

`Down()` (dev-only, section 9): drop CKs, FKs, indexes on the 4 tables, drop the 8 columns, drop States, then Countries (raw indexes go with the tables). Links and any post-deploy edits to the masters are lost on Down; legacy text is untouched, so the backfill can be re-derived by re-running `.backfill.sql`, which must be wrapped in `BEGIN/COMMIT` (or `psql -1`): in autocommit the `ON COMMIT DROP` temp tables vanish after the first statement and the script fails with `relation "_geo_alias" does not exist`. If the new app version has run, prefer the app-only rollback or the pre-deploy snapshot (section 9).

**Rules**
- Normaliser (`pg_temp.geo_norm`, twin of C# `GeoSeedData.Normalize`): lower-case; `&` -> ` and `; `-`, `_`, `/`, `,` -> space; remove `. ' ’ ‘ \``; collapse whitespace; `btrim(...)` (SQL) / `Trim(' ')` (C#) after the collapse; empty -> NULL. "Whitespace" is, in BOTH SQL and C#, the explicit class `[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]` written as escapes (never literal invisible characters, which editors lose, and never `\s`, whose meaning depends on the PG locale ctype and differs in .NET; `btrim`/`Trim()` only trim ASCII space after the collapse). PG ARE supports `\uXXXX` inside brackets with `standard_conforming_strings` on (verified on PG16 under C and C.UTF-8: identical folded set; the Testcontainers image is musl-based). Applied to state names, aliases, ISO-code forms and input alike (so `Ras Al-Khaimah` = `Ras Al Khaimah`, `Mubarak Al Kabeer` = `Mubarak Al-Kabeer`, `32-Kerala` = `32 kerala`). Parity is tested with ASCII-letter inputs plus every whitespace code point class above (section 10).
- **Blank State text (one definition everywhere, D12):** text is blank when it is empty after removing ONLY the whitespace class above (no punctuation): `regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') = ''`. Written inline (no `pg_temp`) so V__ files can use it. Punctuation-only text such as `-` or `.` is NOT blank: it gets no inherited company-GSTIN fallback, stays unlinked and is reported.
- Tried forms of the input: as is; leading 2-digit stripped (`32 kerala`); trailing 2-digit with optional parentheses stripped (`kerala 32`, `kerala (32)`); and, applied after the number strip so the forms combine (`kerala state (32)`), suffix-stripped forms (` state` -> IN; ` emirate` -> AE; ` region`, ` province` -> SA; ` governorate` -> KW, BH, OM; ` municipality` -> QA). **A suffix-stripped form may match only candidates of the country that suffix belongs to** (so `Northern Region` cannot reach Bahrain). **A stripped number is checked:** when it is a seeded GstStateCode (or legacy `25`) and the matched state is Indian, it must equal that state's GstStateCode (`25` for IN-DH, which also accepts its own code); otherwise the row stays unlinked and reaches the unmatched report (`32-Tamil Nadu` does not link).
- **Deny-list** (never matched from any form or candidate, rows go to the unmatched report): `capital`, `capital governorate`, `al asimah`, `asimah`, `northern`, `southern`, `eastern`, `western`, `central`, `sharqiyah`, `ash sharqiyah`, `batinah`, `al batinah`, `ncr`, `delhi ncr`. Bare `ut`, `or`, `ncr` are not aliases. Consequence: exact `Northern` (BH-17), `Southern` (BH-14), `Capital` (KW-KU, BH-13) never auto-link; explicit full phrases in Appendix B (e.g. `Northern Governorate`, `Eastern Region`) do.
- **Single rule for abbreviations and bare numbers:** two-letter codes (`TN`), full ISO forms (`IN-TN`, plus old `IN-OR`, `IN-CT`, `IN-TG`, `IN-UT`, `IN-UL`, `IN-DD`, `IN-DN`) and bare 2-digit GST numbers (`33`) link **only with India context**. India context = a valid GSTIN whose prefix is a seeded GstStateCode or legacy `25`. **Valid GSTIN (D11, final):** the strict official format `^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$` after `upper(btrim())` (2-digit state code + PAN + entity digit + `Z` + checksum character). All-digit Gulf tax numbers (UAE TRN `100...`, Saudi VAT `3...`, Bahrain VAT `2...`) can never match, so they are never Indian. The same regex is used in the context check, both passes, the Suppliers pass, `V__country-state-gstin-conflicts` and the C# copy (`GstinFormat`). The GSTIN is the row's own; for Branches with NULL/blank Gstin it is the company GSTIN (`BusinessSettings.Gstin` by CompanyId) as India context (D9) and, only for blank text, as the pass-2 source (D12). Legacy `25` maps to IN-DH (it is never seeded; accepted as a prefix).
- A candidate set resolving to more than one state is never linked. CountryId comes from the matched state (AC3). Text match always wins: pass 2 never touches a linked row and conflicts are reported, never overwritten.
- Pass 2 (D9 GSTIN fallback): rows still unlinked (`StateId IS NULL AND CountryId IS NULL`) whose GSTIN is valid (D11) with a seeded or legacy prefix get StateId from the prefix and CountryId = IN. **Inherited company GSTIN (D12):** a Branch with NULL/blank Gstin uses the company GSTIN in pass 2 only when its legacy text is blank; with non-blank unmatched text (`Deira`, `Capital`, `Sohar`) it stays unlinked and is reported, so Gulf branches of an Indian company are not silently turned into Indian states. The inherited GSTIN still provides India context in pass 1 (D9). **Suppliers (D13):** own strict-format GSTIN only, no company fallback, `StateId` and `CountryId` both NULL, CountryId = IN; suppliers without a valid GSTIN stay unlinked. Order: Branches, BusinessSettings, Suppliers. Customers have no GSTIN and are not backfilled.
- The text-vs-state-name differences (GSTIN-derived links, links made under inherited India context such as bare `13`, alias links) are reviewed once through the manual owner report (section 8), not a V__ file.

**Backfill SQL** (single embedded resource; the alias INSERT is generated 1:1 from Appendix B; `Ha'il` must be written `Ha''il` in the seed):
```sql
CREATE OR REPLACE FUNCTION pg_temp.geo_norm(t text) RETURNS text LANGUAGE sql IMMUTABLE AS $f$
  SELECT nullif(btrim(regexp_replace(regexp_replace(regexp_replace(
    replace(lower(coalesce(t,'')),'&',' and '),
    '[-_/,]',' ','g'), '[.''’‘`]','','g'),
    '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+',' ','g')),'') $f$;

CREATE TEMP TABLE _geo_alias(alias text, cc text, scode text, india_only boolean) ON COMMIT DROP;
INSERT INTO _geo_alias(alias,cc,scode,india_only) VALUES
  ('tamilnadu','IN','TN',false), ('ct','IN','CG',true), ('pondicherry','IN','PY',false) /* ...every row of Appendix B */;
UPDATE _geo_alias SET alias = pg_temp.geo_norm(alias);

CREATE TEMP TABLE _geo_deny(norm text) ON COMMIT DROP;
INSERT INTO _geo_deny VALUES ('capital'),('capital governorate'),('al asimah'),('asimah'),('northern'),('southern'),
  ('eastern'),('western'),('central'),('sharqiyah'),('ash sharqiyah'),('batinah'),('al batinah'),('ncr'),('delhi ncr');

CREATE TEMP TABLE _geo_suffix(suffix text, cc text) ON COMMIT DROP;
INSERT INTO _geo_suffix VALUES ('state','IN'),('emirate','AE'),('region','SA'),('province','SA'),
  ('governorate','KW'),('governorate','BH'),('governorate','OM'),('municipality','QA');

CREATE TEMP TABLE _geo_cand(norm text, cc text, "StateId" uuid, india_only boolean) ON COMMIT DROP;
INSERT INTO _geo_cand
  SELECT pg_temp.geo_norm(s."Name"), c."Code", s."Id", false FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId"
  UNION ALL SELECT a.alias, a.cc, s."Id", a.india_only FROM _geo_alias a
            JOIN "Countries" c ON c."Code"=a.cc JOIN "States" s ON s."CountryId"=c."Id" AND s."Code"=a.scode
  UNION ALL SELECT pg_temp.geo_norm('IN-'||s."Code"), 'IN', s."Id", true FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId" WHERE c."Code"='IN'
  UNION ALL SELECT pg_temp.geo_norm(s."Code"), 'IN', s."Id", true FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId" WHERE c."Code"='IN'
  UNION ALL SELECT s."GstStateCode", 'IN', s."Id", true FROM "States" s WHERE s."GstStateCode" IS NOT NULL
  UNION ALL SELECT o.code, 'IN', s."Id", true FROM (VALUES ('in or','OD'),('in ct','CG'),('in tg','TS'),('in ut','UK'),('in ul','UK'),
            ('in dd','DH'),('in dn','DH'),('25','DH')) o(code,sc)
            JOIN "States" s ON s."Code"=o.sc AND s."CountryId"=(SELECT "Id" FROM "Countries" WHERE "Code"='IN');
DELETE FROM _geo_cand WHERE norm IN (SELECT norm FROM _geo_deny);

CREATE TEMP TABLE _geo_gst(prefix text, "StateId" uuid) ON COMMIT DROP;   -- GSTIN prefix -> state
INSERT INTO _geo_gst
  SELECT s."GstStateCode", s."Id" FROM "States" s WHERE s."GstStateCode" IS NOT NULL
  UNION ALL SELECT '25', s."Id" FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId" WHERE c."Code"='IN' AND s."Code"='DH';

-- ===== Branches: pass 1 (text) =====
WITH src AS (
  SELECT b."Id", pg_temp.geo_norm(b."State") AS n0, x.gstin
  FROM "Branches" b
  CROSS JOIN LATERAL (SELECT upper(btrim(coalesce(nullif(btrim(b."Gstin"),''),
          (SELECT bs."Gstin" FROM "BusinessSettings" bs WHERE bs."CompanyId"=b."CompanyId" ORDER BY bs."Id" LIMIT 1)))) AS gstin) x
  WHERE b."StateId" IS NULL AND pg_temp.geo_norm(b."State") IS NOT NULL),
ctx AS (
  SELECT "Id", n0, coalesce(gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'
                            AND substr(gstin,1,2) IN (SELECT prefix FROM _geo_gst), false) AS india_ctx FROM src),
b0 AS (   -- number-strip forms; num = the stripped 2-digit number
  SELECT "Id", india_ctx, n0 AS form, NULL::text AS num FROM ctx
  UNION ALL SELECT "Id", india_ctx, regexp_replace(n0,'^\d{2} ',''), substring(n0 from '^(\d{2}) ') FROM ctx WHERE n0 ~ '^\d{2} '
  UNION ALL SELECT "Id", india_ctx, regexp_replace(n0,' \(?\d{2}\)?$',''), substring(n0 from ' \(?(\d{2})\)?$') FROM ctx WHERE n0 ~ ' \(?\d{2}\)?$'),
forms AS (   -- suffix strip is applied after the number strip so the forms combine
  SELECT "Id", india_ctx, form, NULL::text AS cc, num FROM b0
  UNION ALL SELECT b0."Id", b0.india_ctx, left(b0.form, length(b0.form)-length(x.suffix)-1), x.cc, b0.num
            FROM b0 JOIN _geo_suffix x ON b0.form LIKE '% '||x.suffix),
hits AS (
  SELECT DISTINCT f."Id", c."StateId" FROM forms f
  JOIN _geo_cand c ON c.norm = f.form AND (f.cc IS NULL OR c.cc = f.cc) AND (NOT c.india_only OR f.india_ctx)
  JOIN "States" cs ON cs."Id" = c."StateId"
  WHERE f.form <> ''
    AND (f.num IS NULL OR c.cc <> 'IN' OR f.num NOT IN (SELECT prefix FROM _geo_gst)
         OR f.num = cs."GstStateCode" OR (f.num = '25' AND cs."Code" = 'DH'))),
uniq AS (
  SELECT "Id", (array_agg("StateId"))[1] AS "StateId" FROM hits GROUP BY "Id" HAVING count(DISTINCT "StateId") = 1)
UPDATE "Branches" b SET "StateId" = u."StateId", "CountryId" = st."CountryId"
FROM uniq u JOIN "States" st ON st."Id" = u."StateId" WHERE b."Id" = u."Id";

-- ===== Branches: pass 2 (GSTIN prefix; never overwrites a linked row) =====
-- own GSTIN always; the inherited company GSTIN only when the branch text is blank (D12)
WITH src AS (
  SELECT b."Id", upper(btrim(CASE
            WHEN nullif(btrim(b."Gstin"),'') IS NOT NULL THEN b."Gstin"
            WHEN regexp_replace(coalesce(b."State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') = '' THEN
              (SELECT bs."Gstin" FROM "BusinessSettings" bs WHERE bs."CompanyId"=b."CompanyId" ORDER BY bs."Id" LIMIT 1) END)) AS gstin
  FROM "Branches" b WHERE b."StateId" IS NULL AND b."CountryId" IS NULL)
UPDATE "Branches" b SET "StateId" = g."StateId", "CountryId" = st."CountryId"
FROM src s JOIN _geo_gst g ON g.prefix = substr(s.gstin,1,2) JOIN "States" st ON st."Id" = g."StateId"
WHERE b."Id" = s."Id" AND s.gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$';

-- ===== BusinessSettings and Suppliers =====
-- BusinessSettings: repeat the two WITH..UPDATE statements with "Branches" -> "BusinessSettings";
--   src.gstin = upper(btrim(b."Gstin")) (own GSTIN only, no coalesce, no BusinessSettings join).
-- Suppliers (D13): pass 2 ONLY (no legacy text exists): "Branches" -> "Suppliers", own GSTIN only; its WHERE has no text condition.
-- Order: Branches pass 1, Branches pass 2, BusinessSettings pass 1, BusinessSettings pass 2, Suppliers pass 2.
```
The function and all temp tables are created exactly once. `BusinessSettings` PK is `"Id"` (`BusinessSettings.cs:10`, verified).

### 8. Validation SQL files (exact)

Five new files in `backend/db/validation/`; a returned row is a violation; added files satisfy `check-db-scripts.sh`. **Every file starts with exactly these two header lines** (mode default is block in prod and warn elsewhere when the header is missing, `_deploy-template.yml:336`, `MigrationRunner.cs:228`, so the header is always explicit):
```sql
-- sellora:description <one-line description>
-- sellora:on-violation block|warn
```
Modes: `seed-complete` block, `consistency` block, `geo-rules` warn, `unmatched-legacy` warn, `gstin-conflicts` warn. Every column is table-qualified wherever two relations are joined (an unqualified `"Id"` raises 42702, which the migrator records as Failed and blocks the deploy, `MigrationRunner.cs:125-130,233-236`). Each file is executed against probe rows in the test plan (section 10).

`V__country-state-seed-complete.sql` (`-- sellora:on-violation block`; minimum expected, so additions never fail):
```sql
SELECT e.cc, e.n AS expected, count(s."Id") AS actual
FROM (VALUES ('IN',36),('AE',7),('SA',13),('QA',8),('KW',6),('BH',4),('OM',11)) e(cc,n)
LEFT JOIN "Countries" c ON c."Code"=e.cc AND c."IsSystem"
LEFT JOIN "States" s ON s."CountryId"=c."Id" AND s."IsSystem"
GROUP BY e.cc, e.n, c."Id" HAVING c."Id" IS NULL OR count(s."Id") < e.n;
```
`V__country-state-consistency.sql` (`-- sellora:on-violation block`; safety net, structurally 0 with the composite FK):
```sql
SELECT x.t, x."Id" FROM (
  SELECT 'Branches' AS t, "Id", "StateId", "CountryId" FROM "Branches"
  UNION ALL SELECT 'BusinessSettings', "Id", "StateId", "CountryId" FROM "BusinessSettings"
  UNION ALL SELECT 'Customers', "Id", "StateId", "CountryId" FROM "Customers"
  UNION ALL SELECT 'Suppliers', "Id", "StateId", "CountryId" FROM "Suppliers") x
LEFT JOIN "States" s ON s."Id" = x."StateId"
WHERE x."StateId" IS NOT NULL AND (x."CountryId" IS NULL OR s."CountryId" IS DISTINCT FROM x."CountryId");
```
`V__country-state-geo-rules.sql` (`-- sellora:on-violation warn`; the former "active state under inactive country" check is dropped, BR6 permits it):
```sql
SELECT s."Id", 'gst code on non-IN state' AS issue FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId"
WHERE s."GstStateCode" IS NOT NULL AND c."Code" <> 'IN';
```
**V__ output rule.** V__ files run on every deploy and the migrator stores the first 5 rows in the journal (`MigrationRunner.cs:219,229`), shown on the Database releases page (R11, `db-release-automation.md:46-48`: ids only). So they output table, Id, StateId and `gst_prefix` only: never State text, never a full GSTIN (it embeds the PAN). The full owner report is a manual psql query for the release notes (below).

`V__country-state-unmatched-legacy.sql` (`-- sellora:on-violation warn`; ids only; includes ambiguous and deny-listed rows; rows resolved by the GSTIN pass drop out because they are linked; uses the single blank definition; `"CountryId" IS NULL` so a row a user deliberately set to a country only, e.g. a platform-added country without states, clears the warning):
```sql
SELECT 'Branches' AS t, "Id", substr("Gstin",1,2) AS gst_prefix FROM "Branches"
 WHERE "StateId" IS NULL AND "CountryId" IS NULL AND regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') <> ''
UNION ALL SELECT 'BusinessSettings', "Id", substr("Gstin",1,2) FROM "BusinessSettings"
 WHERE "StateId" IS NULL AND "CountryId" IS NULL AND regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') <> '';
```
`V__country-state-gstin-conflicts.sql` (`-- sellora:on-violation warn`; ids and prefixes only; **own GSTIN only**: a GSTIN-less branch is never compared with the company GSTIN, because the company GSTIN is only India context and a D12 fallback source, not a conflict source; valid GSTIN = the strict D11 regex):
```sql
WITH x AS (
  SELECT 'Branches' AS t, b."Id", b."StateId", upper(btrim(b."Gstin")) AS gstin
  FROM "Branches" b WHERE b."StateId" IS NOT NULL
  UNION ALL SELECT 'BusinessSettings', bs."Id", bs."StateId", upper(btrim(bs."Gstin")) FROM "BusinessSettings" bs WHERE bs."StateId" IS NOT NULL
  UNION ALL SELECT 'Suppliers', sp."Id", sp."StateId", upper(btrim(sp."Gstin")) FROM "Suppliers" sp WHERE sp."StateId" IS NOT NULL)
SELECT x.t, x."Id", s."Code" AS state_code, s."GstStateCode", substr(x.gstin,1,2) AS gstin_prefix,
  CASE WHEN substr(x.gstin,1,2)='25' AND s."GstStateCode"='26' AND s."Code"='DH'
       THEN 'legacy Daman and Diu GSTIN (re-registration under 26 expected)'
       WHEN c."Code" <> 'IN' THEN 'indian GSTIN on non-IN state'
       ELSE 'GSTIN prefix differs from state' END AS issue
FROM x JOIN "States" s ON s."Id"=x."StateId" JOIN "Countries" c ON c."Id"=s."CountryId"
WHERE x.gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'
  AND substr(x.gstin,1,2) IN (SELECT "GstStateCode" FROM "States" WHERE "GstStateCode" IS NOT NULL UNION SELECT '25')
  AND (c."Code" <> 'IN' OR substr(x.gstin,1,2) IS DISTINCT FROM s."GstStateCode");
```
Manual owner report (release notes, run once with psql, **not** a V__ file, because its rows can never clear: legacy text is frozen and alias, number and GSTIN-derived links are permanent; contains State text and GSTINs, never paste into logs): the unmatched-legacy query above plus `"State"` and `"Gstin"`, and the text-differs query below:
```sql
SELECT x.t, x."Id", x."StateId", x."State", s."Name", x."Gstin" FROM (
  SELECT 'Branches' AS t, "Id", "StateId", "State", "Gstin" FROM "Branches"
  UNION ALL SELECT 'BusinessSettings', "Id", "StateId", "State", "Gstin" FROM "BusinessSettings") x
JOIN "States" s ON s."Id" = x."StateId"
WHERE btrim(coalesce(x."State",'')) <> '' AND lower(btrim(x."State")) <> lower(s."Name");
```

Manual counts for the release engineer (not a V__ file; they return rows by design): row counts of the 4 tables before and after; per table `SELECT count(*) FILTER (WHERE "StateId" IS NOT NULL) linked, count(*) FILTER (WHERE "StateId" IS NULL AND regexp_replace(coalesce("State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') <> '') unlinked_text, count(*) total` for Branches and BusinessSettings; and `SELECT count(*) FROM "Countries"` (7) / `"States"` (85).

### 9. Release and rollback

- Both release paths are tested: (a) the migrator package / EF bundle (production, db-release-automation D3) and (b) the generated idempotent script (`_deploy-template.yml:98-105`) applied on Postgres starting from `20261117105841_AddReleaseReadinessIndexes`. Path (b) specifically proves the single-Sql backfill survives the `DO $EF$` wrapping.
- **Lock window (the only place this rationale lives):** the migration runs in one transaction, so every ACCESS EXCLUSIVE lock (including the one `ADD COLUMN` takes) is held until commit and the table locked FIRST is held LONGEST. The per-request tables are `Branches` (`CompanyContextMiddleware.cs:125,143`) and `BusinessSettings` (24 call sites of the company settings/GSTIN lookups), so they are locked LAST (table order: Countries/States created, `Customers`, `Suppliers`, `Branches`, `BusinessSettings`) and only for the backfill and constraint scans on tiny tables. The migration's first statement is `SET LOCAL lock_timeout = '5s'`, so a lock wait fails the release fast (SQLSTATE 55P03) instead of queueing the API; the EF bundle path has no timeout of its own (`MigrationRunner.cs:262-275`; the timeout at `:251` covers SQL scripts only). A lock-timeout failure rolls back cleanly (tested, section 10); retry from the Database releases page. FK and CK are plain EF operations (no `NOT VALID`): validating them in the same transaction would not shorten the window, and on all-NULL columns the scan is negligible. If a hypermarket-size `Customers` scan ever matters, split the constraints into a follow-up migration. Record the short lock window in the release notes.
- Procedure: snapshot/branch DB -> apply idempotent script (or bundle) -> run the 5 validation files and the manual counts -> deploy app -> smoke test. The owner report (with State text and GSTINs, including the text-differs query) is a one-off manual query in the release notes, run once with psql (section 8), never a V__ file.
- Release checklist: primary-source confirmation of the seed (section 2); owner review of the manual report; both release paths green. (The GSTIN refinements are decided: D11 strict format, D12 inherited company GSTIN only for blank text, D13 suppliers; no open owner item.)
- Rollback order (`docs/operations/database-release-runbook.md:59-61`): (1) app-only: redeploy the previous api-publish build and keep the schema; the migration is purely additive (nullable columns without defaults; FKs and CHECKs pass on NULL) and the old model never writes CountryId/StateId, so it runs unchanged; (2) restore the pre-deploy snapshot only if the migration or backfill itself damaged data; (3) `Down()` is dev-only (it loses links and post-deploy master edits, section 7).

### 10. Test plan (Postgres integration, Testcontainers)

- Migration: seed counts 7/85, rerun of the idempotent script is a no-op, constraints (unique code/name case-insensitively, CK formats, composite FK rejects state of another country, `State_Requires_Country`).
- **Drift test:** run the migrator (EF migrations + `backend/db/scripts` data scripts, as `MigrationRunnerTests` already does) on a fresh DB, then compare `GeoSeedData` with the resulting rows (ids, codes, names, kinds, GST codes, IsSystem); every Appendix B alias resolves as listed; no normalised alias maps to two states or duplicates another entry, a state name or an ISO code.
- **Backfill text pass, exact outcomes** (probe row = Branch, no GSTIN, no company GSTIN unless stated; "reported" = row appears in the unmatched-legacy V__ file / manual report):

| Probe text | Final result | In unmatched report |
|---|---|---|
| `Kerala`, `KERALA `, `32-Kerala`, `Kerala-32`, `32 Kerala`, `Kerala (32)`, `Kerala State (32)`, `32 Kerala State` | IN-KL | no |
| `33-Tamil Nadu`, `Tamilnadu`, `Orissa`, `Andaman & Nicobar`, `J&K`, `Telengana` | IN-TN, IN-TN, IN-OD, IN-AN, IN-JK, IN-TS | no |
| `32-Tamil Nadu`, `Tamil Nadu (32)` (32 is Kerala's code) | StateId NULL | yes |
| `Mubarak Al Kabeer`, `Ras Al-Khaimah`, `Dubai Emirate` | KW-MU, AE-RK, AE-DU | no |
| `Northern Region`, `Capital`, `Northern`, `Northern Province`, `Ash Sharqiyah`, `Al Batinah`, `NCR`, `UT`, `OR` (any context, incl. Saudi/Indian GSTIN as context only) | StateId NULL | yes |
| `Northern Governorate`, `Southern Governorate`, `Eastern Region` | BH-17, BH-14, SA-04 | no |
| blank, whitespace-only (incl. tab-only, NBSP-only, U+3000-only) | StateId NULL from text; D9/D12 GSTIN pass applies (own GSTIN, else company GSTIN) | no |
| `-`, `.` (punctuation-only = not blank), Branch with NULL own GSTIN, company GSTIN `32...` | StateId NULL (no inherited fallback, D12) | yes |
| tab-only, Branch with NULL own GSTIN, company GSTIN `32...` | IN-KL via inherited GSTIN (blank, D12) | no |

- Normaliser parity (SQL `pg_temp.geo_norm` vs C# `GeoSeedData.Normalize`): ASCII-letter inputs only (PG `lower()` under C collation does not fold non-ASCII). `Kerala` + each of U+00A0, U+2007, U+202F, U+FEFF, **U+0085, U+1680, U+3000**, U+2003 -> IN-KL in both; the explicit escaped class `[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]` is used in both, never `\s` or `Trim()`. Run on both a C and a C.UTF-8 database and on the Testcontainers image to prove locale independence.
- Bare numbers and abbreviations (exact): `33` with own Indian GSTIN `33...` -> IN-TN; `33` with no India context stays NULL (reported); `TN` / `UK` / `IN-TN` / `IN-OR` likewise; `TG` -> Telangana; legacy GSTIN prefix `25` gives India context and `DD` / `25` -> IN-DH.
- **D11 strict GSTIN** (`^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$` after `upper(btrim())`): `33AAAAA0000A1Z5` valid; lower-case/padded accepted; 14 characters, 15 characters without the PAN letters, `Z` missing at position 14 are NOT valid. Gulf tax ids (UAE TRN `100234567800003`, Saudi VAT `300123456700003`, Bahrain VAT `200000898300002`, all digits) are never Indian: no India context and no link for Branches, BusinessSettings and Suppliers, with blank text, with `Riyadh`, and with bare `04`.
- **D9 / D12 fallback** (exact StateId and report membership): blank text + own GSTIN `32...` -> IN-KL, CountryId IN, not reported; unmatched text `Kerla` + own GSTIN `33...` -> IN-TN, not in unmatched report (linked); `Capital` + own valid GSTIN -> linked from prefix; Branch with NULL Gstin and **blank** text, company GSTIN `32...` -> IN-KL (D12 inherit); Branch with NULL Gstin and **unmatched non-blank** text (`Deira`, `Capital`, `Sohar`, `Emirate of Dubai`, `Kerla`) and company GSTIN `32...` -> StateId NULL, CountryId NULL, **in** the unmatched report; the inherited GSTIN still gives India context in pass 1 (`33` text -> IN-TN); BusinessSettings pass (own GSTIN only).
- **D13 suppliers:** supplier with valid own GSTIN `32...` -> IN-KL + CountryId IN; with invalid/Gulf/no GSTIN -> NULL/NULL; no company fallback; a supplier already linked is not touched; Customers untouched.
- Conflict reports: text `Kerala` with own GSTIN `33...` stays IN-KL (text wins) and appears in `V__country-state-gstin-conflicts`; legacy `25` on IN-DH reports the legacy message; a Gulf branch (`Dubai`, no own GSTIN) of an Indian company is NOT reported (own GSTIN only). The text-vs-state-name differences (`Kerala State (32)` with GSTIN `33...`, `Tamilnadu`) are verified through the manual owner-report query, not a V__ file.
- Validation files run on probes: each of the 5 V__ files runs on the migrated template DB with zero rows (Passed), then with inserted violating probe rows (`block` header files -> Failed, `warn` header files -> Warning), through `MigrationRunner` (extend `MigrationRunnerTests.cs:31`); a test also asserts each file's two header lines (description + on-violation) are present and match the mode table in section 8.
- **Lock timeout fail-fast:** from a second connection hold a conflicting lock (`BEGIN; LOCK TABLE "Branches" IN ACCESS EXCLUSIVE MODE;`, uncommitted), run the migration, and expect SQLSTATE `55P03` within about 5 s, with NO `Countries`/`States` table, no new columns and no `AddCountryStateMasters` row in `__EFMigrationsHistory` (no partial state). After the blocker releases, a retry succeeds. Run it for both the EF path and the idempotent script path.
- `Down()` then `Up()` round trip on Postgres: after Down no `Countries`/`States`, no new columns, no `UX_*_Lower` indexes, no AK/CK/FK remain; Up again succeeds.
- Service-level (unit/InMemory plus Postgres where relevant): D8 guard (company 403 when the flag is false, allowed when true, platform always); `canEdit` on company list responses follows `Platform:ReferenceDataEditableByCompanies` (false -> write endpoints 403 and screens read-only); **audit actor:** a company edit writes a `PlatformAuditEvents` row with `ActorUserId` = the NameIdentifier/sub user and `CompanyId` = `ICompanyContext.RequireCompanyId()`; a platform edit writes ActorUserId with CompanyId NULL; the service THROWS when UserId is missing, or CompanyId is missing for a company actor (assert the exception, and assert no audit row is written for a rejected call); audit rows exist for both actor kinds (no early return for company actors); IsSystem immutability (Code, CountryId, GstStateCode); State countryId change -> 400 `countryId`; BR4/BR5/BR6/BR7 (AC2, AC5, AC6, AC7); onboarding writes CountryId/StateId and not the free-text State (D10); platform company creation returns 400 `stateId` for an inactive state and for a state of another country, and 400 `countryId` for `stateId` without `countryId`; platform lookup endpoints return only active countries / active states of the country.
- Embedded SQL pin: a test computes SHA-256 of each `Data/Migrations/Sql/*.sql` resource and compares it with committed hashes, so an edit to an applied migration's SQL fails CI; a second assertion checks the resources contain no `\r` (LF-only, enforced by `.gitattributes`).
- Release: both paths of section 9.

### 11. Effort and impact

S-M for the database: 2 entities, 2 configurations, 4 existing configurations touched, 1 migration with embedded seed (about 120 lines) and backfill (about 150 lines) SQL plus csproj embedded-resource item, helper and `.gitattributes` entry, 5 validation files, `GeoSeedData` (seed + alias map + normaliser) and `DbSeeder` changes, the drift test, the parity test and the lock-timeout test.

Backend (D8 scope): `CountryService` / `StateService`; audit-actor extension; the onboarding change (D10); company endpoints `/api/v1/countries`, `/api/v1/states` (list, create, edit, status) whose list responses carry a mandatory `canEdit`; Platform-console variants (`PlatformReferenceDataController` pattern) for Countries and States list/create/edit/status; platform GET lookups for the onboarding form (active countries; active states by country).

Frontend (D8 scope): 2 Administration screens (read-only when `canEdit` is false); 2 Platform-console screens (Countries, States; precedent: Currencies page, `platform-layout.ts:17`); Country -> State selects in the 4 address forms plus the platform onboarding form (reading the platform lookups, precedent `platform-company-form.ts:95`). This is larger than the original "2 screens + 4+1 selects" estimate: 4 screens and 5 selects.

**API surface notes for the contract (step 2):** company `GET/POST/PUT/PATCH-status` on countries and states (403 on writes when the flag is false); platform `GET/POST/PUT/PATCH-status` on countries and states; platform `GET` lookups `countries?active=true` and `states?countryId=&active=true`; `canEdit` on every company list response; branch, settings, customer, supplier and platform company-create DTOs gain `countryId`, `stateId` (+ names) as trailing parameters with defaults.

## Appendix A: seed rows (code | name | kind | gst)

Countries (Code | Iso3 | Name; all IsActive=true, IsSystem=true): IN IND India; AE ARE United Arab Emirates; SA SAU Saudi Arabia; QA QAT Qatar; KW KWT Kuwait; BH BHR Bahrain; OM OMN Oman. All states: IsActive=true, IsSystem=true; GstStateCode NULL except India.

**IN (36)** S = State, UT = UnionTerritory:
01 JK Jammu and Kashmir UT; 02 HP Himachal Pradesh S; 03 PB Punjab S; 04 CH Chandigarh UT; 05 UK Uttarakhand S; 06 HR Haryana S; 07 DL Delhi UT; 08 RJ Rajasthan S; 09 UP Uttar Pradesh S; 10 BR Bihar S; 11 SK Sikkim S; 12 AR Arunachal Pradesh S; 13 NL Nagaland S; 14 MN Manipur S; 15 MZ Mizoram S; 16 TR Tripura S; 17 ML Meghalaya S; 18 AS Assam S; 19 WB West Bengal S; 20 JH Jharkhand S; 21 OD Odisha S; 22 CG Chhattisgarh S; 23 MP Madhya Pradesh S; 24 GJ Gujarat S; 26 DH Dadra and Nagar Haveli and Daman and Diu UT; 27 MH Maharashtra S; 29 KA Karnataka S; 30 GA Goa S; 31 LD Lakshadweep UT; 32 KL Kerala S; 33 TN Tamil Nadu S; 34 PY Puducherry UT; 35 AN Andaman and Nicobar Islands UT; 36 TS Telangana S; 37 AP Andhra Pradesh S; 38 LA Ladakh UT.
(Format: GST code, ISO code, name, kind.) GST codes 25, 28, 96, 97, 99 are not seeded. 28 states + 8 union territories.

**AE Emirate (7)**: AZ Abu Dhabi; AJ Ajman; DU Dubai; FU Fujairah; RK Ras Al Khaimah; SH Sharjah; UQ Umm Al Quwain.
**SA Region (13)**: 01 Riyadh; 02 Makkah; 03 Madinah; 04 Eastern Province; 05 Al Qassim; 06 Ha'il; 07 Tabuk; 08 Northern Borders; 09 Jazan; 10 Najran; 11 Al Bahah; 12 Al Jawf; 14 Asir (no 13).
**QA Municipality (8)**: DA Doha; KH Al Khor and Al Thakhira; MS Al Shamal; RA Al Rayyan; SH Al Shahaniya; US Umm Salal; WA Al Wakrah; ZA Al Daayen.
**KW Governorate (6)**: AH Al Ahmadi; FA Al Farwaniyah; HA Hawalli; JA Al Jahra; KU Capital; MU Mubarak Al-Kabeer.
**BH Governorate (4)**: 13 Capital; 14 Southern; 15 Muharraq; 17 Northern.
**OM Governorate (11)**: BJ South Al Batinah; BS North Al Batinah; BU Al Buraimi; DA Ad Dakhiliyah; MA Muscat; MU Musandam; SJ South Ash Sharqiyah; SS North Ash Sharqiyah; WU Al Wusta; ZA Ad Dhahirah; ZU Dhofar.

Total 7 countries, 85 states (36+7+13+8+6+4+11).

## Appendix B: alias map (committed; migration INSERT and `GeoSeedData.Aliases` are generated 1:1 from this list)

Every alias is passed through `geo_norm`. Format: `target: alias, alias`. Aliases marked `(i)` are india_only (apply only with India context); all other aliases apply without context. The list is deduplicated AFTER normalisation: no entry equals another entry, a state's own name or an ISO state code once normalised (`&` -> `and`, dots and apostrophes removed, so e.g. `j & k` and `j&k` are one entry; `m.p.`, `t.n.`, `u.p.`, `w.b.` need no entry because the input `M.P.` normalises to the generated code `mp`). The drift test asserts this. The ISO state codes (`tn`, `up`, `ap`, `cg`, ...), the `IN-XX` forms, the old ISO forms and the bare GST numbers are generated from the States table (section 7) as india_only candidates and are not listed here; only abbreviations that are NOT a current ISO code are listed. Excluded on purpose: bare `ut`, `or`, `ncr`, `delhi ncr`, `ua`, `ul`, `sharqia`, `ash sharqiyah`/`sharqiyah`, `capital`/`al asimah`, and every deny-list word. Legacy `25` -> IN-DH is handled by the code candidates.

**IN**
- IN-AN: andaman and nicobar, a&n islands, andaman nicobar, andaman
- IN-AP: andhra, andhrapradesh, andra pradesh
- IN-AR: arunachal
- IN-BR: behar
- IN-CG: ct (i), chattisgarh, chhatisgarh, chattisgadh
- IN-CH: chandigarh ut
- IN-DH: dnhdd (i), dnh (i), dn (i), dd (i), dadra and nagar haveli, daman and diu, daman, diu
- IN-DL: new delhi, nct of delhi, nct delhi, national capital territory of delhi
- IN-GJ: gujrat
- IN-HP: himachal
- IN-HR: hariyana
- IN-JH: jharkand
- IN-JK: j&k, jammu kashmir, jammu, kashmir
- IN-KA: karnatak
- IN-KL: keralam
- IN-LA: ladak, leh, leh ladakh
- IN-LD: lakshdweep
- IN-MH: maharastra
- IN-OD: orissa, odissa
- IN-PB: panjab
- IN-PY: pondicherry, pondichery, puduchery, pondy
- IN-RJ: rajastan
- IN-TN: tamilnadu
- IN-TS: tg (i), telengana
- IN-UK: uttaranchal, uttranchal, uttrakhand, uttarkhand
- IN-WB: bengal, paschim banga

**AE**
- AE-AZ: abu zaby, abu zabi, abudhabi, auh, al ain
- AE-DU: dubayy, dxb
- AE-SH: ash shariqah, al sharjah, shj
- AE-UQ: umm al qaywayn, umm al qaiwain, uaq
- AE-RK: ra's al khaymah, ras al khaima, rak
- AE-FU: al fujayrah, al fujairah, fujeirah

**SA**
- SA-01: ar riyad, al riyadh
- SA-02: mecca, mekkah, makkah al mukarramah, jeddah, jiddah
- SA-03: medina, madina, al madinah, al madinah al munawwarah
- SA-04: eastern region, dammam, khobar
- SA-05: qassim, al qasim
- SA-06: hayil
- SA-08: northern border, al hudud ash shamaliyah
- SA-09: jizan, gizan
- SA-10: nejran
- SA-11: al baha, baha
- SA-12: al jouf, jouf
- SA-14: aseer, abha

**QA**
- QA-DA: ad dawhah, al doha
- QA-KH: al khor, al khawr, al khawr wa adh dhakhirah
- QA-MS: ash shamal, shamal, madinat ash shamal
- QA-RA: ar rayyan, rayyan
- QA-SH: ash shihaniyah, al sheehaniya, shahaniya
- QA-US: umm slal
- QA-WA: al wakra, wakrah, wakra
- QA-ZA: az zaayin, daayen

**KW**
- KW-KU: kuwait city
- KW-HA: hawally
- KW-FA: farwaniya, al farwaniya
- KW-AH: ahmadi
- KW-JA: jahra
- KW-MU: mubarak al kabir

**BH**
- BH-13: manama (`capital governorate` is deny-listed, not aliased)
- BH-14: al janubiyah, southern governorate
- BH-15: al muharraq
- BH-17: ash shamaliyah, al shamaliyah, northern governorate

**OM**
- OM-MA: masqat
- OM-ZU: zufar, salalah
- OM-BU: al buraymi, buraimi
- OM-DA: al dakhiliyah, dakhiliya, dakhiliyah
- OM-BS: shamal al batinah, al batinah north, batinah north
- OM-BJ: janub al batinah, al batinah south, batinah south
- OM-SS: shamal ash sharqiyah, ash sharqiyah north
- OM-SJ: janub ash sharqiyah, ash sharqiyah south
- OM-WU: wusta
- OM-ZA: az zahirah, al dhahirah, dhahira, dhahirah

## API contract
_Pending (step 2)._

## Decisions
- D1 (owner, 2026-11-19) Global masters, not per company.
- D2 (owner, 2026-11-19) Edited by company admins holding `Masters.Edit` (same as Currencies); viewed with `Masters.View`. Clarified by D8.
- D3 (owner, 2026-11-19) Relink all address pages, including Business Settings (Branch, Business Settings, Customer, Supplier).
- D4 (owner, 2026-11-19) Seed India and the GCC countries (UAE, Saudi Arabia, Qatar, Kuwait, Bahrain, Oman).
- D5 (owner, 2026-11-19) Menu: Administration, next to Currencies.
- D6 (owner, 2026-11-19) The free-text State input is replaced by the Country → State dropdowns; while `StateId` is null the legacy text is shown as a read-only hint. The column stays in the DB.
- D7 (owner, 2026-11-19) New Customer/Supplier default to the company's Business Settings country; the user can change it.
- D8 (owner, 2026-11-19) **Same guard as Currencies.** Company `Masters.Edit` holders write only when the platform allows company edits (single-tenant, `Platform:ReferenceDataEditableByCompanies`); otherwise company screens are read-only and edits go through the Platform console (Countries/States pages + platform lookup endpoints for onboarding). No `CreatedByCompanyId`. Seeded rows keep ISO identity locked. Every write is audited with the real actor.
- D9 (owner, 2026-11-19) **GSTIN fallback.** When the State text gives no link, a valid Indian GSTIN prefix links StateId + CountryId = India. Text match always wins; text-vs-GSTIN conflicts are reported, never overwritten.
- D10 (owner, 2026-11-19) **Onboarding** (Platform → create company) uses the Country → State dropdowns and stops writing the free-text State.
- D11 (owner, 2026-11-19) A GSTIN counts only in the strict official format (2-digit state code + PAN with letters); all-digit Gulf tax numbers (UAE TRN, Saudi/Bahrain VAT) are never treated as Indian.
- D12 (owner, 2026-11-19) A Branch with no GSTIN of its own uses the company GSTIN for the D9 fallback **only when its State text is blank**; non-blank unmatched text stays unlinked and appears in the unmatched report.
- D13 (owner, 2026-11-19) Suppliers are linked (Country = India + State) from their own GSTIN prefix (strict format); suppliers without a valid GSTIN stay unlinked.
- D14 (db-engineer, 2026-11-19) Deliberate deviation: the frozen backfill/GeoSeedData punctuation class is written with ARE/regex escapes (`[.'\u2019\u2018`]`), not the literal curly quotes and doubled quote shown in section 7. Behaviour is identical (validated on PG16, C and C.UTF-8). The SHA-pinned resource is not edited.

## Deployment notes
Additive migration + seed + backfill; legacy text columns retained. Run the validation SQL after deploy and share unmatched rows.
