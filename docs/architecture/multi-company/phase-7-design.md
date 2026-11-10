# Phase 7: Scoped RBAC (file-level design, DRAFT for owner approval)
Baseline `analyse` @ aa6b664 (Phases H, 1-6). Sources: arch §7.1-7.6, §14, §16, §17, §19 Ph7; plan ADR-04/10/13, C5; phase-1 D4; phase-5 D2/D8/D10;
phase-6 D5/D11/O1/K3. Size L, one migration, one release. Code anchors verified at HEAD; unverified items marked ASSUMPTION.
## 1. Scope
Delivers: company-owned Roles (ScopeType Company|Branch, BranchId), per-company protected Administrator (ADR-10), `UserRoleAssignments` (replaces
Users.RoleId for evaluation, C5), permission scope eligibility in the catalogue, server-side effective permissions per (membership, working branch)
with AccessVersion-keyed cache (ADR-04), delegation/escalation rules (§7.6), role + assignment APIs, RoleId backward compatibility, company
provisioning with its own Administrator, reference-data (Units/Currencies) leak closed, company creation unblocked (config, owner sign-off).
Excludes: Angular role/assignment screens (Phase 11 carry-over F7; SPA keeps working via compat), role templates for new companies, per-branch
stock (Phase 8: stock-related codes stay company-only), removal of Users.RoleId column (next release), governance changes beyond the role swap.
## 2. Current state (why it must change)
- Roles are global: Role.cs:3-15 (no CompanyId), unique IX_Roles_Name RoleConfiguration.cs:41; one global Users.RoleId (User.cs:13) shared by every
  company of a shared user; RolesController.cs:24 counts holders across companies; GovernanceService.cs:181 / PlatformService.cs:175 pick "the" system role.
- Authorization trusts token claims: PermissionAuthorizationHandler.cs:6-15 (singleton, Program.cs:68), claims minted at login (JwtTokenService.cs:28,
  AuthService.cs:194) -> revocation waits for token expiry; in-code checks SalesController.cs:193, DashboardController.cs:20-22 read claims too.
- Pipeline already suits server-side evaluation: UseAuthentication -> CompanyContextMiddleware -> UseAuthorization (Program.cs:170-172); middleware
  reads the membership per request (CompanyContextMiddleware.cs:141-160) but does not project membership Id / AccessVersion yet.
- Units/Currencies are platform rows (not ICompanyOwned) editable by any company holding Masters.Edit (UnitsController.cs:32-69,
  CurrenciesController.cs:29-78) -> cross-tenant write leak once a second company exists (phase-6 K3).
## 3. Decisions
D1 Roles become ICompanyOwned (Company filter + CompanyId stamping, SelloraDbContext.cs:107-134,171-184). New columns: CompanyId uuid NOT NULL (after
   backfill), ScopeType varchar(20) {Company, Branch} default 'Company', BranchId uuid NULL, CreatedAt, CreatedByUserId NULL, UpdatedAt NULL,
   UpdatedByUserId NULL (FKs Users). Constraints: CK_Roles_Scope ((ScopeType='Company' AND BranchId IS NULL) OR (ScopeType='Branch' AND BranchId IS NOT
   NULL)); CK_Roles_SystemIsCompany (NOT IsSystem OR ScopeType='Company'); FK (CompanyId, BranchId) -> Branches AK (BranchConfiguration.cs:13; MATCH
   SIMPLE, Restrict, navigation-less as phase-2 D3); AK (CompanyId, Id) and AK (CompanyId, Id, ScopeType) for assignment FKs; unique IX_Roles_CompanyId_System
   (CompanyId) WHERE IsSystem; name uniques replace IX_Roles_Name as raw-SQL expression indexes (precedent UnitConfiguration.cs:19):
   UX_Roles_Company_Name_Lower (CompanyId, lower(Name)) WHERE ScopeType='Company'; UX_Roles_Branch_Name_Lower (CompanyId, BranchId, lower(Name)) WHERE
   ScopeType='Branch'. Scope fields (CompanyId, ScopeType, BranchId) are immutable after insert (service rejects; §7.6 "change scope/tenant").
D2 Backfill of roles: "home" company = earliest Companies.CreatedAt, then Id (Phase 1 created only the default company; Dev/test DBs may hold more via
   PlatformService). Existing role rows keep their Id and get CompanyId = home (Users.RoleId FK stays valid). Every other company receives a copy of
   EVERY role (Id md5('role:'||companyId||':'||roleId)::uuid, same Name/Description/IsSystem/IsActive) plus copied RolePermissions. Copy-all, not
   copy-used: no guessing, no lost roles; at most a few extra rows in non-production DBs.
D3 Table UserRoleAssignments (ICompanyOwned): Id; CompanyId; UserCompanyMembershipId; RoleId; ScopeType; BranchId NULL; Status {Active, Ended};
   StartAt; EndAt NULL; AssignmentReason varchar(500) NULL; EndReason NULL; CreatedAt; CreatedByUserId NULL; EndedByUserId NULL. History rule as
   phase-5 D1: written once, closed once (no role/branch change on a row). FKs (explicit names, all <= 63 chars): FK_URA_Membership (CompanyId,
   UserCompanyMembershipId) -> memberships AK (CompanyConfigurations.cs:26); FK_URA_Role (CompanyId, RoleId, ScopeType) -> Roles AK; FK_URA_Branch
   (CompanyId, BranchId) -> Branches AK; FK_URA_RoleBranch (RoleId, BranchId) -> unique UX_Roles_Id_BranchId (raw SQL: EF cannot use a nullable
   alternate key; enforces assignment branch = role branch). CHECKs: Status list; (Status='Active') = (EndAt IS NULL); (ScopeType='Branch') = (BranchId
   IS NOT NULL); Id not empty. Indexes: IX_URA_Membership_Role_Active unique (UserCompanyMembershipId, RoleId) WHERE Active; IX_URA_Role_Active
   (CompanyId, RoleId) WHERE Active (holder bumps); IX_URA_Branch_Active (CompanyId, BranchId) WHERE Active.
D4 Users.RoleId: made nullable, no longer read for authorization or written for new users; FK kept one release; column removed in a later release (C5).
   Every u.Role / IsSystem read moves to assignments: AccessManagementActor.cs:46-52, AuthService.cs:16,52,93,194, AuthController.cs:104,
   UsersController.cs:40,47,144-233,256,282,307-329,394, UserAccessService.cs:244-366, GovernanceService.cs:179-233, PlatformService.cs:174-226,
   DbSeeder.cs:33,167-198. Login runs before the company context exists (phase-1 D4): role loading uses IgnoreQueryFilters(Company) with explicit
   CompanyId predicates. Lessons: filter roots before projecting; no optional->required->required navigation projections under filters (load
   assignment -> role -> codes with explicit joins).
D5 Permission scope eligibility (code catalogue, PermissionCatalog.cs; new PermissionDefinition.Scope {CompanyOnly, CompanyOrBranch}; no DB column).
   Branch-eligible (recommended, Q2): Sales.*, Purchase.*, Inventory.Adjust, StockLocations.View, Expenses.View/Create/Edit/Cancel, Reports.Sales,
   Reports.Purchase, and the new Branch.Roles.View, Branch.Roles.Manage, Branch.Users.AssignRoles. Everything else is CompanyOnly (masters, products,
   stock views/reports - stock is company-wide until Phase 8 -, cash/bank, users, roles, company admin). A branch role may only hold branch-eligible
   codes -> "branch roles cannot become company-wide" is structural. New company-only code Company.Users.AssignRoles (separates assignment from
   Users.Edit, §7.6); backfilled to every role holding Users.Edit (DbSeeder.cs:115-136 pattern, done in the migration).
D6 Effective permission semantics (§7.5), evaluated for the active membership in the current company:
   E1 Company-role codes (union over Active assignments of active company roles) apply to every branch the membership may access (CompanyWide: all;
      AssignedBranches: allocated branches - the Branch filter already bounds data, phase-5 D6).
   E2 Branch-role codes apply only at that role's branch, only while the role is active and the member holds an active allocation to it. Branch roles
      are assignable only to AssignedBranches members allocated to that branch (CompanyWide members use company roles). Ending an allocation, a scope
      switch to CompanyWide, or membership revocation closes the affected branch-role assignments in the same transaction (UserAccessService.cs:54,105,129).
   E3 Administrator assignment = every catalogue code (PermissionResolver semantics, AuthModels.cs:85-103) company-wide; still always CompanyWide (phase-5 D2).
   E4 Allocation alone grants nothing; governance (phase-6) grants no operational code and vice versa; inactive membership/role/branch grants nothing;
      multiple roles = union within their scope.
   E5 Request rule for permission P: Permitted(P) = allowed branches if a company role holds P, else allowed n {branches whose branch roles hold P}.
      Succeed iff Permitted(P) contains the working branch (BranchRules.cs:27, header ?? primary ?? default). Then BranchContext.Narrow(Permitted(P))
      (intersect across requirements) so lists/details of other branches 404/vanish for branch-only holders (no Branch-2 read via a Branch-1 role).
      Documented Branch-filter bypasses (phase-5 D7) are unaffected.
D7 Evaluation plumbing (ADR-04): middleware query adds m.Id + m.AccessVersion (same round-trip) into a new scoped IMembershipContext. New
   IEffectivePermissionService (Domain interface, Infrastructure impl) loads EffectivePermissionSet {IsAdministrator, CompanyCodes, BranchCodes by
   branch} with one query; IMemoryCache key ("perm", membershipId, accessVersion), absolute TTL 5 min, size-limited. Revocation is immediate because
   the version is read from the DB every request. PermissionAuthorizationHandler becomes scoped (Program.cs:68) and evaluates E5; claims ignored.
   SalesController.cs:193 and DashboardController.cs:20-22 use the service (dashboard sections use Has(P) at the working branch; ASSUMPTION: sections
   query only Branch-filtered documents - verify, else narrow per section). JWT stops emitting permission claims (JwtTokenService.cs:28; SPA reads
   user.permissions from the response, auth.service.ts:35); login/refresh/switch/me return effective codes for the working branch.
D8 AccessVersion bumps (same tx): own assignment add/end; role permission edit, role activate/deactivate -> bulk bump of all Active holders' memberships
   (ExecuteUpdate over IX_URA_Role_Active); allocation/scope/status/governance already bump (UserAccessService.cs:478, GovernanceService.cs:323).
D9 Administrator per company: one protected IsSystem Company role per company (D1 unique). Not editable, not deactivatable, not assignable to branch
   scope (CHECK); duplicate allowed (copy is non-system). Assign/end only by an actor holding the company's Administrator assignment (H1) or via the
   governance path (GovernanceService.cs:179-215, ManageAdministrators) which now writes assignments. Last-administrator rule: company keeps >= 1 Active
   CompanyWide membership with an Active Administrator assignment of a non-Disabled user. No self-assignment/ending (UsersController.cs:216 rule kept).
D10 Escalation prevention (§7.6), all writes in AccessManagementScope (AccessManagementActor.cs:36-54; advisory lock, actor re-read after lock):
   R1 Role create/edit/duplicate: codes must be a subset of the actor's effective codes at the role's scope (company: CompanyCodes; branch B: codes at B;
      Administrator: all; existing check RolesController.cs:208 generalised); branch roles only branch-eligible codes (D5).
   R2 Company roles: Roles.Create/Edit/Duplicate/Disable from a company role AND actor membership CompanyWide (Q1; mirrors phase-5 D8).
   R3 Branch roles of branch B: company path (R2 codes) or Branch.Roles.Manage effective at B. Branch actor sees/manages only roles with BranchId = B
      (others 404); cannot create company roles (codes are CompanyOnly).
   R4 Assign/end: company role needs Company.Users.AssignRoles (+ CompanyWide actor, Q1); branch role at B needs it or Branch.Users.AssignRoles at B.
      Role's codes must be a subset of the actor's codes at that scope; target membership Active/Suspended in this company (else 404); branch role
      requires target allocation to B (400 `branchId`); never the actor's own membership (403 `self_role_change_forbidden`); Administrator targets only
      by Administrators (H1); governance permissions are a separate table and never assignable here.
D11 Reference data (phase-6 K3): Units/Currencies writes require the platform session (new PlatformUnits/PlatformCurrencies endpoints reusing the
   existing service logic) when `Platform:ReferenceDataEditableByCompanies` is false; true (default this release) keeps today's company Masters.Edit
   writes for the single-company SPA. PlatformService refuses company creation while the flag is true (fail closed coupling). Reads stay shared.
D12 Company provisioning (PlatformService.cs:174-226): creates the company's Administrator role (IsSystem, new Guid) and the owner's Administrator
   assignment; no other roles (Q4). Existing-user owner no longer needs a global system role (:196 check removed). Platform:CompanyCreationEnabled
   default stays false in appsettings.json:17; production enables it by configuration after release verification + D11 flag false (owner sign-off).
## 4. Files per layer
Domain [WP1]: Entities/Role.cs (+CompanyId, ScopeType, BranchId, audit, ICompanyOwned), UserRoleAssignment.cs, RoleScope.cs (new), User.cs (RoleId Guid?),
  UserCompanyMembership.cs (+RoleAssignments), Permissions/PermissionCatalog.cs (+Scope, 4 codes), Abstractions/IEffectivePermissionService.cs +
  EffectivePermissionSet.cs + IMembershipContext.cs (new; signatures frozen in WP1 so WP2a/WP2b run in parallel).
Infrastructure [WP1]: Configurations/RoleConfiguration.cs, UserRoleAssignmentConfiguration.cs (new), RolePermissionConfiguration.cs (unchanged FKs),
  SelloraDbContext.cs (DbSet), DbSeeder.cs (admin role per default company + assignment), Migrations/<ts>_AddScopedRoles; scripts/sql/seed-permissions.sql.
Infrastructure [WP2a]: Services/EffectivePermissionService.cs (new), Data/BranchContext.cs (+Narrow), MembershipContext.cs (new), Security/JwtTokenService.cs,
  Services/AuthService.cs. Api [WP2a]: Authorization/PermissionAuthorizationHandler.cs, CompanyContextMiddleware.cs, AccessManagementActor.cs (actor =
  UserId, MembershipId, IsAdministrator, IsCompanyWide, EffectivePermissionSet), Program.cs (scoped handler, AddMemoryCache), AuthController.cs,
  SalesController.cs:193, DashboardController.cs:20-22, Contracts/AuthContracts.cs.
Infrastructure [WP2b]: Services/RoleService.cs (new; logic moved out of RolesController), RoleAssignmentService.cs (new), UserAccessService.cs (E2 closes),
  GovernanceService.cs, PlatformService.cs, Security/PlatformOptions.cs. Api [WP2b]: RolesController.cs, UsersController.cs, RoleAssignmentsController.cs
  (new), UnitsController.cs, CurrenciesController.cs, Platform reference-data controllers (new), Contracts/RoleContracts.cs, UserContracts.cs.
Frontend: none (compat). Additive fields ignored.
## 5. API (additive unless noted; ProblemDetails `code`)
- GET roles?scope=Company|Branch&branchId= (default Company - SPA user form keeps listing company roles only); DTO + scopeType, branchId, branchName,
  holders = Active assignments in this company (replaces RolesController.cs:24 global count). POST roles {..., scopeType?, branchId?}.
  PUT roles/{id}: scopeType/branchId present and different -> 400 `scopeType`. Duplicate keeps the source scope. System role edits -> 400 (unchanged).
- GET users/{id}/roles -> {current[], history[]}; POST users/{id}/roles {roleId, reason?} -> 201; POST users/{id}/roles/{assignmentId}/end {reason}.
- GET branches/{branchId}/members [Branch.Users.AssignRoles at branch or Users.View] -> members allocated there (for branch admins).
- Compat: PUT users/{id} RoleId = "set the single company role" (ends other company-role assignments, adds this one) - needs Company.Users.AssignRoles
  when changed; POST users RoleId -> initial company-role assignment. User DTOs keep roleId/roleName (first active company role by name) + roles[].
- Login/refresh/switch/me: user.permissions = effective at the working branch; + roles[] {id, name, scopeType, branchId}. JWT: no permission claims.
- Platform: POST/PUT platform/units, platform/currencies (Platform.Admin) when D11 flag is false.
## 6. Migration `<ts>_AddScopedRoles` (ts > 20261110080545; header layout as 20261110080545_AddPlatformAdminAndGovernance.cs:8-40; one tx)
guards: G1 no Roles row has CompanyId set (re-run/unexpected); G2 if any membership exists, at least one IsSystem active role exists; G3 every
  non-Revoked membership's user has a RoleId; G4 no Permissions row already uses the 4 new codes with a different Module (catalogue drift).
additive: Roles columns (D1) nullable; UserRoleAssignments (D3); Users.RoleId nullable; 4 Permissions rows (Id md5('perm:'||code)::uuid, ON CONFLICT (Code)
  DO NOTHING; PermissionConfiguration.cs:16 unique Code).
backfill: B1 Roles.CompanyId = home company (D2); B2 copies per other company + RolePermissions copies (Ids md5, ON CONFLICT DO NOTHING); B3 one Active
  company-role assignment per non-Revoked membership -> that company's copy of the user's RoleId (Id md5('ura:'||m.Id)::uuid, StartAt now(),
  AssignmentReason 'Migration from Users.RoleId (Phase 7, C5)', CreatedByUserId NULL); B4 Company.Users.AssignRoles granted to roles holding Users.Edit;
  B5 AccessVersion + 1 on every mapped membership; one AuditEvent 'role_assignment.migrated' per mapped membership (Id md5('aev7:'||m.Id)).
enforce: Roles.CompanyId NOT NULL; replace IX_Roles_Name with D1 uniques/AKs/CHECKs/FKs; D3 FKs/CHECKs/indexes; raw-SQL objects (lower() uniques,
  UX_Roles_Id_BranchId, FK_URA_RoleBranch) created via migrationBuilder.Sql and mirrored in Down.
P-block (prod copy, before release): P1 users holding Roles.* or Users.Edit through roles whose membership is AssignedBranches (lose role management, Q1);
  P2 companies and memberships per role (copy volume); P3 shared users (memberships > 1) and their RoleId.
V-block (RAISE on any row): V1 roles without CompanyId; V2 companies without exactly one active IsSystem role; V3 non-Revoked memberships without
  exactly one Active company-role assignment; V4 assignment whose role company <> assignment company; V5 assignment role name <> Users.RoleId role name;
  V6 role count <> source roles x companies; V7 a copied role whose RolePermissions count differs from its source; V8 companies with an Active membership
  but no Active CompanyWide membership holding an Active Administrator assignment; V9 roles holding Users.Edit without Company.Users.AssignRoles;
  V10 audit rows <> mapped memberships.
Down guard: RAISE if any Branch-scope role, any role or assignment with CreatedByUserId NOT NULL, any Ended assignment, any Users.RoleId NULL, or any
  RolePermission on a copied role differing from its source; else remove copies, assignments, new codes' grants, constraints and columns, restore
  IX_Roles_Name and Users.RoleId NOT NULL. Release note: small tables, instant; app-only rollback is unsafe once users are created with RoleId NULL ->
  rollback = restore snapshot + previous build (D10).
## 7. Work packages (max 2 parallel)
WP1 database-engineer: §4 Domain/Infrastructure [WP1] + migration + seed SQL. AC: build green; migration applies on empty DB and on a seeded multi-company
  Postgres DB (Testcontainers) with V-block silent; Down works pre-use and refuses post-use; seed drift test green; snapshot has no unexpected diff.
WP2a backend-engineer (authz core) || WP2b backend-engineer (management), both after WP1, disjoint files per §4. WP2a AC: claims-free JWT works end to
  end; handler scoped; E1-E5 implemented; cache keyed by version; existing tests green (update fixtures that relied on claims). WP2b AC: D9-D12 rules
  with documented codes; compat RoleId path; governance/provisioning on assignments; every write audited + AccessVersion bumped.
WP3 test-engineer (after both): T1 escalation matrix (codes not held, role with more codes, self-assign, Administrator by non-admin, governance codes
  unassignable); T2 branch isolation (B1 admin list/get/edit/assign of B2 roles -> 404/403); T3 company isolation (company A role id in company B ->
  404; FK rejects cross-company assignment on Postgres); T4 branch role cannot hold CompanyOnly codes, cannot change scope; T5 effective semantics
  (company role + AssignedBranches, branch role only at its branch, header switch, multiple roles, inactive role, suspended membership, allocation
  end closes branch roles); T6 narrowing (Sales.View via B1 role never shows B2 invoices; 404 on B2 detail); T7 immediate revocation (end assignment
  / edit role permissions -> next request 403, same token); T8 cache invalidation + no singleton capture (handler lifetime test, phase-6 K7);
  T9 governance/operational separation (Owner without Administrator holds no operational codes; Administrator without governance cannot govern);
  T10 last Administrator kept; T11 provisioning creates Administrator + assignment, creation refused while D11 flag true; T12 compat RoleId
  create/update; T13 migration on Postgres incl. multi-company copies and V-block; T14 login before company context (shared user, two companies,
  different roles per company). AC: unit + integration suites green.
WP4 code-reviewer: invariants above, §7.6 list, filters/bypasses, identifier lengths, Down guard.
## 8. Risks / release notes
K1 Behaviour change (Q1): AssignedBranches users lose company role management/assignment - review P1. K2 Per-request cost: cache hit = memory; miss =
  one query; version already read. K3 InMemory tests do not exercise filters, FKs or raw-SQL constraints - T3/T13 must run on Postgres. K4 Narrowing
  interacts with endpoints carrying two policies (intersection) - grep and list in PR. K5 SPA shows permissions of the working (primary) branch only
  until the Phase 11 picker. K6 Release: snapshot, apply idempotent script, run V-block, seed-permissions.sql, keep CompanyCreationEnabled=false
  until D11 flag is false and T11 verified in the target environment.
## 9. Owner questions (each with recommendation)
Q1 Company role management/assignment require a CompanyWide actor membership? Rec: yes (fail closed, mirrors phase-5 D8).
Q2 Accept the conservative branch-eligible code list (D5)? Rec: yes; widen stock/report codes in Phase 8.
Q3 Units/Currencies become platform-managed (D11), company creation coupled to the flag? Rec: yes; company-specific units are out of scope.
Q4 New companies get only the Administrator role? Rec: yes (Duplicate covers templates).
Q5 Defer role/assignment screens to Phase 11 (F7)? Rec: yes (compat keeps the SPA working).
## 10. Deviations
- Permission scope lives in the code catalogue, not a Permissions column (catalogue is code-owned, phase-1 docx §6; drift test guards SQL).
- JWT permission claims removed rather than kept as UI hints (ADR-04): the SPA already uses the response body; smaller tokens, no stale hint.
- Three DB objects (lower() uniques, UX_Roles_Id_BranchId, FK_URA_RoleBranch) are raw SQL outside the EF model (EF limits; precedent Units).
- Angular screens deferred (arch §19 Ph7 says "as appropriate").

## Owner approval (2026-11-10)

Design approved, deviations accepted. Q1 yes: managing/assigning company roles requires a CompanyWide actor. Q2 yes: conservative branch-eligible permission list, widen in Phase 8. Q3 yes: Units/Currencies writes are platform-managed (`Platform:ReferenceDataEditableByCompanies` = false); company creation is refused while that flag is true. Q4 yes: new companies get only the Administrator role. Q5 yes: role/assignment screens deferred to Phase 11 (carry-over F7).

## Delivery record (2026-11-10)

Status: **delivered; review "Approve with fixes" — fixes applied.** Migration `20261110093408_AddScopedRoles` verified on PostgreSQL by Testcontainers tests (Up on empty and Phase 6 head with several companies, P1–P4 and V1–V11 executed as shipped, guards G1–G6, role copies / permission copies / assignments / AssignRoles grant, Down guards, Down restore incl. audit trigger re-enabled, Down→Up, idempotent script).

| Item | Outcome |
|---|---|
| Bug found by tests | Removing the last company-wide Administrator through governance returned 500 (unmapped `RoleRuleException`); now 400 `last_administrator_protected`. `UserAccessController` maps it too. |
| Review Major | Platform Units/Currencies writes are audited (`platform.unit_*`, `platform.currency_*`, before/after), with the write logic moved into `UnitService` / new `CurrencyService`. |
| Review minors | Branch (de)activation bumps AccessVersion of members holding branch roles there (no stale cached permissions). `Company.Users.AssignRoles` backfilled to roles holding `Users.Create` or `Users.Edit` (otherwise create-only roles could no longer create users). Last-administrator guard also on user disable and membership suspend/revoke. Reference-data flag enforced in the services (company callers → 403 `reference_data_platform_managed`); request validation now runs before that 403. `appsettings.json` diff limited to the new flag. |
| Decisions recorded | Company-level stock locations (Transit, company warehouses, vehicles) stay visible to every member, including branch-only role holders (Phase 3 decision). Services skip the reference-data flag check only when constructed without options (unit tests); DI always supplies them. Permission claims removed from the JWT (deviation from ADR-04's "UI hint"); the SPA reads permissions from the login/me body. |
| Behaviour changes | Revoked permissions take effect on the next request (server-side evaluation; cache keyed by AccessVersion, 5-minute TTL). Creating users or changing their role needs `Company.Users.AssignRoles` and a company-wide actor (Q1). Company Units/Currencies create/edit/status return 403 until Phase 11 adds a platform screen (Q3). |
| Release | Run P1 (AssignedBranches users losing role management) on a production copy; migrate; run the V-block and `scripts/sql/seed-permissions.sql`. Company creation may be enabled by configuration after verification (`Platform:CompanyCreationEnabled=true`, `Platform:ReferenceDataEditableByCompanies=false`). Rollback = snapshot restore. |
