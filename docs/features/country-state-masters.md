# Country and State Masters

status: requirements-approved
<!-- draft → requirements-approved → design-approved → db-done → backend-done → frontend-done → reviewed → done -->
size: L (two new tables + relinking four existing address owners)

## Problem

State is free text on `Branch.State` and `BusinessSettings.State`; Customer and Supplier carry no country/state at all.
Free text cannot drive GST place-of-supply, filters or reports reliably, and GCC operations need their own subdivisions.

## Scope
**In:**
- Global masters `Countries` and `States` (first-level subdivision: state / union territory / emirate / region / governorate).
- Seed: India (states + UTs with GST state codes) and the six GCC countries with their first-level subdivisions.
- Administration → **Countries**, **States** screens (list, create, edit, activate/deactivate).
- Country → State selection on every address-owning page: **Branch, Business Settings, Customer, Supplier**.
- Backfill existing `Branch.State` / `BusinessSettings.State` text to `StateId`, with an unmatched-rows report.

**Out:**
- GST place-of-supply / IGST-vs-CGST+SGST logic on invoices (separate change; this feature only provides the data).
- Districts, cities, postal codes as masters.
- Dropping the legacy free-text `State` columns (later, destructive, separately approved).

## Business rules
- BR1 Masters are **global** (no `CompanyId`), like `Currency`. View: `Masters.View`; create/edit/deactivate: `Masters.Edit`.
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

## Impact
DB: `Countries`, `States` (new); nullable `CountryId`, `StateId` on `Branches`, `BusinessSettings`, `Customers`, `Suppliers` (additive) ·
API: `/api/v1/countries`, `/api/v1/states`, lookup endpoints; branch/settings/customer/supplier DTOs gain `countryId`, `stateId` (+ names) ·
UI: 2 Administration screens; Country/State selects in 4 forms; list columns where useful ·
Permissions: reuse `Masters.View` / `Masters.Edit` · Reports: none in this feature

## Open questions
_None (Q1, Q2 resolved as D6, D7)._

## Database design
_Pending (step 1, database-engineer; requires approval before migration)._

## API contract
_Pending (step 2)._

## Decisions
- D1 (owner, 2026-11-19) Global masters, not per company.
- D2 (owner, 2026-11-19) Edited by company admins holding `Masters.Edit` (same as Currencies); viewed with `Masters.View`.
- D3 (owner, 2026-11-19) Relink all address pages, including Business Settings (Branch, Business Settings, Customer, Supplier).
- D4 (owner, 2026-11-19) Seed India and the GCC countries (UAE, Saudi Arabia, Qatar, Kuwait, Bahrain, Oman).
- D5 (owner, 2026-11-19) Menu: Administration, next to Currencies.
- D6 (owner, 2026-11-19) The free-text State input is replaced by the Country → State dropdowns; while `StateId` is null the legacy text is shown as a read-only hint. The column stays in the DB.
- D7 (owner, 2026-11-19) New Customer/Supplier default to the company's Business Settings country; the user can change it.

## Deployment notes
Additive migration + seed + backfill; legacy text columns retained. Run the validation SQL after deploy and share unmatched rows.
