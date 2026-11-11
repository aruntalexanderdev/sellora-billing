using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 2 / B1 - Branches (D1, D2): company-owned branch table plus one default "Main" branch per company
     * (one migration, one transaction).
     *   additive : Branches (Id PK, AK (CompanyId, Id) = target of the composite same-company FKs, CompanyId FK -> Companies,
     *              Code varchar(6) immutable numbering segment, Name, address/GSTIN/InvoicePrefix, IsDefault, IsActive,
     *              audit columns). Uniques (CompanyId, Code), (CompanyId, Name), and (CompanyId) WHERE IsDefault = one default per
     *              company. CK_Branches_Default_Active (default => active), CK_Branches_Id_NotEmpty.
     *   backfill : one Main per company, IsDefault, IsActive; default company -> reserved Id 0c0a1f00-...-0002, any other company
     *              -> md5('branch:' || CompanyId)::uuid; address/GSTIN/prefix stay NULL (company profile is the fallback).
     *              ON CONFLICT DO NOTHING makes it idempotent. Inserted on empty databases too (FK target for seed/tests).
     *   assert   : every company has exactly one default active branch; the default company's Main is the reserved Id (V4).
     *
     * RELEASE NOTE: new table only, instant. Old application builds ignore it.
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P2 companies (expect 1)
     * SELECT COUNT(*) FROM "Companies";
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; V4 must return no rows.
     *
     * -- V4 every company has exactly one default active branch
     * SELECT "Id" FROM "Companies" c WHERE (SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault" AND b."IsActive") <> 1;
     * -- V4b the default company's Main
     * SELECT * FROM "Branches" WHERE "Id" = '0c0a1f00-0000-4000-8000-000000000002';
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G2 first: raises when any company has more than one branch): drops Branches. Rows of the later migrations
     * reference it, so B3 and B2 are rolled back first (EF does this in order).
     */
    /// <inheritdoc />
    public partial class AddBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 1. additive =====
            migrationBuilder.CreateTable(
                name: "Branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    AddressLine = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    City = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    State = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Gstin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    InvoicePrefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.Id);
                    table.UniqueConstraint("AK_Branches_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Branches_Default_Active", "\"IsActive\" OR NOT \"IsDefault\"");
                    table.CheckConstraint("CK_Branches_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_Branches_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Branches_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Branches_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Branches_CompanyId_Code",
                table: "Branches",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Branches_CompanyId_IsDefault",
                table: "Branches",
                column: "CompanyId",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_CompanyId_Name",
                table: "Branches",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Branches_CreatedByUserId",
                table: "Branches",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_UpdatedByUserId",
                table: "Branches",
                column: "UpdatedByUserId");
            // ===== 2. backfill + assertions: one Main per company =====
            migrationBuilder.Sql("""
                INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt")
                SELECT CASE WHEN c."Id" = '0c0a1f00-0000-4000-8000-000000000001'
                            THEN '0c0a1f00-0000-4000-8000-000000000002'::uuid
                            ELSE md5('branch:' || c."Id"::text)::uuid END,
                       c."Id", 'MAIN', 'Main', true, true, now()
                FROM "Companies" c
                ON CONFLICT DO NOTHING;

                DO $s$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE (SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault" AND b."IsActive") <> 1) THEN
                        RAISE EXCEPTION 'AddBranches: every company must have exactly one default active branch (V4)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" WHERE "Id" = '0c0a1f00-0000-4000-8000-000000000001')
                       AND NOT EXISTS (SELECT 1 FROM "Branches" WHERE "Id" = '0c0a1f00-0000-4000-8000-000000000002'
                                       AND "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' AND "IsDefault") THEN
                        RAISE EXCEPTION 'AddBranches: the default company must own the reserved Main branch (V4b)';
                    END IF;
                END $s$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G2: extra branches cannot be folded back into the single-branch schema.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Branches" GROUP BY "CompanyId" HAVING COUNT(*) > 1) THEN
                        RAISE EXCEPTION 'AddBranches Down: a company has more than one branch; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropTable(
                name: "Branches");
        }
    }
}
