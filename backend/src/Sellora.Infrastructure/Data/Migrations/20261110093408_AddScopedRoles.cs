using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 7 - Scoped RBAC (design D1-D5, D9; owner approval 2026-11-10, Q1-Q5): one migration, one transaction.
     *   guard    : G1 Roles.CompanyId does not exist yet (re-run / unexpected state). G2 when a membership exists, an active system role exists. G3 every
     *              non-Revoked membership's user has a RoleId. G4 the 4 new permission codes, if present, already sit in module Administration. G5 (added
     *              by the DB engineer) no two roles differ only by lower(Name) (the new case-insensitive unique would fail) and at most one system role exists
     *              (IX_Roles_CompanyId_System); G6 when roles exist a company exists (the home company).
     *   additive : Roles columns (CompanyId nullable until backfilled, ScopeType default 'Company', BranchId, audit columns), Users.RoleId nullable,
     *              IX_Roles_Name dropped (names become unique per company), the 4 Permissions rows (Id md5('perm:'||code)::uuid, ON CONFLICT (Code) DO NOTHING).
     *   backfill : B1 every role keeps its Id and gets CompanyId = home company (earliest Companies.CreatedAt, then Id). B2 every other company receives a copy of
     *              EVERY role (Id md5('role:'||companyId||':'||roleId)::uuid) plus copied RolePermissions. B4 Company.Users.AssignRoles granted to every role
     *              holding Users.Create OR Users.Edit (originals and copies). Roles.CompanyId is then asserted complete (V1) and made NOT NULL.
     *   enforce  : Roles: NOT NULL, AK (CompanyId, Id) and (CompanyId, Id, ScopeType), CK_Roles_Scope / CK_Roles_SystemIsCompany / CK_Roles_Id_NotEmpty, FKs
     *              (Companies, Branches composite MATCH SIMPLE, Users), IX_Roles_CompanyId_System, raw SQL: UX_Roles_Company_Name_Lower, UX_Roles_Branch_Name_Lower
     *              (lower(Name) partial uniques, precedent Units) and constraint UX_Roles_Id_BranchId (UNIQUE (Id, BranchId), target of FK_URA_RoleBranch).
     *              UserRoleAssignments is created AFTER the role backfill (DB engineer: created empty with every constraint, then filled, so the backfill is
     *              validated by the constraints): FK_URA_Membership, FK_URA_Role (CompanyId, RoleId, ScopeType), FK_URA_Branch, raw FK_URA_RoleBranch
     *              (RoleId, BranchId) -> UX_Roles_Id_BranchId, CHECKs, partial indexes. All identifiers <= 63 characters.
     *   backfill : B3 one Active company-role assignment per non-Revoked membership -> that company's copy of the user's RoleId (Id md5('ura:'||membershipId)::uuid,
     *              StartAt now(), reason 'Migration from Users.RoleId (Phase 7, C5)', CreatedByUserId NULL). B5 AccessVersion + 1 on every mapped membership and one
     *              AuditEvent 'role_assignment.migrated' per mapped membership (Id md5('aev7:'||membershipId)::uuid). Written before any later trigger work.
     *   assert   : V1-V10 below run in the same transaction and RAISE on any violation (V3, V5-V7, V10 Up only: later services legitimately change them).
     *
     * RELEASE NOTE: small tables, instant (Roles rewrite-free; ADD COLUMN with constant defaults). New role copies = (companies - 1) x roles (a few rows outside
     * production). Users.RoleId stays (nullable, FK kept) for one release; the new application ignores it for evaluation. Old application builds break once any
     * user is created with RoleId NULL or a second company role exists: rollback = restore the pre-deploy snapshot + previous build (D10), not Down. AccessVersion
     * is never decremented by Down. Behaviour change (Q1): AssignedBranches members lose company role management / assignment - review P1 before release.
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P1 users who will lose role management (Roles.* / Users.Edit through their role while the membership is AssignedBranches; Q1)
     * SELECT c."Code" AS company, u."Username", u."Email", m."AccessScope", r."Name" AS role,
     *        r."IsSystem" AS is_system, (SELECT string_agg(p."Code", ', ' ORDER BY p."Code") FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId"
     *                                    WHERE rp."RoleId" = r."Id" AND (p."Code" LIKE 'Roles.%' OR p."Code" = 'Users.Edit')) AS affected_codes
     * FROM "UserCompanyMemberships" m JOIN "Companies" c ON c."Id" = m."CompanyId" JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
     * WHERE m."Status" <> 'Revoked' AND m."AccessScope" = 'AssignedBranches'
     *   AND (r."IsSystem" OR EXISTS (SELECT 1 FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId"
     *                                WHERE rp."RoleId" = r."Id" AND (p."Code" LIKE 'Roles.%' OR p."Code" = 'Users.Edit')))
     * ORDER BY c."Code", u."Username";
     * -- P2 companies, memberships and roles (copy volume = (companies - 1) x roles)
     * SELECT (SELECT COUNT(*) FROM "Companies") AS companies, (SELECT COUNT(*) FROM "Roles") AS roles, (SELECT COUNT(*) FROM "RolePermissions") AS role_permissions,
     *        (SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "Status" <> 'Revoked') AS non_revoked_memberships;
     * -- P3 shared users (memberships > 1) and their single RoleId
     * SELECT u."Username", u."RoleId", r."Name" AS role, COUNT(*) AS memberships FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId"
     *   JOIN "Roles" r ON r."Id" = u."RoleId" WHERE m."Status" <> 'Revoked' GROUP BY u."Username", u."RoleId", r."Name" HAVING COUNT(*) > 1;
     * -- P4 guard probes (expected: no rows): roles equal ignoring case, more than one system role, users without a role
     * SELECT lower("Name"), COUNT(*) FROM "Roles" GROUP BY lower("Name") HAVING COUNT(*) > 1;
     * SELECT "Id", "Name" FROM "Roles" WHERE "IsSystem" AND (SELECT COUNT(*) FROM "Roles" WHERE "IsSystem") > 1;
     * SELECT m."Id" FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" WHERE m."Status" <> 'Revoked' AND u."RoleId" IS NULL;
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; every query must return no rows ("SELECT 1 WHERE ..." forms return one row on violation).
     *
     * -- V1 roles without a company
     * SELECT "Id" FROM "Roles" WHERE "CompanyId" IS NULL;
     * -- V2 companies without exactly one active system role (skipped on a database without roles)
     * SELECT c."Id" FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "Roles")
     *   AND (SELECT COUNT(*) FROM "Roles" r WHERE r."CompanyId" = c."Id" AND r."IsSystem" AND r."IsActive") <> 1;
     * -- V3 non-Revoked memberships without exactly one Active company-role assignment (Up only: later users may hold several roles)
     * SELECT m."Id" FROM "UserCompanyMemberships" m WHERE m."Status" <> 'Revoked'
     *   AND (SELECT COUNT(*) FROM "UserRoleAssignments" a WHERE a."UserCompanyMembershipId" = m."Id" AND a."Status" = 'Active' AND a."ScopeType" = 'Company') <> 1;
     * -- V4 assignments whose role company differs from the assignment company (also FK-enforced)
     * SELECT a."Id" FROM "UserRoleAssignments" a JOIN "Roles" r ON r."Id" = a."RoleId" WHERE r."CompanyId" <> a."CompanyId";
     * -- V5 migrated assignments whose role name differs from the Users.RoleId role name (Up only)
     * SELECT a."Id" FROM "UserRoleAssignments" a JOIN "UserCompanyMemberships" m ON m."Id" = a."UserCompanyMembershipId" JOIN "Users" u ON u."Id" = m."UserId"
     *   JOIN "Roles" r ON r."Id" = a."RoleId" JOIN "Roles" ur ON ur."Id" = u."RoleId" WHERE a."CreatedByUserId" IS NULL AND r."Name" <> ur."Name";
     * -- V6 role count <> source roles x companies (Up only)
     * SELECT 1 WHERE (SELECT COUNT(*) FROM "Roles") <>
     *   (SELECT COUNT(*) FROM "Roles" r WHERE r."CompanyId" = (SELECT "Id" FROM "Companies" ORDER BY "CreatedAt", "Id" LIMIT 1)) * (SELECT COUNT(*) FROM "Companies");
     * -- V7 copied roles whose RolePermissions count differs from the source role (Up only)
     * SELECT c."Id" FROM "Roles" c JOIN "Roles" s ON s."CompanyId" <> c."CompanyId" AND md5('role:' || c."CompanyId"::text || ':' || s."Id"::text)::uuid = c."Id"
     *   WHERE (SELECT COUNT(*) FROM "RolePermissions" WHERE "RoleId" = c."Id") <> (SELECT COUNT(*) FROM "RolePermissions" WHERE "RoleId" = s."Id");
     * -- V8 companies with an Active membership but no Active CompanyWide membership of a non-Disabled user holding an Active Administrator assignment
     * SELECT c."Id" FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
     *   AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "UserRoleAssignments" a ON a."UserCompanyMembershipId" = m."Id"
     *     JOIN "Roles" r ON r."Id" = a."RoleId" WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled'
     *       AND a."Status" = 'Active' AND r."IsSystem" AND r."IsActive");
     * -- V9 roles holding Users.Create or Users.Edit without Company.Users.AssignRoles
     * SELECT r."Id" FROM "Roles" r WHERE EXISTS (SELECT 1 FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = r."Id" AND p."Code" IN ('Users.Create', 'Users.Edit'))
     *   AND NOT EXISTS (SELECT 1 FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = r."Id" AND p."Code" = 'Company.Users.AssignRoles');
     * -- V10 migration audit rows <> migrated assignments (Up only)
     * SELECT 1 WHERE (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'role_assignment.migrated')
     *   <> (SELECT COUNT(*) FROM "UserRoleAssignments" WHERE "CreatedByUserId" IS NULL AND "AssignmentReason" = 'Migration from Users.RoleId (Phase 7, C5)');
     * -- V11 a row when a raw-SQL object is missing
     * SELECT 1 WHERE (SELECT COUNT(*) FROM pg_indexes WHERE indexname IN ('UX_Roles_Company_Name_Lower', 'UX_Roles_Branch_Name_Lower')) <> 2
     *   OR (SELECT COUNT(*) FROM pg_constraint WHERE conname IN ('UX_Roles_Id_BranchId', 'FK_URA_RoleBranch')) <> 2;
     * -- Row counts before/after: Roles x companies, RolePermissions grows by copies + grants, UserRoleAssignments = non-Revoked memberships, Users unchanged.
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard first: raises when any Branch-scope role, any role or assignment with CreatedByUserId NOT NULL, any Ended assignment, any assignment other than
     * the migrated/seeded one-per-membership, any assignment whose role name differs from Users.RoleId's, any Users.RoleId NULL, any role_assignment.* audit row other
     * than the migration's, or any copied role whose RolePermissions differ from its source exists - post-release data would be lost; restore the pre-deploy snapshot).
     * Else: removes the migration audit rows (the append-only row trigger on AuditEvents is disabled for that one DELETE and re-enabled), the assignments, the 4 new
     * codes and their grants, the role copies, the raw SQL objects, constraints, FKs and columns; restores IX_Roles_Name and Users.RoleId NOT NULL.
     */
    /// <inheritdoc />
    public partial class AddScopedRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guards G1-G6 (before anything is written).
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Roles' AND column_name = 'CompanyId') THEN
                        RAISE EXCEPTION 'AddScopedRoles: Roles.CompanyId already exists (G1)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships") AND NOT EXISTS (SELECT 1 FROM "Roles" WHERE "IsSystem" AND "IsActive") THEN
                        RAISE EXCEPTION 'AddScopedRoles: memberships exist but no active system role exists (G2)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" WHERE m."Status" <> 'Revoked' AND u."RoleId" IS NULL) THEN
                        RAISE EXCEPTION 'AddScopedRoles: a non-revoked membership belongs to a user without a role (G3)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Permissions" WHERE "Code" IN ('Company.Users.AssignRoles', 'Branch.Roles.View', 'Branch.Roles.Manage', 'Branch.Users.AssignRoles')
                                                              AND "Module" <> 'Administration') THEN
                        RAISE EXCEPTION 'AddScopedRoles: a new permission code already exists in another module (G4)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Roles" GROUP BY lower("Name") HAVING COUNT(*) > 1) THEN
                        RAISE EXCEPTION 'AddScopedRoles: two roles differ only by case; rename one before migrating (G5)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "Roles" WHERE "IsSystem") > 1 THEN
                        RAISE EXCEPTION 'AddScopedRoles: more than one system role exists; only one Administrator per company is allowed (G5)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Roles") AND NOT EXISTS (SELECT 1 FROM "Companies") THEN
                        RAISE EXCEPTION 'AddScopedRoles: roles exist but no company exists to own them (G6)';
                    END IF;
                END $g$;
                """);

            // ===== 1. additive =====
            migrationBuilder.DropIndex(
                name: "IX_Roles_Name",
                table: "Roles");

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                table: "Users",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Roles",
                type: "uuid",
                nullable: true);

            // Nullable until the backfill below; NOT NULL in the enforce step.
            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Roles",
                type: "uuid",
                nullable: true);

            // Existing roles are stamped with the migration instant; the temporary default is dropped right after (the model has none).
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "Roles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.Sql("ALTER TABLE \"Roles\" ALTER COLUMN \"CreatedAt\" DROP DEFAULT;");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "Roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScopeType",
                table: "Roles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Company");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Roles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedByUserId",
                table: "Roles",
                type: "uuid",
                nullable: true);

            // The 4 new catalogue rows (same text as PermissionCatalog / scripts/sql/seed-permissions.sql).
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description")
                SELECT md5('perm:' || v."Code")::uuid, v."Code", v."Module", v."Action", v."Description"
                FROM (VALUES
                  ('Company.Users.AssignRoles', 'Administration', 'AssignRoles', 'Assign and end company-wide roles for users (company-wide administrators only).'),
                  ('Branch.Roles.View', 'Administration', 'View', 'View the roles scoped to the branch.'),
                  ('Branch.Roles.Manage', 'Administration', 'Manage', 'Create, edit, duplicate and deactivate roles scoped to the branch.'),
                  ('Branch.Users.AssignRoles', 'Administration', 'AssignRoles', 'Assign and end branch roles for users allocated to the branch.')
                ) AS v("Code", "Module", "Action", "Description")
                ON CONFLICT ("Code") DO NOTHING;
                """);

            // ===== 2. backfill roles (D2) =====
            migrationBuilder.Sql("""
                -- B1: every existing role keeps its Id and belongs to the home company (earliest Companies.CreatedAt, then Id)
                UPDATE "Roles" SET "CompanyId" = (SELECT c."Id" FROM "Companies" c ORDER BY c."CreatedAt", c."Id" LIMIT 1);

                -- B2: every other company receives a copy of EVERY home role (copy-all, not copy-used) and of its permission rows
                INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "ScopeType", "BranchId", "IsSystem", "IsActive", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId")
                SELECT md5('role:' || c."Id"::text || ':' || r."Id"::text)::uuid, c."Id", r."Name", r."Description", 'Company', NULL, r."IsSystem", r."IsActive", now(), NULL, NULL, NULL
                FROM "Roles" r
                CROSS JOIN "Companies" c
                WHERE r."CompanyId" = (SELECT h."Id" FROM "Companies" h ORDER BY h."CreatedAt", h."Id" LIMIT 1)
                  AND c."Id" <> r."CompanyId"
                ON CONFLICT DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT md5('role:' || c."Id"::text || ':' || rp."RoleId"::text)::uuid, rp."PermissionId", rp."GrantedAt"
                FROM "RolePermissions" rp
                JOIN "Roles" r ON r."Id" = rp."RoleId"
                CROSS JOIN "Companies" c
                WHERE r."CompanyId" = (SELECT h."Id" FROM "Companies" h ORDER BY h."CreatedAt", h."Id" LIMIT 1)
                  AND c."Id" <> r."CompanyId"
                ON CONFLICT DO NOTHING;

                -- B4: assigning roles was part of Users.Create/Users.Edit (D5); every role holding either (originals and copies) receives Company.Users.AssignRoles
                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId" AND ep."Code" IN ('Users.Create', 'Users.Edit')
                CROSS JOIN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'Company.Users.AssignRoles') np
                ON CONFLICT DO NOTHING;

                -- V1 (before NOT NULL): no role without a company
                DO $s$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Roles" WHERE "CompanyId" IS NULL) THEN
                        RAISE EXCEPTION 'AddScopedRoles: a role has no company after the backfill (V1)';
                    END IF;
                END $s$;
                """);

            // ===== 3. enforce Roles =====
            migrationBuilder.AlterColumn<Guid>(
                name: "CompanyId",
                table: "Roles",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Roles_CompanyId_Id",
                table: "Roles",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Roles_CompanyId_Id_ScopeType",
                table: "Roles",
                columns: new[] { "CompanyId", "Id", "ScopeType" });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_CompanyId_BranchId",
                table: "Roles",
                columns: new[] { "CompanyId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_CompanyId_System",
                table: "Roles",
                column: "CompanyId",
                unique: true,
                filter: "\"IsSystem\"");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_CreatedByUserId",
                table: "Roles",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_UpdatedByUserId",
                table: "Roles",
                column: "UpdatedByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_Id_NotEmpty",
                table: "Roles",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_Scope",
                table: "Roles",
                sql: "(\"ScopeType\" = 'Company' AND \"BranchId\" IS NULL) OR (\"ScopeType\" = 'Branch' AND \"BranchId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_SystemIsCompany",
                table: "Roles",
                sql: "NOT \"IsSystem\" OR \"ScopeType\" = 'Company'");

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Branches_CompanyId_BranchId",
                table: "Roles",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Companies_CompanyId",
                table: "Roles",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Users_CreatedByUserId",
                table: "Roles",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Users_UpdatedByUserId",
                table: "Roles",
                column: "UpdatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Raw SQL objects EF cannot model (D1/D3; precedent UnitConfiguration): case-insensitive name uniques and the nullable (Id, BranchId) target.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_Roles_Company_Name_Lower" ON "Roles" ("CompanyId", lower("Name")) WHERE "ScopeType" = 'Company';
                CREATE UNIQUE INDEX "UX_Roles_Branch_Name_Lower" ON "Roles" ("CompanyId", "BranchId", lower("Name")) WHERE "ScopeType" = 'Branch';
                ALTER TABLE "Roles" ADD CONSTRAINT "UX_Roles_Id_BranchId" UNIQUE ("Id", "BranchId");
                """);

            // ===== 4. UserRoleAssignments (created empty with every constraint, filled in step 5) =====
            migrationBuilder.CreateTable(
                name: "UserRoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserCompanyMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AssignmentReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoleAssignments", x => x.Id);
                    table.CheckConstraint("CK_URA_Active_EndAt", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
                    table.CheckConstraint("CK_URA_EndAt_After_StartAt", "\"EndAt\" IS NULL OR \"EndAt\" >= \"StartAt\"");
                    table.CheckConstraint("CK_URA_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_URA_Scope_Branch", "(\"ScopeType\" = 'Branch') = (\"BranchId\" IS NOT NULL)");
                    table.CheckConstraint("CK_URA_Status", "\"Status\" IN ('Active', 'Ended')");
                    table.ForeignKey(
                        name: "FK_URA_Branch",
                        columns: x => new { x.CompanyId, x.BranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_URA_Membership",
                        columns: x => new { x.CompanyId, x.UserCompanyMembershipId },
                        principalTable: "UserCompanyMemberships",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_URA_Role",
                        columns: x => new { x.CompanyId, x.RoleId, x.ScopeType },
                        principalTable: "Roles",
                        principalColumns: new[] { "CompanyId", "Id", "ScopeType" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignments_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignments_Users_EndedByUserId",
                        column: x => x.EndedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_URA_Branch_Active",
                table: "UserRoleAssignments",
                columns: new[] { "CompanyId", "BranchId" },
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_URA_Membership_History",
                table: "UserRoleAssignments",
                columns: new[] { "CompanyId", "UserCompanyMembershipId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_URA_Membership_Role_Active",
                table: "UserRoleAssignments",
                columns: new[] { "UserCompanyMembershipId", "RoleId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_URA_Role_Active",
                table: "UserRoleAssignments",
                columns: new[] { "CompanyId", "RoleId" },
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_URA_Role_Scope",
                table: "UserRoleAssignments",
                columns: new[] { "CompanyId", "RoleId", "ScopeType" });

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_CreatedByUserId",
                table: "UserRoleAssignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_EndedByUserId",
                table: "UserRoleAssignments",
                column: "EndedByUserId");

            // Assignment branch = role branch (with FK_URA_Role: scope = role scope). MATCH SIMPLE: company-role rows (BranchId NULL) are not checked.
            migrationBuilder.Sql("""
                ALTER TABLE "UserRoleAssignments" ADD CONSTRAINT "FK_URA_RoleBranch" FOREIGN KEY ("RoleId", "BranchId") REFERENCES "Roles" ("Id", "BranchId") ON DELETE RESTRICT;
                """);

            // ===== 5. backfill assignments (B3, B5) =====
            migrationBuilder.Sql("""
                -- B3: one Active company-role assignment per non-Revoked membership -> that company's copy of the user's RoleId
                INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "BranchId", "Status", "StartAt", "EndAt",
                                                   "AssignmentReason", "EndReason", "CreatedAt", "CreatedByUserId", "EndedByUserId")
                SELECT md5('ura:' || m."Id"::text)::uuid, m."CompanyId", m."Id",
                       CASE WHEN m."CompanyId" = (SELECT h."Id" FROM "Companies" h ORDER BY h."CreatedAt", h."Id" LIMIT 1)
                            THEN u."RoleId" ELSE md5('role:' || m."CompanyId"::text || ':' || u."RoleId"::text)::uuid END,
                       'Company', NULL, 'Active', now(), NULL, 'Migration from Users.RoleId (Phase 7, C5)', NULL, now(), NULL, NULL
                FROM "UserCompanyMemberships" m
                JOIN "Users" u ON u."Id" = m."UserId"
                WHERE m."Status" <> 'Revoked'
                ON CONFLICT DO NOTHING;

                -- B5: every mapped membership gets a new AccessVersion and one audit event (written before any append-only concern: INSERT only)
                UPDATE "UserCompanyMemberships" m SET "AccessVersion" = m."AccessVersion" + 1
                WHERE EXISTS (SELECT 1 FROM "UserRoleAssignments" a WHERE a."Id" = md5('ura:' || m."Id"::text)::uuid);

                INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "ActorUserId", "Action", "TargetType", "TargetId", "SubjectUserId", "Reason", "Before", "After", "CorrelationId")
                SELECT md5('aev7:' || m."Id"::text)::uuid, m."CompanyId", now(), NULL, 'role_assignment.migrated', 'UserRoleAssignment', a."Id", m."UserId",
                       'Migration from Users.RoleId (Phase 7, C5)', NULL,
                       jsonb_build_object('membershipId', m."Id", 'roleId', a."RoleId", 'roleName', r."Name", 'scopeType', 'Company'), NULL
                FROM "UserRoleAssignments" a
                JOIN "UserCompanyMemberships" m ON m."CompanyId" = a."CompanyId" AND m."Id" = a."UserCompanyMembershipId"
                JOIN "Roles" r ON r."Id" = a."RoleId"
                WHERE a."Id" = md5('ura:' || m."Id"::text)::uuid AND a."CreatedByUserId" IS NULL
                ON CONFLICT DO NOTHING;
                """);

            // ===== 6. assertions V2-V11 (same transaction) =====
            migrationBuilder.Sql("""
                DO $s$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "Roles")
                               AND (SELECT COUNT(*) FROM "Roles" r WHERE r."CompanyId" = c."Id" AND r."IsSystem" AND r."IsActive") <> 1) THEN
                        RAISE EXCEPTION 'AddScopedRoles: a company does not have exactly one active system role (V2)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."Status" <> 'Revoked'
                               AND (SELECT COUNT(*) FROM "UserRoleAssignments" a WHERE a."UserCompanyMembershipId" = m."Id" AND a."Status" = 'Active' AND a."ScopeType" = 'Company') <> 1) THEN
                        RAISE EXCEPTION 'AddScopedRoles: a non-revoked membership does not have exactly one active company-role assignment (V3)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserRoleAssignments" a JOIN "Roles" r ON r."Id" = a."RoleId" WHERE r."CompanyId" <> a."CompanyId") THEN
                        RAISE EXCEPTION 'AddScopedRoles: an assignment references a role of another company (V4)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserRoleAssignments" a JOIN "UserCompanyMemberships" m ON m."Id" = a."UserCompanyMembershipId" JOIN "Users" u ON u."Id" = m."UserId"
                               JOIN "Roles" r ON r."Id" = a."RoleId" JOIN "Roles" ur ON ur."Id" = u."RoleId" WHERE r."Name" <> ur."Name") THEN
                        RAISE EXCEPTION 'AddScopedRoles: an assignment role name differs from the Users.RoleId role name (V5)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "Roles") <>
                       (SELECT COUNT(*) FROM "Roles" r WHERE r."CompanyId" = (SELECT h."Id" FROM "Companies" h ORDER BY h."CreatedAt", h."Id" LIMIT 1)) * (SELECT COUNT(*) FROM "Companies") THEN
                        RAISE EXCEPTION 'AddScopedRoles: role count differs from source roles x companies (V6)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Roles" c JOIN "Roles" s ON s."CompanyId" <> c."CompanyId" AND md5('role:' || c."CompanyId"::text || ':' || s."Id"::text)::uuid = c."Id"
                               WHERE (SELECT COUNT(*) FROM "RolePermissions" WHERE "RoleId" = c."Id") <> (SELECT COUNT(*) FROM "RolePermissions" WHERE "RoleId" = s."Id")) THEN
                        RAISE EXCEPTION 'AddScopedRoles: a copied role has a different number of permissions than its source (V7)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
                               AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "UserRoleAssignments" a ON a."UserCompanyMembershipId" = m."Id"
                                               JOIN "Roles" r ON r."Id" = a."RoleId"
                                               WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled'
                                                 AND a."Status" = 'Active' AND r."IsSystem" AND r."IsActive")) THEN
                        RAISE EXCEPTION 'AddScopedRoles: a company has no active company-wide Administrator (V8)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Roles" r
                               WHERE EXISTS (SELECT 1 FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = r."Id" AND p."Code" IN ('Users.Create', 'Users.Edit'))
                                 AND NOT EXISTS (SELECT 1 FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE rp."RoleId" = r."Id" AND p."Code" = 'Company.Users.AssignRoles')) THEN
                        RAISE EXCEPTION 'AddScopedRoles: a role holds Users.Create or Users.Edit without Company.Users.AssignRoles (V9)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'role_assignment.migrated')
                       <> (SELECT COUNT(*) FROM "UserRoleAssignments" WHERE "CreatedByUserId" IS NULL AND "AssignmentReason" = 'Migration from Users.RoleId (Phase 7, C5)') THEN
                        RAISE EXCEPTION 'AddScopedRoles: migration audit rows differ from migrated assignments (V10)';
                    END IF;
                    IF (SELECT COUNT(*) FROM pg_indexes WHERE indexname IN ('UX_Roles_Company_Name_Lower', 'UX_Roles_Branch_Name_Lower')) <> 2
                       OR (SELECT COUNT(*) FROM pg_constraint WHERE conname IN ('UX_Roles_Id_BranchId', 'FK_URA_RoleBranch')) <> 2 THEN
                        RAISE EXCEPTION 'AddScopedRoles: a raw SQL constraint or index is missing (V11)';
                    END IF;
                END $s$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard: post-release role data would be lost; restore the pre-deploy snapshot instead (D10).
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Roles" WHERE "ScopeType" <> 'Company' OR "CreatedByUserId" IS NOT NULL OR "UpdatedByUserId" IS NOT NULL OR "UpdatedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: branch roles or roles created/edited after the migration exist; restore the pre-deploy snapshot instead.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserRoleAssignments" WHERE "CreatedByUserId" IS NOT NULL OR "EndedByUserId" IS NOT NULL OR "Status" <> 'Active' OR "ScopeType" <> 'Company') THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: assignments were created or ended after the migration; restore the pre-deploy snapshot instead.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE (SELECT COUNT(*) FROM "UserRoleAssignments" a WHERE a."UserCompanyMembershipId" = m."Id") > 1) THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: a membership holds more than one role assignment; restore the pre-deploy snapshot instead.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserRoleAssignments" a JOIN "UserCompanyMemberships" m ON m."Id" = a."UserCompanyMembershipId" JOIN "Users" u ON u."Id" = m."UserId"
                               JOIN "Roles" r ON r."Id" = a."RoleId" JOIN "Roles" ur ON ur."Id" = u."RoleId" WHERE r."Name" <> ur."Name") THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: an assignment differs from Users.RoleId; restore the pre-deploy snapshot instead.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Users" WHERE "RoleId" IS NULL) THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: users without Users.RoleId exist (created after the migration); restore the pre-deploy snapshot instead.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "AuditEvents" WHERE "Action" LIKE 'role_assignment.%' AND "Action" <> 'role_assignment.migrated') THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: role assignment audit events beyond the migration exist; restore the pre-deploy snapshot instead.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Roles" c JOIN "Roles" s ON s."CompanyId" <> c."CompanyId" AND md5('role:' || c."CompanyId"::text || ':' || s."Id"::text)::uuid = c."Id"
                               WHERE EXISTS (SELECT 1 FROM "RolePermissions" x WHERE x."RoleId" = c."Id" AND NOT EXISTS (SELECT 1 FROM "RolePermissions" y WHERE y."RoleId" = s."Id" AND y."PermissionId" = x."PermissionId"))
                                  OR EXISTS (SELECT 1 FROM "RolePermissions" y WHERE y."RoleId" = s."Id" AND NOT EXISTS (SELECT 1 FROM "RolePermissions" x WHERE x."RoleId" = c."Id" AND x."PermissionId" = y."PermissionId"))) THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: a copied role has permissions that differ from its source; restore the pre-deploy snapshot instead.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Roles" c WHERE c."CompanyId" <> (SELECT h."Id" FROM "Companies" h ORDER BY h."CreatedAt", h."Id" LIMIT 1)
                               AND NOT EXISTS (SELECT 1 FROM "Roles" s WHERE s."CompanyId" <> c."CompanyId" AND md5('role:' || c."CompanyId"::text || ':' || s."Id"::text)::uuid = c."Id")) THEN
                        RAISE EXCEPTION 'AddScopedRoles Down: a role other than a migration copy exists in a non-home company; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;

                -- The migration audit rows go first; the append-only row trigger is disabled for this one DELETE only (same transaction).
                ALTER TABLE "AuditEvents" DISABLE TRIGGER "TR_AuditEvents_AppendOnly";
                DELETE FROM "AuditEvents" WHERE "Action" = 'role_assignment.migrated';
                ALTER TABLE "AuditEvents" ENABLE TRIGGER "TR_AuditEvents_AppendOnly";
                """);

            migrationBuilder.DropTable(
                name: "UserRoleAssignments");

            migrationBuilder.Sql("""
                -- the 4 new codes and their grants
                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions"
                    WHERE "Code" IN ('Company.Users.AssignRoles', 'Branch.Roles.View', 'Branch.Roles.Manage', 'Branch.Users.AssignRoles'));
                DELETE FROM "Permissions" WHERE "Code" IN ('Company.Users.AssignRoles', 'Branch.Roles.View', 'Branch.Roles.Manage', 'Branch.Users.AssignRoles');

                -- the copies of the other companies (the home company keeps the original rows and Ids)
                DELETE FROM "RolePermissions" WHERE "RoleId" IN (SELECT r."Id" FROM "Roles" r
                    WHERE r."CompanyId" <> (SELECT h."Id" FROM "Companies" h ORDER BY h."CreatedAt", h."Id" LIMIT 1));
                DELETE FROM "Roles" WHERE "CompanyId" <> (SELECT h."Id" FROM "Companies" h ORDER BY h."CreatedAt", h."Id" LIMIT 1);

                -- raw SQL objects
                DROP INDEX IF EXISTS "UX_Roles_Company_Name_Lower";
                DROP INDEX IF EXISTS "UX_Roles_Branch_Name_Lower";
                ALTER TABLE "Roles" DROP CONSTRAINT IF EXISTS "UX_Roles_Id_BranchId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Branches_CompanyId_BranchId",
                table: "Roles");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Companies_CompanyId",
                table: "Roles");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Users_CreatedByUserId",
                table: "Roles");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Users_UpdatedByUserId",
                table: "Roles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Roles_CompanyId_Id",
                table: "Roles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Roles_CompanyId_Id_ScopeType",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_CompanyId_BranchId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_CompanyId_System",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_CreatedByUserId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_UpdatedByUserId",
                table: "Roles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_Id_NotEmpty",
                table: "Roles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_Scope",
                table: "Roles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_SystemIsCompany",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "ScopeType",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "Roles");

            // Guarded above: no NULL Users.RoleId remains.
            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                table: "Users",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);
        }
    }
}
