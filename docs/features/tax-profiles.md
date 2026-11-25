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
| POST | /api/v1/tax/preview | Sales.Create or Purchase.Create | branchId, partyId, date, direction, lines[], bill discount/charges | per-line breakdown + totals (same calculator as save) | 400 per line |

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
- T26 TP-1 lead decisions T23–T25 confirmed by the owner. TP-2 design approved with DQ-TP2-1 to DQ-TP2-7 as
  recommended. PR #39 (TP-1) waits for CI before merging.
- T27 TP-3 design approved; TP3-Q1 to TP3-Q6 as recommended. No company uses a 3-decimal base currency, so the
  existing numeric(18,2) purchase header columns stay; new header columns use numeric(18,4).
- T28 TP-3 API contract (section "TP-3 design › API / DTO") approved; TP-3 WP1 done (commit b20c667).
- T29 TP-4 design approved; TP4-Q1 to TP4-Q9 as recommended. TP4-Q2: no company has live sales yet, so no company's
  stored prices change meaning at release; P3 stays in the release notes as a safety check.
- T30 TP-4 API contract (section "TP-4 design › API / DTO") approved; TP-4 WP1 done (commit 39dfaed). Runbook order:
  AddSalesTaxSnapshot → CONCURRENTLY index pre-create → ValidateSalesTaxConstraints.
- T31 (TP-4 review, lead decisions) (a) Screens and APIs that show a product's tax rate (POS catalog, product detail)
  read the version valid today through the resolver, not the mirror tax; the mirror stays for rollback and is refreshed
  on tax writes; before rolling back to a pre-TP-4 build, refresh mirrors (runbook). (b) The sales invoice response
  carries the seller's name, address, GSTIN/TRN, state name and GST state code from the server, so every printed tax
  invoice shows them regardless of the cashier's permissions; place of supply carries its GST state code. (c) For a
  customer in another country, the place-of-supply state is left empty. (d) `AddSalesTaxSnapshot` locks the referenced
  tables (States, TaxProfiles, TaxProfileVersions) before the sales tables. (e) Accepted: numeric(18,2) header columns
  and numeric(6,3) line `TaxRatePercent` (display only), noted in the release notes.
- T32 TP-5 design approved; TP5-Q1 to TP5-Q11 as recommended. TP-5 ships as 5a (additive) then 5b (destructive, only after
  a separate owner sign-off, CI green and one filed return period from the new reports). HSN quantity sums across
  products are allowed only within one HSN and identical base unit (bounded exception to the no-cross-product-sum rule).
- T33 TP-5a API contract (section "TP-5 design › API / frontend") approved; TP-5a WP1 done (commit 7880953).
  `Reports.Tax` is branch-scopable like `Reports.Sales`.
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

## TP-2 design (tax calculator)

Status: **implemented and reviewed** on `feature/tax-calculator` (DQ-TP2-1 to DQ-TP2-7 as recommended, T26; DQ-TP2-8 from review).

### Scope
- In: a pure, deterministic calculator in the Domain (BR16–BR22, T9), a return-reversal helper (BR25), place-of-supply and
  rate-set resolution on `ITaxResolver`, `POST /api/v1/tax/preview`, unit tests for every listed AC.
- Out: sales, POS, purchase and return save paths stay on `SaleMoney` / `Product.Tax` until TP-3/TP-4. No schema change,
  no frontend.

### Calculator (`Sellora.Domain/Taxes/`)
- Files: `TaxCalculation.cs` (records), `TaxCalculator.cs`, `TaxReversal.cs`, `TaxMoney.cs` (`Round` = AwayFromZero,
  twin of `SaleMoney.Round`, which lives in Infrastructure). New enums `PlaceOfSupply {IntraState, InterState,
  NotApplicable}`, `PartyTaxStatus {Unspecified}`, `TaxDirection {Sale, Purchase}`.
- Pure: no EF, no I/O, no clock, `decimal` only. Validation errors are collected into one `DomainValidationException`
  keyed `lines[i].field`, `billDiscount`, `billCharges`.
- Input: `TaxRateSet` (profile, version, regime, treatment, components with method/applicability/rate/amount) per line;
  `TaxCalcLine(Qty, BaseQty, UnitPrice, LineDiscount, OtherCharges, PriceIncludesTax, Rates)`;
  `TaxCalcDocument(Precision, PlaceOfSupply, PartyStatus, BillDiscountPercent?, BillDiscountAmount?, BillCharges, Lines)`.
- Output: per line gross, discount, bill shares, other charges, taxable, Σ applied percent, tax rows (base, amount), tax
  total, line total; header subtotal, discount, charges, taxable, tax, grand total, bill discount amount and effective
  percent (for the T15 cap).

### Algorithm (rounded at document precision, per component, per line)
1. `Gross = R(Qty × Price)`; discount/charges rounded; negatives, discount > gross, both bill percent and amount,
   percent outside 0–100, over-precise amounts and empty documents are field errors.
2. Components: GST uses `Applicability` = place of supply or `Always`; VAT uses all. Non-taxable treatments give one
   zero row carrying the treatment code (AC19).
3. Bill shares: weight = `Gross − LineDiscount` in the line's own basis (E2); discount = amount, or `R(W × pct)`;
   shares `R(D × w/W)`, remainder to the **largest-weight line** (DQ-TP2-1); discount share never exceeds its weight.
4. Exclusive: `Taxable = Gross − LD − BDS + BCS + OtherCharges`; percent rows `R(Taxable × r)`, per-unit rows
   `R(BaseQty × amount)`.
5. Inclusive (T9): `Incl = Gross − LD − BDS`; per-unit tax first (E3 if it exceeds `Incl`);
   `G0 = R((Incl − PU) / (1 + ΣP))`; percent rows from `G0`; taxable goods = `Incl − PU − Σ rows` (customer pays exactly
   `Incl`, CGST = SGST); bill/other charges are then added exclusive and taxed at the line's percent rates.
6. Zero lines (E10) give zero rows; the calculator accepts qty 0 for previews, saves still reject it.
7. Header = sums of lines (BR20/BR21), `GrandTotal = TaxableTotal + TaxTotal`.
8. Foreign currency (BR22): the calculator never sees an exchange rate; TP-3's PurchaseService converts each part and
   each row to base and stores document-currency values in the `Original*` columns.

### Place of supply and rates (`ITaxResolver` additions)
- `ResolvePlaceOfSupplyAsync(direction, branchId, partyId)`: country → regime (VAT → `NotApplicable`); own state =
  branch state, else company state (T14), else 400; customer/supplier state; no party state (walk-in) → intra-state
  (supplier without state also warns); equal states → intra, else inter. A party in another country → inter-state for now.
- `ResolveRateSetsAsync(direction, countryId, date, products)`: resolver + version valid on the document date (BR5),
  purchase profile for purchases (BR7), per-line error fields. Reused by TP-3/TP-4.

### Return reversal (BR25)
`TaxReversal` applies the existing proportional rule (last return takes the remainder; same as `SalesService.cs:414`)
to every tax row (and `Original*` for purchases), matching rows by component id or code, so legacy rows reverse too.
Return line tax = Σ reversed rows.

### `POST /api/v1/tax/preview`
- Policy `Sales.Create|Purchase.Create` (real codes; the spec's "Purchases.Create" was wrong), then the code matching
  `direction` is required.
- Request: `direction`, `partyId?`, `date`, `ratesIncludeTax?` (purchases), `billDiscountPercent?`,
  `billDiscountAmount?`, `billCharges?`, `lines[{productId, productUnitId, qty, unitPrice, discount, otherCharges?}]`.
  The branch is the working branch, as on save, so the preview always matches the save (DQ-TP2-5).
- Server: price basis = product → company setting for sales, `ratesIncludeTax` for purchases; precision = base currency
  (sales) or supplier currency (purchases); unit and quantity checks as on save; BR19a permission and cap. Read-only.
- Response: place of supply, regime, currency, precision, header totals, warnings (HSN T11, supplier without state) and
  per line: profile, version, source, HSN, price basis, base qty, money parts and tax rows.

### Tests
Pure calculator tests with the spec's numbers: AC1–AC9, AC15, AC19, AC23, E2, E3, E10, inclusive + charge, bill
percent, precision 0/2/3 cases, every validation rule. Invariants over 500 seeded random documents (Σ lines = header,
CGST = SGST, inclusive total exact, shares sum exactly and stay within weight, returns sum exactly). Parity with
`SaleMoney.Line` for single-component lines. Service tests for place of supply, purchase profile, field keys, BR19a and
currency precision; endpoint authorization test.

Worked checks (lead): AC7 → shares 60/40, taxable 540/360, tax 13.50+13.50 and 72.00+72.00, grand 1,071.00.
AC9 → shares 200/100, CGST = SGST 198/99. E2 (inclusive 118 + exclusive 100, bill discount 21.80) → shares 11.80/10.00,
both taxable 90.00, grand 212.40. Return of 2, 2, 3 out of 7 on CGST 85.50 → 24.43 / 24.43 / 36.64.

### Work packages
1. backend-engineer: enums, calculator, reversal, money helper, resolver extensions, preview service/controller/contracts
   (record signatures frozen first).
2. test-engineer, in parallel once signatures are frozen: the tests above.
3. code-reviewer, then `scripts/verify.sh all`. Acceptance: all tests green, `SaleMoney` and save paths unchanged, no
   migration.

### Design questions (decided as recommended, T26)
- DQ-TP2-1 Rounding remainder of bill shares: BR19 says "last line"; a tiny or zero-weight last line could go negative.
  [Give it to the largest-weight line, ties to the last; AC7/AC8 still hold.]
- DQ-TP2-2 Domestic purchases today store tax unrounded (up to 4 dp); BR16 rounds to currency precision, changing TP-3
  values by up to 0.005 per line. [Accept; note in TP-3 release notes.]
- DQ-TP2-3 Non-taxable rows and legacy rows both have no component id. [TP-3's additive migration adds a nullable
  `Treatment` column to the line-tax tables.]
- DQ-TP2-4 Return line tax = Σ reversed rows instead of today's line-level proportion; may differ by 0.01 on partial
  returns of multi-row lines, final return exact. [Accept; rows are authoritative.]
- DQ-TP2-5 Preview uses the working branch, not a `branchId` parameter. [Accept, so preview = save.]
- DQ-TP2-6 Per-unit row `BaseAmount` holds the base quantity, not money. [Accept; the invoice prints "qty × amount".]
- DQ-TP2-7 A party in another country is treated as inter-state until exports are in scope. [Accept.]
- DQ-TP2-8 (TP-2 review, lead decision) Bill charges on a document whose total line weight is 0 are spread
  equally over its lines (still taxed, sums exact). Sales lines carry no line-level other charges: a non-zero
  `otherCharges` on a sale line is rejected (400 `lines[i].otherCharges`). Preview requests are bounded (quantity,
  price, amounts, at most 500 lines) so oversized input gives 400, never 500.

Risks: CGST and SGST rounded separately can differ by a paisa from today's single-rate tax, so TP-4 totals for the same
cart may move by 0.01 (release notes). At precision 0 the T9 rule moves the rounding into the taxable value visibly
(inclusive 99 at GST 5% → taxable 95, CGST 2, SGST 2); this is correct but should be shown to the owner with GCC/India
precision 2–3 being the normal case.

## TP-3 design (purchases on the calculator)

Status: **implemented and reviewed** (TP3-Q1..Q6 as recommended, T27). Branch `feature/tax-purchases`.

### Scope
- Changes: purchase create (place of supply + rate sets + `TaxCalculator`), purchase returns (`TaxReversal`), purchase
  DTOs and controllers, purchase register report (minimal), purchase form / detail / print / return screens.
- Purchases have **no line edit** today (header PUT changes invoice number and notes only; cancel does not touch tax
  rows), so there is no "recompute an old purchase" case.
- Unchanged: sales, POS and sales returns (TP-4); `Taxes`/`TaxId` (dropped in TP-5); party status stays `Unspecified`;
  no ledger posting; full tax reports and HSN summary (TP-5); imports and reverse charge (later).

### Migration `AddPurchaseTaxSnapshot` (additive, one transaction) + `ValidatePurchaseTaxConstraints`
All new columns are nullable or have a constant default (no table rewrite; the TP-2 build can still insert during the
swap and can be redeployed without a database rollback).

| Table | New columns | History value |
|---|---|---|
| PurchaseOrders | `RatesIncludeTax` (default false), `BillDiscountPercent` (null), `BillDiscountAmount`, `BillCharges`, `OriginalBillDiscountAmount`, `OriginalBillCharges` (default 0), `TaxableTotal` numeric(18,4) null, `PlaceOfSupply`, `PlaceOfSupplyStateId`, `SupplierStateId` (null), `PartyTaxStatus` (default `Unspecified`) | defaults; `TaxableTotal` null = legacy, DTO falls back to Subtotal + OtherChargesTotal − DiscountTotal |
| PurchaseOrderLines | `TaxProfileId`, `TaxProfileVersionId`, `PlaceOfSupply`, `HsnCode` (null), `PriceIncludesTax` (default false), `BillDiscountShare`, `BillChargeShare` (default 0), `OriginalRate`, `OriginalDiscount`, `OriginalOtherCharges`, `OriginalBillDiscountShare`, `OriginalBillChargeShare`, `OriginalTaxableValue`, `OriginalTaxAmount`, `OriginalLineTotal` (null) | null / defaults (profile ids backfilled in M3, D4) |
| PurchaseReturnLines | `TaxProfileId`, `TaxProfileVersionId`, `PlaceOfSupply`, `OriginalTaxableValue`, `OriginalTaxAmount` (null) | null |
| All four `*LineTaxes` | `Treatment` (null = legacy row; DQ-TP2-3) | null |

Order: `lock_timeout 5s`; re-run the frozen `AddTaxProfiles.legacy-rows.sql` first (D2 catch-up, idempotent, same
SHA pin); add columns PurchaseReturnLines → PurchaseOrderLines → PurchaseOrders with FKs/CKs `NOT VALID`; add
`Treatment` to the four line-tax tables **last** (shortest lock on the sales tables). The second migration validates
the constraints and creates the FK indexes (briefly blocks purchase writes only). Rollback is application-only.

Validation SQL:
- `V__tax-line-rows-consistency.sql`: legacy filter becomes `TaxComponentId IS NULL AND Treatment IS NULL` (otherwise
  non-taxable calculator rows pass as legacy); add Σ row `OriginalTaxAmount` = line `OriginalTaxAmount`.
- New `V__tax-line-rows-missing-purchases.sql` (**block**): purchase and purchase-return lines without rows; the
  existing warn file then covers sales only until TP-4.
- New `V__tax-purchase-snapshot.sql` (block): version belongs to the profile and company; line `TaxId` = the profile's
  mirror; `LineTotal = TaxableValue + TaxAmount`; every new row has `Treatment`; intra lines have no IGST, inter lines no
  CGST/SGST, CGST = SGST; header sums equal line sums; return lines carry the original's version.
- New `scripts/sql/tax-purchases-release-notes.sql`: P (before) Indian companies with neither branch nor company state —
  their purchases are refused after TP-3; R1 catch-up rows; R2 suppliers without state; the rounding note.

### Service changes
- Resolver: `ResolvedLineRates.MirrorTaxId`; `PlaceOfSupplyResolution.OwnStateId`, `PartyStateId` (trailing fields).
- Create: resolve place of supply (branch, supplier) → rate sets for the purchase date (BR5, BR7/AC13) → calculator in the
  **document currency** (precision = transaction currency; `RatesIncludeTax`; bill discount/charges) → convert each part
  and each row to base with today's `ToBase` (per-unit row base = quantity, not converted). Line `TaxAmount` = Σ
  converted rows; `TaxableValue` = sum of converted parts (exclusive) or converted taxable (inclusive);
  `Original*` = calculator values (also for domestic, so null always means legacy).
- **Landed cost:** `UnitCost = TaxableValue / BaseQty` as today; the taxable value now includes the bill charge share and
  line other charges and is net of line and bill discount, so AC9 reaches `PurchaseBatch.UnitCost`. Cost still excludes
  all tax (TP3-Q3).
- Mirror fields: line `TaxId` = the **purchase** profile's mirror; `TaxRatePercent` = Σ applied percent rates. This keeps
  the TP-2 build and the T25 "used version" check working.
- One tax row per calculator row with `Treatment`; the legacy-row helper is no longer called for new purchases.
- Header: `Subtotal` = Σ gross (tax-inclusive for inclusive lines), `DiscountTotal` = Σ (line + bill discount),
  `OtherChargesTotal` = Σ (other + bill charges), `TaxTotal`, `TaxableTotal`, **`GrandTotal` = Σ line totals** (today's
  formula is wrong once lines can be inclusive), `OriginalGrandTotal` = Σ original line totals, place-of-supply snapshot,
  warnings returned (supplier without state, HSN).
- Concurrency (deviation from "locks unchanged"): take `FOR KEY SHARE` on the tax profiles used, after the product locks,
  so a purchase cannot commit on a version being superseded by `AddVersionAsync` (which locks the profile `FOR UPDATE`).
  No new deadlock path.
- Rounding (DQ-TP2-2): domestic gross, tax and totals now round to currency precision; new 400s for rates over 4
  decimals and over-precise discount/charges; error keys become `lines[i].field`.
- Returns: per-row `TaxReversal` (legacy single rows reverse exactly as today); return line tax = Σ rows (DQ-TP2-4);
  `Original*` reversed at document precision for TP-3 originals; copy profile, version, place of supply, treatment.
  Cancel unchanged.

### API / DTO
- `CreatePurchaseRequest`: optional `ratesIncludeTax` (default false), `billDiscountPercent`, `billDiscountAmount`,
  `billCharges` (default 0) — old clients unchanged.
- `PurchaseLineDto`: `taxes[]` (component, code, kind, method, rate, amount per unit, base, tax, original base/tax,
  treatment, isLegacy), `placeOfSupply`, `priceIncludesTax`, `hsnCode`, shares, originals, `taxProfileName`.
- `PurchaseDetailDto`: `taxableTotal` (fallback for history), rates-include-tax, bill discount/charges (+ originals),
  place of supply and state names, party status, warnings. Purchase return line DTO: `taxes[]`, place of supply,
  originals. Contract frozen before frontend work.

### Frontend
- New shared `shared/tax/tax-preview.service.ts` and `shared/tax/line-tax-breakdown/` (reused by TP-4).
- Purchase form: "Rates include tax" toggle, bill discount (percent or amount) and bill charges, live breakdown via
  `/api/v1/tax/preview` (debounced), per-line component rows, place-of-supply badge, warnings, `lines[i]` error mapping.
- Purchase detail/print: HSN, taxable, dynamic component columns (CGST/SGST/IGST/Cess/VAT; legacy shown as one row),
  bill discount/charges, place of supply. Purchase return form (estimated tax, server authoritative) and detail.

### Reports
Purchase register gains `taxableTotal` and tax by component kind (legacy rows under "Other"). Everything else in TP-5.

### Tests
- xUnit: AC9 incl. landed cost, AC13 and purchase-profile mirror, intra/inter/VAT/Exempt, inclusive (AC3/AC4), per-unit
  cess (AC5), BR5, supplier without state, missing own state, foreign currency (converted rows, Σ rows = tax, originals),
  AC15 on returns incl. originals, legacy-document return = old result, rounding change, T25 still enforced.
- Postgres: migration on TP-2-shaped data (defaults, no rewrite, catch-up, re-run no-op, NOT VALID → VALIDATE,
  lock timeout, Down/Up, old-build insert still works), every new/edited V__ file, purchase → return → cancel with stock
  reconciliation.
- Vitest: preview request and debounce, percent/amount exclusivity, error mapping, dynamic columns incl. legacy, return
  estimate.

### Work packages
1. database-engineer: entities, configurations, two migrations, V__ files, release notes (gate: design approved).
2. backend-engineer: resolver fields, PurchaseService, locks, contracts, controllers, report; test-engineer in parallel:
   fixture helper first, then Postgres migration/V__ tests (gate: API contract frozen).
3. frontend-engineer; test-engineer in parallel: xUnit service tests and Vitest.
4. code-reviewer, then `scripts/verify.sh all`.

### Design questions (decided as recommended, T27)
- TP3-Q1 Header `TaxableTotal` for history: null with a DTO fallback, or backfill every purchase (row locks, blocks
  purchase payments while it runs)? [Null + fallback.]
- TP3-Q2 Header money columns are numeric(18,2) while lines are (18,4); with a 3-decimal base currency (BHD, OMR, KWD)
  the header rounds silently and Σ lines ≠ header. [New header columns use (18,4); widening the existing ones rewrites
  the table — separate release. Owner: does any company use a 3-decimal base currency?]
- TP3-Q3 Landed cost excludes all tax, including tax that may not be recoverable (cess, VAT for unregistered buyers).
  [Keep as today; revisit with party status.]
- TP3-Q4 Suppliers in another country get IGST (as today); real import invoices carry no GST. [Keep with a warning;
  imports are "Later".]
- TP3-Q5 Foreign currency with inclusive rates can differ by one base unit between converted parts and taxable. [Allow;
  rare.]
- TP3-Q6 Profile `FOR KEY SHARE` lock on purchase save (deviation from "locks unchanged"). [Accept.]

Risks: Indian companies without branch and company state cannot purchase after release (check P first); new 400s for
over-precise input and `lines[i]` error keys may affect API clients sending many decimals; `Treatment` is added to
`SalesInvoiceLineTaxes` in a purchase phase (required by DQ-TP2-3, placed last to keep the lock short).

## TP-4 design (sales, POS and sales returns on the calculator)

Status: **implemented and reviewed** (TP4-Q1..Q9 as recommended, T29; review decisions T31). Branch `feature/tax-sales`.

### Scope
- `SalesService.CreateSaleAsync` is the only sale path: POS (`sales/new`) and the van workspace both post to
  `POST /api/v1/sales`. Plus sales returns, sales contracts, the POS catalog, sales detail/print, return screens and the
  sales report (minimal).
- Unchanged: cancel, payments, refunds, reversals, stock/FIFO.
- TP-5: drop `TaxId`/`Taxes`, history `TaxProfileId` backfill (D4), tax reports, HSN summary, HSN warning → error.
  Later: party status, round-off, thermal receipt, buyer GSTIN.

### Migration `AddSalesTaxSnapshot` (additive, one transaction) + `ValidateSalesTaxConstraints`
All columns nullable or with a constant default (no rewrite; the TP-3 build keeps inserting and can be redeployed).

| Table | New columns | History |
|---|---|---|
| SalesInvoiceLines | `TaxProfileId`, `TaxProfileVersionId` (null), `PlaceOfSupply`, `HsnCode` (null), `PriceIncludesTax` (default false — history lines were exclusive), `BillDiscountShare`, `BillChargeShare` (default 0) | defaults |
| SalesInvoices | `BillDiscountPercent` (null), `BillDiscountAmount`, `BillCharges` (default 0), `TaxableTotal` numeric(18,4) null (null = legacy; DTO falls back to Subtotal − DiscountTotal), `PlaceOfSupply`, `PlaceOfSupplyStateId`, `SellerStateId` (null), `PartyTaxStatus` (default `Unspecified`) | defaults |
| SalesReturnLines | `TaxProfileId`, `TaxProfileVersionId`, `PlaceOfSupply` (null) | null |

- For a sale the GST place of supply is the **buyer's** state: `PlaceOfSupplyStateId` = customer state, else own state;
  the seller's state goes in `SellerStateId` (TP-3 stores the own state, correct for purchases).
- Order: `lock_timeout 5s`; re-run the frozen legacy-rows SQL (catch-up); one `LOCK TABLE SalesInvoices,
  SalesInvoiceLines, SalesReturnLines IN ACCESS EXCLUSIVE MODE` in the application's own lock order, followed only by
  metadata ALTERs (milliseconds); CKs and FKs `NOT VALID`.
- `ValidateSalesTaxConstraints`: VALIDATE (does not block inserts); partial indexes on SalesInvoiceLines profile/version
  ids (`WHERE … IS NOT NULL`, `IF NOT EXISTS`) — the runbook pre-creates them `CONCURRENTLY` so sales are not blocked.
- Validation SQL: new `V__tax-sales-snapshot.sql` (block; profile/version/company, mirror TaxId, LineTotal = Taxable +
  Tax, Treatment set, intra/inter/VAT component rules, CGST = SGST, **inclusive exact-price invariant**, header sums,
  return lines carry the original's profile/version/place, returned rows never exceed the original and fully returned
  lines sum exactly); `V__tax-line-rows-missing.sql` switches to **block**.
- Release notes `scripts/sql/tax-sales-release-notes.sql`: P1 (blocking) Indian companies with neither branch nor company
  state — their POS stops; P2 sellable products with no sales profile version valid today; P3 companies with
  `SellingPricesIncludeTax = true` that already have sales (TP4-Q2); R1 catch-up; R2 non-walk-in customers in another
  state (now IGST); R3 walk-in customers with a state; the 0.01 rounding note.

### Service flow (mirrors TP-3 purchases)
1. Transaction, idempotency, customer, branch, number, location unchanged; idempotent replay returns stored rows.
2. Locks: products KEY SHARE → place of supply (branch, customer) → rate sets for the invoice date → tax profiles
   `FOR KEY SHARE` + re-check versions (400 `lines`) → locations → stock items. No new deadlock path.
3. Walk-in customers are always intra-state (resolver change, so preview = save). A customer without a state is
   intra-state without warning (BR12); a customer in another country is inter-state (DQ-TP2-7).
4. Line preparation keeps the existing checks (unit, decimals, open list price, list-price equality and
   `Sales.PriceOverride`, discount ≤ gross) with errors keyed `lines[i].field`. The stored selling price is the shelf
   price in the product's basis (T16); an inclusive line discount reduces the inclusive amount (BR17).
5. Price basis per line: product → company setting (shared `SalesTaxRules.PriceIncludesTax`, used by preview and save).
   Existing companies keep exclusive (T18).
6. Bill discount: 403 in the controller without `Sales.BillDiscount` at the working branch (re-checked in the service);
   cap check moved to a shared `SalesTaxRules.EnsureBillDiscountCap` (400 on the field sent).
7. Calculator at base precision, place of supply, bill discount/charges; sale lines carry no line charges (DQ-TP2-8).
8. Line: discount, taxable, tax = Σ rows, line total; mirror `TaxId` = sales profile mirror, `TaxRatePercent` = Σ
   percent; profile, version, place, HSN, price basis, shares; one tax row per component with `Treatment`.
9. Header: `Subtotal` = Σ gross (inclusive for inclusive lines), `DiscountTotal`, `TaxTotal`, `TaxableTotal`, bill
   amounts, **`GrandTotal` = Σ line totals**, place/state snapshots, HSN warnings.
10. POS cash: new optional `expectedGrandTotal`; if sent and different → 400 `expectedGrandTotal` ("The total is now
    X"), so cash is never taken on a stale total. Paid/due/change logic unchanged. An all-inclusive cart without bill
    charges totals exactly Σ(shelf × qty − line discount) − bill discount (T9).
11. Returns: `TaxReversal` per row (taxable share, rows, Σ rows = line tax, last return takes the remainder); copy
    profile/version/place/treatment; a line without rows falls back to one legacy row, so legacy invoices return as
    today.
12. Retired: `SaleMoney` and `LegacyLineTax` (no callers after TP-4) are deleted (TP4-Q8).
13. T19 (no future-dated versions) is lifted: both write paths resolve versions by date.

### API / DTO (append-only)
- `CreateSaleRequest`: `billDiscountPercent?` (0–100), `billDiscountAmount?`, `billCharges` (0), `expectedGrandTotal?`.
- `SalesInvoiceLineDto`: `taxes[]` (as TP-3, without originals), `placeOfSupply`, `priceIncludesTax`, `hsnCode`,
  shares, `taxProfileName`. `SalesInvoiceDetailDto`: `taxableTotal` (fallback), bill discount/charges,
  `placeOfSupply`, `regime`, place-of-supply and seller state names, `partyTaxStatus`, `warnings[]`.
  `SalesReturnLineDto`: `taxes[]`, `placeOfSupply`. `SaleCatalogProductDto`: `priceIncludesTax`, `hsnCode`.
  Sales report row: `taxableTotal`, tax by kind.
- Errors: `lines[i].field`, `lines`, `billDiscountPercent`/`billDiscountAmount`, `billCharges`, `branchId`,
  `expectedGrandTotal`, `customerId`. Keys move from `Lines` to `lines[i].*` (API release note); the controller returns
  every field error, not only the first. No new permissions.

### Frontend (reuses `shared/tax/*`)
- POS: server preview (debounced) drives line breakdown and totals; "Complete sale" waits for a pending preview and
  sends `expectedGrandTotal`; change and due from the preview total; inclusive badge per line; bill discount input only
  with `Sales.BillDiscount` (shows the cap error); bill charges input; `lines[i]` error mapping; precision from the
  preview.
- Sales detail / print: "Tax Invoice"; seller GSTIN (or TRN for VAT); place of supply as state name and code; per line
  HSN, qty/unit, rate (incl. tax note), discount, taxable, dynamic CGST/SGST or IGST (+ Cess) or VAT columns (legacy:
  one Tax column); footer taxable, tax per component, bill discount, bill charges, grand total. A4 only.
- Return form keeps an "estimate" column; return detail shows component rows; sales report gets taxable and tax-by-kind.

### Tests
- xUnit `Sales/SalesTaxTests.cs`: AC1–AC8, AC18, AC19, AC22 (403 and cap), AC23, walk-in, VAT branch, E3, bill
  charges, `expectedGrandTotal` mismatch, idempotent replay, BR5, T25, preview total = save total, AC15 returns, legacy
  invoice return, inclusive final return exact, payments/due/change on inclusive totals, cancel unchanged; `SaleMoney`
  parity tests replaced by fixed expected values.
- Postgres: migration (no rewrite, catch-up, NOT VALID → VALIDATE, lock timeout, Down/Up, old-build insert),
  constraints, every new/changed V__ file, POS end to end (sale → payment → partial return → refund → cancel + stock
  reconciliation), sale vs rate-version race.
- Vitest: POS preview/debounce, pending blocks completion, `expectedGrandTotal`, change, bill discount permission/cap,
  errors, inclusive badge; print columns (intra/inter/VAT/legacy); return detail; report.

### Work packages
1. database-engineer: entities, configurations, two migrations, V__ files, release-notes SQL, CONCURRENTLY runbook step.
2. backend-engineer: resolver walk-in change, `SalesTaxRules`, `SalesService`, controller, contracts, catalog, report,
   T19 lift, deletions; test-engineer in parallel: Postgres migration/V__/race tests (gate: API contract frozen).
3. frontend-engineer; test-engineer in parallel: xUnit service tests and Vitest.
4. code-reviewer, then `scripts/verify.sh all`.

### Design questions (decided as recommended, T29)
- TP4-Q1 POS live total: server preview, client copy of the calculator, or compute on save? [Server preview +
  `expectedGrandTotal`; one round trip after a 300 ms pause.]
- TP4-Q2 Companies created after TP-1 have `SellingPricesIncludeTax = true` (T18) but have been selling exclusive; at
  release their stored prices start to count as tax-inclusive, so customers pay less. [Run P3 and decide per company:
  keep inclusive (prices are shelf labels) or set exclusive before the release.]
- TP4-Q3 Walk-in customer with a stored state. [Always intra-state.]
- TP4-Q4 Customers without a state are intra-state without warning. [Keep (BR12); inter-state delivery needs the
  customer's state.]
- TP4-Q5 Round-off to the nearest rupee/fils. [Later.]
- TP4-Q6 Print format. [A4 tax invoice now; thermal receipt later; no buyer GSTIN until the party-status phase.]
- TP4-Q7 Lift T19 in TP-4. [Yes.]
- TP4-Q8 Delete `SaleMoney` and `LegacyLineTax` and replace their tests. [Yes — tests of deleted code, not weakened.]
- TP4-Q9 Profile/version indexes on SalesInvoiceLines pre-created `CONCURRENTLY`. [Yes.]

Risks: CGST/SGST split can move exclusive carts by 0.01 (release note); `TaxRatePercent` is numeric(6,3), so a Σ applied
percent with 4 decimals would round silently (reject in the version validator or accept — review); rollback to the TP-3
build is safe for reads, but a return made by the old build on a TP-4 line writes one legacy row next to the component
rows (totals stay right, per-component split approximate); Indian companies without a state, or products without a
valid version, cannot sell at all — P1 and P2 must be clean before release.

## TP-5 design (tax reports and clean-up)

Status: **approved by the owner** (TP5-Q1..Q11 as recommended, T32). TP-5a in progress on `feature/tax-reports`; TP-5b needs
a separate owner sign-off.

### Findings that shape the design
- F1 Only `SalesInvoiceLines`, `PurchaseOrderLines` and `Products` have `TaxId` (return lines never had one).
- F2 The `CK_*_TaxProfile_Pair` constraints require profile and version both null or both set.
- F3 The snapshot validation files recognise new lines by `TaxProfileId IS NOT NULL`; backfilling history would make
  them fail on legacy lines.
- F4 No reliable version exists for a legacy line (rates were editable before TP-1; TP-1..TP-3 builds wrote today's mirror
  rate even on back-dated documents). Attaching one would invent a snapshot (BR24).
- F5 The HSN summary (GSTR-1 Table 12) must add quantities across products within one HSN — an exception to "no
  cross-product quantity sums", bounded to identical base units.
- F6 Units have no UQC; customers have no GSTIN (no B2B split possible yet).
- F7 Several read paths still use `Product.Tax` (products, pricing, stock, catalog fallback, return fallbacks).

### Two releases (D10: destructive last)
- **TP-5a (additive, app rollback possible):** three tax reports and `Reports.Tax`; HSN warning → error behind a
  per-company date; history `TaxProfileId` backfill; "used version" check moved to profiles; every read path off
  `Product.Tax`/`TaxId`. Mirrors are still written, so the TP-4 build can be redeployed.
- **TP-5b (destructive, after owner sign-off and a soak):** drop the `TaxId` columns, `TaxProfiles.LegacyTaxId`, `Taxes`,
  `/api/v1/taxes` and the mirror code. M3b-1 makes columns nullable before the app swap; M3b-2 drops them after the
  smoke test.

### Reports (read-only; `GET api/v1/reports/tax/...`; new permission `Reports.Tax`)
Common rules: period `dateFrom`–`dateTo` (≤ 366 days), optional branch (branch access enforced), company filter;
document date from the header (returns by return date); cancelled documents excluded; base-currency amounts; taxable
counted once per line; legacy rows in a "Legacy (unsplit)" bucket by rate (never split, T10); zero-treatment rows in
their treatment bucket; GST rate = Σ CGST+SGST/UTGST+IGST rates, cess as an amount; return lines use the original's
place of supply and HSN; bounded groups, no paging, CSV export on the client.
- **GST summary:** section (Outward, OutwardReturn, Inward, InwardReturn) × seller GSTIN (current branch, else company) ×
  place-of-supply state × supply type × treatment × rate × legacy; taxable, CGST, SGST/UTGST, IGST, cess, legacy tax,
  document count; net blocks per GSTIN. Shaped for GSTR-1 B2CS and GSTR-3B 3.1/4(A)(5). Limitations shown: all outward
  supplies are B2C until party GSTIN exists; input tax is "as invoiced", not "eligible ITC".
- **HSN summary** (sales or purchases): HSN (line snapshot; null = "HSN not recorded" for pre-TP-3/TP-4 lines) × base unit
  × rate; UQC (new `Units.Uqc`) or unit code, base quantity net of returns, taxable, tax by component, total value.
- **VAT summary:** standard-rated by rate and region (UAE emirate box), zero-rated, exempt, out-of-scope, legacy; output
  VAT (sales − returns) and input VAT (purchases − returns); no B2B/B2C split (banner).
- Performance: existing header date indexes and row indexes cover it; EXPLAIN on ~1M lines in tests, add an index only
  if needed. Frontend: three report pages (GST or VAT shown by company regime), limitation banners, CSV export.

### HSN missing becomes an error (T11)
For products whose resolved profile is GST + Taxable and with no HSN (product, else subcategory): product save → 400
`hsnCode`; sale/purchase create and preview → 400 `lines[i].hsnCode`. Returns, cancels and non-tax edits never block.
Grace: new `BusinessSettings.HsnRequiredFrom` (date) — documents dated on/after it are blocked; null = warning only
(existing companies); new companies get their creation date; product saves are blocked once a date is set. Release-notes
query lists products without HSN per company first.

### History backfill (M3a, additive)
- Migration `PrepareTaxHistoryBackfill`: replace the pair constraints with "version needs profile" (profile may be set
  without a version), add `Units.Uqc` (seeded for standard units), `BusinessSettings.HsnRequiredFrom`, `Reports.Tax` and
  grants.
- Script `backend/db/scripts/tax-history-backfill.sql` (outside a transaction, batches of 5,000 with a commit each,
  `SKIP LOCKED`, resumable and idempotent): set `TaxProfileId` on historic sales and purchase lines from their `TaxId`
  through the 1:1 mirror mapping, then on return lines from their original line. **The version stays NULL on history.**
  Applied after the migration.
- Validation: the snapshot files switch to `TaxProfileVersionId IS NOT NULL` as the "new line" test; new
  `V__tax-history-profile.sql` (warn in 5a, block in 5b) flags lines without a profile or with a mismatching `TaxId`.
- "Used version" check: profile id, or (not yet backfilled) the mirror `TaxId`, over the same date range; 5b drops the
  fallback.
- Release notes `scripts/sql/tax-reports-release-notes.sql`: products without HSN, backfill progress and counts, history
  lines whose version rate differs from the stored rate (information).

### Destructive M3b (TP-5b)
Preflight (fails the release): no line without a profile, no line without tax rows, every `V__tax-*` clean, owner sign-off
recorded, backup taken. M3b-1 `RelaxLegacyTaxColumns` (metadata only): drop NOT NULL and FKs to `Taxes`. M3b-2
`DropLegacyTaxes`: drop the three `TaxId` columns, `LegacyTaxId`, `Taxes`. No rollback after M3b-2 (restore backup + 5a
build); archive `Taxes` as CSV. Kept forever: line `TaxRatePercent` (display), legacy tax rows, line amounts. Code removed:
`Tax` entity, `TaxesController`, mirror sync and creation, line `TaxId` writes, `Product.Tax` fallbacks, seeder/reset
references, `V__tax-mirror-drift.sql` and the mirror checks in other validation files.

### API / frontend
5a (append-only): three report endpoints; `hsnRequiredFrom` in business settings; product/pricing/stock tax labels and
rates from the resolver; catalog fallback no longer reads the mirror; deprecation of `ProductDetailDto.taxId`,
`TaxProfileDto.mirrorTaxId`, `GET /api/v1/taxes`; new errors `hsnCode`, `lines[i].hsnCode`, `dateTo`. 5b: those removed.
Frontend: report pages, settings field, HSN error on the product form and POS/purchase entry.

### Tests
xUnit report fixtures (intra, inter, cess, inclusive, exempt/nil, VAT zero/exempt, legacy, partial and full returns across
periods, cancelled excluded, foreign-currency purchases, return in a later period), HSN error matrix, "used version"
with backfilled/unbackfilled/new lines, read paths without `Product.Tax`, permission and branch isolation. Postgres:
constraint swap, backfill on ~500k lines (batching, kill and resume, re-run no-op, concurrent sale), every edited
validation file, report SQL + EXPLAIN, M3b preflight and the 5a build after M3b-1. Vitest: report pages, CSV, banners,
HSN error mapping, settings.

### Work packages
5a: (1) database-engineer — M3a, backfill script, validation files, release notes, UQC seed, permission; (2)
backend-engineer + test-engineer (Postgres backfill/validation tests), gate: API contract approved; (3)
frontend-engineer + test-engineer (xUnit report fixtures, Vitest); (4) code review, `verify.sh all`. 5b after sign-off:
(5) database-engineer M3b; (6) backend/frontend removal + fixture updates; (7) code review, `verify.sh all`.

### Design questions (decided as recommended, T32)
- TP5-Q1 Split into 5a (additive) and 5b (destructive)? [Yes.]
- TP5-Q2 New permission `Reports.Tax`, granted to current `Reports.Sales` holders? [Yes.]
- TP5-Q3 Add `Units.Uqc` with a seeded mapping? [Yes.]
- TP5-Q4 HSN errors from a per-company `HsnRequiredFrom` date (null for existing companies)? [Yes.]
- TP5-Q5 HSN error for Taxable products only? [Yes, per T11; revisit nil/exempt with the GSTR export.]
- TP5-Q6 History lines get the profile only; the version stays NULL; pair constraint relaxed? [Yes.]
- TP5-Q7 Soak before 5b: one filed return period from the new reports + CI green? [Yes.]
- TP5-Q8 Reports use the branch's current GSTIN? [Accept; snapshot with the party-status phase.]
- TP5-Q9 CSV export built in the browser? [Yes.]
- TP5-Q10 Returns reported in the period of the return date? [Yes.]
- TP5-Q11 HSN quantity sums across products within one HSN and identical base unit (F5)? [Yes.]

Risks: POS stops for products without HSN once a company sets the date (check the release-notes query first); backfill
duration on large tenants (resumable); legacy periods cannot be split by component or HSN (shown as Legacy / "Not
recorded"); no app rollback after 5b; large integration-fixture churn in 5b.

## Deployment notes

### TP-3 release notes (purchases)
1. Before: run section P of `scripts/sql/tax-purchases-release-notes.sql` — Indian branches with neither a branch nor a
   company state cannot purchase after TP-3. R1 before and after (after must be 0 rows).
2. Apply `AddPurchaseTaxSnapshot` then `ValidatePurchaseTaxConstraints` (the second briefly blocks purchase writes
   while it validates and indexes). Then all `V__tax-*.sql` must return zero failing rows.
3. Behaviour changes to announce:
   - Purchase tax and totals round to the currency's decimals per tax component (DQ-TP2-2); foreign-currency purchases
     convert CGST and SGST separately, so totals can differ by 0.01 from the previous calculation.
   - API clients: a rate with more than 4 decimals, or a discount / other charges / bill charges with more decimals than
     the supplier currency, is now refused (400) instead of silently rounded; errors are keyed `lines[i].field`,
     `billDiscountPercent` / `billDiscountAmount`, `billCharges`.
   - A purchase needs the company country, a branch or company state, and a purchase tax profile for every product.
   - Landed cost now includes each line's share of whole-bill charges and is net of whole-bill discount.
4. Accepted rounding (TP-3 review): for foreign suppliers, `OriginalTaxableValue × rate` can differ from the base
   taxable value by up to one base unit per line (parts are converted separately); header sums stay exact. Purchase
   header money columns stay numeric(18,2) (T27: no 3-decimal base currency in use).

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
