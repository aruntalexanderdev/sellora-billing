# Phase 5: User company membership and branch allocation (file-level design, DRAFT for owner approval)
Baseline `analyse` @ 3ad13f5. Sources: arch §6.1-6.5, §7.6, §14, §16, §17.3, §19 Ph5; plan §5 ADR-04/05/13, §10 Q7 + login rules + Phase H;
phase-1 D1/§8; phase-2 D5/D6/D8; phase-3/4 visibility (CanAccess). Size L, one migration, one release.

## 1. Scope
Delivers: historical `UserBranchAssignments` (multi-branch, one active primary); AssignedBranches scope made live (middleware loads
allocations; header-less = primary); named EF filter "Branch" on branch documents; allocation management API (company-level permission
`Company.Users.ManageBranchAccess`, branch-restricted actors cannot manage); minimal append-only `AuditEvents` for membership/allocation
changes; `AccessVersion` stamp (cache seam); Q7 backfill (non-admin users -> AssignedBranches + Main primary); login/me expose scope and
allowed branches; user create gets an initial allocation.
Excludes: branch picker / allocation screens in Angular (Phase 11); scoped roles, UserRoleAssignments, effective-permission cache (Phase 7);
governance members/permissions (Phase 6); per-branch stock and stock-report scoping (Phase 8 - stock stays company-wide); future-dated
allocations; generic audit browser; audit of non-access entities (later phases append to the same table); HR/Employee entities (arch §6.5).

## 2. Decisions
D1 Table `UserBranchAssignments` (ICompanyOwned -> Company filter): Id uuid PK; CompanyId; UserCompanyMembershipId; BranchId; IsPrimary bool; Status varchar(20) {Active, Ended}; StartAt timestamptz (= now, no future dating); EndAt timestamptz NULL; AssignmentReason varchar(500) NULL;
   EndReason varchar(500) NULL; CreatedAt; CreatedByUserId NULL FK Users; EndedByUserId NULL FK Users. FKs (composite, navigation-less on Branch per phase-2 D3): (CompanyId, UserCompanyMembershipId) -> UserCompanyMemberships AK (CompanyId, Id)
   (exists, CompanyConfigurations.cs:26); (CompanyId, BranchId) -> Branches AK. Membership gets nav collection `BranchAssignments`. Partial uniques: `IX_UserBranchAssignments_Membership_Branch_Active` (UserCompanyMembershipId, BranchId) WHERE Status='Active';
   `IX_UserBranchAssignments_Membership_Primary` (UserCompanyMembershipId) WHERE Status='Active' AND IsPrimary; index (CompanyId, BranchId) WHERE Status='Active'. CHECKs: Status list; `(Status='Active') = (EndAt IS NULL)`; EndAt >= StartAt; Id not empty.
   History rule: a row is written once, and only closed once (Status/EndAt/EndedBy/EndReason). IsPrimary/BranchId never change on a row: a primary change closes the affected rows and inserts successors (two SaveChanges in the tx: close first, then insert, so the partial
   unique never sees two primaries; EF does not order update-before-insert for unique indexes).
D2 Service invariants (not SQL-expressible): an Active AssignedBranches membership has >= 1 active assignment and exactly one active primary; a CompanyWide membership has no active assignment (switching to CompanyWide closes them, reason recorded); Administrator-role users
   (Role.IsSystem, AccessManagementActor.cs:52) are always CompanyWide (Q7); promotion to Administrator (UsersController.cs:215) forces CompanyWide in the same tx; demotion keeps the current scope.
D3 Q7 backfill is a documented default mapping, not fabricated data: owner answer 2026-11-09 (plan §10 Q7) explicitly maps existing users to Main as primary, Administrator stays company-wide; arch §17.3 allows a documented default mapping. Recorded in the migration header, in
   AssignmentReason of each row and as one AuditEvent per mapped membership. StartAt = migration instant (no invented past dates).
D4 Middleware (CompanyContextMiddleware.cs:100-112 query extended, still one round-trip): project AccessScope + active assignments (BranchId, IsPrimary). CompanyWide -> allowed null, primary null. AssignedBranches -> allowed = active assignment branch ids (inactive
   branches included so history stays readable), primary = primary row. Empty set -> 403 `branch_required` except endpoints carrying a new marker `[AllowWithoutBranch]` (AuthController.Me, SwitchCompany; UsersController me/username, me/change-password). Header rules
   unchanged (:70-94). IBranchContext gains `Guid? PrimaryBranchId`; BranchContext.Set(requested, allowed, primary). BranchRules.ResolveWorkingBranchAsync (BranchRules.cs:27-41): requested ?? primary ?? default; default only if CanAccess (CompanyWide);
   inactive primary -> existing 400 `branchId`. Existing SPA sends no header (no X-Branch-Id in frontend/src/app) -> mapped users work in Main.
D5 Login (AuthService.cs:122-127 picks first membership by CreatedAt): pick the first *usable* membership (CompanyWide, or >= 1 active assignment); Active memberships but none usable -> new LoginFailureReason.NoBranchAccess -> 403 `no_branch_access` (AuthController.cs:27
   pattern). Refresh/switch-company apply the same usability check (403 `no_branch_access`).
D6 "Branch" named filter (BranchQueryFilter.Name, beside CompanyQueryFilter): mandatory on PurchaseOrders, PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments: `Allowed == null || Allowed.Contains(e.BranchId)`; optional on Expenses, FinancialAccounts:
   `e.BranchId == null || Allowed == null || Allowed.Contains(e.BranchId.Value)` (NULL = company-level, visible). Parameter = private `Guid[]? CurrentAllowedBranchIds` instance member (as CurrentCompanyId, SelloraDbContext.cs:19). Children (lines, payments) inherit
   through their existing navigation-based filters (SelloraDbContext.cs:96-101) - ASSUMPTION: EF applies the navigated entity's filters; proven by test T3 before anything else relies on it. Behaviour: lists/reports/dashboard silently narrow to allowed branches (= "all my
   branches"); explicit `branchId` of a non-allowed branch -> 403 `branch_access_denied` (BranchLabels.cs:39, unchanged); detail, payment, cancel, return of a document in a non-allowed branch -> 404 (filter; no existence leak). CompanyWide users: unchanged, "All branches".
   Branches table itself is not filtered (labels, management screen). Stock tables and stock reports stay company-wide until Phase 8.
D7 Mandatory "Branch" bypasses (company-integrity reads; each gets `IgnoreQueryFilters([BranchQueryFilter.Name])` + "documented bypass" comment, Company filter kept): DocumentNumbers.cs:160-165 (EXP series :165 is company-wide - would duplicate numbers); ProductUnitUsage.cs
   (all document-line usage checks - otherwise ProductUnit immutability, CLAUDE.md rule, is bypassable); FinancialAccountService.cs:18,:60 (name unique index FinancialAccountConfiguration.cs:16 -> 500); ProductService base-unit history check, supplier/customer/currency/tax
   usage checks, StockLocationUsageService, BranchService.cs:256 (backend-engineer greps every Any/Count over the 7 entities and their children; list in PR). Label-only lookups for company-wide stock views: SalesController.cs:151,:300-302 and StockController.cs:117-118
   traverse batch -> PurchaseOrderLine -> PurchaseOrder; a batch bought at another branch would become "Adjustment"/null under the filter -> replace with an id lookup that ignores "Branch" (ref number is company-internal traceability; the link 404s for the restricted user).
   Same for stock-movement reference enrichment (ASSUMPTION: verify MovementSnapshot/enrichment lookups). Phase 4 rule: filter roots before projections. FinancialTransactionsController: restrict rows to visible accounts (account BranchId null or allowed) explicitly.
D8 Management API authorisation (arch §6.4, §7.6): writes need policy `Company.Users.ManageBranchAccess` (new code, ADR-13, Administration group, not granted to any non-system role by default; system role holds all - RolesController.cs:227) AND the actor's own membership must
   be CompanyWide (branch-restricted actors = branch admins -> 403 `access_management_forbidden`); actor cannot change own access (403 `self_access_change_forbidden`); Administrator targets only by Administrators (H1) and cannot be AssignedBranches (400 `accessScope`);
   target must have a membership in the current company (L1, else 404); shared users are allowed (allocation is per company, unlike UsersController.cs:311 platform fields). All writes in AccessManagementScope (AccessManagementActor.cs:36-54) so the actor is re-read
   after the advisory lock; actor scope re-read inside the lock too.
D9 Membership status: Active <-> Suspended; Active/Suspended -> Revoked (closes all active assignments); Revoked -> Active requires a scope and, for AssignedBranches, branch ids + primary in the same request. No self change. Middleware requires Active (:106) -> next request 401.
D10 Revocation effective immediately: middleware reads membership + assignments per request (no cache, ADR-04 cache is Phase 7). Cost: the existing query gains a filtered LEFT JOIN on an indexed set (<= a few rows) - negligible. `UserCompanyMemberships.AccessVersion bigint
   NOT NULL DEFAULT 0` incremented on every access change = the invalidation stamp Phase 7's cache keys on (avoids a second migration).
D11 AuditEvents now (arch §19 Ph5 scope "add audit events"): Id; CompanyId NOT NULL (ICompanyOwned; Phase 6 may relax to NULL - instant); OccurredAt; ActorUserId NULL FK Users; Action varchar(64); TargetType varchar(64); TargetId uuid; SubjectUserId uuid NULL FK Users;
   Reason varchar(500) NULL; Before jsonb NULL; After jsonb NULL; CorrelationId varchar(64) NULL (HttpContext.TraceIdentifier). Indexes (CompanyId, TargetType, TargetId, OccurredAt) and (CompanyId, SubjectUserId, OccurredAt). Append-only: SelloraDbContext.SaveChanges
   throws on Modified/Deleted AuditEvent entries; no update/delete API. Written in the same tx as the change. No secrets in payloads. Actions: membership.created, membership.scope_changed, membership.status_changed, membership.access_mapped (migration),
   branch_assignment.created, branch_assignment.ended, branch_assignment.primary_changed, access.reassigned.
D12 User create (UsersController.cs:119-173): additive optional request fields AccessScope, BranchIds, PrimaryBranchId. Omitted -> documented default: Administrator role -> CompanyWide; else AssignedBranches + one primary assignment at the creator's working branch
   (BranchRules.ResolveWorkingBranchAsync - cannot exceed the creator's own reach). Supplying the fields requires ManageBranchAccess + CompanyWide actor (D8). Writes membership.created + branch_assignment.created audit rows. (Owner question O1.)
D13 Branch deactivation (BranchService.cs:157-182): refuse while the branch is the active primary of an Active membership (400 `IsActive`, "N users have this as primary branch"); non-primary assignments stay (they cannot work there: header requires active).
D14 Phase 3/4 visibility becomes live without code change: StockLocationsController.cs:41-44,:63, warehouse/vehicle CanAccess (phase-4 D9/D11), BranchesController.Accessible :35-43 (now = allocations; DTO gains IsPrimary). Phase H rules H1-H4 unchanged and reused.
D15 Reassignment (§6.5) minimal, in scope: one endpoint = close listed assignments + create destination (optionally primary) atomically, one access.reassigned audit event. Temporary extra branch = plain add (primary untouched).

## 3. Files per layer
Domain: Entities/UserBranchAssignment.cs, AssignmentStatus.cs, AuditEvent.cs (new); UserCompanyMembership.cs (+AccessVersion, +BranchAssignments); Abstractions/IBranchContext.cs (+PrimaryBranchId); Auth/AuthModels.cs (+NoBranchAccess, summary +AccessScope/PrimaryBranchId/AllowedBranches);
  Permissions/PermissionCatalog.cs (+Company.Users.ManageBranchAccess near :32-35); Services/IUserAccessService.cs + input records (new).
Infrastructure: Configurations/UserAccessConfigurations.cs (new: assignments, audit) + CompanyConfigurations.cs (AccessVersion); SelloraDbContext.cs (DbSets, "Branch" filter, audit append-only guard); Data/BranchQueryFilter.cs (new); BranchContext.cs (primary,
  Restricted(...) test factory); Services/BranchRules.cs; Services/UserAccessService.cs (new, all D8/D9/D15 rules + audit writer); Services/AuditLog.cs (new static helper); AuthService.cs (D5); BranchService.cs (D13); bypass sites of D7; DbSeeder.cs (admin stays
  CompanyWide; demo users if any get Main primary); Migrations/<ts>_AddUserBranchAssignments (+Designer, snapshot).
Api: Authorization/CompanyContextMiddleware.cs (D4), Authorization/AllowWithoutBranchAttribute.cs (new); Controllers/UserAccessController.cs (new); UsersController.cs (D12, D2 promotion); AuthController.cs (me, codes); BranchesController.cs (IsPrimary); SalesController.cs,
  StockController.cs, FinancialTransactionsController.cs (D7); Contracts/UserAccessContracts.cs (new), AuthContracts.cs, UserContracts.
Frontend: none required (O3). Additive JSON fields are ignored by the SPA.

## 4. Migration `<ts>_AddUserBranchAssignments` (ts > 20261110043527; header layout as 20261109105800_AddBranches.cs:8-39; one tx)
additive: UserBranchAssignments, AuditEvents (D1, D11); UserCompanyMemberships.AccessVersion (default 0, instant).
guard G1: RAISE unless every company has exactly one active default branch (AddBranches V4). G2: RAISE if any membership is already
  AssignedBranches (Phase 1 service rejected it; unexpected state). G3: RAISE if a company has no Active membership of an active Administrator user (would leave nobody able to manage access).
backfill (Q7): for every membership whose user's role has IsSystem = false: AccessScope -> 'AssignedBranches', AccessVersion + 1; for those with Status 'Active' insert one assignment: Id md5('uba:'||m."Id")::uuid, BranchId = company default branch, IsPrimary true, Status
  'Active', StartAt now(), AssignmentReason 'Migration default mapping (owner decision Q7, 2026-11-09)', CreatedByUserId NULL; one AuditEvent 'membership.access_mapped' per mapped membership (Id md5('aev:'||m."Id")::uuid). ON CONFLICT DO NOTHING; WHERE guards make
  re-runs no-ops. Administrator memberships untouched (CompanyWide).
P-block (prod copy, before release): P1 users per role with IsSystem flag and membership status (owner reviews who becomes branch-only, esp. accountant/owner roles - O2); P2 documents per branch (non-Main documents become invisible to mapped users).
V-block (must return no rows): V1 Active AssignedBranches memberships without exactly one active primary; V2 Active memberships of non-system-role users still CompanyWide; V3 memberships of system-role users not CompanyWide; V4 assignments whose branch is not the
  company default; V5 count(assignments) <> count(Active non-admin memberships) (as a SELECT returning a row on mismatch); V6 companies without an Active CompanyWide Administrator membership; V7 audit rows <> mapped memberships.
Down guard: RAISE if any assignment has CreatedByUserId NOT NULL or Status 'Ended', or any AuditEvent has Action other than membership.access_mapped (post-release data would be lost); else restore CompanyWide on mapped memberships, then remove the two tables
  and the column. Release note: two new tables + one defaulted column, instant; old builds ignore them (old middleware fails AssignedBranches closed = mapped users locked out on rollback of app only -> rollback = restore snapshot + previous build, D10).

## 5. API / DTO contract (additive; codes in ProblemDetails `code`)
- Login/refresh/me `user` (UserSummaryDto, AuthContracts.cs:15-29; MeResponse :34-45): + `accessScope` ("CompanyWide"|"AssignedBranches"), `primaryBranchId` (null for CompanyWide), `allowedBranches` [{id, code, name, isPrimary, isDefault}] (CompanyWide: all active branches).
- GET branches/accessible: + `isPrimary`.
- GET  users/{id}/access [Users.View] -> {membershipId, status, accessScope, accessVersion, current[], history[]}; assignment = {id, branchId, branchCode, branchName, isPrimary, status, startAt, endAt, assignmentReason, endReason, createdBy, endedBy}.
- GET  users/{id}/access/events?page= [ManageBranchAccess] -> paged audit events for the subject user.
- PUT  users/{id}/access/scope {accessScope, branchIds?, primaryBranchId?, reason} -> to AssignedBranches requires branchIds+primary.
- POST users/{id}/access/assignments {branchId, isPrimary, reason?} -> 201; isPrimary closes+reissues the old primary (D1).
- POST users/{id}/access/assignments/{assignmentId}/end {reason} -> 200; ending the primary or the last assignment -> 400 `assignmentId`.
- POST users/{id}/access/primary {branchId, reason?} -> branch must hold an active assignment.
- POST users/{id}/access/reassign {fromAssignmentIds[], toBranchId, makePrimary, reason} (D15).
- POST users/{id}/access/status {status, reason, accessScope?, branchIds?, primaryBranchId?} (D9).
- POST users: + accessScope?, branchIds?, primaryBranchId? (D12).
- Errors: 403 branch_required, no_branch_access, access_management_forbidden, self_access_change_forbidden; existing branch_access_denied,
  branch_header_invalid, company_context_invalid; 400 field errors (branchId, branchIds, primaryBranchId, accessScope, assignmentId, status); 404 foreign/unknown user or assignment (identical bodies).

## 6. Work packages (max 2 parallel; no file overlap)
WP1 database-engineer: Domain entities/enum, configurations, DbSets, audit append-only guard, migration + Designer + snapshot, P/V/guards, DbSeeder. AC: build green; MigrationTests no pending changes; V-block empty on seeded DB; Down guard proven.
WP2 backend-engineer (after WP1): IBranchContext/BranchContext, middleware + AllowWithoutBranch, BranchRules, "Branch" filter, all D7 bypasses + label lookups, AuthService/AuthController/AuthContracts, BranchesController, BranchService D13. AC: T1-T6 pass; bypass list in PR.
WP3 backend-engineer #2 (after WP1, parallel with WP2): PermissionCatalog code, IUserAccessService/UserAccessService, AuditLog, UserAccessController, UserAccessContracts, UsersController D12/D2. AC: T7-T9 pass; every write audited + AccessVersion bumped.
  (Shared touchpoint: WP3 reads IBranchContext.PrimaryBranchId only via BranchRules - WP2 owns the file; WP3 starts against the WP1 build.)
WP4 test-engineer (after WP2+WP3): tests below, incl. updating constructors of AuthenticatedUserSummary/BranchContext in existing tests.
WP5 code-reviewer: arch §6/§7.6/§16, ADR-04/05/13, phase-1 §8 and phase-4 projection rules, D7 bypass completeness, migration checklist.

## 7. Tests
Unit (Sellora.Tests, InMemory): T1 ResolveWorkingBranch header > primary > default; restricted without primary fails; inactive primary 400. T2 filter: other-branch headers hidden, NULL-branch expenses/accounts visible, CompanyWide sees all. T7 access rules: self 403,
  restricted actor 403, missing permission 403, Administrator target by non-admin 403, admin cannot be AssignedBranches, duplicate active 400, end primary/last 400, primary change leaves closed predecessor rows, reassign atomic, revoke closes all, scope->CompanyWide closes
  all, AccessVersion bumps, one audit row per action with before/after, audit modify/delete throws. T8 create-user defaults (D12).
Integration (Sellora.IntegrationTests, PG): T3 children inherit (lines/payments of hidden invoice not returned) - gate for D6; T4 migration: seed admin + non-admin + suspended at previous migration, apply, V-block empty, rerun idempotent, Down guard raises after
  a manual assignment; partial uniques reject 2 primaries / duplicate active; composite FK rejects another company's branch. T5 HTTP restricted user - harness gap closed by a `RestrictedUserWorld` helper: admin creates the user through POST users (branchIds,
  primaryBranchId) and the new access API, then LoginAsync(username, password) (Invited -> Active on login, AuthService.cs:27); covers header-less write lands in primary; header of non-allocated branch 403 branch_access_denied; list hides other branch; ?branchId=other
  403; detail/payment/return of other-branch document 404; dashboard/report totals only allowed branches; branches/accessible and me show allocations; end assignment -> very next request with the same token loses access; suspend -> 401 company_context_invalid; all
  assignments removed via SQL -> 403 branch_required but GET auth/me 200; login with no usable membership 403 no_branch_access. T6 bypasses: EXP numbers unique across branches by restricted users; account name clash with hidden branch account -> 400 not 500;
  ProductUnit immutability sees other-branch usage; sale detail and batch list keep the purchase ref of another branch's batch (extend MixedBatchDetailApiTests); transactions list limited to visible accounts. T9 EndpointAuthorizationTests (new routes, policy
  codes); IsolationCases for new routes (foreign-company user/assignment -> 404); concurrent set-primary x2 -> one wins, V1 holds. Phase 3/4: branch warehouse/location of non-allocated branch hidden/404; D13 deactivation guard.

## 8. Risks / release notes
R1 Silent visibility change: after release, non-admin users see only Main documents; owner/accountant roles that are not the system Administrator become branch-only (O2). R2 Filter blast radius: every missed integrity read becomes a false negative (duplicate numbers,
  mutable ProductUnits); mitigated by D7 grep list, T6 and review. R3 ASSUMPTION navigation-filter inheritance (T3 first). R4 EF Contains over a nullable array parameter (verify translation; fallback: empty-array sentinel + flag). R5 Rollback of app alone locks mapped users
  out (old middleware fails AssignedBranches closed) -> restore snapshot. R6 Stock reports remain company-wide for restricted users until Phase 8 (documented). R7 Permission code appears via DbSeeder.cs:100 sync at startup (ASSUMPTION: seeder runs in every environment).

## 9. Owner questions
O1 Default for users created without access fields: AssignedBranches at the creator's working branch (recommended, least privilege, matches Q7) or CompanyWide (today's behaviour)?
O2 Q7 says company-wide = owner, admin, accountant, but the migration answer keeps only Administrator company-wide. Recommend: migrate as approved, review P1, then grant CompanyWide to accountant/owner via the new API right after release.
O3 Frontend: none needed (no header -> primary). Recommend deferring all screens to Phase 11; optionally map `branch_required` / `no_branch_access` to a readable message in the existing error handling (S-size) - confirm.

## 10. Deviations
- Migration maps existing non-admins to branch-only although arch §17.3 step 7 warns against assuming it: overridden by the explicit owner answer Q7 (documented default mapping, recorded in rows + audit).
- AccessVersion added now (ADR-04 stamp) although no cache exists: avoids a Phase 7 migration.
- AuditEvents limited to access changes; other §16 events join the same table in their phases.
- Users.Create alone may give the documented default allocation (D12) without ManageBranchAccess (arch §6.4 asks for permission): justified because the default never exceeds the creator's own working branch; explicit allocation still needs the permission.
- No conflict with unit-analysis D1-D10 (no stock/ledger change).

## Owner approval (2026-11-10)

Design approved, deviations accepted. O1 yes: new users default to AssignedBranches at the creator's working branch. O2 yes: migrate per Q7 (non-admins → Main primary, Administrators CompanyWide); the owner grants CompanyWide to accountants/owners through the new API after release. O3 yes: **no frontend changes in Phase 5** — all Angular work is carried to Phase 11, explicitly including: F1 branch context (allowed/primary branches from login/me, `X-Branch-Id` on every request, header branch switcher when >1 branch), F2 clear handling of `branch_required` / `no_branch_access` / `branch_access_denied` and revoked access, F3 sidebar showing real company/branch names, F4 user "Branch access" section (allocations + history, add/end with reason, set primary, scope change, suspend/revoke), F5 Vitest specs; lazy-loaded to respect the bundle budget. Until Phase 11, multi-branch users work in their primary branch and allocations are managed through the API.

## Delivery record (2026-11-10)

Status: **delivered; review "Approve with fixes" — fixes applied.** Migration `20261110054716_AddUserBranchAssignments` verified on PostgreSQL by the Testcontainers migration tests (Up on empty and Phase 4 head, V-block, guards, Q7 backfill content, Down guard, Down restore, Down→Up, idempotent script).

| Item | Outcome |
|---|---|
| Review Major 1–2 | Accounts reports (purchase/sales payments, cash-flow, payment methods) and the financial-transactions list (rows, totals, money in/out) are branch-scoped through one helper `FinancialTransactionVisibility.VisibleTo(db)`: a transaction is visible iff its source document (PO / invoice / expense) is visible, or — with no source — its account is visible or company-level. Number labels use id lookups (Phase 1 §8). |
| Review Major 3 | Expense retagging checks the paying account's branch with a documented Branch-filter bypass, so a hidden-branch account still blocks it. |
| Review minors | Idempotent replay of a document no longer visible → 400 on `IdempotencyKey` (was 500). Single DI constructor on `UsersController`. **Accepted as recorded decisions:** batch reference labels (PO number / ids) of hidden-branch batches remain visible on stock and sales detail, consistent with stock and movements being company-wide until Phase 8; audit append-only is enforced in the application (`SaveChanges`), not by a database trigger — revisit with the Phase 6 audit hardening. |
| Not specified by the design, pinned by tests | `AccessVersion` starts at 0; reason optional when adding an assignment, required for end/scope/status changes; `AssignedBranches` with no `branchIds` on user create → creator's working branch. |
| Release | Run `scripts/sql/seed-permissions.sql` (adds `Company.Users.ManageBranchAccess`). After deploy every non-admin user is branch-restricted to Main; grant CompanyWide to accountants/owners through `PUT api/v1/users/{id}/access/scope` (O2). Rollback = snapshot restore + previous build (app-only rollback would lock mapped users out). |
