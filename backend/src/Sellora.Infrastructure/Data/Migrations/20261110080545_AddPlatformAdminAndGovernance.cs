using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 6 - Platform separation and company governance (design D1, D7, D8, D12; owner decisions O1-O4 of 2026-11-10): one migration, one transaction.
     *   guard    : G1 (before anything is written) no Permissions row has a governance code ('Company.Governance.%'; the CHECK added at the end would fail).
     *              G2 every company that has an Active membership has a root-owner candidate = Active, CompanyWide membership of a non-Disabled user whose
     *              role IsSystem AND IsActive (Phase 5 G3/V6 should make a violation impossible). Companies without any Active membership (a fresh database)
     *              have nothing to map and are skipped.
     *   additive : CompanyGovernanceMembers (composite FK (CompanyId, UserCompanyMembershipId) -> UserCompanyMemberships AK; AK (CompanyId, Id); partial unique
     *              one Active row per membership; CHECKs Title, Status, Active <=> EndAt NULL, Id not empty), GovernancePermissions (platform catalogue, a table of its
     *              own), GovernanceMemberPermissions (composite FK -> members AK, unique (member, permission)), PlatformAdministrators, PlatformAuditEvents.
     *              GovernancePermissions rows for the 5 codes (Id md5('govperm:'||code)::uuid, same ids as DbSeeder).
     *   backfill : owner decision O2 (ADR-10): per company the root-owner candidate with the earliest Users.CreatedAt, then Users.Id, becomes the ONLY migrated
     *              Owner member (Id md5('gov:'||membershipId)::uuid, Title Owner, StartAt now(), CreatedByUserId NULL) with all 5 grants (Id md5('govgrant:'||member||code))
     *              and one AuditEvent governance.owner_mapped (Id md5('govaudit:'||member), reason 'Migration default owner (ADR-10)'). Other Administrators get no
     *              governance (O2). Platform administrators: NONE (O3, granted only by scripts/sql/grant-platform-admin.sql). Re-run is a no-op (companies that already
     *              have a governance member are skipped; ON CONFLICT DO NOTHING).
     *   enforce  : CHECK CK_Permissions_NotGovernance (after G1); then function sellora_audit_append_only() and triggers TR_AuditEvents_AppendOnly,
     *              TR_PlatformAuditEvents_AppendOnly (row: UPDATE/DELETE) and TR_AuditEvents_NoTruncate, TR_PlatformAuditEvents_NoTruncate (statement: TRUNCATE) that RAISE
     *              restrict_violation (23001). Created last, after the backfill wrote its audit rows.
     *   assert   : V1-V9 below run in the same transaction and RAISE on any violation.
     *
     * RELEASE NOTE: five new tables, five catalogue rows, one Owner + 5 grants + 1 audit row per company, one CHECK, one function and four triggers; instant. Old
     * application builds ignore the new objects and never rewrite audit rows, so rolling back the application alone is safe; after the release audit rows can only be
     * repaired by a migration (triggers) - intended. Down refuses once post-release governance/platform data exists: restore the pre-deploy snapshot (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release (the owner reviews P1 - O2).
     *
     * -- P1 per company: every root-owner candidate in order; "becomes_owner" = TRUE is the user the migration makes Owner with all 5 governance codes
     * SELECT c."Code" AS company, u."Username", u."Email", u."Status" AS user_status, u."CreatedAt" AS user_created_at,
     *        ROW_NUMBER() OVER (PARTITION BY m."CompanyId" ORDER BY u."CreatedAt", u."Id") = 1 AS becomes_owner
     * FROM "UserCompanyMemberships" m JOIN "Companies" c ON c."Id" = m."CompanyId" JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
     * WHERE m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive"
     * ORDER BY c."Code", u."CreatedAt", u."Id";
     * -- P2 companies with an Active membership but no candidate (G2 would raise; expected: no rows)
     * SELECT c."Id", c."Code" FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
     *   AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
     *                   WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive");
     * -- P3 permission rows that would violate the new CHECK (G1; expected: no rows)
     * SELECT "Id", "Code" FROM "Permissions" WHERE "Code" LIKE 'Company.Governance.%';
     * -- P4 row counts before/after (AuditEvents grows by exactly the number of becomes_owner rows in P1; Permissions and UserCompanyMemberships unchanged)
     * SELECT 'AuditEvents' t, COUNT(*) FROM "AuditEvents" UNION ALL SELECT 'Permissions', COUNT(*) FROM "Permissions" UNION ALL SELECT 'UserCompanyMemberships', COUNT(*) FROM "UserCompanyMemberships";
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying (and after services wrote rows); every query must return no rows. V1-V9 are also asserted inside Up (V3, V8 Up only).
     *
     * -- V1 companies with a root-owner candidate without exactly one migrated Owner member (migrated = CreatedByUserId NULL)
     * SELECT c."Id" FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
     *     WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive")
     *   AND (SELECT COUNT(*) FROM "CompanyGovernanceMembers" g WHERE g."CompanyId" = c."Id" AND g."Title" = 'Owner' AND g."CreatedByUserId" IS NULL) <> 1;
     * -- V2 companies with an Active membership but no root owner (D10 R4: Active Owner, membership Active, user not Disabled, holding View + ManageOwners + AssignPermissions)
     * SELECT c."Id" FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
     *   AND NOT EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId" JOIN "Users" u ON u."Id" = m."UserId"
     *     WHERE g."CompanyId" = c."Id" AND g."Title" = 'Owner' AND g."Status" = 'Active' AND m."Status" = 'Active' AND u."Status" <> 'Disabled'
     *       AND (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p JOIN "GovernancePermissions" gp ON gp."Id" = p."GovernancePermissionId"
     *            WHERE p."GovernanceMemberId" = g."Id" AND gp."Code" IN ('Company.Governance.View', 'Company.Governance.ManageOwners', 'Company.Governance.AssignPermissions')) = 3);
     * -- V3 governance members whose membership is not Active/CompanyWide (Up only: later services may suspend memberships)
     * SELECT g."Id" FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
     *   WHERE m."Status" <> 'Active' OR m."AccessScope" <> 'CompanyWide';
     * -- V4 a row when the catalogue does not hold exactly the 5 governance codes
     * SELECT 1 WHERE (SELECT COUNT(*) FROM "GovernancePermissions" WHERE "Code" IN ('Company.Governance.View', 'Company.Governance.ManageMembers', 'Company.Governance.ManageOwners',
     *   'Company.Governance.AssignPermissions', 'Company.Governance.ManageAdministrators')) <> 5 OR (SELECT COUNT(*) FROM "GovernancePermissions") <> 5;
     * -- V5 operational Permissions carrying a governance code
     * SELECT "Id" FROM "Permissions" WHERE "Code" LIKE 'Company.Governance.%';
     * -- V6 migrated members whose grant count <> 5
     * SELECT g."Id" FROM "CompanyGovernanceMembers" g WHERE g."CreatedByUserId" IS NULL AND (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p WHERE p."GovernanceMemberId" = g."Id") <> 5;
     * -- V7 owner_mapped audit rows <> migrated members (returns one row on mismatch)
     * SELECT 1 WHERE (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'governance.owner_mapped') <> (SELECT COUNT(*) FROM "CompanyGovernanceMembers" WHERE "CreatedByUserId" IS NULL);
     * -- V8 PlatformAdministrators not empty (Up only: operators are granted after the release)
     * SELECT "UserId" FROM "PlatformAdministrators";
     * -- V9 a row when an append-only trigger or the function is missing
     * SELECT 1 WHERE (SELECT COUNT(*) FROM pg_trigger WHERE NOT tgisinternal AND tgname IN ('TR_AuditEvents_AppendOnly', 'TR_AuditEvents_NoTruncate', 'TR_PlatformAuditEvents_AppendOnly', 'TR_PlatformAuditEvents_NoTruncate')) <> 4
     *   OR NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'sellora_audit_append_only');
     * -- V10 same-company integrity (also FK-enforced)
     * SELECT g."Id" FROM "GovernanceMemberPermissions" g JOIN "CompanyGovernanceMembers" m ON m."Id" = g."GovernanceMemberId" WHERE m."CompanyId" <> g."CompanyId";
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G6 first: raises when any governance member/grant has CreatedByUserId/GrantedByUserId NOT NULL, any member was ended or updated, any
     * PlatformAdministrators or PlatformAuditEvents row exists, any Company was created after the migration, or any governance.* AuditEvent other than
     * owner_mapped exists - post-release data would be lost, restore the pre-deploy snapshot instead): drops the triggers and the function, removes the owner_mapped
     * audit rows, drops CK_Permissions_NotGovernance and the 5 tables.
     */
    /// <inheritdoc />
    public partial class AddPlatformAdminAndGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guards G1-G2 (before anything is written).
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Permissions" WHERE "Code" LIKE 'Company.Governance.%') THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: Permissions contains governance codes; governance codes live in GovernancePermissions only (G1)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
                                 AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                                                 WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide'
                                                   AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive")) THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: a company has no root-owner candidate (Active CompanyWide membership of a non-disabled user with an active system role) (G2)';
                    END IF;
                END $g$;
                """);

            // ===== 1. additive =====
            migrationBuilder.CreateTable(
                name: "CompanyGovernanceMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserCompanyMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyGovernanceMembers", x => x.Id);
                    table.UniqueConstraint("AK_CompanyGovernanceMembers_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_CompanyGovernanceMembers_Active_EndAt", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
                    table.CheckConstraint("CK_CompanyGovernanceMembers_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_CompanyGovernanceMembers_Status", "\"Status\" IN ('Active', 'Ended')");
                    table.CheckConstraint("CK_CompanyGovernanceMembers_Title", "\"Title\" IN ('Owner', 'BoardMember', 'Director', 'DelegatedAdministrator')");
                    table.ForeignKey(
                        name: "FK_CompanyGovernanceMembers_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyGovernanceMembers_Memberships_Company",
                        columns: x => new { x.CompanyId, x.UserCompanyMembershipId },
                        principalTable: "UserCompanyMemberships",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyGovernanceMembers_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyGovernanceMembers_Users_EndedByUserId",
                        column: x => x.EndedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyGovernanceMembers_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GovernancePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernancePermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAdministrators",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAdministrators", x => x.UserId);
                    table.CheckConstraint("CK_PlatformAdministrators_Active_RevokedAt", "(\"Status\" = 'Active') = (\"RevokedAt\" IS NULL)");
                    table.CheckConstraint("CK_PlatformAdministrators_Status", "\"Status\" IN ('Active', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_PlatformAdministrators_Users_GrantedByUserId",
                        column: x => x.GrantedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlatformAdministrators_Users_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlatformAdministrators_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Before = table.Column<string>(type: "jsonb", nullable: true),
                    After = table.Column<string>(type: "jsonb", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAuditEvents", x => x.Id);
                    table.CheckConstraint("CK_PlatformAuditEvents_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_PlatformAuditEvents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlatformAuditEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GovernanceMemberPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GovernanceMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    GovernancePermissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernanceMemberPermissions", x => x.Id);
                    table.CheckConstraint("CK_GovernanceMemberPermissions_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_GovMemberPerms_GovPermissions_Permission",
                        column: x => x.GovernancePermissionId,
                        principalTable: "GovernancePermissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GovMemberPerms_Members_Company",
                        columns: x => new { x.CompanyId, x.GovernanceMemberId },
                        principalTable: "CompanyGovernanceMembers",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GovernanceMemberPermissions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GovernanceMemberPermissions_Users_GrantedByUserId",
                        column: x => x.GrantedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyGovernanceMembers_CreatedByUserId",
                table: "CompanyGovernanceMembers",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyGovernanceMembers_EndedByUserId",
                table: "CompanyGovernanceMembers",
                column: "EndedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyGovernanceMembers_UpdatedByUserId",
                table: "CompanyGovernanceMembers",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GovMembers_Company_Membership",
                table: "CompanyGovernanceMembers",
                columns: new[] { "CompanyId", "UserCompanyMembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_GovMembers_Membership_Active",
                table: "CompanyGovernanceMembers",
                column: "UserCompanyMembershipId",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceMemberPermissions_GrantedByUserId",
                table: "GovernanceMemberPermissions",
                column: "GrantedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GovMemberPerms_Company_Member",
                table: "GovernanceMemberPermissions",
                columns: new[] { "CompanyId", "GovernanceMemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_GovMemberPerms_Member_Permission",
                table: "GovernanceMemberPermissions",
                columns: new[] { "GovernanceMemberId", "GovernancePermissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GovMemberPerms_Permission",
                table: "GovernanceMemberPermissions",
                column: "GovernancePermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_GovernancePermissions_Code",
                table: "GovernancePermissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAdministrators_GrantedByUserId",
                table: "PlatformAdministrators",
                column: "GrantedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAdministrators_RevokedByUserId",
                table: "PlatformAdministrators",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditEvents_ActorUserId",
                table: "PlatformAuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditEvents_Company",
                table: "PlatformAuditEvents",
                columns: new[] { "CompanyId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditEvents_OccurredAt",
                table: "PlatformAuditEvents",
                column: "OccurredAt");

            // ===== 2. catalogue seed + backfill (owner decision O2) =====
            migrationBuilder.Sql("""
                -- 2a. governance catalogue (deterministic ids, identical to DbSeeder / GovernancePermissionCatalog.IdFor)
                INSERT INTO "GovernancePermissions" ("Id", "Code", "Description")
                SELECT md5('govperm:' || v."Code")::uuid, v."Code", v."Description"
                FROM (VALUES
                  ('Company.Governance.View', 'View governance members, their titles and grants, and the governance audit trail.'),
                  ('Company.Governance.ManageMembers', 'Add, retitle and end governance members other than owners.'),
                  ('Company.Governance.ManageOwners', 'Add, retitle, end or change the grants of Owner-title governance members.'),
                  ('Company.Governance.AssignPermissions', 'Grant and revoke governance permissions (only codes the actor holds).'),
                  ('Company.Governance.ManageAdministrators', 'Grant and remove the Administrator role (delegated company administration).')
                ) AS v("Code", "Description")
                ON CONFLICT DO NOTHING;

                -- 2b. the earliest root-owner candidate of every company that has no governance member yet becomes its Owner
                INSERT INTO "CompanyGovernanceMembers" ("Id", "CompanyId", "UserCompanyMembershipId", "Title", "Status", "StartAt", "EndAt", "EndReason", "CreatedAt", "CreatedByUserId", "EndedByUserId", "UpdatedAt", "UpdatedByUserId")
                SELECT md5('gov:' || c."MembershipId"::text)::uuid, c."CompanyId", c."MembershipId", 'Owner', 'Active', now(), NULL, NULL, now(), NULL, NULL, NULL, NULL
                FROM (SELECT DISTINCT ON (m."CompanyId") m."CompanyId", m."Id" AS "MembershipId"
                      FROM "UserCompanyMemberships" m
                      JOIN "Users" u ON u."Id" = m."UserId"
                      JOIN "Roles" r ON r."Id" = u."RoleId"
                      WHERE m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive"
                      ORDER BY m."CompanyId", u."CreatedAt", u."Id") c
                WHERE NOT EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" g WHERE g."CompanyId" = c."CompanyId")
                ON CONFLICT DO NOTHING;

                -- 2c. all five governance codes for each migrated Owner
                INSERT INTO "GovernanceMemberPermissions" ("Id", "CompanyId", "GovernanceMemberId", "GovernancePermissionId", "GrantedAt", "GrantedByUserId")
                SELECT md5('govgrant:' || g."Id"::text || gp."Code")::uuid, g."CompanyId", g."Id", gp."Id", now(), NULL
                FROM "CompanyGovernanceMembers" g
                CROSS JOIN "GovernancePermissions" gp
                WHERE g."CreatedByUserId" IS NULL AND g."Title" = 'Owner'
                  AND g."Id" = md5('gov:' || g."UserCompanyMembershipId"::text)::uuid
                ON CONFLICT DO NOTHING;

                -- 2d. one audit event per migrated Owner (written before the append-only triggers exist)
                INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "ActorUserId", "Action", "TargetType", "TargetId", "SubjectUserId", "Reason", "Before", "After", "CorrelationId")
                SELECT md5('govaudit:' || g."Id"::text)::uuid, g."CompanyId", now(), NULL, 'governance.owner_mapped', 'CompanyGovernanceMember', g."Id", m."UserId",
                       'Migration default owner (ADR-10)', NULL,
                       jsonb_build_object('title', 'Owner', 'membershipId', m."Id",
                                          'permissionCodes', (SELECT jsonb_agg(gp."Code" ORDER BY gp."Code") FROM "GovernanceMemberPermissions" p JOIN "GovernancePermissions" gp ON gp."Id" = p."GovernancePermissionId" WHERE p."GovernanceMemberId" = g."Id")),
                       NULL
                FROM "CompanyGovernanceMembers" g
                JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
                WHERE g."CreatedByUserId" IS NULL AND g."Title" = 'Owner'
                  AND g."Id" = md5('gov:' || g."UserCompanyMembershipId"::text)::uuid
                ON CONFLICT DO NOTHING;
                """);

            // ===== 3. enforce =====
            // 3a. the operational catalogue can never hold a governance code (G1 proved it holds none)
            migrationBuilder.AddCheckConstraint(
                name: "CK_Permissions_NotGovernance",
                table: "Permissions",
                sql: "\"Code\" NOT LIKE 'Company.Governance.%'");

            // 3b. database-level append-only audit tables (D12): reject UPDATE, DELETE (row triggers) and TRUNCATE (statement triggers)
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION sellora_audit_append_only() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP USING ERRCODE = 'restrict_violation';
                END;
                $fn$;

                DROP TRIGGER IF EXISTS "TR_AuditEvents_AppendOnly" ON "AuditEvents";
                CREATE TRIGGER "TR_AuditEvents_AppendOnly" BEFORE UPDATE OR DELETE ON "AuditEvents" FOR EACH ROW EXECUTE FUNCTION sellora_audit_append_only();
                DROP TRIGGER IF EXISTS "TR_AuditEvents_NoTruncate" ON "AuditEvents";
                CREATE TRIGGER "TR_AuditEvents_NoTruncate" BEFORE TRUNCATE ON "AuditEvents" FOR EACH STATEMENT EXECUTE FUNCTION sellora_audit_append_only();
                DROP TRIGGER IF EXISTS "TR_PlatformAuditEvents_AppendOnly" ON "PlatformAuditEvents";
                CREATE TRIGGER "TR_PlatformAuditEvents_AppendOnly" BEFORE UPDATE OR DELETE ON "PlatformAuditEvents" FOR EACH ROW EXECUTE FUNCTION sellora_audit_append_only();
                DROP TRIGGER IF EXISTS "TR_PlatformAuditEvents_NoTruncate" ON "PlatformAuditEvents";
                CREATE TRIGGER "TR_PlatformAuditEvents_NoTruncate" BEFORE TRUNCATE ON "PlatformAuditEvents" FOR EACH STATEMENT EXECUTE FUNCTION sellora_audit_append_only();
                """);

            // ===== 4. assertions V1-V9 (same transaction) =====
            migrationBuilder.Sql("""
                DO $s$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                                             WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND m."AccessScope" = 'CompanyWide' AND u."Status" <> 'Disabled' AND r."IsSystem" AND r."IsActive")
                                 AND (SELECT COUNT(*) FROM "CompanyGovernanceMembers" g WHERE g."CompanyId" = c."Id" AND g."Title" = 'Owner' AND g."CreatedByUserId" IS NULL) <> 1) THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: a company does not have exactly one migrated Owner member (V1)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active')
                                 AND NOT EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" g
                                                 JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
                                                 JOIN "Users" u ON u."Id" = m."UserId"
                                                 WHERE g."CompanyId" = c."Id" AND g."Title" = 'Owner' AND g."Status" = 'Active' AND m."Status" = 'Active' AND u."Status" <> 'Disabled'
                                                   AND (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p JOIN "GovernancePermissions" gp ON gp."Id" = p."GovernancePermissionId"
                                                        WHERE p."GovernanceMemberId" = g."Id"
                                                          AND gp."Code" IN ('Company.Governance.View', 'Company.Governance.ManageOwners', 'Company.Governance.AssignPermissions')) = 3)) THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: a company has no root owner (V2)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
                               WHERE m."Status" <> 'Active' OR m."AccessScope" <> 'CompanyWide') THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: a governance member membership is not Active/CompanyWide (V3)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "GovernancePermissions" WHERE "Code" IN ('Company.Governance.View', 'Company.Governance.ManageMembers', 'Company.Governance.ManageOwners',
                            'Company.Governance.AssignPermissions', 'Company.Governance.ManageAdministrators')) <> 5
                       OR (SELECT COUNT(*) FROM "GovernancePermissions") <> 5 THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: the governance catalogue does not hold exactly the 5 codes (V4)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Permissions" WHERE "Code" LIKE 'Company.Governance.%') THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: Permissions contains a governance code (V5)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" g WHERE g."CreatedByUserId" IS NULL
                               AND (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p WHERE p."GovernanceMemberId" = g."Id") <> 5) THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: a migrated member does not hold exactly 5 grants (V6)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'governance.owner_mapped') <> (SELECT COUNT(*) FROM "CompanyGovernanceMembers" WHERE "CreatedByUserId" IS NULL) THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: owner_mapped audit rows differ from the migrated member count (V7)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PlatformAdministrators") THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: PlatformAdministrators must be empty after the migration (V8)';
                    END IF;
                    IF (SELECT COUNT(*) FROM pg_trigger WHERE NOT tgisinternal AND tgname IN ('TR_AuditEvents_AppendOnly', 'TR_AuditEvents_NoTruncate',
                            'TR_PlatformAuditEvents_AppendOnly', 'TR_PlatformAuditEvents_NoTruncate')) <> 4
                       OR NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'sellora_audit_append_only') THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance: the audit append-only triggers are missing (V9)';
                    END IF;
                END $s$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G6: post-release governance/platform data would be lost; restore the pre-deploy snapshot instead.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "CompanyGovernanceMembers" WHERE "CreatedByUserId" IS NOT NULL OR "EndedByUserId" IS NOT NULL OR "UpdatedByUserId" IS NOT NULL
                                                                            OR "Status" <> 'Active' OR "UpdatedAt" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM "GovernanceMemberPermissions" WHERE "GrantedByUserId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance Down: governance was changed after the migration; restore the pre-deploy snapshot instead (D-G6).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PlatformAdministrators") OR EXISTS (SELECT 1 FROM "PlatformAuditEvents") THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance Down: platform administrators or platform audit events exist; restore the pre-deploy snapshot instead (D-G6).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE c."Id" <> '0c0a1f00-0000-4000-8000-000000000001'
                                 AND c."CreatedAt" > COALESCE((SELECT MAX(g."CreatedAt") FROM "CompanyGovernanceMembers" g WHERE g."CreatedByUserId" IS NULL),
                                                              (SELECT MIN(a."OccurredAt") FROM "AuditEvents" a), now())) THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance Down: a company was created after the migration; restore the pre-deploy snapshot instead (D-G6).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "AuditEvents" WHERE "Action" LIKE 'governance.%' AND "Action" <> 'governance.owner_mapped') THEN
                        RAISE EXCEPTION 'AddPlatformAdminAndGovernance Down: governance audit events beyond the migration mapping exist; restore the pre-deploy snapshot instead (D-G6).';
                    END IF;
                END $g$;

                -- triggers and function first: the migration's own audit rows can then be removed
                DROP TRIGGER IF EXISTS "TR_AuditEvents_AppendOnly" ON "AuditEvents";
                DROP TRIGGER IF EXISTS "TR_AuditEvents_NoTruncate" ON "AuditEvents";
                DROP TRIGGER IF EXISTS "TR_PlatformAuditEvents_AppendOnly" ON "PlatformAuditEvents";
                DROP TRIGGER IF EXISTS "TR_PlatformAuditEvents_NoTruncate" ON "PlatformAuditEvents";
                DROP FUNCTION IF EXISTS sellora_audit_append_only();

                DELETE FROM "AuditEvents" WHERE "Action" = 'governance.owner_mapped';
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Permissions_NotGovernance",
                table: "Permissions");

            migrationBuilder.DropTable(
                name: "GovernanceMemberPermissions");

            migrationBuilder.DropTable(
                name: "PlatformAdministrators");

            migrationBuilder.DropTable(
                name: "PlatformAuditEvents");

            migrationBuilder.DropTable(
                name: "GovernancePermissions");

            migrationBuilder.DropTable(
                name: "CompanyGovernanceMembers");
        }
    }
}
