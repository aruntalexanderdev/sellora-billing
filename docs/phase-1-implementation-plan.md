# Sellora Billing — Phase 1 Implementation Plan

## Context

The user handed off a Claude Design prototype bundle (`Sellora Billing.dc.html` + `DataTable.dc.html` + the original design-prompt docx) for a shop billing/inventory system called **Sellora Billing**. The prototype is a clickable, in-memory JS demo covering login, RBAC, products, purchase entry, stock/pricing, POS billing, invoices, and a reports screen — but it is a **trimmed subset** of the full docx spec (missing: Forgot Password, Basic Masters (Units/Taxes/Settings), a separate Permissions catalogue, Stock Details/Adjustments, Purchase List/Details/Edit, Invoice Details, and 4 of the 5 report types).

The user confirmed:
- Build the **full docx spec scope**, not just what's in the prototype.
- Frontend: **Angular** (per docx §19).
- Backend: **ASP.NET Core Web API** (latest stable .NET), **EF Core + Npgsql**, **PostgreSQL**. Backend must enforce RBAC itself via policy/claims-based authorization — Angular guards are UX-only.
- This turn: **plan only**, no scaffolding yet. Implementation will start in a later session, phase by phase, with check-ins.

The project directory (`/Users/Projects/Sellora Billing`) is currently empty and not a git repo — this is a greenfield build.

Working reference material (already extracted, no need to re-read the zip):
- Design tokens/visual language: `Sellora Billing.dc.html` — Geist font, teal accent `#0d7f8c` / `#0b6e7a` hover, dark sidebar `#10262a`, neutral grays (`#f4f6f6` bg, `#16191c` text, `#6b7480` muted), 10px card radius, badge tone system (good/warn/bad/info/mute with specific hex pairs), `sl-rise` toast animation, `dt-spin` loading spinner.
- Reusable list pattern: `DataTable.dc.html` — toolbar (search + filters + actions) → loading (skeleton rows) / error (retry) / empty (title+body+CTA) / ready (table + footer pagination) state machine. This becomes the Angular `<app-data-table>` shared component.
- Full requirements: the docx (`Sellora_Billing_Phase_1_Prototype_Design_Prompt.docx`), sections 1–20 — sitemap, permission model, per-screen field lists, business rules (§17), and acceptance checklist (§18).

## Architecture decisions

**Repo layout** (monorepo under `/Users/Projects/Sellora Billing`):
```
/backend    ASP.NET Core Web API solution (Sellora.Api, Sellora.Domain, Sellora.Infrastructure, Sellora.Tests)
/frontend   Angular workspace (single app, standalone components, feature folders)
/docs       carry over the docx spec + a copy of this plan for future reference
```

**Auth & permissions** (addresses docx §6 — action-level, backend-enforced):
- Custom user/role/permission tables (not ASP.NET Identity's built-in role system — permissions are dynamic, admin-mapped, action-level per module).
- `Permissions` table is **system-seeded and read-only** (matches docx §8.4): code like `Inventory.View`, `Inventory.Adjust`, `Purchase.Cancel`, `Reports.Sales`, etc., grouped by module.
- `RolePermissions` join table maps roles → permissions. Administrator role is hardcoded to bypass the check (always all permissions), matching the prototype's `can()` logic and docx §6.
- On login, issue a JWT whose claims include the user's resolved permission codes. Angular reads these for nav/button visibility (UX only). Every backend endpoint is decorated with `[Authorize(Policy = "Inventory.View")]` etc., using a custom `IAuthorizationPolicyProvider` that maps policy name → permission-claim check. This is the real gate — matches the explicit requirement that "frontend permission checks do not replace backend authorization."
- Short-lived access token + refresh token endpoint (simple rotation, no external identity provider needed for Phase 1).

**Money & quantities**: `decimal` end-to-end (C# `decimal`, Postgres `numeric(18,4)` for currency, `numeric(18,3)` for quantities to support values like `0.250 KG`) — never `float`/`double`, per docx §17.

**Costing rule (COGS)** — docx §9.2/§12.5/§17 explicitly forbid using "only the latest purchase price" and require a defensible, traceable basis:
- **FIFO batch costing.** Each purchase line creates a `PurchaseBatch` (qty received, remaining qty, unit cost). Each sale line consumes remaining batches oldest-first, recording the actual cost consumed → real gross margin per sale, and giving Stock Details its "batch, purchased qty, remaining qty, cost" section directly.
- Historical purchase/sale records never get mutated when current price changes (docx §17) — prices live on `SellingPrice` rows with `EffectiveFrom`/`EffectiveTo`, not overwritten in place.

**Selling modes / unit conversion** (docx §9.3, "Rice 10kg Bag / 1kg / Loose kg / 500g"):
- `Product.BaseUnit` is the stock-of-record unit (e.g., kg).
- `ProductSellingMode` rows (per product): mode label, conversion factor to base unit, active flag, and its own effective-dated price. Purchase and POS both select a selling mode; the app converts to base units for all stock math.

**Shared UI kit**: an Angular component library ported pixel-for-pixel from the dc.html tokens — layout shell (dark sidebar + sticky header + toast), buttons (primary/secondary/destructive/disabled per docx §4/§26), badge component (good/warn/bad/info/mute), form inputs, confirmation-dialog component, and the `DataTable` component (search/filters/toolbar actions/loading-skeleton/error-retry/empty-CTA/ready+pagination) mirroring `DataTable.dc.html`'s state machine 1:1. Every feature module's list screen composes this instead of rebuilding table chrome.

## Database schema (PostgreSQL, EF Core)

Grouped by module, FK relationships implied by naming:

- **Identity/RBAC**: `Users`, `Roles`, `Permissions` (seeded), `RolePermissions`
- **Masters**: `Units`, `Taxes`, `BusinessSettings` (single row), `Suppliers`, `Customers` (incl. walk-in flag), `Products`, `ProductSellingModes`, `SellingPrices` (effective-dated, per selling mode)
- **Purchase**: `PurchaseOrders`, `PurchaseOrderLines`, `PurchaseBatches`
- **Inventory**: `StockItems` (derived on-hand qty + avg cost per product, kept in sync transactionally), `StockMovements` (unified ledger: Purchase/Sale/Adjustment, signed qty, reference type+id+no, user), `StockAdjustments` (type: Damage/Loss/Correction/Expiry/Opening → writes a `StockMovement`)
- **Sales**: `SalesInvoices`, `SalesInvoiceLines` (records selling mode, qty, unit price, tax, batch-cost consumed for COGS)

All transactional writes (purchase save, sale checkout, stock adjustment) happen inside a DB transaction that updates the ledger (`StockMovements`), the derived total (`StockItems`), and batch remainders (`PurchaseBatches`) together — this is what docx §10/§17 mean by "kept consistent as one business transaction."

## API surface (ASP.NET Core, `/api/v1/...`)

One controller per module, REST conventions, all list endpoints supporting search/filter/pagination to match the `DataTable` component's contract:
`AuthController`, `UsersController`, `RolesController`, `PermissionsController` (read-only), `UnitsController`, `TaxesController`, `SettingsController`, `SuppliersController`, `CustomersController`, `ProductsController` (+ nested selling-modes/pricing endpoints), `PurchasesController`, `StockController` (overview/details/movement), `StockAdjustmentsController`, `SalesController`, `ReportsController` (5 report endpoints), `DashboardController`.

## Angular structure

Standalone components, one feature folder per module mirroring the API/DB grouping (`auth`, `administration/{users,roles,permissions}`, `masters/{units,taxes,settings,suppliers,customers}`, `inventory/{products,stock-overview,stock-details,stock-adjustments,stock-pricing,stock-movement}`, `purchase/{list,new,details}`, `sales/{pos,list,details}`, `reports`, `dashboard`), plus `shared/ui` (the ported component kit) and `core` (auth service, permission guard, HTTP interceptor for bearer token + 401/403 handling).

Functional route guards check resolved permission claims from `AuthService` (decoded JWT) for menu/route visibility — UX layer only, per the RBAC decision above.

## Build order (dependency-driven, matches docx §13 end-to-end scenario and §18 checklist)

1. **Phase 0 — Foundation**: repo scaffold (Angular workspace + .NET solution + Postgres + EF Core migration pipeline), shared UI kit + app shell (sidebar/header/toast) ported from the dc.html tokens.
2. **Phase 1 — Auth & RBAC**: Users, Roles, Permissions catalogue (seeded), Role-Permission mapping UI, JWT auth incl. login/forgot-password/disabled-account/server-error states, backend policy enforcement, permission-denied page, route guards.
3. **Phase 2 — Basic Masters**: Units, Taxes, Business Settings, Suppliers, Customers, Products (selling modes scaffolded, no pricing yet).
4. **Phase 3 — Purchase lifecycle**: Purchase List/New/Details/Edit/Cancel, batch creation, stock movement + `StockItems` increments.
5. **Phase 4 — Inventory**: Stock Overview, Stock Details (batches + movement history), Stock Adjustments (writes movements).
6. **Phase 5 — Stock Pricing**: selling-mode price configuration, effective-dated, respecting unit conversions and stock availability.
7. **Phase 6 — Sales & Billing**: POS/New Sale (FIFO batch consumption for COGS), Sales List, Invoice Details/print.
8. **Phase 7 — Reports**: Current Stock, Stock Movement, Purchase, Sales, Profit/Margin (with a visible calculation-basis tooltip per docx §12.5).
9. **Phase 8 — Dashboard**: summary cards/tables aggregating across all modules — built last since it reads everything above.
10. **Phase 9 — Polish**: remaining required UI states (validation, confirmation modals, unsaved-changes warning, disabled-button reasons with tooltips, pagination/no-more-results, long-name/large-number overflow handling), responsive pass, full walkthrough of the docx §13 end-to-end scenario, and a pass against the docx §18 acceptance checklist.

Each phase should ship with basic automated tests (xUnit for API business rules — especially stock math and FIFO costing; Jasmine/Karma or Jest for Angular components) and be reviewed before moving to the next phase, per the user's chosen increment size.

## Verification approach (once implementation starts)

- **Per phase**: backend unit tests for business rules (esp. unit conversion, FIFO batch consumption, permission policy enforcement) + manual run of the affected Angular screens via the built-in browser against the local API.
- **End of build**: replay the docx §13 end-to-end scenario live (create role → map permissions → create user → login as that user → see filtered nav → purchase 10kg rice → stock appears → configure pricing/selling modes → POS sale → stock decreases → movement ledger shows both entries → Purchase/Sales/Profit reports reflect the transactions) and check off docx §18's acceptance list item by item.
