---
name: unit-conversion-rules
description: Sellora Unit Master and hierarchical ProductUnit conversion rules — data model, recursive factor calculation, validation (cycles, categories, decimals, immutability), precision and rounding. Load for any work touching units, product packaging, quantities entered in non-base units, pricing per unit, or conversion previews.
user-invocable: false
---

# Unit conversion rules (accepted target design)

Source: `docs/analysis/unit-conversion-restructuring-analysis.md` §8–§10, decisions D1, D9.

## Model
- **Unit** (global, table `Units`): `Id`, `ShortCode` (unit code), `Name`, `MeasurementCategory` ∈ {Count, Weight, Volume, Length, Packaging}, `DecimalPlaces` (0–3), `StandardFactor` (nullable; only Weight/Volume/Length, relative to kg / L / m), `IsActive`. No product-specific factors here.
- **ProductUnit** (per product, table `ProductUnits`, renamed from `ProductSellingModes`, **Ids preserved**): `ProductId`, `UnitId`, `InnerProductUnitId` (the unit it contains; NULL only for base), `InnerQuantity` (> 0), `ConversionFactorToBase` (materialised), `IsBaseUnit`, `Label?` (display override), `Barcode?`, `IsPurchasable`, `IsSellable`, `IsDefaultPurchaseUnit`, `IsDefaultSalesUnit`, `SortOrder`, `IsActive`.
- Wording: "1 **Box** contains 10 **Piece**" → Box.Inner = Piece, Box.InnerQuantity = 10.

## Factor calculation
```
factor(base) = 1
factor(u)    = u.InnerQuantity × factor(u.Inner)
convert(qty, from, to) = qty × factor(from) / factor(to)
```
Computed on save for the whole product graph (walk from base), stored in `ConversionFactorToBase`. Transactions read the stored factor and snapshot it — they never recurse.

Canonical checks: Pen (Piece 1, Box 10, Half Carton 50, Full Carton 100); Box-of-10 vs Box-of-20 on two products sharing the global `Box` unit; Rice Kg base with Gm 0.001 and Bag 25; Oil L base with mL 0.001, Bottle(500 mL) 0.5, Case(12 Bottle) 6; base-only product.

## Validation (service-level; reject with field errors)
1. Exactly one base row; its `UnitId = Product.BaseUnitId`; base category ≠ Packaging.
2. Every non-base row has an Inner in the same product; walk reaches base (cycle detection via visited set; orphan detection).
3. Category compatibility: Packaging may wrap anything; Weight/Volume/Length must equal base category; Count may be non-base only over a Count base (D9: allowed, e.g. Dozen over Piece).
4. If unit and inner both have `StandardFactor`, `InnerQuantity` must equal `unit.StandardFactor / inner.StandardFactor`.
5. Base `DecimalPlaces = 0` ⇒ every `ConversionFactorToBase` is an integer.
6. 1e-6 ≤ factor ≤ 1e9.
7. Units used must be active; `(ProductId, UnitId)` unique; at most one default purchase and one default sales unit.
8. **Immutability**: a ProductUnit referenced by any document line, adjustment or movement cannot change `UnitId`, `InnerProductUnitId`, `InnerQuantity`, `ConversionFactorToBase`. Change = deactivate + add new row.
9. Base unit of a product cannot change once stock history exists (existing rule, `ProductService.UpdateProductAsync`).
10. Product-unit edits lock the `Products` row (`FOR UPDATE`) so concurrent edits cannot both pass validation.

Warnings (not errors): two units with identical factors.

## Precision & rounding
| Value | Type |
|---|---|
| InnerQuantity | numeric(18,6) |
| ConversionFactorToBase (master + line snapshots) | numeric(28,10) |
| Base quantities (stock, ledger, batches, line BaseQty) | numeric(18,3) |
| Entered quantity | numeric(18,3), validated against entered unit `DecimalPlaces` |
| Per-base-unit costs | numeric(18,6) |

- `BaseQty = EnteredQty × Factor` must be exactly representable at 3 dp → otherwise reject (`StockQuantity.EnsureStorable`). Never round base quantities.
- Conversions apply to quantity only; money is rounded only at line totals (existing convention).

## Pricing (D2, D3)
- Prices are per ProductUnit (`SellingPrices.ProductUnitId`).
- No price for a sellable unit ⇒ POS refuses; pricing grid may *suggest* base price × factor.
- Server validates submitted unit price against the current list price; override requires `Sales.PriceOverride`; snapshot list price on the line.

## Display breakdown
Greedy decomposition of a base quantity over active sellable units, largest factor first (e.g. 122 pc → 1 Full Carton, 2 Box, 2 Piece). Nominal only (D4: no sealed/loose tracking).
