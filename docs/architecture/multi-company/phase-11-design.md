# Phase 11 - Angular integration and final verification - file-level design

Base: `analyse` @ fd61d24 (backend H–10 committed, nothing released; last migration `20261111040145_AddStockTransfers`). Size XL, **split into 11a and 11b** (O2). **No migration.** Backend paths relative to `backend/src`, frontend relative to `frontend/src/app`.

## 1. Scope

**11a – Context and access (makes backend Phases 1–7 usable)**: core (context service, branch/company selector, context-aware `hasPermission`, single-flight refresh, error-code handling, correlation id, lazy routes, build-time `apiBaseUrl`); admin screens for branches, warehouses, vehicles, stock locations (read-only), user branch access, scoped roles and role assignments, governance, platform console.

**11b – Inventory and documents (makes Phases 8–10 usable)**: location selectors on POS, purchase, adjustment and purchase-return forms; per-location stock views; transfer lifecycle screens and van workspace; branch/location filters and columns on lists and reports, seller GSTIN, restock-location display; full regression run and release runbook.

**Excluded**: report restructuring (unit-analysis Phase 7; only branch/location filters added); dashboard per branch; van trip/settlement document (Phase 10 O1); company creation from the company UI (platform-only); adding users of another company (no endpoint); new UI/state libraries.

## 2. Verified facts

**Frontend today**
- Session `{accessToken, refreshToken, user}` in localStorage `sellora.auth` (`core/auth/auth.service.ts:7-22`); `hasPermission` reads a flat cached list (`:34-40`), 57 call sites — keep its signature; `refresh()` not single-flight (`:48-53`).
- Interceptor retries a 401 once per request, so parallel 401s each refresh (`core/auth/auth.interceptor.ts:18-33`); no 403 handling, no `X-Branch-Id`.
- `AuthenticatedUser` has no company/branch fields (`core/auth/auth.models.ts:1-9`); `ApiProblem` has `code` and `errors` (`:23-28`).
- Sidebar tenant name hard-coded (`layout/sidebar/sidebar.html:5`); nav is static `NAV_DEF` filtered by `auth.hasPermission` (`layout/shell/shell.ts:37-109`, `:132`).
- Routes: 48 eager `component:` vs 29 `loadComponent` (`app.routes.ts:1-40`). Budgets: initial 700kB warn / 1MB error; component style 6/10kB (`frontend/angular.json:44-55`); CI builds prod, so the budget gates merges.
- Prod `apiBaseUrl: '/api/v1'` (`environments/environment.ts:3`); `staticwebapp.config.json` has no API route; SWA workflow `.github/workflows/azure-static-web-apps-*.yml:5-33`.
- `STOCK_MOVEMENT_TYPES` lacks TransferOut/TransferIn (`shared/units/stock-movement.ts:4-13`; backend `Domain/Entities/StockMovementType.cs:13-14`).
- `roleId: string` in `features/administration/users/users.models.ts:9,28,37` vs backend `Guid? RoleId` (`Api/Contracts/UserContracts.cs:11,17`).
- No frontend code uses branch/location/`sellerGstin` yet; 19 spec files, none for core/auth, shell or admin.

**Auth and context API**
- Login/refresh return `UserSummaryDto` (companyId/Name, accessScope, primaryBranchId, allowedBranches[{id, code, name, isPrimary, isDefault}], roles) (`Api/Contracts/AuthContracts.cs:16-41`); `GET /auth/me` adds `companies[]` and `governance` {memberId, title, permissions} (`:46-62`, `:83`), returns permissions at the working branch (header, else primary, else default) (`Api/Controllers/AuthController.cs:127-130`), `[AllowWithoutBranch]` (`:94`).
- Refresh is anonymous (primary-branch permissions), bound to the token's company; revoked membership → 401 `invalid_refresh_token` or 403 `no_branch_access` (`AuthController.cs:39-54`).
- `POST /auth/switch-company {companyId, refreshToken}` → new `AuthResponse`; errors 403 `company_access_denied`, 403 `no_branch_access`, 401 (`AuthController.cs:58-82`). Login codes `account_disabled`, `no_branch_access`, `no_company_access` (`:26-31`).
- Middleware: missing/inactive membership → 401 `company_context_invalid` (`Api/Authorization/CompanyContextMiddleware.cs:77-85`); no allocation → 403 `branch_required` (`:99-107`); malformed `X-Branch-Id` → 400 `branch_header_invalid` (`:113-121`); unknown/foreign/inactive/not allowed → 403 `branch_access_denied` (`:124-133`; also hidden locations, Phase 10 D3); platform endpoints with a company token → 401 `platform_session_required` (`:47-56`).
- CORS allowed headers: `Authorization, Content-Type, Idempotency-Key, X-Branch-Id` (`Api/Program.cs:93`). No correlation-id header today; audit uses `HttpContext.TraceIdentifier` (e.g. `UserAccessController.cs:94`); ProblemDetails carry `traceId` (`Api/ErrorHandling/GlobalExceptionHandler.cs:63`).
- Domain error codes: roles (`Domain/Roles/RoleModels.cs:21-28`), governance (`Domain/Abstractions/IGovernanceService.cs:13-19`), access (`Domain/Abstractions/IUserAccessService.cs:25-26`), `platform_admin_required`, `company_creation_disabled` (`PlatformAuthController.cs:25`, `PlatformCompaniesController.cs:65`), `reference_data_platform_managed` (`GlobalExceptionHandler.cs:37`).

**Admin and inventory endpoints consumed**
- Branches `BranchesController.cs:20-101` (`GET /branches/accessible` `:33` needs no permission → filter dropdowns); warehouses `WarehousesController.cs:19-101`; vehicles `VehiclesController.cs:19-109`; stock locations GET-only under `StockLocations.View` (`StockLocationsController.cs:14-58`), DTO has `CanSell`, `CanReceivePurchase` (`Contracts/StockLocationContracts.cs:4-15`), branch-eligible (`PermissionCatalog.cs:20`).
- User access `UserAccessController.cs:24-82` (contracts `UserAccessContracts.cs:7-32`); roles `RolesController.cs:26-87` (`?scope&branchId`); assignments `RoleAssignmentsController.cs:26-66` (contracts `RoleContracts.cs:7-46`); governance `GovernanceController.cs:28-74`; platform login `PlatformAuthController.cs:15-33` (access token only, `PlatformContracts.cs:13`), companies `PlatformCompaniesController.cs:21-70`, units/currencies `PlatformReferenceDataController.cs:27-129`.
- Transfers `StockTransfersController.cs:24-193` (`direction` relative to the working branch, `:47`).
- Sales catalog/barcode take `stockLocationId` (`SalesController.cs:27,51`); list filters `branchId`, `stockLocationId` (`:142-151`); detail `SellerGstin` + location fields (`:216-232`, `SalesContracts.cs:65-66`). Returns lists `SalesReturnsController.cs:24`, `PurchaseReturnsController.cs:25`. Purchases list `PurchasesController.cs:26`; detail `?stockLocationId` + `locationBalances` (`:67`, `PurchaseContracts.cs:26`). Stock adjustments list has only `branchId` (`StockAdjustmentsController.cs:22-25`).
- Stock overview/detail take `stockLocationId` (`StockController.cs:20,91`), detail `locations[]` (`StockContracts.cs:39-43`); reports current stock and movement take `stockLocationId` (`ReportsController.cs:20,122`), purchase/sales/profit take `branchId` (`:171,211,248`).
- Movement DTOs have ReferenceType/ReferenceNo but no ReferenceId (`StockContracts.cs:12-19`, `ReportContracts.cs:14-19`; entity `StockMovement.cs:30`).
- Payment-account options carry `BranchId` (`FinancialAccountContracts.cs:21`).

## 3. Decisions

**D1 Context model** (`core/context/context.service.ts`): signals `company`, `companies`, `accessScope`, `allowedBranches`, `primaryBranchId`, `workingBranchId`, `permissions`, `governance`, `status` (`loading | ready | noBranch | error`). Hydrate from `GET /auth/me` at start (`provideAppInitializer`), after login, company switch and branch switch. Working branch in sessionStorage per tab (`sellora.branch.<userId>.<companyId>`), default = localStorage last-used, else primary, else default branch; a stored value is only a preference and valid only if in `allowedBranches`. Permissions come only from `/me` (login/refresh permission lists ignored; server re-checks everything).

**D2 `X-Branch-Id`**: `core/http/context.interceptor.ts` adds the working branch to every `apiBaseUrl` request except `/auth/login|refresh|logout` and `/platform/*`; a company-wide user with no selection sends none.

**D3 Branch switch**: header selector (branch dropdown when > 1 allowed branch; company menu when > 1 company). Confirm when the route has unsaved changes; set `workingBranchId`, re-fetch `/me`, re-run route guards; go to `/dashboard` if the route is no longer permitted, else reload in place (`onSameUrlNavigation: 'reload'`; components re-fetch). Company switch: `switch-company`, persist the new session, clear the branch preference, `location.assign('/dashboard')`.

**D4 Single-flight refresh**: one shared in-flight refresh (`shareReplay` + reset); queued 401s wait and retry once; a `storage` listener adopts tokens rotated in another tab and refresh is skipped if the stored refresh token already changed; on failure logout to `/login?reason=<code>`.

**D5 Error handling** (`core/http/api-errors.ts` + `core/http/error.interceptor.ts`; features use `applyProblem(form, problem, fieldMap?)` and `problemMessage(problem)`, showing `traceId`):

| Code | Behaviour |
|---|---|
| 401 `company_context_invalid` | One refresh; on failure logout, reason `revoked` |
| 401 `invalid_refresh_token` | Logout, reason `expired` |
| 403 `branch_required`, `no_branch_access` | `/no-branch-access` page with sign-out |
| 400 `branch_header_invalid` | Clear selection, re-hydrate |
| 403 `branch_access_denied` | Re-fetch `/me` with the same header: if `/me` also 403 → branch revoked, reset to primary + toast; else resource-level (hidden location) → message |
| 403 without code | Re-fetch `/me` once (debounced, resync nav), permission toast; GETs → `/permission-denied` |
| `company_access_denied`, `no_company_access`, `account_disabled` | Login and switch forms |
| Role, governance, access, platform codes | Readable messages |
| 409 | "Reload and try again" |

**D6 Correlation id**: client UUID per request as `X-Correlation-Id`; backend adopts it as `TraceIdentifier` (G1); error toasts show `traceId`.

**D7 Platform console separate**: `/platform/**` lazy tree with its own layout, `PlatformAuthService` (sessionStorage `sellora.platform`, 30-min token, no refresh), guard; platform token only on `/platform/*`, never `X-Branch-Id`; 401 `platform_session_required`/expiry → `/platform/login`; no shared tokens, no link from the company shell.

**D8 Lazy routes**: `app.routes.ts` keeps login, forgot-password, the shell and `loadChildren` per area (`administration`, `company`, `inventory`, `purchases`, `sales`, `accounts`, `reports`, `platform`); each area gets `features/<area>/<area>.routes.ts` with `loadComponent` (one route file per WP). WP-A adds every new nav entry to `NAV_DEF` up front (permission, `governance` flag).

**D9 `apiBaseUrl` — build-time setting per environment, default relative (owner decision 2026-11-11)**
- `frontend/src/environments/environment.ts` stays `apiBaseUrl: '/api/v1'` (+ a comment that the deploy workflow may rewrite it). No placeholder: local, CI and PR-preview builds behave exactly as today.
- New `frontend/scripts/set-api-base-url.mjs` (Node, no dependencies) reads env `SELLORA_API_BASE_URL`: empty/unset → exit 0, no change; otherwise must match `^https://[^/\s]+(/[^\s]*)?$`, trailing `/` stripped, rewrites only the `apiBaseUrl:` literal to `'<url>/api/v1'` and prints it; malformed → exit 1 (deploy fails, a typo cannot ship).
- SWA workflow: step `Set API base URL` after checkout, before "Build And Deploy": `env: SELLORA_API_BASE_URL: ${{ vars.SELLORA_API_BASE_URL }}`, `run: node frontend/scripts/set-api-base-url.mjs`. Per environment via a GitHub Environment variable of the same name (repo-level as fallback). Rejected: `fileReplacements` per environment (commits URLs, one configuration per environment).
- Interceptors (D2, D6, D7) match on `environment.apiBaseUrl` as a prefix; WP-A specs run with both `'/api/v1'` and `'https://api.example.test/api/v1'`.
- CORS later, configuration only: App Service `Cors__AllowedOrigins__0=https://<swa-host>` (`__1..n` for custom domain / previews); allowed headers already include `X-Branch-Id`, `Idempotency-Key`, `Content-Type`; G1 adds `X-Correlation-Id`.
- Runbook (WP-R, release-runbook.md): (1) set `SELLORA_API_BASE_URL=https://<app-service-host>` (no `/api/v1`, no trailing slash) on the GitHub Environment or repo; (2) set App Service `Cors__AllowedOrigins__0=https://<swa-host>`, restart the API; (3) re-run the SWA workflow; (4) smoke: calls go to `https://<app-service-host>/api/v1/...` with a successful OPTIONS preflight, branch switch works. Rollback: clear the variable, re-run the workflow.
- Ownership: WP-R (devops-release-engineer) owns the script and workflow step, in 11a (independent, inert while unset); WP-A owns the `environment.ts` comment and prefix matching.

**D10 Location selection**: shared `LocationSelect` (`purpose: 'sale' | 'purchase' | 'adjust' | 'transfer' | 'filter'`) filters `GET /stock-locations` by `CanSell` / `CanReceivePurchase` / non-Transit, default = working branch's BranchStore; rendered only with `StockLocations.View` and > 1 eligible location; otherwise `stockLocationId` is omitted (server default).

**D11 Van workspace** (Phase 10 O9): POS with a vehicle selected; "Load vehicle" / "Unload vehicle" = transfer-create shortcuts with endpoints prefilled, chaining create(submit) → approve → dispatch → receive (own idempotency key per step) as far as the user's permissions allow, else stopping and linking to the transfer; vehicle stock = stock overview filtered by the vehicle.

**D12 Reference data**: units and currencies read-only in the company UI (writes hidden; managed in the platform console); `reference_data_platform_managed` mapped anyway.

## 4. Design table

| WP | Files (frontend unless noted) | Change |
|---|---|---|
| A | core/auth/auth.models.ts, auth.service.ts, auth.guard.ts, auth.interceptor.ts | Company/branch/governance fields; `hasPermission` delegates to context; D4; `permissionGuard` waits for context `ready`; `governanceGuard` |
| A | core/context/context.service.ts, context.models.ts (new) | D1, D3 |
| A | core/http/context.interceptor.ts, error.interceptor.ts, correlation.interceptor.ts, api-errors.ts (new); app.config.ts | D2, D5, D6; order correlation → auth → context → error |
| A | layout/header/*, layout/sidebar/sidebar.html:5, layout/shell/shell.ts:37-132 | Selector (D3); real company/branch names; full new `NAV_DEF` |
| A | features/no-branch-access/* (new) | `branch_required` / `no_branch_access` page |
| A | app.routes.ts; features/<area>/<area>.routes.ts (new, routes moved) | D8; paths unchanged |
| A | shared/locations/{stock-locations.service, locations.models, location-select}.ts; shared/branches/branch-select.ts (new) | D10; branch filter via `/branches/accessible` |
| A | environments/environment.ts (comment only) | D9 prefix matching in interceptors |
| R (11a) | frontend/scripts/set-api-base-url.mjs (new); .github/workflows/azure-static-web-apps-*.yml | D9 build-time substitution, inert while unset |
| B | features/company/{branches,warehouses,vehicles,stock-locations}/* | List + form each; status actions with reason; vehicle reassignment shows server `InUse` error; stock locations read-only with type/branch/inactive filters |
| C | features/administration/users/* (roleId → `string \| null`); users/user-access/* (new) | Allocations + history, add/end with reason, primary, reassign, scope, suspend/revoke, audit events; "Roles" tab (current/history, assign, end with reason) |
| C | features/administration/roles/* | Scope + branch on create, read-only on edit; list filter `?scope&branchId`; branch roles only branch-eligible codes; duplicate; branch members view |
| C | features/administration/governance/* (new) | Members, add, title, permissions, end, administrators grant/remove, events |
| C | features/platform/* (new) | D7: companies (list, create disabled-aware, status with reason), units/currencies CRUD + status; masters units/currencies writes hidden (D12) |
| D1 | shared/units/stock-movement.ts; features/inventory/stock/{stock.models, stock-overview, stock-detail} | TransferOut/TransferIn labels; location filter/column; per-location table; movement → transfer link (G2); Transit badge |
| D1 | features/sales/pos/*; purchases/purchase-form/*; inventory/stock-adjustments/stock-adjustment-form/*; purchases/purchase-returns/purchase-return-form/* | LocationSelect (sale/purchase/adjust); POS catalog/barcode pass `stockLocationId` (location change → confirm, re-price/clear cart); purchase-return location picker from `locationBalances`, sends `stockLocationId` + `Idempotency-Key`; sales-return form sends `Idempotency-Key`; `stockLocationId` field errors; POS accounts filtered to `branchId ∈ {null, working}`; missing unsavedChangesGuard on adjustment-new |
| D2 | features/inventory/stock-transfers/* (new: list, detail, form, dispatch/receive/resolve dialogs, van-load wizard) | Phase 9 API; list filters; actions gated by permission + status; receive accepted/damaged + outstanding; resolve WriteOff/ReturnToSource; field errors; idempotency keys; D11 |
| E | sales/purchases/returns/adjustments/expenses lists + details; reports/* | Branch filter/column (company-wide users) and location filter/column; seller GSTIN, branch, location on invoice detail; restock location on sales-return detail; returnable per location; reports branch/location filters; `api-errors.ts` everywhere |
| Backend G1 | Api/Program.cs (small middleware before auth; `X-Correlation-Id` in CORS `:93`) | Adopt a valid header (`^[A-Za-z0-9-]{8,64}$`) as TraceIdentifier |
| Backend G2 | Api/Contracts/StockContracts.cs:12, ReportContracts.cs:14; StockController.cs:112-120; ReportsController movement projection | Additive `Guid? ReferenceId = null` |
| Backend G3 | StockAdjustmentsController.cs:22-25 | `stockLocationId` list filter |
| Release | docs/architecture/multi-company/release-runbook.md (new) | Runbook (§8) |

## 5. Migration

None. G1–G3 are API-only and additive.

## 6. API consumption and contracts

Frontend models mirror the DTOs in camelCase with string enums (`RoleScope`, `CompanyAccessScope`, `AssignmentStatus`, `MembershipStatus`, `GovernanceTitle`, `GovernanceMemberStatus`, `StockTransferStatus`, `StockTransferResolution`, `StockLocationType`). New headers: `X-Branch-Id`, `X-Correlation-Id` (G1), `Idempotency-Key` (returns; transfer create/dispatch/receive/resolve). No contract changes beyond G1–G3; the frontend tolerates their absence (`referenceId?`).

## 7. Work packages (max 2 parallel, never sharing files)

**11a**
1. **WP-A frontend-engineer** (core, layout, route split, shared selects, nav, error utilities) ‖ **WP-G backend-engineer** (G1, G2, G3 + tests); **WP-R1 devops-release-engineer** (D9 script + workflow step) after WP-G. AC: existing specs green; route paths unchanged; prod build within budget (before/after initial size recorded); branch switch changes permissions and nav without re-login; 5 parallel 401s → exactly 1 refresh; no `X-Branch-Id` on auth/platform calls.
2. **WP-B** ‖ **WP-C** frontend-engineers, after A. B AC: CRUD + status for 4 screens; server field errors on controls; no action shown without permission. C AC: allocation changes visible in the target user's next `/me`; role scope immutable on edit; governance/admin actions hidden without `governance.permissions`; platform console unreachable with a company token and vice versa.
3. **WP-T1 test-engineer**: Vitest per arch §18.3 — ContextService, interceptors (header, single-flight, storage sync, every D5 code, correlation id), guards, shell nav per scoped permission, selector visibility, user-access and role-scope forms, governance screens, empty/loading/error states.
4. code-reviewer → lead `scripts/verify.sh all` → **CHECKPOINT 11a** (releasable alone).

**11b**
5. **WP-D1** ‖ **WP-E**. D1 AC: POS sells from a vehicle; a cashier without `StockLocations.View` sees no selector and posts at the default store; purchase return from another location works; quantities use QuantityPipe. E AC: every Phase 8/10 filter wired; company-wide users' lists show branch.
6. **WP-D2** (after D1 lands stock-movement.ts): transfers + van. AC: full lifecycle incl. partial receipt, variance, resolve; key replay does not double-post; van-load wizard stops at the first missing permission.
7. **WP-T2 test-engineer**: Vitest for transfer screens, location-aware forms, list filters; lead-run Playwright regression (arch §18.4) against a local API + PostgreSQL, not committed (O8).
8. **WP-R devops-release-engineer**: `release-runbook.md` (backend → validation → frontend order; idempotent migration script; seed-permissions.sql; grant-platform-admin.sql; `Cors:AllowedOrigins`; flags; both validation SQL files; smoke list; rollback; known limitations; D9 URL runbook).
9. code-reviewer → lead `verify all` + regression → **CHECKPOINT 11b**.

## 8. Risks and release

- Bundle budget unknown until WP-A measures a prod build; lazy-loading the 48 eager routes is the lever (CI fails at 1MB).
- `branch_access_denied` covers both a revoked header branch and a hidden location; D5's `/me` probe distinguishes them — test both.
- Refresh-token rotation across tabs needs D4 storage sync (else a second tab logs out).
- In-flight requests during a branch switch may return old-branch data; D3 re-runs guards and components re-fetch.
- Allocation revoked mid-session is enforced server-side immediately; the UI corrects on the next 401/403.
- SWA PR previews may hit an older API; the frontend tolerates missing G2/G3.
- Release order: backend H–10 + scripts → 11a frontend → 11b frontend; 11a must not ship before the backend serving `/auth/me` with companies + governance.
- Until `SELLORA_API_BASE_URL` is set, behaviour is unchanged. Once set, requests are cross-origin, so the CORS origin and allowed headers must be in place before the SWA re-run (runbook order).

## 9. Owner questions (with recommendation)

- **O1** URLs: **decided** — build-time setting, URLs supplied later (D9).
- **O2** Split into 11a and 11b with separate checkpoints and releases? Rec: yes.
- **O3** Branch switch re-navigates in place (rec) vs always hard reload? Company switch always hard reloads.
- **O4** Working branch per browser tab (sessionStorage, last-used as default)? Rec: yes.
- **O5** Location selector on POS/purchase/adjustment only for `StockLocations.View` holders (van sellers get a branch role including it)? Rec: yes.
- **O6** Van-load wizard chains every step the user may perform? Rec: yes.
- **O7** Unit/currency writes hidden in the company UI, managed only in the platform console? Rec: yes.
- **O8** Playwright only for a lead-run regression, nothing committed or added to CI in Phase 11? Rec: yes.
- **O9** Backend gaps G1 (correlation header), G2 (`ReferenceId` on movements), G3 (adjustment location filter)? Rec: yes to all three.

## 10. Deviations

- No `X-Company-Id` header (company is token-bound, changed by switch-company; arch §15 satisfied by `/me` re-validation).
- "Company administration" = existing settings page + branch/warehouse/vehicle/location screens; company creation is platform-only.
- Correlation id needs a small backend change (G1).
- Reports get branch/location filters only; breakdown/entered-unit columns stay in unit-analysis Phase 7.
- Phase split into 11a/11b (arch §19 allows sub-phases).
- Phase 2 WP5 plan (placeholder token, build fails when unset) replaced by the owner's default-to-relative rule (D9).

## Owner approval (2026-11-11)

Design approved, deviations accepted. O1 decided earlier (build-time `apiBaseUrl`, URLs later). O2–O9 all as recommended: split into 11a and 11b with separate checkpoints/releases; branch switch re-navigates in place (company switch hard reload); working branch per tab with last-used default; location selector only for `StockLocations.View` holders; van-load wizard chains every permitted step; unit/currency writes only in the platform console; Playwright for a lead-run regression only (not committed / not in CI); backend gaps G1, G2, G3 approved.

## Owner decisions during 11a (2026-11-11)

**D13 "All branches" in the switcher.** Shown when the user may work in more than one branch: an AssignedBranches user with 2+ allocations, or a company-wide user/administrator (all company branches). Selecting it sends no `X-Branch-Id`; reads then cover every allowed branch, narrowed per permission (existing server behaviour, `CompanyContextMiddleware` + `PermissionAuthorizationHandler`). It is an explicit choice; the default stays last-used → primary → default branch. Because a write without the header lands on the primary branch, routes that create or change documents (POS, new purchase/sale/adjustment/return/transfer, transfer actions, branch-bound forms) carry `data.requiresBranch`; in All-branches mode a guard asks the user to pick a branch first (no silent primary). Navigation in All-branches mode uses `/auth/me` `permissionsAnyBranch` (new, additive: company-level codes ∪ codes held at any allowed active branch) so menus for branch-role holders stay visible; the server still authorises every request.

**D14 Reason on activate/deactivate.** Branch, warehouse and vehicle status changes take a required reason (1–500 chars), recorded in the audit event; the screens use the reason prompt instead of a plain confirm. Additive API change (request body gains `reason`); missing reason → 400 field `reason`.

## 11a checkpoint — delivery record (2026-11-11)

Status: **11a delivered — review "Approve with fixes" (no Blocker), fixes applied.** Verify: 1584 unit / 1043 PostgreSQL integration / 527 Vitest, 0 warnings, prod build within budget (initial 402 kB). No migration. Releasable alone **after** the backend H–10 release (11a needs `/auth/me` with companies, governance and `permissionsAnyBranch`, and D14 status reasons — frontend and backend D14 must ship together).

**Delivered**
- Core (WP-A): ContextService (company/branch context, per-tab working branch, `/auth/me` hydrate), header branch/company switcher with "All branches" (D13), context-aware `hasPermission` (uses `permissionsAnyBranch` in All-branches mode), single-flight refresh + cross-tab session sync (a different user/company or a logout in another tab resets this tab), interceptors (correlation → platform → auth → context → error), D5 error-code handling, `requiresBranch` guard with branch picker for document-creating routes, lazy routes per area (initial bundle 688 kB → ~402 kB; paths unchanged), full navigation, shared BranchSelect/LocationSelect.
- Admin (WP-B): branches, warehouses, vehicles (list/create/edit/status with reason, D14), stock locations (read-only).
- Access (WP-C): user access (allocations, primary, scope, suspend/revoke, role assignments, audit), roles (scope on create; branch admins via `Branch.Roles.*`), governance, separate platform console (own login/session, companies, units, currencies); company-app units/currencies read-only (O7).
- Backend: G1 correlation id (middleware, CORS allowed + exposed header, all ProblemDetails carry it), G2 `ReferenceId` on movement DTOs, G3 adjustment location filter, `PermissionDto.Scope`, D13 `/auth/me` `permissionsAnyBranch`, D14 required `reason` on branch/warehouse/vehicle status with audit events (`*.status_changed`).
- Release (WP-R1): `frontend/scripts/set-api-base-url.mjs` + SWA workflow step (no-op while `SELLORA_API_BASE_URL` is unset; rejects malformed values).

**Fixes during delivery**: review Majors (cross-tab company/user change and logout); case-insensitive auth/platform URL matching; background-GET 403 no longer ejects the page; branch switch rolls back on hydrate failure; single session-ending owner; unreachable unit/currency forms removed; correlation header survives 500s; replacement-pattern safety in the URL script; workflow line endings restored; branch admins can open roles.

**Breaking API note (D14)**: `POST /branches|warehouses|vehicles/{id}/status` now require `reason`; deploy backend and 11a frontend together.

**Open / carried to 11b**: flag transfer create/action routes `requiresBranch` when built; screens opt into `primaryLoad()` for 403 → `/permission-denied`; status-change audit has no correlation id (services have no HTTP context).

## 11b checkpoint — delivery record (2026-11-11)

Status: **11b delivered — review "Approve with fixes" (no Blocker), fixes applied; Playwright regression run against a real local stack.** Verify: 1586 unit / 1043 PostgreSQL integration / 755 Vitest, 0 warnings, prod build within budget (initial 403 kB); after D15: 1587 unit, 755 Vitest, 163 permission/role integration tests green. No migration. Release after 11a (see `release-runbook.md`).

**Delivered**
- Location-aware forms (WP-D1): POS (incl. selling from a vehicle; location change confirms and clears the cart; `?locationId` preselect from the van workspace), purchase, adjustment, purchase return (location picker from `locationBalances`), sales return; `stockLocationId` and Idempotency-Key sent; account options filtered to company-level + working branch; stock overview/detail per location (Transit badge, movement → transfer link, `?stockLocationId` URL filter).
- Lists/details/reports (WP-E, `shared/scope/*`): branch + location filters and columns on sales, returns, purchases, adjustments, expenses (branch only); seller GSTIN, branch, location, restock/issuing location on details; returnable per location on purchase detail; report branch/location filters; `primaryLoad()` on detail main loads.
- Transfers + van (WP-D2): list/detail/create/edit, dispatch/receive (partial, damage)/resolve dialogs, status × permission gating, van workspace with load/unload wizard chaining every permitted step.
- Release runbook (WP-R): `docs/architecture/multi-company/release-runbook.md` (order, idempotent migration script command verified, validation SQL, settings, smoke, rollback, known limitations, URL steps).
- Backend: stock-locations list uses the effective owner (BranchId ?? vehicle AssignedBranchId) for the branch filter and visibility (DEF-2).

**Regression (Playwright, not committed, O8)**: 19 flows on PostgreSQL 17 + API + Angular dev server: login, branch/company switch incl. All branches and picker, POS store/van, purchase into warehouse, adjustment, transfer lifecycle (partial receipt, damage, excess refused, resolve), purchase return from a moved location, sales return, sale cancel, van load/sell/unload, stock and reports per location, allocation revoke, branch-role limits, governance, platform console, 30 existing screens — 0 console errors, 0 5xx. Two defects found and fixed: DEF-1 (branch picker invisible on deep link / reload in All-branches mode → host moved to the app root) and DEF-2 (branch-assigned vehicles missing from the van workspace).

**Review fixes**: transfer dialog idempotency survives close/reopen and reloads after ambiguous failures; idempotency keys rotate when the request body changes (all forms, wizard, dialogs); adjustment preview fails closed instead of guessing a store; POS catalog cleared on location change; runbook wording.

**D15 (owner decision 2026-11-11)**: `Customers.View` becomes branch-eligible (CompanyOrBranch), so a branch-only cashier can pick named customers at POS through a branch role. Customers stay company master data (no branch filter). `Masters.View` and others unchanged.
