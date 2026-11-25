# Tax Profiles and Product Tax Mapping

status: design-approved
<!-- draft → requirements-approved → design-approved → db-done → backend-done → frontend-done → reviewed → done -->
size: L (new tax masters, per-component line snapshots, sales/purchase money logic, data migration)

Prototype (for discussion, example data only): https://claude.ai/artifact/2UtMo7sNf1ExKV4qWQ6XBF

## Problem

A tax today is one row (`Tax`: name + `RatePercent`) and a product points at exactly one (`Product.TaxId`).
Sales and purchase lines snapshot `TaxId` + `TaxRatePercent` and always add tax on top of the entered price
(`SaleMoney.Line`, `PurchaseService` line build). This cannot produce correct documents for the target market
(Indian and GCC hypermarkets):

- GST is not one tax. An Indian invoice must print **CGST + SGST** within a state and **IGST** across states, with
  each component reported and (later) posted separately. Cess can sit on top, and may be a % or an amount per unit.
- Retail shelf prices (MRP) **include** tax; the POS must work the taxable value out of the price. Today it can't.
- Purchase and sales tax cannot differ, and the tax cannot be set once per category.
- Editing `RatePercent` changes the rate for all future documents with no date. Rates change by notification
  (GST slab change of 22 Sep 2025; Saudi 5% → 15% in 2020; Bahrain 5% → 10% in 2022), so they need effective dates.
- There is no HSN/SAC code (required on GST invoices and the GSTR-1 HSN summary).
- There is no whole-bill discount or whole-bill charge on sales, and none on purchases beyond per-line fields.

## Scope

**In:**
- Tax components, tax profiles with dated versions, and the lookup that assigns a sales and a purchase profile to a
  product through product → subcategory → category → company default (per country).
- A single tax calculation used by sales, POS, purchases and both return flows, covering the phase-1 scenarios in
  [Calculation scenarios](#calculation-scenarios).
- Tax-inclusive and tax-exclusive prices; whole-bill discount and whole-bill charges spread across lines.
- Per-component tax rows snapshotted on every document line; returns reverse from the snapshot.
- HSN/SAC code on subcategory and product (inherited).
- Inventory › Tax setup screen (Components, Profiles, Mapping) and the product form's tax section.
- Data migration from the current `Tax` / `Product.TaxId` without recomputing history.
- India GST and GCC VAT (UAE, Saudi Arabia, Bahrain, Oman) from day one (T1).

**Out (later phases, with a data hook now where stated):**
- Party GST/VAT status screens (registered, composition, unregistered, SEZ, export) — engine input exists from phase 1 (T4).
- After-tax discounts → financial credit note and discount on payment (T6).
- Reverse charge, imports (customs + IGST on bill of entry), free goods / schemes, TCS/TDS.
- Charges billed as a separate service line with its own profile (option B of T7).
- Invoice round-off to the nearest rupee / fils.
- Ledger posting of output/input tax per component (no general ledger exists yet).
- GSTR-1 / GSTR-3B / VAT return exports and e-invoicing (IRN/QR) — the snapshots in this feature are their source.

## Concepts

```
Tax component   CGST, SGST/UTGST, IGST, Cess, VAT          (what prints on the invoice and posts to an account)
      ▲ grouped by
Tax profile     "GST 18%", "GST 40%", "GST Nil rated", "VAT 15% (KSA)"   (one country, one treatment)
  └ version     effective from a date; holds the component rates
      ▲ assigned to
Assignment      company default → category → subcategory → product override   (sales profile + purchase profile)
      ▼ used at line build time with the document context
Line snapshot   profile, version, place of supply, price basis, one tax row per component
```

## Business rules

**Components and profiles**
- BR1 A tax component has a code, name, kind (`Cgst`, `Sgst`, `Utgst`, `Igst`, `Cess`, `Vat`, `Other`) and a
  calculation method: `Percent` (of the line's taxable value) or `AmountPerBaseUnit` (× the line's base quantity).
- BR2 A tax profile belongs to one country (its jurisdiction), has a regime (`Gst`, `Vat`) and a treatment:
  `Taxable`, `NilRated`, `Exempt`, `ZeroRated`, `NonTaxable`. Non-taxable treatments carry no components and are
  separate profiles (they report differently and treat input credit differently), never "0% taxable".
- BR3 A profile has one or more versions. Each version has `EffectiveFrom` (date) and component lines. Each component
  line has an applicability: `IntraState`, `InterState` or `Always` (GST), or `Always` (VAT).
  A GST `Taxable` version must have a complete intra-state set (CGST+SGST or CGST+UTGST, equal rates) and an
  inter-state set (IGST = CGST + SGST). Cess lines are `Always`.
- BR4 Versions of one profile never overlap. Adding a version closes the previous one the day before. A version used
  by any document line is immutable (rate, components, dates); a mistake is fixed by a new version from the same date
  only when nothing uses the old one yet, otherwise from a later date.
- BR5 The version used is the one valid on the **document date** (`InvoiceDate`, `PurchaseDate`). A document date with
  no valid version is a validation error on the line.
- BR6 Profiles and components are deactivated, never deleted. An inactive profile cannot be newly assigned or used on
  new documents; history keeps it.

**Assignment (T3)**
- BR7 An assignment sets a sales profile and a purchase profile for a scope (company, category, subcategory or
  product) and a country. Purchase profile empty = same as sales.
- BR8 Resolution for a document line: product → subcategory → category → company default, for the country of the
  document's branch (fallback: company country from `BusinessSettings`). No result is a validation error naming the
  product. The resolved source (e.g. "subcategory Soft Drinks") is returned to the UI.
- BR9 HSN/SAC code is stored on subcategory and product and resolves product → subcategory. A product resolved to a
  GST profile with treatment `Taxable` and no HSN code gets a warning on the product form and on document lines; the
  save is allowed (T11).
- BR10 A profile's country must match the assignment's country.

**Document context**
- BR11 Regime comes from the branch's country: India → GST, AE/SA/BH/OM → VAT, any other country → error until a
  profile exists for it.
- BR12 Place of supply (GST only; a branch with no state uses the company's state, T14): sales — branch state vs customer state; a customer with no state (walk-in, POS) is
  intra-state. Purchases — supplier state vs receiving branch state; a supplier with no state is intra-state with a
  warning. Same state → `IntraState` components; different state → `InterState` components.
- BR13 Party tax status is an input of the calculation from phase 1 with value `Unspecified` (behaves as today:
  registered business / consumer). Status values and their effects arrive with the later party-status phase (T4).

**Price basis (T2)**
- BR14 `BusinessSettings.SellingPricesIncludeTax` (default **true**). A product may override it (`null` = follow the
  company). Sales lines snapshot the basis used.
- BR15 Purchase documents carry `RatesIncludeTax` (default **false**, as supplier invoices are normally exclusive);
  each line snapshots it.

**Calculation** (precision = document currency's decimals, rounding `AwayFromZero`, per component, per line)
- BR16 Exclusive line: `Gross = Qty × Price`; `Taxable = Gross − LineDiscount − BillDiscountShare + ChargeShare`
  (+ purchase line `OtherCharges`); each component: `Percent` → `round(Taxable × rate)`, `AmountPerBaseUnit` →
  `round(BaseQty × amount)`; `LineTotal = Taxable + Σ tax`.
- BR17 Inclusive line: `Incl = Qty × Price − LineDiscount − BillDiscountShare`;
  `PerUnitTax = Σ round(BaseQty × amount)`; `GoodsTaxable = round((Incl − PerUnitTax) / (1 + ΣPercent))`;
  percent components are computed from `GoodsTaxable`, then `GoodsTaxable` is set to
  `Incl − PerUnitTax − Σ percent tax` so the customer pays exactly `Incl` and CGST always equals SGST (D-T9).
  A charge share is then added as an exclusive amount with its own tax (BR16).
- BR18 Discounts in phase 1 are before tax only (T6): they reduce the taxable value. Discount after tax is not offered.
- BR19 Whole-bill discount (percent or amount) and whole-bill charges (amount) are spread across lines in proportion
  to each line's `Qty × Price − LineDiscount` (in the line's own basis); the last line takes the rounding remainder so
  the shares sum exactly to the entered amount. Each line stores `BillDiscountShare` and `BillChargeShare` (T7).
  Charges are tax-exclusive and take the line's rate. On purchases the charge share also feeds landed `UnitCost`.
- BR19a Giving a whole-bill discount on a sale needs `Sales.BillDiscount` (403 without it), and its effective percent of
  the bill's entered amount may not exceed `BusinessSettings.MaxBillDiscountPercent` when set (400 on `billDiscount`) (T15).
  Purchases are not limited (the supplier sets the discount).
- BR20 Header totals are sums of lines: `Subtotal = Σ Gross`, `DiscountTotal = Σ (LineDiscount + BillDiscountShare)`,
  `ChargesTotal = Σ BillChargeShare (+ OtherCharges)`, `TaxTotal = Σ tax rows`, and
  `GrandTotal = Σ LineTotal` exactly.
- BR21 For inclusive lines the existing `Subtotal`/`GrandTotal` identity changes: `Subtotal` is the tax-inclusive gross.
  Header DTOs gain `TaxableTotal` so screens never derive it (see Impact).
- BR22 Purchases in a foreign currency: compute per component in the document currency, then convert each amount to
  base currency the way `PurchaseService` converts today (no change to that rule).

**Snapshots and returns**
- BR23 Every sales, purchase and return line stores: `TaxProfileId`, `TaxProfileVersionId`, `PlaceOfSupply`,
  `PriceIncludesTax`, `HsnCode`, the bill shares, and one tax row per component
  (component id, code, kind, method, rate or amount per unit, base amount, tax amount). `TaxRatePercent` and
  `TaxAmount` stay on the line as totals for existing reports.
- BR24 History is never recomputed. A later rate change or reassignment does not touch existing lines.
- BR25 A return line reverses each tax row of its original line in proportion to returned base quantity, with the
  final return taking the remainder (the rule `SalesService`/`PurchaseService` already use for `TaxableValue`/
  `TaxAmount`). Bill discount and charge shares are already inside the line's taxable value, so they reverse with it.

## Edge cases

- E1 Mixed tax rates on one bill (rice 5%, cola 40%) with a ₹100 bill discount: shares follow line value; each line's
  tax drops at its own rate.
- E2 Mixed price bases on one document (one product overrides to exclusive): shares follow each line's own entered
  amount; for exclusive lines the payable falls by share + tax. Shown in the line breakdown.
- E3 Inclusive line whose per-unit cess exceeds the inclusive amount (deep discount): validation error on the line.
- E4 A document dated before a profile's first version (back-dated entry): BR5 error.
- E5 A rate change takes effect mid-day: versions are by date only; the document date decides.
- E6 Profile used in a POS cart while a new version is saved: the invoice is computed at save time on the server, so
  the stored snapshot uses the version valid on the invoice date.
- E7 A partial return after a bill discount: reverses the proportional share already embedded in the line (BR25).
- E8 A branch with no state in India: treated as the company's state; if neither is set, GST documents are refused with
  a message to set the branch state.
- E9 A company with branches in two countries: a product needs an assignment for each country (or a company default
  per country); BR8 resolves by the branch's country.
- E10 Zero quantity or zero price lines: zero tax rows are still stored for an auditable snapshot.

## Calculation scenarios

| Group | Scenario | Phase |
|---|---|---|
| Price basis | Exclusive price | 1 (exists) |
| Price basis | Inclusive price (MRP), back-calculated | 1 |
| Price basis | Inclusive price with per-unit cess | 1 |
| Discount | Line discount before tax | 1 (exists) |
| Discount | Discount on an inclusive price | 1 |
| Discount | Whole-bill discount, spread by value | 1 |
| Discount | Discount after tax (settlement, cash, loyalty) | Later: financial credit note + discount on payment |
| Charges | Line charges, taxable (purchase `OtherCharges`) | 1 (exists on purchase) |
| Charges | Whole-bill charges, spread by value | 1 |
| Charges | Charges outside tax (reimbursement) | Later |
| Charges | Charges as separate service line with own profile | Later |
| Structure | Single-rate VAT | 1 |
| Structure | CGST + SGST / UTGST intra-state, IGST inter-state | 1 |
| Structure | Cess as percent, per unit, or both | 1 |
| Structure | Nil rated / Exempt / Zero-rated / Non-taxable | 1 |
| Structure | Tax on tax | Not needed (not used by GST or GCC VAT on invoices) |
| Time | Rate change from a date (versions) | 1 |
| Context | Customer / supplier in another state | 1 |
| Context | Party tax status (registered, composition, SEZ, export, B2B/B2C) | Hook in 1, behaviour later |
| Context | Reverse charge, imports | Later |
| Context | Free goods / schemes | Later |
| Rounding | Per component, per line | 1 |
| Rounding | Invoice round-off | Later |
| Returns | Returns reverse the original components | 1 |

## Acceptance criteria

Worked numbers use 2 decimals, `AwayFromZero`.

- AC1 Given profile "GST 18%" and a same-state customer, When a line of 10 × 100.00 exclusive with 5% line discount
  is saved, Then taxable 950.00, CGST 9% 85.50, SGST 9% 85.50, line total 1,121.00, and two tax rows are stored.
- AC2 Given the same line for a customer in another state, Then one IGST 18% row of 171.00 is stored.
- AC3 Given "GST 18%" and an inclusive price of 118.00 × 1, Then taxable 100.00, CGST 9.00, SGST 9.00, total 118.00.
- AC4 Given "GST 5%", inclusive 99.00 × 1, Then the customer pays exactly 99.00, CGST = SGST, and
  taxable + CGST + SGST = 99.00.
- AC5 Given a profile with CGST 14 / SGST 14 / Cess 5% / Cess 2.00 per base unit and 10 units exclusive at 100.00,
  Then taxable 1,000.00, CGST 140.00, SGST 140.00, Cess 50.00, Cess-per-unit 20.00, total 1,350.00.
- AC6 Given an inclusive line with per-unit cess, Then the per-unit cess is removed before dividing by (1 + Σ%).
- AC7 Given a bill of two lines (rice 600.00 at 5%, cola 400.00 at 40%, both exclusive) and a bill discount of 100.00,
  Then shares are 60.00 and 40.00, taxable 540.00 and 360.00, and each line's tax is on its reduced taxable value.
- AC8 Given a bill discount of 100.00 over three equal lines, Then shares are 33.33, 33.33, 33.34.
- AC9 Given a purchase with a whole-bill freight of 300.00 over lines of 2,000.00 and 1,000.00, Then shares 200.00 and
  100.00 are added to each line's taxable value and to its landed unit cost.
- AC10 Given "GST 12%" valid to 2025-09-21 and "GST 5%" from 2025-09-22, When invoices dated 2025-09-21 and
  2025-09-22 are saved, Then they snapshot 12% and 5% versions respectively.
- AC11 Given a version used by any line, When its rate or dates are edited, Then 400 on that field.
- AC12 Given category Beverages → GST 5%, subcategory Soft Drinks → GST 40% and no product override, When a Soft Drinks
  product is sold, Then GST 40% is used and the API reports source "subcategory".
- AC13 Given a product override with purchase profile empty, Then purchases use the sales profile.
- AC14 Given a UAE branch and a product assigned only for India, When it is sold, Then 400 naming the product and the
  missing country assignment.
- AC15 Given a sale with CGST/SGST rows, When half the quantity is returned and then the rest, Then each return
  reverses each row proportionally and the two returns sum exactly to the original rows.
- AC16 Given existing invoices before migration, When the migration runs, Then every historic line has exactly one
  legacy tax row equal to its `TaxAmount` and its `TaxableValue`, `TaxAmount` and `LineTotal` are unchanged.
- AC17 Given each existing `Tax` row, When the migration runs, Then one profile + version exists with the same total
  rate, and every product has a product-level assignment to it for its company's country.
- AC18 Given `SellingPricesIncludeTax = true` and a product override `false`, Then that product's sale lines are
  exclusive and snapshot `PriceIncludesTax = false`.
- AC19 Given treatment `Exempt` or `NilRated`, Then the line stores one zero tax row with the treatment and no CGST/SGST.
- AC20 Given a GST taxable product without HSN, When it is saved or sold, Then the response carries a warning on
  `hsnCode` and the save succeeds (T11).
- AC21 Given a new company in India, When it is created, Then profiles GST Nil rated, Exempt, 5%, 18% and 40% exist with
  their components and a company-default assignment of GST 18%; for a UAE company, VAT 5%, Zero-rated and Exempt exist (T12).
- AC22 Given a user without `Sales.BillDiscount`, When a sale with a bill discount is saved, Then 403; given
  `MaxBillDiscountPercent = 10` and a 12% bill discount, Then 400 on `billDiscount` (T15).
- AC23 Given an inclusive product with selling price 99.00, When it is sold, Then the customer pays 99.00; given the
  product is switched to exclusive, Then the same stored 99.00 is treated as before tax (T16).

## Impact

DB: 6 new tables, new columns on Product, Subcategory, BusinessSettings, sales/purchase/return headers and lines;
backfill; `Taxes` and `*.TaxId` retired in a later destructive migration.
API: new `/api/v1/tax-components`, `/api/v1/tax-profiles`, `/api/v1/tax-assignments`, `/api/v1/tax/preview`;
product, sales, purchase and return contracts gain tax breakdown fields; `/api/v1/taxes` deprecated.
UI: Inventory › Tax setup (3 tabs); product form tax section; POS / sales / purchase line breakdown, bill discount and
charges inputs; invoice print shows components and HSN.
Permissions: `Tax.View`, `Tax.Manage` (new; today taxes sit under `Masters.View/Edit`); `Sales.BillDiscount` (T15).
Reports: sales/purchase tax reports read the component rows (legacy rows for history); new HSN summary later.

## Open questions

_None. Q1–Q6 are resolved as T11–T16._

## Database design

Draft for the design gate (database-engineer + solution-architect refine names, indexes and SQL). Conventions: quoted
PascalCase tables, Guid ids, string enums, `CompanyId` on company-owned rows, `numeric` money as today.

| Table | Key columns |
|---|---|
| `TaxComponents` | Id, CompanyId, Code (≤20, unique per company), Name, Kind, Method, IsActive |
| `TaxProfiles` | Id, CompanyId, CountryId, Code, Name (unique per company+country), Regime, Treatment, LegacyTaxId (nullable, migration only), IsActive |
| `TaxProfileVersions` | Id, TaxProfileId, EffectiveFrom (date), EffectiveTo (date, nullable), TotalPercent (display), CreatedAt/By; exclusion on overlapping ranges per profile |
| `TaxProfileVersionComponents` | Id, TaxProfileVersionId, TaxComponentId, Applicability, RatePercent numeric(9,4) null, AmountPerBaseUnit numeric(18,6) null, SortOrder; CK exactly one of rate/amount matching the component's method |
| `TaxAssignments` | Id, CompanyId, ScopeType (Company/Category/Subcategory/Product), ScopeId, CountryId, SalesTaxProfileId, PurchaseTaxProfileId (null = same); unique (CompanyId, ScopeType, ScopeId, CountryId) |
| `SalesInvoiceLineTaxes`, `PurchaseOrderLineTaxes`, `SalesReturnLineTaxes`, `PurchaseReturnLineTaxes` | Id, LineId, TaxComponentId (null for legacy), ComponentCode, Kind, Method, RatePercent, AmountPerBaseUnit, BaseAmount, TaxAmount (+ Original* on purchase rows for document currency) |

New columns:
- `Products.HsnCode` (≤8), `Products.PriceIncludesTax` (bool null); `ProductSubcategories.HsnCode`.
- `BusinessSettings.SellingPricesIncludeTax` (bool, default true); `BusinessSettings.MaxBillDiscountPercent`
  (numeric(5,2), null = no limit) (T15).
- Sales/purchase lines: `TaxProfileId`, `TaxProfileVersionId`, `PlaceOfSupply`, `PriceIncludesTax`, `HsnCode`,
  `BillDiscountShare`, `BillChargeShare` (+ Original* on purchase lines). Return lines: `TaxProfileId`,
  `TaxProfileVersionId`, `PlaceOfSupply` copied from the original line.
- Sales invoice / purchase headers: `BillDiscountPercent` (null), `BillDiscountAmount`, `BillCharges`,
  `TaxableTotal`, `PlaceOfSupplyStateId` (snapshot), `PartyTaxStatus` (string, default `Unspecified`);
  purchase header `RatesIncludeTax`.

Migration plan (D10: additive first, destructive last, never startup auto-migrate in production):
1. **M1 additive + backfill (one release):** create tables and nullable columns. Per company, seed components for its
   country. Each `Taxes` row → `TaxProfiles` (LegacyTaxId set; regime/treatment from country; rate 0 → `NilRated`
   and listed in a review report) with one version from `2000-01-01`: India → CGST/SGST halves intra, IGST inter;
   GCC → VAT. `Products.TaxId` → product-level `TaxAssignments` (sales = purchase). Historic lines get
   `TaxProfileId` from LegacyTaxId and **one legacy tax row** (`TaxComponentId` null, `ComponentCode` = the old tax
   name, rate = `TaxRatePercent`, amount = `TaxAmount`) — history is not split into CGST/SGST (BR24).
   Validation SQL: every product resolves; every line has ≥1 tax row; Σ rows = line `TaxAmount`; header totals unchanged.
2. **M2 code switch:** services use the resolver and calculator; `Product.TaxId` and line `TaxId` are still written
   (mirror of the profile's legacy tax where one exists) so a rollback to the previous build keeps working.
3. **M3 destructive (later release, after sign-off):** drop `Products.TaxId`, line `TaxId` columns and FKs, retire
   `Taxes` and `/api/v1/taxes`.

## API contract

Draft; finalised at the API-contract gate.

| Method | Route | Permission | Request | Response | Errors |
|---|---|---|---|---|---|
| GET | /api/v1/tax-components | Tax.View | — | TaxComponentDto[] | — |
| POST/PUT | /api/v1/tax-components[/{id}] | Tax.Manage | code, name, kind, method | TaxComponentDto | 400 duplicate code; 400 method change when used |
| GET | /api/v1/tax-profiles?countryId=&active= | Tax.View | — | TaxProfileDto[] (current version, components) | — |
| GET | /api/v1/tax-profiles/{id} | Tax.View | — | TaxProfileDetailDto (all versions) | 404 |
| POST/PUT | /api/v1/tax-profiles[/{id}] | Tax.Manage | code, name, countryId, regime, treatment | TaxProfileDto | 400 field errors |
| POST | /api/v1/tax-profiles/{id}/versions | Tax.Manage | effectiveFrom, components[] | VersionDto | 400 overlap, incomplete GST set, unequal CGST/SGST |
| POST | /api/v1/tax-profiles/{id}/status | Tax.Manage | isActive | TaxProfileDto | — |
| GET | /api/v1/tax-assignments?countryId= | Tax.View | — | tree: category → subcategory → overrides, with resolved source | — |
| PUT | /api/v1/tax-assignments | Tax.Manage | scopeType, scopeId, countryId, salesProfileId, purchaseProfileId | AssignmentDto | 400 profile/country mismatch, inactive |
| DELETE | /api/v1/tax-assignments/{id} | Tax.Manage | — | 204 | 400 deleting the company default |
| POST | /api/v1/tax/preview | Sales.Create or Purchases.Create | branchId, partyId, date, direction, lines[], bill discount/charges | per-line breakdown + totals (same calculator as save) | 400 per line |

Product DTO gains `hsnCode`, `priceIncludesTax`, resolved `salesTaxProfile`, `purchaseTaxProfile`, `taxSource`.
Sales, purchase and return line DTOs gain `taxes[]`, `placeOfSupply`, `priceIncludesTax`, `billDiscountShare`,
`billChargeShare`, `hsnCode`; headers gain `taxableTotal`, `billDiscount*`, `billCharges`.

## Delivery phases

Each phase is approved, built, verified and reviewed before the next.

| Phase | Content |
|---|---|
| TP-1 Masters | Components, profiles, versions, assignments, HSN fields, company price-basis setting; Tax setup screen; product form tax section; M1 migration + validation SQL; resolver; standard profile seeds (T12) |
| TP-2 Calculator | Single tax calculator (BR16–BR22) with unit tests for every AC; `/tax/preview` |
| TP-3 Purchases | Purchase + purchase return on the calculator: component rows, place of supply, `RatesIncludeTax`, bill discount/charges into landed cost |
| TP-4 Sales and POS | Sales, POS and sales return on the calculator: inclusive pricing, bill discount/charges, invoice print with components and HSN |
| TP-5 Reports and clean-up | Tax reports from component rows, HSN summary; M3 destructive migration after sign-off |
| Later | Party tax status, financial credit notes + discount on payment, charges as service lines, reverse charge, imports, round-off, ledger posting, GSTR/VAT exports, e-invoicing |

## Decisions

- T1 India GST and GCC VAT supported from day one.
- T2 Selling prices include tax by default (company setting), with a per-product override.
- T3 Product tax lookup: product override → subcategory → category → company default.
- T4 Party GST status: screens later; the calculation takes a party-status input from phase 1 (`Unspecified`).
- T5 Cess is a tax component with method Percent, AmountPerBaseUnit, or both on one profile.
- T6 No after-tax discount in phase 1; later delivered as financial credit note + discount on payment.
- T7 Whole-bill discount and whole-bill charges are spread across lines by value before tax; each line stores its share.
- T8 Rates are effective-dated profile versions; a version used by history is immutable.
- T9 Inclusive rounding: the paisa difference goes into the taxable value, never into a component, so the
  price paid is exact and CGST = SGST. (The prototype puts it on the last component; the spec supersedes it.)
- T10 Historic lines get one legacy tax row; history is never split into CGST/SGST retroactively.
- T11 (Q3) A GST taxable product without an HSN code gives a warning in phase 1 (save and sale are allowed); it becomes
  an error once the HSN summary report ships.
- T12 (Q5) New companies are seeded with standard profiles for their country: India — GST 0% Nil rated, Exempt, 5%,
  18%, 40% with CGST/SGST/IGST components; GCC — VAT standard rate (AE 5%, SA 15%, BH 10%, OM 5%), Zero-rated and
  Exempt. Existing companies get the same seed in M1 next to their migrated legacy profiles (names that already exist
  are skipped and reported). For a new company the standard rate (GST 18% or the country VAT rate) becomes the company
  default assignment. Seeded profiles are ordinary rows the company can edit, version or deactivate.
- T13 (Q1) Ledger posting per component is out of scope. The account placeholder columns are also deferred to the
  ledger work (owner, TP-1 WP1 review).
- T22 The TP-1 API contract (section "TP-1 design › API") is approved; WP1 is done (commit 8668a04).
- T14 (Q2) A branch in India with no state uses the company's state (`BusinessSettings.StateId`) for place of supply;
  only when neither is set are GST documents refused.
- T15 (Q4) Whole-bill discount on sales needs the new permission `Sales.BillDiscount` and is capped by
  `BusinessSettings.MaxBillDiscountPercent` (empty = no cap).
- T17 Design deviations a–g in the TP-1 design are approved.
- T18 (DQ1) Existing companies keep `SellingPricesIncludeTax = false` (stored prices keep their meaning); new companies
  get `true`.
- T19 (DQ2) Until TP-4 ships, a new version's `effectiveFrom` may not be later than today (400 `effectiveFrom`).
- T20 (DQ3) TP-1 adds a Mapping-tab action "Clear overrides equal to the inherited profile" (per company and country,
  confirm step, reports how many were cleared).
- T21 (DQ4) Company default for existing companies is the standard rate (GST 18% / country VAT standard); the
  most-used legacy tax is only the fallback when no standard profile exists for that company.
- T23 (BA consult, owner away; lead chose the BA recommendation) Platform data reset and tax tables:
  `keepTaxes=true` keeps Taxes, TaxComponents, TaxProfiles, TaxProfileVersions and TaxProfileVersionComponents;
  keeps company-scope TaxAssignments; keeps category/subcategory-scope assignments only with `keepCategories=true`;
  always deletes product-scope assignments (products are always wiped); restores a missing company default per T21.
  `keepTaxes=false` wipes all nine tax tables and then re-seeds the T12 standard profiles and company default in the
  same operation, so the company can create products immediately. The reset preview shows TaxAssignments as a
  partial action and reports re-seeded profiles. Alternatives considered: keep masters but wipe all assignments
  (loses the default, product create fails); remove the option (breaks the API contract).
- T24 (TP-1 review, lead decision while owner away) Changing `BusinessSettings.CountryId` is allowed only while the
  company has no products and no sales/purchase documents; the standard profiles and company default of the new
  country are then seeded (old-country profiles stay, unused). Otherwise 400 on `countryId`. Reason: a later change
  would leave product mirrors and history on the old country's taxes.
  Refinement (implementation): once products or documents exist, an omitted `countryId` on the settings PUT keeps the
  current country and state instead of clearing them; only an explicit different country is refused.
- T25 (TP-1 review) A profile can be assigned (company, category, subcategory or product) only if it has a version
  valid today; a new version may not start on or before the date of the latest document that used the profile
  (BR4: a used version's dates are immutable). Settings PUT is a full replace: omitting `maxBillDiscountPercent`
  clears the cap (the UI always sends it). Legacy tax names matching a standard profile become the company default
  only when the rate also matches.
- T16 (Q6) A stored selling price is in the product's price basis: what the shelf label says (tax included when the
  product's prices include tax). Changing a product's basis does not convert existing prices; the UI warns.

## TP-1 design

Status: **approved by the owner** (design gate passed; deviations a–g and DQ1–DQ4 decided, see T17–T21). Produced by the solution architect from this spec and the
code on `analyse_copy`. Nothing below is implemented yet.

### Key design decisions
- **D1 Line tax tables in M1.** M1 creates the four `*LineTaxes` tables and inserts one legacy row per historic line
  (AC16), so history is migrated once. M1 does **not** alter any line or header table; line/header columns
  (`TaxProfileId`, `PlaceOfSupply`, `PriceIncludesTax`, `HsnCode`, bill shares, header totals) arrive in TP-3/TP-4.
  Billing keeps running during M1 (line tables only take ACCESS SHARE).
- **D2 Gap lines.** From TP-1 the four write paths (`SalesService.cs:641`, `:437`; `PurchaseService.cs:730`, `:495`)
  add one legacy row via a shared `LegacyLineTax.Build` helper. Lines saved by the old build between the M1 commit and
  the app swap are caught up by re-running the frozen, idempotent `.legacy-rows.sql` in the TP-3 and TP-4 migrations.
- **D3 Mirror tax from TP-1.** Every `TaxProfile` has a mirror `Taxes` row (`LegacyTaxId`, kept until M3). Billing stays
  on `Product.Tax` in TP-1 (`SalesService.cs:635`, `PurchaseService.cs:700`, `SalesController.cs:102`), so
  `Product.TaxId` = mirror of the product's resolved sales profile for the company country, kept in sync by
  ProductService and TaxAssignmentService. Mirror `RatePercent` = TotalPercent of the version valid today. The
  previous build can be redeployed with no database rollback.
- **D4 History `TaxProfileId` deferred.** Not set in M1 (it would rewrite the largest tables). History stays traceable
  1:1 through line `TaxId` → `TaxProfiles.LegacyTaxId`; TP-5 M3 backfills it in batches before dropping `TaxId`.
- **D5 No exclusion constraint for version overlap** (needs `btree_gist`, not used anywhere, untestable in InMemory).
  Instead: versions are append-only (new `EffectiveFrom` ≥ latest), profile row locked `FOR UPDATE`, unique
  (profile, from), one open version per profile, plus a blocking V__ overlap check.
- **D6 Rules spanning rows** (equal CGST/SGST, complete GST set, TotalPercent) are service rules plus blocking V__ checks.

### Deviations from the draft database design (approved, T17)
| # | Change | Reason |
|---|---|---|
| a | `TaxComponents.CountryId` | A company with branches in several GCC countries gets one VAT component per country |
| b | Typed `CategoryId` / `SubcategoryId` / `ProductId` on `TaxAssignments` instead of `ScopeId` | Real same-company foreign keys and per-scope unique indexes |
| c | `Method` copied onto version components | Composite FK makes "rate matches the component's method" a database rule |
| d | History `TaxProfileId` deferred to M3 (D4) | Avoids rewriting the largest tables in M1 |
| e | VAT rate 0 → `ZeroRated` (spec said `NilRated`) | `NilRated` is a GST term |
| f | Mirror tax for every profile from TP-1 (D3) | Applies the spec's M2 rollback rule early |
| g | `/api/v1/taxes` becomes read-only in TP-1 | Writes would bypass profiles |

### Domain and persistence
- Enums: `TaxComponentKind`, `TaxCalculationMethod`, `TaxRegime`, `TaxTreatment`, `TaxApplicability`, `TaxScope`.
- Entities (all `ICompanyOwned`, company filter automatic, `SelloraDbContext.cs:141-148`): `TaxComponent`, `TaxProfile`
  (`LegacyTaxId` NOT NULL after M1), `TaxProfileVersion`, `TaxProfileVersionComponent`, `TaxAssignment`; line tax
  entities share a C# base, one table each, filtered through their parent like `SelloraDbContext.cs:150-157`.
- Key constraints: unique (CompanyId, CountryId, Code) on components and profiles; unique lower(Name) per company and
  country; unique (TaxProfileId, EffectiveFrom); one open version per profile; CK on rate vs amount per method;
  CK on assignment scope shape with partial uniques per scope; composite FK (CompanyId, CountryId, ProfileId) enforces
  BR10; `HsnCode` CK `^[0-9]{4}([0-9]{2}){0,2}$`; `MaxBillDiscountPercent` CK 0–100.
- File: new `Data/Configurations/TaxProfileConfigurations.cs`; line tax configs next to the existing sales/purchase
  configurations.

### Migration M1 `AddTaxProfiles` (one transaction, `SET LOCAL lock_timeout = '5s'`)
1. Create the five master tables.
2. Run `seed.sql` + `backfill.sql` (frozen, SHA-pinned resources as `MigrationSql.cs:9`; deterministic
   `md5('sellora:…')::uuid` ids like `DbSeeder.Md5Guid`):
   preflight (a company with taxes but no `BusinessSettings.CountryId` fails the release), components per company by
   country, one legacy profile + version `2000-01-01` per `Taxes` row (India: CGST/SGST halves intra, IGST inter;
   GCC: VAT; rate 0 → NilRated / ZeroRated and reported), standard seeds (T12, skipped by name or code and reported)
   with their mirror taxes, product-level assignments from `Products.TaxId` (AC17), company default assignment.
3. Create the four line tax tables (without line FKs), run `legacy-rows.sql` (idempotent), add the line FKs `NOT VALID`.
4. Add columns last: Products, ProductSubcategories, BusinessSettings (shortest lock on the per-request tables).
5. Insert permissions and grants: `Tax.View` → holders of Masters.View / Inventory.Create / Inventory.Edit;
   `Tax.Manage` → Masters.Edit holders; `Sales.BillDiscount` → Sales.PriceOverride holders.

Second migration in the same release: `ValidateTaxLineForeignKeys` (VALIDATE CONSTRAINT ×4, does not block inserts).

Validation SQL (`backend/db/validation`):

| File | Mode | Checks |
|---|---|---|
| `V__tax-profiles-integrity.sql` | block | version overlap, profile without open version, Taxable version without components, non-taxable version with components, component/profile country mismatch, missing mirror |
| `V__tax-gst-rules.sql` | block | GST Taxable version missing CGST, SGST/UTGST or IGST; CGST ≠ SGST; IGST ≠ CGST+SGST; VAT in a GST profile; TotalPercent ≠ Σ percent |
| `V__tax-products-resolve.sql` | block | product not resolving, or `Products.TaxId` ≠ resolved profile's mirror |
| `V__tax-line-rows-consistency.sql` | block | Σ rows ≠ line TaxAmount; legacy row base ≠ TaxableValue |
| `V__tax-line-rows-missing.sql` | warn (block from TP-4) | line without tax rows |
| `V__tax-mirror-drift.sql` | warn | mirror rate or IsActive ≠ current version |

Release-notes queries: zero-rate taxes converted, skipped seeds, company default chosen, before/after counts.
Rollback: app-only (purely additive, mirrors keep the old build working); `Down()` is dev-only.

### Standard seeds for new companies (T12)
`Data/TaxSeedData.cs` is the single C# source (twin of `GeoSeedData`); `TaxSeeder.SeedCompanyAsync` uses the same
md5 ids and is called from `PlatformService.CreateCompanyAsync` (`PlatformService.cs:244`) and from DbSeeder
(replacing `SeedTaxesAsync`, `DbSeeder.cs:434-453`). No country → nothing seeded; `POST /tax-profiles/seed-standard`
seeds later and is idempotent. A drift test compares M1's output with `TaxSeedData` for IN/AE/SA/BH/OM/QA.

### Services (interfaces in Domain, implementations in Infrastructure)
- `ITaxComponentService` — create / update (400 `method` once used) / set active.
- `ITaxProfileService` — create (regime must match country), update (code, name only), `AddVersionAsync`
  (BR3 set rules; earlier date → 400 `effectiveFrom`; same date replaces only an unused version; later date closes the
  previous one; TotalPercent ≤ 3 dp; mirror sync), set active (blocked while assigned), seed standard.
- `ITaxAssignmentService` — upsert / delete (company default cannot be deleted); re-syncs `Products.TaxId` under a
  per-company advisory lock.
- `ITaxResolver` — `ResolveAsync`, `ResolveManyAsync`, `PreviewAsync` → profiles, source, HSN and HSN warning;
  `ResolveDocumentCountryAsync` (branch → company → 400); `ResolveVersionAsync` (BR5, ready for TP-3/4).
- `ProductService` — input drops `TaxId`, gains `HsnCode`, `PriceIncludesTax`, optional sales/purchase override
  profiles for the company country; writes the mirror `TaxId`; returns HSN warnings (T11).
  `ProductCategoryService` — subcategory `HsnCode`.

### API
- `TaxComponentsController` `/api/v1/tax-components`; `TaxProfilesController` `/api/v1/tax-profiles`
  (list, detail, create, update, `/{id}/versions`, `/{id}/status`, `/seed-standard`); `TaxAssignmentsController`
  `/api/v1/tax-assignments` (tree, `/resolve`, upsert, delete). Reads `Tax.View`, writes `Tax.Manage`.
- Permissions added to `PermissionCatalog.cs`, `R__seed-permissions.sql` and DbSeeder grants.
- Product DTO gains `hsnCode`, `effectiveHsnCode`, `hsnSource`, `priceIncludesTax`, resolved sales/purchase profile,
  `taxSource`, override ids, `warnings`; request no longer requires `taxId`. Business settings DTO gains
  `sellingPricesIncludeTax`, `maxBillDiscountPercent`.
- `/api/v1/taxes`: GET kept (deprecated); POST/PUT/status removed.

### Frontend
- `features/inventory/tax-setup/` (tabs: Components, Profiles with version timeline and add-version form, Mapping tree
  per country), route `inventory/tax-setup` (Tax.View), nav entry "Tax setup"; Administration › Taxes removed.
- New `product-form/product-tax-section/` child: resolved source, override toggle, purchase "same as sales", HSN with
  inherited placeholder and warning, price basis (Company default / Inclusive / Exclusive) with the T16 warning.
- Business settings page gets the two new fields.
- **Sequencing:** `feature/category-ux` edits the same product-form files; it must be merged into `analyse_copy` and
  then into `feature/tax-profiles` before frontend work starts.

### Tests
- xUnit: every BR3/BR4 rule, AC10–AC14, AC20, AC21, mirror sync, assignment guards, seeder idempotency, legacy row on
  the four write paths.
- Postgres integration: AC16, AC17, seed skip, default choice, preflight failure, re-run no-op, constraints and
  composite FKs, lock_timeout, Down/Up, NOT VALID → VALIDATE, each V__ file (clean then probe), drift and SHA pin.
- Vitest: GST version rules, HSN validator, product tax section, add-version form, mapping tab.

### Work packages (max two agents at once)
1. database-engineer — entities, enums, configurations, M1 + validate migration, SQL resources, V__ files,
   `TaxSeedData`, permission seed. Gate: this design approved.
2. backend-engineer — services, resolver, seeder, product/category services, legacy-row write paths, controllers,
   contracts, permissions; in parallel test-engineer — migration and integration tests. Gate: API contract approved.
3. frontend-engineer (after the API gate and the category-ux merge); in parallel test-engineer — xUnit and Vitest.
4. code-reviewer, then `scripts/verify.sh all`.

### Risks
- ALTERs on Products and BusinessSettings hold ACCESS EXCLUSIVE until commit; they run last under the 5 s lock timeout.
- Legacy-row insert time grows with line count (billing continues).
- Mirror rate can lag a future-dated version (warn check; see design question DQ2).
- The product form edits only the company-country override; other countries use the Mapping tab.

### Design questions (decided: T18–T21)
- DQ1 `SellingPricesIncludeTax` for **existing** companies: keep `false` (today's prices keep their meaning;
  recommended) or `true` (BR14 default, silently changes what existing prices mean at TP-4)? New companies get `true`.
- DQ2 Until TP-4, reject versions with `effectiveFrom` later than today (legacy billing cannot switch rate on a date)?
  Recommended yes.
- DQ3 After M1 every product carries an override, so category mapping has no effect. Add a "Clear overrides equal to
  the inherited profile" action on the Mapping tab in TP-1? Recommended yes.
- DQ4 Company default for existing companies: the standard rate (GST 18% / country VAT; recommended) or the most-used
  legacy tax?

## Deployment notes

### TP-1 release runbook (additions)
1. Before the release: run section P of `scripts/sql/tax-profiles-release-notes.sql`. Every company that has taxes must
   have `BusinessSettings.CountryId`; otherwise `AddTaxProfiles` stops the release (preflight, by design).
2. A database upgraded from an old baseline in one idempotent script gets `CountryId` from the earlier
   country/state migration in the same run. If its backfill cannot link a company's country (see that migration's
   unmatched report), set the country manually between the two migrations, or the preflight fails.
3. Back up, then apply `AddTaxProfiles` and `ValidateTaxLineForeignKeys` from the Database releases page (never at
   startup, D10). Expect short ACCESS EXCLUSIVE locks on Products, ProductSubcategories and BusinessSettings at the end
   of the transaction (lock_timeout 5 s; on 55P03 retry).
4. After the release: all `V__tax-*.sql` checks must return zero failing rows (the missing-rows and mirror-drift
   checks warn only). Then run sections R1–R7 and send the owner: zero-rate conversions, skipped standard seeds,
   company default per company (R7 lists defaults whose rate differs from the standard), companies without a country.
5. Deploy the TP-1 application build. Rollback is application-only: the previous build keeps working because every
   profile has a mirror tax and `Products.TaxId` is maintained.
6. Not verified before release while CI is down (until 1 Dec): Testcontainers suites that run psql inside the
   container (release rehearsal, idempotent-script psql paths) and PostgreSQL 17. Run full CI green before any deploy.

- M1 runs as a manual, pre-deploy database release (D10), never at startup. Back up first; validation SQL must return
  zero failing rows before M2 code is deployed.
- M2 keeps writing legacy `TaxId` mirrors so the previous build can be redeployed without a database rollback.
- M3 ships in a separate release after owner sign-off that reports and returns are correct on the new rows.
- Rates in seeds are examples; confirm against current GST notifications and GCC rates before production
  (the tobacco/cess position after 2025 was unclear in research).
