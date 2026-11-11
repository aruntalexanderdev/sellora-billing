using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 5 - User branch allocation (D1-D3, D10, D11; owner decisions O2 and Q7): one migration, one transaction.
     *   guard    : G1 (before anything is written) every company has exactly one active default branch (AddBranches V4); G2 no membership
     *              is AssignedBranches yet (the Phase 1 service rejected it; such a row is unexpected state); G3 a company that has an
     *              Active non-Administrator membership also has an Active membership of an Active Administrator user (else nobody could
     *              manage the new allocations). G3 is skipped for companies with nothing to map (a fresh database has no users yet).
     *   additive : UserCompanyMemberships.AccessVersion bigint NOT NULL DEFAULT 0 (instant; the Phase 7 cache invalidation stamp).
     *              UserBranchAssignments (history; written once, closed once) with composite FKs (CompanyId, UserCompanyMembershipId) ->
     *              UserCompanyMemberships AK and (CompanyId, BranchId) -> Branches AK, partial uniques (one active row per membership+branch;
     *              one active primary per membership), CHECKs (Status list, Active <=> EndAt NULL, EndAt >= StartAt, Id not empty).
     *              AuditEvents (append-only by SaveChanges guard; before/after jsonb), indexes (Target, Subject).
     *   backfill : Q7 documented default mapping (NOT fabricated history): owner answer 2026-11-09 maps existing users to the Main branch as
     *              primary, Administrators stay company-wide. For every CompanyWide membership of a user whose role is NOT a system role:
     *              one 'membership.access_mapped' AuditEvent (Id md5('aev:'||membership), Before/After scope), AccessScope -> 'AssignedBranches',
     *              AccessVersion + 1; and for those with Status 'Active' one primary Active assignment at the company's default branch
     *              (Id md5('uba:'||membership), StartAt = migration instant, AssignmentReason cites the owner decision, CreatedByUserId NULL).
     *              Suspended/Revoked memberships are re-scoped but get no assignment (reactivation must supply branches). Administrator
     *              memberships are untouched. The WHERE AccessScope = 'CompanyWide' guard plus ON CONFLICT DO NOTHING make a re-run a no-op.
     *   assert   : V1-V7 below run in the same transaction and RAISE on any violation.
     *
     * RELEASE NOTE: two new tables and one defaulted column, instant. After release non-Administrator users see only Main documents (silent
     * visibility change, risk R1); the owner grants CompanyWide to accountant/owner roles through the access API (O2). Old application
     * builds ignore the new objects but their middleware fails AssignedBranches closed (mapped users locked out): rolling back the
     * application alone is not safe - restore the pre-deploy snapshot with the previous build (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release (owner reviews the result - O2).
     *
     * -- P1 users per role (IsSystem) and membership status: who becomes branch-only
     * SELECT r."Name", r."IsSystem", m."Status", COUNT(*) FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" GROUP BY 1, 2, 3 ORDER BY 2 DESC, 1, 3;
     * -- P2 documents per branch (non-Main documents become invisible to mapped users)
     * SELECT 'SalesInvoices' t, "BranchId", COUNT(*) FROM "SalesInvoices" GROUP BY 2 UNION ALL SELECT 'PurchaseOrders', "BranchId", COUNT(*) FROM "PurchaseOrders" GROUP BY 2
     *   UNION ALL SELECT 'SalesReturns', "BranchId", COUNT(*) FROM "SalesReturns" GROUP BY 2 UNION ALL SELECT 'PurchaseReturns', "BranchId", COUNT(*) FROM "PurchaseReturns" GROUP BY 2
     *   UNION ALL SELECT 'StockAdjustments', "BranchId", COUNT(*) FROM "StockAdjustments" GROUP BY 2;
     * -- P3 record row counts of UserCompanyMemberships (must be unchanged after) and the stock reconciliation SQL (skill stock-ledger-invariants).
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying (and after services wrote rows); every query must return no rows. V1-V7 are also asserted inside Up.
     *
     * -- V1 Active AssignedBranches memberships without exactly one active primary
     * SELECT m."Id" FROM "UserCompanyMemberships" m WHERE m."Status" = 'Active' AND m."AccessScope" = 'AssignedBranches'
     *   AND (SELECT COUNT(*) FROM "UserBranchAssignments" a WHERE a."UserCompanyMembershipId" = m."Id" AND a."Status" = 'Active' AND a."IsPrimary") <> 1;
     * -- V2 Active memberships of non-system-role users still CompanyWide (Up only: owners may later regrant CompanyWide through the API)
     * SELECT m."Id" FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE m."Status" = 'Active' AND NOT r."IsSystem" AND m."AccessScope" = 'CompanyWide';
     * -- V3 memberships of system-role users not CompanyWide
     * SELECT m."Id" FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE r."IsSystem" AND m."AccessScope" <> 'CompanyWide';
     * -- V4 migration assignments whose branch is not the company default (Up only: later services may allocate other branches)
     * SELECT a."Id" FROM "UserBranchAssignments" a JOIN "Branches" b ON b."Id" = a."BranchId" WHERE a."CreatedByUserId" IS NULL AND NOT b."IsDefault";
     * -- V5 assignment count <> Active non-admin membership count (Up only; returns one row on mismatch)
     * SELECT 1 WHERE (SELECT COUNT(*) FROM "UserBranchAssignments" WHERE "CreatedByUserId" IS NULL) <> (SELECT COUNT(*) FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE m."Status" = 'Active' AND NOT r."IsSystem");
     * -- V6 companies with an Active non-admin membership but without an Active Administrator membership
     * SELECT c."Id" FROM "Companies" c WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND NOT r."IsSystem")
     *   AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND r."IsSystem" AND u."Status" = 'Active' AND m."AccessScope" = 'CompanyWide');
     * -- V7 migration audit rows <> mapped memberships (Up only; returns one row on mismatch)
     * SELECT 1 WHERE (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'membership.access_mapped') <> (SELECT COUNT(*) FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE NOT r."IsSystem");
     * -- V8 same-company integrity (also FK-enforced)
     * SELECT a."Id" FROM "UserBranchAssignments" a JOIN "Branches" b ON b."Id" = a."BranchId" WHERE b."CompanyId" <> a."CompanyId";
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G5 first: raises when any assignment has CreatedByUserId NOT NULL or Status 'Ended', or any AuditEvent has another
     * Action than membership.access_mapped - post-release data would be lost, restore the pre-deploy snapshot instead): restores
     * CompanyWide on the mapped memberships (reverses the backfill), then drops AuditEvents, UserBranchAssignments and AccessVersion.
     */
    /// <inheritdoc />
    public partial class AddUserBranchAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guards G1-G3 (before anything is written).
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE (SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault" AND b."IsActive") <> 1) THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: every company must have exactly one active default branch (G1)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" WHERE "AccessScope" = 'AssignedBranches') THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: an AssignedBranches membership already exists without assignments (G2)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                                             WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND NOT r."IsSystem")
                                 AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                                                 WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND r."IsSystem" AND u."Status" = 'Active')) THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: a company with branch-bound users has no active Administrator membership (G3)';
                    END IF;
                END $g$;
                """);

            // ===== 1. additive =====
            migrationBuilder.AddColumn<long>(
                name: "AccessVersion",
                table: "UserCompanyMemberships",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Before = table.Column<string>(type: "jsonb", nullable: true),
                    After = table.Column<string>(type: "jsonb", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.CheckConstraint("CK_AuditEvents_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_AuditEvents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Users_SubjectUserId",
                        column: x => x.SubjectUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserBranchAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserCompanyMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("PK_UserBranchAssignments", x => x.Id);
                    table.CheckConstraint("CK_UserBranchAssignments_Active_EndAt", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
                    table.CheckConstraint("CK_UserBranchAssignments_EndAt_After_StartAt", "\"EndAt\" IS NULL OR \"EndAt\" >= \"StartAt\"");
                    table.CheckConstraint("CK_UserBranchAssignments_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_UserBranchAssignments_Status", "\"Status\" IN ('Active', 'Ended')");
                    table.ForeignKey(
                        name: "FK_UserBranchAssignments_Branches_CompanyId_BranchId",
                        columns: x => new { x.CompanyId, x.BranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserBranchAssignments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserBranchAssignments_Memberships_Company",
                        columns: x => new { x.CompanyId, x.UserCompanyMembershipId },
                        principalTable: "UserCompanyMemberships",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserBranchAssignments_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserBranchAssignments_Users_EndedByUserId",
                        column: x => x.EndedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorUserId",
                table: "AuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Subject",
                table: "AuditEvents",
                columns: new[] { "CompanyId", "SubjectUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SubjectUserId",
                table: "AuditEvents",
                column: "SubjectUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Target",
                table: "AuditEvents",
                columns: new[] { "CompanyId", "TargetType", "TargetId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserBranchAssignments_Branch_Active",
                table: "UserBranchAssignments",
                columns: new[] { "CompanyId", "BranchId" },
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_UserBranchAssignments_CreatedByUserId",
                table: "UserBranchAssignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserBranchAssignments_EndedByUserId",
                table: "UserBranchAssignments",
                column: "EndedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserBranchAssignments_Membership_Branch_Active",
                table: "UserBranchAssignments",
                columns: new[] { "UserCompanyMembershipId", "BranchId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_UserBranchAssignments_Membership_History",
                table: "UserBranchAssignments",
                columns: new[] { "CompanyId", "UserCompanyMembershipId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserBranchAssignments_Membership_Primary",
                table: "UserBranchAssignments",
                column: "UserCompanyMembershipId",
                unique: true,
                filter: "\"Status\" = 'Active' AND \"IsPrimary\"");

            // ===== 2. backfill (Q7 documented default mapping, owner decision 2026-11-09) + 3. in-transaction assertions V1-V7 =====
            migrationBuilder.Sql("""
                -- 2a. one audit event per mapped membership (must read the still-CompanyWide rows, so before the UPDATE)
                INSERT INTO "AuditEvents" ("Id", "CompanyId", "OccurredAt", "ActorUserId", "Action", "TargetType", "TargetId", "SubjectUserId", "Reason", "Before", "After", "CorrelationId")
                SELECT md5('aev:' || m."Id"::text)::uuid, m."CompanyId", now(), NULL, 'membership.access_mapped', 'UserCompanyMembership', m."Id", m."UserId",
                       'Migration default mapping (owner decision Q7, 2026-11-09): non-administrator users are branch-restricted to the default branch',
                       '{"accessScope":"CompanyWide"}'::jsonb,
                       CASE WHEN m."Status" = 'Active'
                            THEN jsonb_build_object('accessScope', 'AssignedBranches', 'primaryBranchId', b."Id")
                            ELSE '{"accessScope":"AssignedBranches"}'::jsonb END,
                       NULL
                FROM "UserCompanyMemberships" m
                JOIN "Users" u ON u."Id" = m."UserId"
                JOIN "Roles" r ON r."Id" = u."RoleId"
                JOIN "Branches" b ON b."CompanyId" = m."CompanyId" AND b."IsDefault" AND b."IsActive"
                WHERE NOT r."IsSystem" AND m."AccessScope" = 'CompanyWide'
                ON CONFLICT DO NOTHING;

                -- 2b. one primary active assignment at the default branch per Active mapped membership
                INSERT INTO "UserBranchAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "BranchId", "IsPrimary", "Status", "StartAt", "EndAt", "AssignmentReason", "EndReason", "CreatedAt", "CreatedByUserId", "EndedByUserId")
                SELECT md5('uba:' || m."Id"::text)::uuid, m."CompanyId", m."Id", b."Id", true, 'Active', now(), NULL,
                       'Migration default mapping (owner decision Q7, 2026-11-09)', NULL, now(), NULL, NULL
                FROM "UserCompanyMemberships" m
                JOIN "Users" u ON u."Id" = m."UserId"
                JOIN "Roles" r ON r."Id" = u."RoleId"
                JOIN "Branches" b ON b."CompanyId" = m."CompanyId" AND b."IsDefault" AND b."IsActive"
                WHERE NOT r."IsSystem" AND m."AccessScope" = 'CompanyWide' AND m."Status" = 'Active'
                ON CONFLICT DO NOTHING;

                -- 2c. scope switch + invalidation stamp
                UPDATE "UserCompanyMemberships" m
                SET "AccessScope" = 'AssignedBranches', "AccessVersion" = m."AccessVersion" + 1, "UpdatedAt" = now()
                FROM "Users" u, "Roles" r
                WHERE u."Id" = m."UserId" AND r."Id" = u."RoleId" AND NOT r."IsSystem" AND m."AccessScope" = 'CompanyWide';

                -- 3. assertions (V1-V7)
                DO $s$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" m WHERE m."Status" = 'Active' AND m."AccessScope" = 'AssignedBranches'
                               AND (SELECT COUNT(*) FROM "UserBranchAssignments" a WHERE a."UserCompanyMembershipId" = m."Id" AND a."Status" = 'Active' AND a."IsPrimary") <> 1) THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: an Active AssignedBranches membership lacks exactly one active primary (V1)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                               WHERE m."Status" = 'Active' AND NOT r."IsSystem" AND m."AccessScope" = 'CompanyWide') THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: an Active non-administrator membership is still CompanyWide (V2)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                               WHERE r."IsSystem" AND m."AccessScope" <> 'CompanyWide') THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: an administrator membership is not CompanyWide (V3)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "UserBranchAssignments" a JOIN "Branches" b ON b."Id" = a."BranchId" WHERE NOT b."IsDefault") THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: a mapped assignment is not at the default branch (V4)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "UserBranchAssignments")
                       <> (SELECT COUNT(*) FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                           WHERE m."Status" = 'Active' AND NOT r."IsSystem") THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: assignment count differs from the Active non-administrator membership count (V5)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                                             WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND NOT r."IsSystem")
                                 AND NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId"
                                                 WHERE m."CompanyId" = c."Id" AND m."Status" = 'Active' AND r."IsSystem" AND u."Status" = 'Active' AND m."AccessScope" = 'CompanyWide')) THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: a company has no Active CompanyWide Administrator membership (V6)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'membership.access_mapped')
                       <> (SELECT COUNT(*) FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE NOT r."IsSystem") THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments: audit rows differ from the mapped membership count (V7)';
                    END IF;
                END $s$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G5: post-release allocation history / audit would be lost; restore the pre-deploy snapshot instead.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "UserBranchAssignments" WHERE "CreatedByUserId" IS NOT NULL OR "Status" = 'Ended') THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments Down: allocations were changed after the migration; restore the pre-deploy snapshot instead (D-G5).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "AuditEvents" WHERE "Action" <> 'membership.access_mapped') THEN
                        RAISE EXCEPTION 'AddUserBranchAssignments Down: audit events beyond the migration mapping exist; restore the pre-deploy snapshot instead (D-G5).';
                    END IF;
                END $g$;

                -- reverse the backfill: mapped memberships (those with a migration audit row) return to CompanyWide
                UPDATE "UserCompanyMemberships" m
                SET "AccessScope" = 'CompanyWide'
                WHERE m."AccessScope" = 'AssignedBranches'
                  AND EXISTS (SELECT 1 FROM "AuditEvents" e WHERE e."Action" = 'membership.access_mapped' AND e."TargetId" = m."Id");
                """);
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "UserBranchAssignments");

            migrationBuilder.DropColumn(
                name: "AccessVersion",
                table: "UserCompanyMemberships");
        }
    }
}
