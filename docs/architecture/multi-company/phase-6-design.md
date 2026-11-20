# Phase 6: Platform separation and company governance (file-level design, DRAFT for owner approval)
Baseline `analyse` @ f63ffcc. Sources: arch §5.1-5.4, §7.6, §14, §16, §17, §19 Ph6; plan ADR-04/10/13, §10, Phase H, Ph11 carry-over;
phase-1 D1/D2/D4/O1/§8, phase-2 D1/D2, phase-3 R1/D5, phase-4 D5 (CompanyLocks), phase-5 D8/D10/D11 + delivery record. Size L, one migration, one release.

## 1. Scope
Delivers: platform administrators outside company RBAC (own table, own session kind, own audit table); platform API (company list/create/
suspend, platform-admin grant/revoke, platform audit); atomic company provisioning (Company, BusinessSettings, Main, its BranchStore, Transit,
Main Cash, owner membership + Owner governance member); company governance members with titles that grant nothing; separate governance
permission catalogue + per-member grants; delegated company administration (grant/remove Administrator role); sensitive-operation rules
(no self change, delegation ceiling, last root owner, protected accounts); DB-level append-only enforcement for both audit tables.
Excludes: Roles.CompanyId / scoped roles / per-company Administrator role (Phase 7, phase-1 D4 - Administrator semantics UNCHANGED: system role
still resolves every operational catalogue code, PermissionResolver AuthModels.cs:96-99, and receives NO governance code); approval/voting
workflow (arch §5.4); platform support access to customer data (none, arch §5.1); MFA; Angular (Phase 11, carry-over F6); RLS (arch §16).

## 2. Decisions
D1 Platform admin = table `PlatformAdministrators` (UserId PK+FK Users, Status {Active, Revoked}, GrantedAt, GrantedByUserId NULL, RevokedAt NULL,
   RevokedByUserId NULL, Reason varchar(500); CHECK `(Status='Active') = (RevokedAt IS NULL)`). Not a Users flag: keeps Users (platform table,
   edited by company admins via UsersController) free of a privilege bit, and gives grant/revoke metadata. Not ICompanyOwned, no filter.
D2 Platform session (separation, ADR-10): `POST auth/platform/login` (new PlatformAuthController, [AllowAnonymous]) verifies password, user not
   Disabled, active PlatformAdministrators row; issues an access token with claim `sellora_session=platform`, NO company_id, NO permission
   claims, lifetime `Platform:SessionMinutes` (default 30), no refresh token (O4). Normal login is unchanged: a platform admin without membership
   still gets 403 no_company_access (AuthService.cs:30-34); a platform admin who is also a member gets an ordinary company session with no
   platform rights. Platform tokens on company endpoints already fail (no company_id -> 401 company_context_invalid, middleware :49-68).
D3 Middleware (CompanyContextMiddleware.cs:38-43): endpoints carrying new `[PlatformEndpoint]` metadata skip company/branch resolution entirely
   (no company context -> Company filter fails closed, CompanyContext.cs:15-30: no business row reachable even by a coding mistake; later, deliberate exception: PlatformDataResetController runs the fresh-setup reset script
   as raw SQL over every company's business tables, only while Platform:DataResetEnabled is true (default false), for Platform.Admin, with one
   audit row naming the actor; it returns table names and row counts, never business rows) and require
   the platform session claim, else 401 `platform_session_required`. Policy `Platform.Admin` (PlatformAdminRequirement, scoped handler) re-reads
   DB per request: user not Disabled + Active PlatformAdministrators row (revocation immediate), else 403 `platform_admin_required`.
D4 Company provisioning `POST platform/companies` (CompanyProvisioningService), one tx under the `sellora:access-management` advisory lock
   (AccessManagementActor.cs:42) - sets the request's CompanyContext once to the new id (CompanyContext.Set, legal: platform requests have
   none) so ICompanyOwned stamping (SelloraDbContext.cs:161) works. Inserts: Company (Code rule `^[A-Z0-9][A-Z0-9-]{1,19}$`, uppercased,
   unique IX_Companies_Code CompanyConfigurations.cs:14, `DEFAULT` reserved, immutable after create; unique violation -> 400 `code`);
   BusinessSettings (BusinessName, InvoicePrefix, active BaseCurrencyId, optional GSTIN/address); Main branch (Code MAIN, IsDefault, new Guid;
   phase-2 D1 rules); its BranchStore location (Code MAIN, Name = branch name) and Transit (Code TRANSIT, 'In transit', active) - phase-3 R1/D5;
   "Main Cash" FinancialAccount (parity with DbSeeder.cs:140; ASSUMPTION: POS cash flow expects one); NO DocumentSequences rows (created lazily,
   DocumentNumbers.cs:87,93); owner membership (Active, CompanyWide); Owner governance member + all governance codes; audit rows. Owner =
   existing user id, or a new user (CreateUserRequest field rules, UserContracts.cs:16-24, status Invited). Owner must hold the active system
   Administrator role until Phase 7 (roles are global, phase-1 D4); owner != actor (no self-grant); owner must not be an active platform admin
   (separation). Any failure rolls everything back.
D5 Creation gate: `Platform:CompanyCreationEnabled` (default false; true in Development + tests) -> 403 `company_creation_disabled`. Reason:
   phase-1 O1 accepted platform surfaces "while company creation is disabled" - Roles (global RoleId, Roles.Edit in company B changes company
   A users) and Units/Currencies writes leak across tenants until Phase 7. Code + tests ship now; production enable after Phase 7 (O1).
D6 Suspension `POST platform/companies/{id}/status {isActive, reason}`: Companies.IsActive only. Effect already wired: next request 401
   (middleware :132), login/refresh skip it (AuthService.cs:164). No data change; reactivation symmetric. Reason required.
D7 Governance tables (ICompanyOwned, "Company" filter): `CompanyGovernanceMembers` Id; CompanyId; UserCompanyMembershipId (composite FK
   (CompanyId, UserCompanyMembershipId) -> UserCompanyMemberships AK, phase-5 D1 pattern; no UserId copy); Title varchar(30) {Owner, BoardMember,
   Director, DelegatedAdministrator}; Status {Active, Ended}; StartAt; EndAt NULL; EndReason NULL; CreatedAt; CreatedByUserId NULL; EndedByUserId
   NULL; UpdatedAt/UpdatedByUserId NULL. AK (CompanyId, Id); partial unique `IX_GovMembers_Membership_Active` (UserCompanyMembershipId) WHERE
   Status='Active'; CHECKs title list, status list, `(Status='Active') = (EndAt IS NULL)`, Id not empty. Title edit in place (audited);
   ending closes the row, re-adding = new row.
   `GovernancePermissions` (platform catalogue: Id, Code varchar(100) unique, Description) - a SEPARATE table, so RolePermissions (FK to
   Permissions) can never reference a governance code (ADR-13). `GovernanceMemberPermissions` Id; CompanyId; GovernanceMemberId (composite FK
   -> members AK); GovernancePermissionId FK Restrict; GrantedAt; GrantedByUserId NULL; unique (GovernanceMemberId, GovernancePermissionId).
   Revocation removes the grant row (history = audit before/after). Ending a member removes its grants in the same tx.
   Belt and braces: CHECK `CK_Permissions_NotGovernance` ("Code" NOT LIKE 'Company.Governance.%') on Permissions.
   All constraint names explicit and <= 63 chars (EF defaults for these composite FKs exceed 63).
D8 Governance catalogue (Domain `Permissions/GovernancePermissionCatalog.cs`, NOT in PermissionCatalog, so the system role never gets it):
   Company.Governance.View; .ManageMembers (non-Owner members/titles); .ManageOwners (anything touching an Owner-title member);
   .AssignPermissions (grant/revoke governance codes); .ManageAdministrators (delegated company administration, D11).
   §5.3 operational candidates map to EXISTING codes (ADR-13, no near-duplicates): Company.Users.View/Manage/Suspend -> Users.View/Edit/Disable;
   ManageBranchAccess exists; Company.Branches/Warehouses/Vehicles.* exist; Company.Roles.* / Permissions.Assign -> Roles.*. Not seeded:
   ApproveSensitiveActions (no approval workflow), Security.Manage / Settings.Manage (no security-critical settings exist) - add with their feature.
D9 Evaluation (§7.6 "separate authorization checks"): never in JWT permission claims, never via PermissionResolver. Policy name prefix
   `Governance:<code>` recognised by PermissionPolicyProvider (:14,:25) -> GovernanceRequirement; scoped handler queries: Active member in the
   current company, membership Active (middleware already ensures), grant exists. No cache (Phase 7 cache keys on AccessVersion; every
   governance write bumps the subject membership's AccessVersion). Writes re-check inside the lock. `auth/me` gains a `governance` hint block.
D10 Sensitive-operation rules (service, inside AccessManagementScope; reason required on every write):
   R1 no self: actor cannot add/end/retitle own member row or change own grants (403 `self_governance_change_forbidden`).
   R2 delegation ceiling: grant AND revoke only codes the actor holds (403 `governance_delegation_exceeded`); needs AssignPermissions.
   R3 Owner title (add, end, retitle to/from, change grants of an Owner) needs ManageOwners; other members need ManageMembers.
   R4 last root owner: after any write the company must keep >= 1 root owner = Active Owner member, membership Active, user not Disabled, holding
      View+ManageOwners+AssignPermissions -> else 400 `last_owner_protected`. Also checked by user disable, membership suspend/revoke.
   R5 protected accounts: operational APIs (UsersController.cs:200,254,272 edit/status/role, :264-280 reset-password returns a temp password =
      takeover; UserAccessService SetStatus) refuse targets that are active governance members unless the actor holds ManageMembers (Owner
      target: ManageOwners) -> 403 `governance_member_protected`; targets that are active platform admins are never editable from a company
      session -> 403 `platform_account_protected` (ops/platform only). Membership Revoked ends the governance member (audited, same tx).
   R6 platform admin: no self-revoke; last active platform admin cannot be revoked (400).
D11 Delegated administration: `POST governance/administrators {userId, reason}` sets RoleId = Administrator + CompanyWide (UserAccessService
   EnsureCompanyWideForAdministratorAsync, UsersController.cs:235); `.../{userId}/remove {replacementRoleId, reason}` (non-system, active). Needs
   ManageAdministrators; R1, R5; target member only of this company (phase-1 review Major 1); company keeps >= 1 active CompanyWide Administrator.
   Extends H1 (only Administrators touch Administrator accounts) with exactly one governed path. Phase 7 swaps RoleId for a role assignment.
D12 Audit: governance -> AuditEvents (company-owned, AuditLog.cs). Platform -> new `PlatformAuditEvents` (Id, OccurredAt, ActorUserId, Action,
   TargetType, TargetId, CompanyId NULL FK, Reason, Before/After jsonb, CorrelationId; indexes (OccurredAt), (CompanyId, OccurredAt)): keeps
   AuditEvents.CompanyId NOT NULL + filter intact (no interface change). governance/events also returns platform rows with CompanyId = current.
   Actions: governance.member_added/_ended/_title_changed/_permissions_changed/_owner_mapped, governance.administrator_granted/_removed;
   platform.login, platform.company_created, platform.company_status_changed, platform.admin_granted/_revoked.
   DB enforcement now (closes phase-5 accepted minor): function `sellora_audit_append_only()` + row triggers `TR_AuditEvents_AppendOnly`,
   `TR_PlatformAuditEvents_AppendOnly` (before update or row removal -> RAISE) + statement triggers rejecting bulk-clear. SaveChanges guard
   (SelloraDbContext.cs:148) extended to PlatformAuditEvent. No test fixture rewrites audit rows (grep tests: none) - ASSUMPTION re-checked in WP4.

## 3. Files per layer
Domain [WP1]: Entities/PlatformAdministrator.cs, PlatformAuditEvent.cs, CompanyGovernanceMember.cs, GovernancePermission.cs, GovernanceMemberPermission.cs, enums GovernanceTitle/GovernanceMemberStatus/PlatformAdminStatus; AuditEvent.cs (+actions/targets);
  Permissions/GovernancePermissionCatalog.cs. [WP2] Auth/AuthClaimTypes.cs (+SessionKind), Abstractions/ITokenService (+CreatePlatformAccessToken), IPlatformService + inputs. [WP3] Abstractions/IGovernanceService + inputs; IUserAccessService (protection hooks).
Infrastructure [WP1]: Configurations/GovernanceConfigurations.cs, PlatformConfigurations.cs (new), PermissionConfiguration.cs (CHECK); SelloraDbContext.cs (DbSets, append-only guard); Migrations/<ts>_AddPlatformAdminAndGovernance (+Designer, snapshot); DbSeeder.cs (catalogue sync,
  Owner member for seeded admin, dev-only platform grant to seeded admin). [WP2] Services/AuthService.cs (PlatformLoginAsync), TokenService, CompanyProvisioningService.cs, PlatformAdminService.cs, PlatformAuditLog.cs (new). [WP3] GovernanceService.cs, GovernanceRules.cs (R1-R4 query
  helpers), UserAccessService.cs (R4/R5, revoke ends member), AuditLog.cs (unchanged API).
Api [WP2]: Authorization/PlatformEndpointAttribute.cs, PlatformAdminRequirement+Handler, GovernanceRequirement+Handler (scoped; Program.cs:68 registers
  handlers singleton today), PermissionPolicyProvider.cs, CompanyContextMiddleware.cs (D3), Program.cs (DI, options); Controllers/PlatformAuthController.cs,
  PlatformCompaniesController.cs, PlatformAdministratorsController.cs; Contracts/PlatformContracts.cs. [WP3] AccessManagementActor.cs (+governance codes loaded after the lock), Controllers/GovernanceController.cs, UsersController.cs (R5), AuthController.cs + AuthContracts.cs (me governance block),
  Contracts/GovernanceContracts.cs.
Scripts [WP1]: scripts/sql/seed-permissions.sql (+ governance catalogue, ON CONFLICT DO NOTHING; no update/remove statements, test :55); scripts/sql/grant-platform-admin.sql (psql -v username=..., inserts the row + platform.admin_granted audit row, idempotent).
Frontend: none (Ph11 carry-over F6: platform console, governance screens, `governance` in me, new error codes).

## 4. Migration `<ts>_AddPlatformAdminAndGovernance` (ts > 20261110054716; header layout as 20261110054716; one tx)
additive: 5 tables (D1, D7, D12); GovernancePermissions rows for the 5 codes (Id md5('govperm:'||code)::uuid).
guards: G1 RAISE if any Permissions code LIKE 'Company.Governance.%' (before CHECK). G2 RAISE if a company has no root-owner candidate =
  Active, CompanyWide membership of a non-Disabled user whose role IsSystem AND IsActive (phase-5 G3/V6 should make this impossible).
backfill (ADR-10, owner decision 2026-11-09): per company the candidate with the earliest Users.CreatedAt, then Id -> one member (Id
  md5('gov:'||membershipId)::uuid, Title Owner, StartAt now(), CreatedByUserId NULL) + 5 grants (md5('govgrant:'||member||code)) + one AuditEvent
  governance.owner_mapped (reason 'Migration default owner (ADR-10)'); ON CONFLICT DO NOTHING. Other Administrators: no governance (O2).
  Platform admins: NONE (explicit grant only, O3). Then CHECK CK_Permissions_NotGovernance; then function + 4 triggers (last).
P-block (prod copy, before release): P1 Active Administrator members per company ordered by CreatedAt (owner confirms who becomes owner);
  P2 users who will become protected (R5) - informs support staff.
V-block (no rows): V1 companies without exactly one migrated Owner member; V2 companies without a root owner (D10 R4); V3 governance members whose
  membership is not Active/CompanyWide; V4 a row when GovernancePermissions count <> 5; V5 Permissions with governance codes; V6 grants per migrated
  member <> 5; V7 owner_mapped audit rows <> migrated members; V8 PlatformAdministrators not empty; V9 a row when pg_trigger lacks either trigger.
Down guard: RAISE if any member/grant has CreatedByUserId/GrantedByUserId NOT NULL, any PlatformAdministrators or PlatformAuditEvents row, any
  Company created after the migration, or any governance.* AuditEvent other than owner_mapped. Else: triggers + function first, then the
  owner_mapped audit rows, CHECK, then the 5 tables. Release: new tables + small inserts, instant; old build ignores them (app-only rollback
  safe; old code never rewrites audit rows) -> rollback = previous build, or snapshot restore (D10).

## 5. API / DTO contract (additive; codes in ProblemDetails `code`)
Platform (platform session + Platform.Admin): POST auth/platform/login {usernameOrEmail, password} -> {accessToken, expiresAt, user{id, name}};
  GET platform/companies -> [{id, code, name, isActive, createdAt, memberCount, ownerCount}] (no business data); POST platform/companies {code, name,
  businessName, invoicePrefix, baseCurrencyId, gstin?, addressLine?, city?, state?, phone?, email?, owner{userId? | newUser{firstName, lastName,
  username, email, phoneNumber?, password, confirmPassword}}, reason?} -> 201 {id, code, mainBranchId, ownerUserId}; POST platform/companies/{id}/status
  {isActive, reason}; GET platform/administrators; POST platform/administrators {usernameOrEmail, reason}; POST platform/administrators/{userId}/revoke
  {reason}; GET platform/audit-events?companyId=&page=. GET platform/currencies needed for baseCurrencyId (ASSUMPTION; else accept currency code).
Company (company session): GET governance/permissions [Gov.View]; GET governance/members?includeEnded= [Gov.View] -> [{id, userId, displayName,
  title, status, startAt, endAt, permissions[]}]; POST governance/members {userId, title, permissionCodes[], reason}; POST governance/members/{id}/title
  {title, reason}; PUT governance/members/{id}/permissions {permissionCodes[], reason}; POST governance/members/{id}/end {reason};
  POST governance/administrators {userId, reason}; POST governance/administrators/{userId}/remove {replacementRoleId, reason};
  GET governance/events?page= [Gov.View]. auth/me: + governance {memberId, title, permissions[]} | null.
Errors: 401 platform_session_required; 403 platform_admin_required, company_creation_disabled, self_governance_change_forbidden,
  governance_delegation_exceeded, governance_member_protected, platform_account_protected; 400 last_owner_protected + field errors (code,
  invoicePrefix, baseCurrencyId, owner, title, permissionCodes, reason, replacementRoleId); 404 member/user of another company (identical bodies).
Seed: governance codes in GovernancePermissions only (migration + seeder + seed-permissions.sql); Permissions catalogue unchanged.

## 6. Work packages (max 2 parallel; no file overlap)
WP1 database-engineer: §3 [WP1] files, migration P/V/guards/Down, scripts. AC: build green; MigrationTests no pending changes; Up on empty DB and Phase 5 head; V-block empty; Down guard raises after a manual grant; identifiers <= 63.
WP2 backend-engineer A (after WP1): platform session, middleware, both requirements/handlers + policy provider, provisioning, platform admin, platform audit, platform controllers/contracts. AC: T1-T4 pass; provisioning reuses no BranchService path that needs a prior company lock.
WP3 backend-engineer B (after WP1, parallel with WP2): governance service/rules/controller/contracts, AccessManagementActor, UsersController + UserAccessService protections, auth/me block. Uses policy names `Governance:<code>` (string contract; WP2 owns the provider - stub-free because
  WP3 unit tests call services directly). AC: T5-T8 pass; every write audited + AccessVersion bumped.
WP4 test-engineer (after WP2+WP3) - tests below; WP5 code-reviewer: arch §5/§7.6/§16, ADR-10/13, phase-1 §8 + projection lessons, R1-R6 coverage.

## 7. Tests
Unit (InMemory): T5 rules R1-R4 matrix (self add/end/retitle/grant 403; grant/revoke code not held 403; Owner ops without ManageOwners 403;
  last root owner via end, retitle, grant removal, user disable, membership suspend/revoke -> 400); titles alone grant nothing (Owner with zero
  grants gets 403 on View); T6 catalogue disjointness (GovernancePermissionCatalog ∩ PermissionCatalog = ∅; system role resolves no governance
  code); T7 R5 protected accounts (admin disables/resets/demotes owner -> 403; with ManageOwners -> ok; platform admin target -> 403);
  T8 delegated administration (grant/remove, self 403, multi-company target 403, last CompanyWide Administrator kept); seed script drift test extended.
Integration (PG/Testcontainers): T1 platform admin WITHOUT membership: normal login 403 no_company_access; platform token on GET products/
  sales/branches/auth/me -> 401; company token on platform routes -> 401 platform_session_required; revoked platform admin's live token -> 403 next
  request; platform list shows no business data. T2 provisioning atomic: creates Company, settings, Main (IsDefault), store, Transit, Main Cash,
  CompanyWide owner membership, Owner + 5 grants, audit rows; forced failure (duplicate owner username) leaves zero rows; concurrent same code
  -> one 201 + one 400; owner logs in / switch-company works; first expense in new company is EXP-0001 while default keeps its sequence;
  isolation both ways; flag off -> 403. T3 suspension: member's next request 401, login 403, reactivate restores. T4 migration: Up on Phase 5
  head with 2 admins -> earliest is Owner, V-block empty, rerun idempotent, G1/G2 raise and roll back, Down guard raises after a grant, Down -> Up,
  idempotent script. T9 constraints: RolePermissions cannot reference a governance code (FK + CHECK); two active members per membership rejected;
  member of company B on company A's membership rejected (composite FK); audit trigger rejects update, row removal and bulk-clear on both tables.
  T10 EndpointAuthorizationTests + IsolationCases for every new route (platform routes in the platform allow-list); concurrent "end owner" x2
  on a 2-owner company -> one succeeds, R4 holds.

## 8. Risks / release notes
K1 Behaviour change: Administrators can no longer disable/reset/demote governance members or platform admins (R5) - communicate. K2 Lost root owner (only via ops SQL) has no in-app recovery by design; runbook: ops script under change control + platform audit row. K3 Enabling creation
  before Phase 7 leaks roles/units/currencies (D5). K4 Platform session has no MFA/refresh (O4). K5 Triggers make audit repair impossible without a migration - intended. K6 Release: apply script, run seed-permissions.sql, run grant-platform-admin.sql for the chosen operator,
  keep CompanyCreationEnabled=false. K7 Scoped auth handlers (DbContext) - verify no singleton capture (handler lifetime test).

## 9. Owner questions
O1 Keep company creation disabled in production until Phase 7? Rec: yes (D5).
O2 Migration Owner = earliest active Administrator of the default company, all 5 codes; others get nothing until the owner grants? Rec: yes, confirm with P1.
O3 Platform admins granted only by ops script (none by migration; dedicated operator account without membership)? Rec: yes.
O4 Platform session: 30-min access token, no refresh, re-login; MFA later? Rec: yes.

## 10. Deviations
- §5.3 list trimmed to 5 governance codes; operational candidates mapped to existing codes (ADR-13); ApproveSensitiveActions/Security/Settings
  deferred; new code Company.Governance.ManageAdministrators implements "delegated company administration".
- Platform audit in its own table (AuditEvents stays company-owned, NOT NULL CompanyId) instead of relaxing AuditEvents.CompanyId (phase-5 D11 note).
- ADR-10 "Administrator becomes a per-company protected system role" stays deferred to Phase 7 (phase-1 D4); Administrator semantics unchanged.
- Company creation built but feature-flagged off; DocumentSequences not pre-created (lazy by design). No conflict with unit-analysis D1-D10.

## Owner approval (2026-11-10)

Design approved, deviations accepted. O1 yes: company creation built but disabled (`Platform:CompanyCreationEnabled` = false) until Phase 7. O2 yes: per company, the earliest active Administrator becomes the only migrated Owner (holding all governance codes); the owner reviews the P1 query result before release. O3 yes: platform administrators are granted only by `scripts/sql/grant-platform-admin.sql`, never through the application. O4 yes: platform sessions are 30-minute access tokens with no refresh. No frontend in Phase 6 (Phase 11 carry-over F6: platform console and governance screens).

## Delivery record (2026-11-10)

Status: **delivered; review "Approve with fixes" — fixes applied.** Migration `20261110080545_AddPlatformAdminAndGovernance` and `scripts/sql/grant-platform-admin.sql` verified on PostgreSQL by Testcontainers tests (Up on empty and Phase 5 head, O2 owner selection, V-block, guards, CHECK, append-only triggers for UPDATE/DELETE/TRUNCATE, Down guard, Down restore, Down→Up, idempotent script; grant script run through real `psql -v` in the container).

| Item | Outcome |
|---|---|
| Review Major | Ending a governance member is treated as revoking all its grants: the actor must hold every code the member holds (403 `governance_delegation_exceeded`). Ending an Owner therefore needs all five codes. Ending through membership revocation (UserAccessService) remains a documented system exception, gated by membership permissions and R5. |
| Review minors | Grant script refuses an active governance member; platform session clamped to ≤30 minutes regardless of configuration (O4); company provisioning rejects an owner who already belongs to another company (400 `owner`); failed platform logins are audited (`platform.login_failed`, reason category, never the password); a multi-company target in administrator grant/remove → 403 `shared_account_protected` (was 400). |
| Decisions recorded | An Owner may lose the operational Administrator role (governance is independent of operational roles; still gated by ManageOwners and the last-administrator rule). Platform-administrator endpoints are not built (O3: SQL script only). A read endpoint for PlatformAuditEvents is deferred (read via SQL). R4 last-owner check runs after SaveChanges and relies on the transaction rollback (verified on PostgreSQL). |
| Risks | No rate limiting on platform login yet; no in-app recovery if the last owner is lost (operations script needed); platform sessions have no MFA. |
| Release | Run `scripts/sql/seed-permissions.sql`; review the P1 query (owner per company) before migrating; grant operator platform admins with `scripts/sql/grant-platform-admin.sql`; keep `Platform:CompanyCreationEnabled=false` until Phase 7. Frontend (Phase 11 F6) should map `shared_account_protected` and the other new codes. |
