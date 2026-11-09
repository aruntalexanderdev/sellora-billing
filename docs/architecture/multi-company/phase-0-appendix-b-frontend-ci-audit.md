# Phase 0 audit: frontend, CI/CD and deployment (multi-company / branch / location)

Read-only audit. Repo: /home/user/sellora-billing (branch `analyse`, HEAD a7c1c4e). Spec: docs/architecture/Sellora-Multi-Company-Branch-Architecture-Claude.md §15 (L1141), §16 (L1195), §18.3 (L1366), §19 Phase 11 (L1537).

## 1. Frontend current state

### Stack and structure
- Angular 22.1 (standalone components, signals), TypeScript 6.0, Vitest 4 via `@angular/build:unit-test`, jsdom. No NgRx or any state library; no UI library (own kit in shared/ui). Source: frontend/package.json.
- Bootstrap: frontend/src/main.ts -> frontend/src/app/app.config.ts (`provideRouter(routes)`, `provideHttpClient(withInterceptors([authInterceptor]))`). Only one interceptor.
- Layout:
  - frontend/src/app/layout/shell/shell.ts: holds a hard-coded `NAV_DEF` (groups Overview, Administration, Inventory, Purchases, Sales & Billing, Accounts, Accounts Reports, Reports, System). Each item is filtered by a single permission string via `auth.hasPermission`. Sidebar open state is kept in localStorage `sellora.sidebarOpen`.
  - frontend/src/app/layout/header/header.html: menu toggle, spacer, user avatar, name and role, Sign out. **No company, branch or location selector or context display.**
  - frontend/src/app/layout/sidebar/sidebar.html: brand block with a **hard-coded tenant label `SELLORA MART`** (`.sb-tenant`). This will need to show the active company and branch.
- core/: auth/auth.service.ts, auth/auth.guard.ts, auth/auth.interceptor.ts, auth/auth.models.ts, idempotency.ts (Idempotency-Key header helper), unsaved-changes.guard.ts, api.models.ts (`PagedResult<T>`). There is no context or tenant service, no error/403 interceptor, and no permission directive.
- shared/: ui/{badge,button,card,confirm-dialog,data-table,toast}, forms/quantity-input, units/{unit-conversion,return-units,stock-movement}, styles/{_form-fields,_page-head,_report-filters}.scss. There is no shared select or combobox component that a context switcher could reuse. Native `<select>` is used in pages.
- Feature areas (frontend/src/app/features/):
  - auth (login, forgot-password)
  - dashboard
  - profile
  - permission-denied
  - ui-showcase
  - administration: users (list, form), roles (list, form), permissions (list), currencies, exchange-rates
  - masters: units, taxes, settings (BusinessSettings, a single-business record)
  - inventory: products (list, form, product-units-editor), stock (overview, detail), stock-adjustments (list, form, detail), pricing
  - purchases: list, form, detail, purchase-returns (list, form, detail), suppliers
  - sales: pos, sales-list, sales-detail, sales-returns (list, form, detail), customers
  - accounts: expenses (list, form, detail, categories), cash-bank (list, form, detail, transfer), financial-transactions, reports (payment, outstanding, expense, cash-flow, cash-bank, payment-method, with a shared report-date-filter)
  - reports: current-stock, stock-movement, purchase, sales, profit-summary
  - There is no companies/, branches/, warehouses/, vehicles/, governance/ or stock-transfers/ folder.

### Auth and permission flow
- Login: `POST {apiBaseUrl}/auth/login` returns `AuthResponse {accessToken, refreshToken, expiries, user: AuthenticatedUser}`. `AuthenticatedUser` = id, names, username, email, `roleName` (one role), `permissions: string[]` (flat list). See core/auth/auth.models.ts.
- `AuthService` (core/auth/auth.service.ts) keeps one `signal<StoredSession>` and **persists accessToken, refreshToken and the whole user object, including permissions, to localStorage key `sellora.auth`**. `hasPermission` checks that cached array. The JWT is never decoded on the client. Backend JWT also carries the permission claims (backend/src/Sellora.Infrastructure/Security/JwtTokenService.cs L25-27) plus RoleName.
- Backend has `GET auth/me` (backend/src/Sellora.Api/Controllers/AuthController.cs L57), but **the frontend never calls it**. Permissions are refreshed only at login, at token refresh (`persist` overwrites the user), or patched by `updateCurrentUser`. A revoked permission stays visible in the UI until the next refresh. That is about 15 minutes, based on the dev example `AccessTokenMinutes`.
- Permission checks in screens: imperative `auth.hasPermission(...)` in about 25 component .ts files, such as user-list, role-list, purchase-detail (6), sales-detail (5), stock-detail, product-list and dashboard-page. There is no structural directive (`*appHasPermission`) and no pipe. Changing to scoped permissions means touching every one of these call sites, or keeping the `hasPermission(code)` signature and making it context-aware internally (recommended).
- `hasPermission` is not reactive to the context because it reads the session signal. It is reactive only if called inside a computed. Shell's `navGroups` is a computed, so it would react if a context signal were added.

### Routing and guards
- frontend/src/app/app.routes.ts: one `Shell` parent with `canActivate: [authGuard]`. About 85 child routes, each with `permissionGuard('<Code>')` (a factory in core/auth/auth.guard.ts) and `data: {group, page}`. Failure redirects to `/permission-denied?permission=...`.
- Lazy loading is **partial**:
  - Lazy (`loadComponent`): stock-adjustments, purchase detail and returns, all accounts/*, sales detail and returns.
  - Eager (top-level imports in app.routes.ts): dashboard, users, roles, permissions, units, taxes, currencies, exchange-rates, settings, suppliers, products, purchases list and form, stock overview and detail, pricing, customers, POS, sales list, and all 5 reports.
  - New admin areas (companies, branches, warehouses, vehicles, governance, transfers) should be lazy (`loadChildren` per feature) to protect the budget.
- `unsavedChangesGuard` is on most forms. It is **missing on `inventory/stock-adjustments/new`**, a pre-existing gap.
- No guard requires an "active company/branch selected" state, and there is no resolver that loads a context.

### HTTP and API base URL
- frontend/src/environments/environment.ts (prod): `apiBaseUrl: '/api/v1'`, relative. This relies on an Azure SWA linked backend or proxy. staticwebapp.config.json only has navigationFallback, with no route to the API.
- environment.development.ts: `http://localhost:5019/api/v1`. angular.json swaps it only for the `development` configuration.
- 23 files build URLs as module constants: `const BASE_URL = \`${environment.apiBaseUrl}/<resource>\``, with query params passed as `Record<string,string>`. No central API client. A company/branch header (for example `X-Company-Id` / `X-Branch-Id`) can be added in one place: a new interceptor next to authInterceptor.
- authInterceptor (core/auth/auth.interceptor.ts) attaches the Bearer token, skips auth endpoints, and on 401 calls `refresh()` and retries. **There is no single-flight refresh**: concurrent 401s each call refresh, which is risky with rotating refresh tokens. **No 403 handling**: grep finds no `403` or `Forbidden` anywhere in features. Errors are handled per page (toast or `serverError` signals). §15 "handle stale context and revoked access gracefully" needs a 403 / `context_revoked` path (for example, refetch /me, clear context, redirect to the selector or permission-denied).

### Global state
- The only global state is `AuthService.session` (signal) and per-page signals. `ToastService` and `ConfirmDialogService` are root services.
- There is no context store. Phase 11 needs a `ContextService` (signals: memberships, activeCompanyId, activeBranchId, effective permissions per context). The selection can be stored in localStorage as a preference only (§15: do not trust it). It must be re-validated against `/me` on load.

### Bundle
- angular.json production budgets: initial 700kB warn / **1MB error**; anyComponentStyle 6kB warn / 10kB error. outputHashing all.
- frontend/dist/ is a dev build with source maps: main.js about 866kB and one chunk about 1.0MB. This is **not** a reliable production size. Run `ng build` (prod) to measure the headroom before Phase 11. The many eager routes are the main lever.

## 2. Screen impact map

Legend: SEL = depends on the global company/branch selector; FILT = add a location (warehouse/vehicle) or branch filter; COL = add a branch/location column; FORM = add a branch/location form field; GUARD = scoped-permission / context guard.

| Screen (file) | Required change |
|---|---|
| layout/header/header.html | Add a company switcher (if more than 1 membership) and a branch switcher (if more than 1 active allocation). Show the active context. React to revoked context. |
| layout/sidebar/sidebar.html | Replace the hard-coded `SELLORA MART` with the active company and branch names. |
| layout/shell/shell.ts NAV_DEF | Add groups: Organization (Companies (platform), Branches, Warehouses, Vehicles), Governance, Stock Transfers. Filter items by context-scoped permissions. Separate the platform-admin nav from the company nav. |
| core/auth/* | `AuthenticatedUser` needs memberships, allocations and scoped permissions (or fetch them from /me per context). Make `hasPermission` context-aware. Add a context guard and a context interceptor. Stop treating cached permissions as authoritative. |
| dashboard/dashboard-page | SEL. KPIs, low stock and recent sales/purchases per company/branch. Optional location filter. `GET /dashboard` currently has no params. |
| inventory/stock/stock-overview | SEL + FILT (location: warehouse/vehicle/all branch locations) + COL (location) or a per-location breakdown. The "Adjust" action must pass the location. |
| inventory/stock/stock-detail | FILT/COL: per-location balances, plus batches and movements by location. |
| inventory/stock-adjustments list/form/detail | FORM: location (required). FILT: location/branch. COL: location. Add the missing unsavedChangesGuard. GUARD: branch-scoped Inventory.Adjust. |
| inventory/products list/form | Products are probably company-scoped masters. SEL (company) only. Possibly per-branch reorder levels later. |
| inventory/pricing | SEL. Clarify whether pricing is company-wide or per branch (spec decision). |
| purchases list/form/detail | FORM: receiving branch + location (warehouse). FILT/COL: branch. GUARD: branch-scoped Purchase.*. |
| purchase-returns list/form/detail | FORM: source location (defaults from the purchase). FILT/COL: branch. |
| purchases/suppliers | SEL (company-scoped master). |
| sales/pos | FORM/SEL: selling branch + stock location (counter warehouse or van/vehicle). Catalog availability per location (`/sales/catalog` has no location param today). Barcode lookup per location. |
| sales list/detail | FILT/COL: branch, location. |
| sales-returns list/form/detail | FORM: return-to location. FILT/COL: branch. |
| sales/customers | SEL (company-scoped). |
| accounts/* (expenses, cash-bank, financial-transactions, accounts reports) | SEL (company). Branch FILT/COL if accounts become branch-scoped. Out of the spec's inventory focus but affected by company scoping. |
| reports/current-stock, stock-movement | SEL + FILT (branch, location) + COL (location). Stock movement also needs a transfer movement type and counter-location. |
| reports/purchase, sales, profit-summary | SEL + FILT (branch). |
| administration/users list/form | Single `roleId` today. Needs company membership, branch allocations (with history and primary branch), role assignments per scope, and an access-reassignment workflow. A branch admin must see only their branch (§15). |
| administration/roles list/form | Role scope (company vs branch), scope selection and filtering, escalation-safe permission picker (show only permissions the actor holds). |
| administration/permissions | Group the catalogue by scope (platform / governance / company / branch). |
| masters/settings (BusinessSettings) | Becomes company settings (name, GSTIN, invoicePrefix, base currency per company). Possibly per-branch invoice prefix. SEL. |
| masters/units, taxes, currencies, exchange-rates | Decide global vs company-scoped (spec decision). At minimum SEL if company-scoped. |
| profile | Show memberships/allocations and the default branch. |
| permission-denied | Add a "context revoked / no active branch" variant. |
| NEW: companies, branches, warehouses, vehicles, governance members & individual permissions, branch allocations, stock-transfers (request / approve / dispatch / receive), stock-location views | New lazy feature folders per §15 and Phase 11. |

## 3. Gaps vs spec §15 / §16 / §18.3

§15:
- No company or branch context selection, no context store, no context-scoped permissions. These are all absent.
- Trusted data in localStorage: permissions and user are persisted in `sellora.auth` and used directly for UI gating. The spec says "Do not store trusted permissions or tenant ownership solely in local storage". The UI gating is acceptable only if the server re-authorizes, which it does via the policy provider. Even so, rehydrate from `/auth/me` on startup and on context switch instead of trusting the cache. Do not persist active-company ownership as a trusted value.
- Stale context and revoked access: no 403 handling, no /me revalidation, no single-flight refresh.
- Branch-admin isolation in the users/roles screens: not present (single global role per user).
- Missing feature folders: companies, branches, warehouses, vehicles, governance, stock-transfers, membership/allocation management.
- Platform Super Admin UI vs company UI separation: not present.

§16 (frontend-facing):
- No correlation/request ID header is sent. Add `X-Correlation-Id` in an interceptor so audit records can correlate.
- No "reason/comment" fields on sensitive operations, for example allocation closure, governance permission change, adjustments and transfer approvals. Stock adjustments already capture reasonCode and notes.
- No audit-log viewer screen. This is optional, but useful for governance.

§18.3 test requirements (context selection, nav visibility, allocation screens, role scope filtering, governance screens, forbidden handling, empty/loading/error states): none are covered today. See §4.

## 4. Frontend test inventory (Vitest)

19 spec files, about 165 test cases (counted with `it(`/`test(`):
- app.spec.ts 1
- shared/units/unit-conversion 31
- product-units-editor 30
- unit-form 28
- purchase-units 11
- product-form 10
- pos 10
- stock-adjustment-form 8
- stock-adjustment-list 6
- stock-adjustment-detail 4
- unit-list 4
- pos-units 4
- sales-return-form 4
- quantity-input 4
- stock-movement (shared) 4
- pricing-page 2
- stock-movement-report 2
- stock-detail 1
- return-units 1

Coverage is concentrated on units/quantities and stock adjustments. **Zero tests** for:
- core/auth: AuthService, authGuard, permissionGuard, authInterceptor including the refresh path
- layout: shell nav filtering, header, sidebar
- users, roles and permissions admin
- dashboard, purchases list/detail, sales list/detail, reports (except stock-movement), accounts

Gaps to add before or with Phase 11:
- Interceptor tests: token, refresh, single-flight, 403, and the context header.
- Guard tests: permission plus context.
- ContextService tests: hydrate from /me, switch, revoked context.
- Shell nav visibility per scoped permission.
- Header selector: hidden when there is 1 membership or allocation.
- User allocation and role scope forms.
- Governance screens.
- Transfer lifecycle screens.
- Regression specs for POS, purchase-form and stock-overview with location params.

CI runs `npx ng test --watch=false`, so new specs gate merges automatically.

## 5. CI/CD and deployment

Current workflows (.github/workflows/):
- ci.yml: runs on every push and on PRs to dev/main. Backend: .NET 10 restore, build Release, `dotnet test` (Testcontainers integration tests run because Docker is on ubuntu-latest). Frontend: Node 22, `npm ci`, `npm run build` (prod config, so the **1MB budget error fails CI**), `ng test`. No lint step, no migration check (for example `dotnet ef migrations has-pending-model-changes` or a generated idempotent script artifact).
- dev_sellora-billing-dev-api.yml: push to `dev` (backend/** paths) runs build, test and publish, then deploys to Azure App Service `sellora-billing-dev-api` (slot Production) via OIDC azure/login. There is no migration step: the schema is migrated by the app at startup.
- dev_sellora-billing-dev-api-claude.yml: the same, for branch `claude_dev` to App Service `sellora-billing-dev-api-claude`.
- azure-static-web-apps-wonderful-bay-04d447700.yml: push or PR on `claude_dev` makes Azure SWA build `./frontend` and deploy `dist/frontend/browser`, with PR preview environments. There is **no SWA workflow for the `dev` branch** in the repo (assumption: the dev frontend is deployed elsewhere or manually). There is also no production workflow.
- There are no Dockerfile or docker-compose files in the repo. Integration tests use Testcontainers. scripts/verify.sh is a local build/test wrapper (unit|backend|frontend|all).

Configuration and migrations:
- backend/src/Sellora.Api/appsettings.json ships `Database:ApplyMigrationsOnStartup: true`. It has no ConnectionStrings and no Jwt section; these come from App Service settings or env vars. The appsettings.Development.json.example has a localhost PG connection string and the flag set to true. There is no appsettings.Production/Staging.json.
- backend/src/Sellora.Api/Program.cs L82-109: when the flag is true it runs `MigrateAsync()` and `DbSeeder.SeedAsync`. In Production it only **logs a warning**: "the dev Azure app currently runs with the Production environment name". The comment documents decision D10: production must set the flag to false and apply a reviewed `dotnet ef migrations script --idempotent` after a DB snapshot. The TODO to flip the base appsettings to false is still open.
- CORS allows only `http://localhost:4200`. Prod uses the relative `/api/v1` (assumption: SWA linked backend or same-origin reverse proxy). Confirm this before adding custom headers such as X-Company-Id / X-Branch-Id, because SWA linked backends forward custom headers but any CORS path would need them allowed.
- Migrations: backend/src/Sellora.Infrastructure/Data/Migrations (latest 20261109053202_AddMovementTypesAndReasonCode).

What a multi-phase migration rollout needs:
1. Flip the base `appsettings.json` to `ApplyMigrationsOnStartup=false`. Set `Database__ApplyMigrationsOnStartup=true` explicitly only on the dev App Services, if wanted. Consider failing startup (not just warning) when it is true in a real production environment. That needs a separate environment name for the dev apps (`ASPNETCORE_ENVIRONMENT=Development/Staging`), because they currently run as "Production".
2. Add a CI job that runs `dotnet ef migrations script --idempotent` (from the last released migration to head) and uploads it as an artifact for review. Also add `dotnet ef migrations has-pending-model-changes` to catch a forgotten migration.
3. Add a deploy-pipeline migration step (manual-approval environment gate): take a DB snapshot or branch, apply the idempotent script with psql against the target, run the validation SQL (backfill counts, orphan checks per §17.3 and §17.4), then deploy the app. Keep the additive-then-destructive ordering. Destructive migrations ship in a later release only after validation.
4. Startup seeding (`DbSeeder`) must become idempotent for the new data: default company, default branch, default warehouse, permission catalogue and scoped roles. Otherwise move it into migration backfill SQL. With auto-migrate off in prod, seed changes must be scripted too.
5. Release ordering: backend (backward-compatible API, with context defaulting to the single default company/branch) before the frontend. SWA deploys independently of the API (different workflows and triggers), so the frontend must tolerate an older API, and vice versa, during rollout. Use feature flags or capability detection from `/auth/me`.
6. Environment-specific risks:
   - The dev App Services auto-migrate on every backend push, so a half-finished phase migration lands in the dev DB immediately.
   - The two dev APIs (`dev` and `claude_dev`) may or may not share a DB. Concurrent migrations from both branches would conflict. Confirm the connection strings.
   - The SWA PR preview environments hit the same backend, so a preview frontend with context headers could hit an old API.
   - There is no staging slot. Deploys go straight to slot "Production" of the dev apps.
   - No production deploy pipeline exists yet. One must be designed (manual approval, migration step, slot swap).

## 6. Risks and assumptions

- Assumption: prod and dev frontends reach the API via the SWA linked backend (relative `/api/v1`). Verify in the Azure portal. If CORS is involved, the new context and correlation headers need to be allowed.
- Risk: cached permissions in localStorage plus no /me revalidation. After Phase 7 (scoped RBAC), the UI may show stale or incorrect actions until the token refreshes. Server enforcement covers security, but UX and test expectations need /me hydration.
- Risk: no single-flight token refresh. A context switch that triggers parallel reloads can race on refresh-token rotation and log the user out.
- Risk: the context header vs path design is not yet decided by the backend (header `X-Company-Id`/`X-Branch-Id` vs route segment vs token claim re-issued on switch). The frontend design depends on this. If context is embedded in the JWT, switching requires a re-issue endpoint.
- Risk: bundle headroom is unknown (only a dev dist exists). About 20 eager routes plus new admin screens could approach the 1MB initial error budget. Mitigate by making new features and existing eager routes lazy.
- Risk: about 25 components call `hasPermission` directly, and about 85 routes use flat permission codes. A changed permission catalogue (renamed or split codes) requires synchronized backend/frontend changes. Keep the code strings stable and add scope as a separate dimension.
- Risk: `BusinessSettings` is a singleton today. Invoices and printing (invoicePrefix, GSTIN, business name) must become company/branch-aware, or invoices will print the wrong legal entity.
- Risk: POS and stock-adjustment forms keep in-progress state in signals. A context switch mid-form must be blocked (unsavedChangesGuard-style confirm) or must reset the form.
- Risk: no frontend tests on auth/guards/interceptor/shell. Regressions in the core plumbing changed by Phase 11 would go unnoticed.
- Assumption: Angular 22 stays (§15 says no upgrade). Project conventions are in the `angular-conventions` skill (signals, standalone, Vitest).
- Pre-existing small issues: `inventory/stock-adjustments/new` route lacks unsavedChangesGuard. The sidebar tenant name is hard-coded.
