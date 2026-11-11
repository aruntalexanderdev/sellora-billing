using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 1 / M1 - Companies, default tenant, UserCompanyMemberships (one migration, one transaction).
     *   additive : Companies (Id PK, Code unique, Name, IsActive, CreatedAt, CK_Companies_Id_NotEmpty: Guid.Empty is the "no
     *              company" sentinel of the query filter); UserCompanyMemberships (arch 6.1 columns, unique (UserId, CompanyId),
     *              AK (CompanyId, Id), CHECKs on Status / AccessScope); BusinessSettings.CompanyId and RefreshTokens.CompanyId
     *              as nullable columns (metadata-only in PG).
     *   guard    : G1 raises (and rolls back) when BusinessSettings has more than one row (P1): the singleton cannot be mapped to
     *              one default company.
     *   backfill : B1 the default company (Id 0c0a1f00-0000-4000-8000-000000000001, Code 'DEFAULT', Name = the business name or
     *              'Default Company'; inserted on empty databases too so seeding and tests always have the FK target; ON
     *              CONFLICT DO NOTHING). B2 BusinessSettings.CompanyId = default. B3 one Active / CompanyWide membership per
     *              existing user (CreatedAt = Users.CreatedAt, CreatedByUserId NULL: not fabricated; arch 17.3 step 6, ADR-11;
     *              Phase 5 re-maps non-admin users to branches). RefreshTokens.CompanyId stays NULL (refresh resolves the
     *              default membership). All statements are idempotent.
     *   assert   : memberships = users; BusinessSettings row count unchanged and all on the default company.
     *   enforce  : BusinessSettings.CompanyId NOT NULL, unique IX_BusinessSettings_CompanyId (1:1 profile), FKs to Companies
     *              (Restrict) from BusinessSettings and RefreshTokens.
     * Default tenant constants live in Sellora.Domain.Companies.DefaultTenant. Roles are NOT company-scoped in Phase 1 (design D4).
     *
     * RELEASE NOTE: tiny tables only (Users, BusinessSettings); SET NOT NULL scans BusinessSettings once. Startup auto-migrate
     * must stay off in production (D10). Order: 1. snapshot / branch; 2. P-block (P1 <= 1); 3. idempotent script M1-M4;
     * 4. V-blocks; 5. deploy API; 6. smoke test. Full P/V catalogue: docs/architecture/multi-company/phase-1-design.md D5.
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P0 volume
     * SELECT (SELECT COUNT(*) FROM "Users") AS users, (SELECT COUNT(*) FROM "BusinessSettings") AS settings;
     * -- P1 singleton settings (must be 0 or 1)
     * SELECT COUNT(*) FROM "BusinessSettings";
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying. V1 equals P0; the rest must return no rows.
     *
     * -- V1 volume (same as P0)
     * SELECT (SELECT COUNT(*) FROM "Users") AS users, (SELECT COUNT(*) FROM "BusinessSettings") AS settings;
     * -- V2 exactly the default company exists (Phase 1)
     * SELECT "Id" FROM "Companies" WHERE "Id" <> '0c0a1f00-0000-4000-8000-000000000001';
     * SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM "Companies" WHERE "Id" = '0c0a1f00-0000-4000-8000-000000000001' AND "Code" = 'DEFAULT');
     * -- V3 users without an Active membership in the default company
     * SELECT u."Id" FROM "Users" u WHERE NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m
     *   WHERE m."UserId" = u."Id" AND m."CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' AND m."Status" = 'Active');
     * -- V4 settings not on the default company
     * SELECT "Id" FROM "BusinessSettings" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001';
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G first: raises when a second company, or any membership / settings row of another company, exists - a
     * second tenant cannot be folded back): drops the FKs, indexes and both CompanyId columns, then UserCompanyMemberships
     * and Companies. The backfilled memberships and the default company are derived data; Up recreates them.
     */
    /// <inheritdoc />
    public partial class AddCompaniesAndMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 0. guard G1 =====
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF (SELECT COUNT(*) FROM "BusinessSettings") > 1 THEN
                        RAISE EXCEPTION 'AddCompaniesAndMemberships: BusinessSettings has more than one row (run P1).';
                    END IF;
                END $g$;
                """);

            // ===== 1. additive =====
            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.CheckConstraint("CK_Companies_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "UserCompanyMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccessScope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCompanyMemberships", x => x.Id);
                    table.UniqueConstraint("AK_UserCompanyMemberships_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_UserCompanyMemberships_AccessScope", "\"AccessScope\" IN ('CompanyWide', 'AssignedBranches')");
                    table.CheckConstraint("CK_UserCompanyMemberships_Status", "\"Status\" IN ('Active', 'Suspended', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "BusinessSettings", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "RefreshTokens", type: "uuid", nullable: true);

            // ===== 2. backfill + assertions =====
            // The temp table is ON COMMIT DROP, so it vanishes with the migration transaction.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE m1_count ON COMMIT DROP AS
                SELECT (SELECT COUNT(*) FROM "Users") AS users, (SELECT COUNT(*) FROM "BusinessSettings") AS settings;

                -- B1 default company (also on an empty database)
                INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt")
                VALUES ('0c0a1f00-0000-4000-8000-000000000001', 'DEFAULT',
                        COALESCE((SELECT "BusinessName" FROM "BusinessSettings" LIMIT 1), 'Default Company'), TRUE, now())
                ON CONFLICT DO NOTHING;

                -- B2 the settings singleton becomes the default company's profile
                UPDATE "BusinessSettings" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;

                -- B3 every existing user is a CompanyWide member of the default company (ADR-11)
                INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt")
                SELECT md5('membership:' || u."Id"::text || ':0c0a1f00-0000-4000-8000-000000000001')::uuid, u."Id",
                       '0c0a1f00-0000-4000-8000-000000000001', 'Active', 'CompanyWide', u."CreatedAt"
                FROM "Users" u
                WHERE NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m
                                  WHERE m."UserId" = u."Id" AND m."CompanyId" = '0c0a1f00-0000-4000-8000-000000000001');

                DO $a$
                BEGIN
                    IF (SELECT COUNT(*) FROM "UserCompanyMemberships") <> (SELECT users FROM m1_count) THEN
                        RAISE EXCEPTION 'AddCompaniesAndMemberships: memberships <> users (V3)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Users" u WHERE NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m
                               WHERE m."UserId" = u."Id" AND m."CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' AND m."Status" = 'Active')) THEN
                        RAISE EXCEPTION 'AddCompaniesAndMemberships: a user has no Active default membership (V3)';
                    END IF;
                    IF (SELECT COUNT(*) FROM "BusinessSettings") <> (SELECT settings FROM m1_count) THEN
                        RAISE EXCEPTION 'AddCompaniesAndMemberships: BusinessSettings row count changed (V1)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "BusinessSettings" WHERE "CompanyId" IS NULL OR "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001') THEN
                        RAISE EXCEPTION 'AddCompaniesAndMemberships: BusinessSettings not on the default company (V4)';
                    END IF;
                END $a$;
                """);

            // ===== 3. enforce =====
            migrationBuilder.AlterColumn<Guid>(
                name: "CompanyId",
                table: "BusinessSettings",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(name: "IX_BusinessSettings_CompanyId", table: "BusinessSettings", column: "CompanyId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_RefreshTokens_CompanyId", table: "RefreshTokens", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_Companies_Code", table: "Companies", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_UserCompanyMemberships_CompanyId", table: "UserCompanyMemberships", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_UserCompanyMemberships_CreatedByUserId", table: "UserCompanyMemberships", column: "CreatedByUserId");
            migrationBuilder.CreateIndex(name: "IX_UserCompanyMemberships_UpdatedByUserId", table: "UserCompanyMemberships", column: "UpdatedByUserId");
            migrationBuilder.CreateIndex(name: "IX_UserCompanyMemberships_UserId_CompanyId", table: "UserCompanyMemberships", columns: new[] { "UserId", "CompanyId" }, unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BusinessSettings_Companies_CompanyId",
                table: "BusinessSettings",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RefreshTokens_Companies_CompanyId",
                table: "RefreshTokens",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G: a second tenant cannot be folded back into the single-tenant schema.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF (SELECT COUNT(*) FROM "Companies") > 1
                       OR EXISTS (SELECT 1 FROM "UserCompanyMemberships" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "BusinessSettings" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001') THEN
                        RAISE EXCEPTION 'AddCompaniesAndMemberships Down: more than one company exists; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropForeignKey(name: "FK_BusinessSettings_Companies_CompanyId", table: "BusinessSettings");
            migrationBuilder.DropForeignKey(name: "FK_RefreshTokens_Companies_CompanyId", table: "RefreshTokens");

            migrationBuilder.DropTable(name: "UserCompanyMemberships");
            migrationBuilder.DropTable(name: "Companies");

            migrationBuilder.DropIndex(name: "IX_RefreshTokens_CompanyId", table: "RefreshTokens");
            migrationBuilder.DropIndex(name: "IX_BusinessSettings_CompanyId", table: "BusinessSettings");

            migrationBuilder.DropColumn(name: "CompanyId", table: "RefreshTokens");
            migrationBuilder.DropColumn(name: "CompanyId", table: "BusinessSettings");
        }
    }
}
