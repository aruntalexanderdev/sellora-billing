# Tax Profiles and Product Tax Mapping

status: requirements-approved
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
- T13 (Q1) Ledger posting per component is out of scope; components keep a placeholder for output/input accounts.
- T14 (Q2) A branch in India with no state uses the company's state (`BusinessSettings.StateId`) for place of supply;
  only when neither is set are GST documents refused.
- T15 (Q4) Whole-bill discount on sales needs the new permission `Sales.BillDiscount` and is capped by
  `BusinessSettings.MaxBillDiscountPercent` (empty = no cap).
- T16 (Q6) A stored selling price is in the product's price basis: what the shelf label says (tax included when the
  product's prices include tax). Changing a product's basis does not convert existing prices; the UI warns.

## Deployment notes

- M1 runs as a manual, pre-deploy database release (D10), never at startup. Back up first; validation SQL must return
  zero failing rows before M2 code is deployed.
- M2 keeps writing legacy `TaxId` mirrors so the previous build can be redeployed without a database rollback.
- M3 ships in a separate release after owner sign-off that reports and returns are correct on the new rows.
- Rates in seeds are examples; confirm against current GST notifications and GCC rates before production
  (the tobacco/cess position after 2025 was unclear in research).
