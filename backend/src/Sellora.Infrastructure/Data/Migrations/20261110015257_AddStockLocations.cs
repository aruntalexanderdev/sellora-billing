using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 3 - Stock locations (D1-D6): company-owned StockLocations table, one BranchStore per branch, one
     * Transit per company (one migration, one transaction).
     *   guard    : G5 (before anything is written) raises when a company has no exactly-one default branch.
     *   additive : StockLocations (Id PK, AK (CompanyId, Id) = Phase 8 composite FK target, CompanyId FK -> Companies, BranchId NULL
     *              with navigation-less composite FK (CompanyId, BranchId) -> Branches, LocationType varchar(20) string enum,
     *              Code varchar(20), Name, IsActive, audit). Unique (CompanyId, Code); partial uniques: one BranchStore per branch,
     *              one Transit per company. CHECKs: LocationType in the 5 values, Type_Branch (store/warehouse <=> BranchId set),
     *              Transit_Active, Id_NotEmpty.
     *   backfill : BranchStore per branch (default branch -> reserved Id 0c0a1f00-...-0003, else md5('location:store:' || BranchId)),
     *              Code/Name/IsActive mirror the branch; Transit per company (default company -> ...-0004, else
     *              md5('location:transit:' || CompanyId)), Code 'TRANSIT'. ON CONFLICT DO NOTHING = idempotent; runs on empty DBs too.
     *   assert   : V1-V3 empty in-transaction; the default company's reserved ...0003 / ...0004 exist.
     *
     * RELEASE NOTE: new table only, instant. Old application builds ignore it. Production must also add the
     * StockLocations.View permission row (seeding runs only where startup seeding runs). No stock table is touched.
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P1 branches (expect 1)       SELECT COUNT(*) FROM "Branches";
     * -- P2 companies (expect 1)      SELECT COUNT(*) FROM "Companies";
     * -- P3 stock reconciliation SQL (skill stock-ledger-invariants): record results, must be identical after.
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; every query must return no rows.
     *
     * -- V1 every branch has exactly one matching store
     * SELECT b."Id" FROM "Branches" b WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore' AND l."CompanyId" = b."CompanyId" AND l."IsActive" = b."IsActive" AND l."Code" = b."Code") <> 1;
     * -- V2 every company has exactly one active Transit
     * SELECT c."Id" FROM "Companies" c WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit' AND l."IsActive") <> 1;
     * -- V3 location and branch belong to the same company
     * SELECT l."Id" FROM "StockLocations" l JOIN "Branches" b ON b."Id" = l."BranchId" WHERE b."CompanyId" <> l."CompanyId";
     * -- V4 Phase 3 creates only these two types
     * SELECT "Id" FROM "StockLocations" WHERE "LocationType" NOT IN ('BranchStore', 'Transit');
     * -- V5 regression: stock reconciliation SQL unchanged vs P3.
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G3 first: raises when a location of another type exists - Phase 4 data; restore the snapshot instead):
     * drops StockLocations. All rows are derived from branches/companies, so Up rebuilds them.
     */
    /// <inheritdoc />
    public partial class AddStockLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard G5: every company must have exactly one default branch before locations are derived from branches.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE (SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault") <> 1) THEN
                        RAISE EXCEPTION 'AddStockLocations: every company must have exactly one default branch (G5)';
                    END IF;
                END $g$;
                """);

            // ===== 1. additive =====
            migrationBuilder.CreateTable(
                name: "StockLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLocations", x => x.Id);
                    table.UniqueConstraint("AK_StockLocations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_StockLocations_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_StockLocations_LocationType", "\"LocationType\" IN ('BranchStore','BranchWarehouse','CompanyWarehouse','Vehicle','Transit')");
                    table.CheckConstraint("CK_StockLocations_Transit_Active", "\"LocationType\" <> 'Transit' OR \"IsActive\"");
                    table.CheckConstraint("CK_StockLocations_Type_Branch", "(\"LocationType\" IN ('BranchStore','BranchWarehouse')) = (\"BranchId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_StockLocations_Branches_CompanyId_BranchId",
                        columns: x => new { x.CompanyId, x.BranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLocations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLocations_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLocations_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockLocations_BranchId_BranchStore",
                table: "StockLocations",
                column: "BranchId",
                unique: true,
                filter: "\"LocationType\" = 'BranchStore'");

            migrationBuilder.CreateIndex(
                name: "IX_StockLocations_CompanyId_BranchId",
                table: "StockLocations",
                columns: new[] { "CompanyId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLocations_CompanyId_Code",
                table: "StockLocations",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockLocations_CompanyId_Transit",
                table: "StockLocations",
                column: "CompanyId",
                unique: true,
                filter: "\"LocationType\" = 'Transit'");

            migrationBuilder.CreateIndex(
                name: "IX_StockLocations_CreatedByUserId",
                table: "StockLocations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLocations_UpdatedByUserId",
                table: "StockLocations",
                column: "UpdatedByUserId");

            // ===== 2. backfill + assertions =====
            migrationBuilder.Sql("""
                INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt")
                SELECT CASE WHEN b."Id" = '0c0a1f00-0000-4000-8000-000000000002'
                            THEN '0c0a1f00-0000-4000-8000-000000000003'::uuid
                            ELSE md5('location:store:' || b."Id"::text)::uuid END,
                       b."CompanyId", b."Id", 'BranchStore', b."Code", b."Name", b."IsActive", now()
                FROM "Branches" b
                ON CONFLICT DO NOTHING;

                INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt")
                SELECT CASE WHEN c."Id" = '0c0a1f00-0000-4000-8000-000000000001'
                            THEN '0c0a1f00-0000-4000-8000-000000000004'::uuid
                            ELSE md5('location:transit:' || c."Id"::text)::uuid END,
                       c."Id", NULL, 'Transit', 'TRANSIT', 'In transit', true, now()
                FROM "Companies" c
                ON CONFLICT DO NOTHING;

                DO $s$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Branches" b
                               WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore'
                                      AND l."CompanyId" = b."CompanyId" AND l."IsActive" = b."IsActive" AND l."Code" = b."Code") <> 1) THEN
                        RAISE EXCEPTION 'AddStockLocations: every branch must have exactly one matching BranchStore (V1)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" c
                               WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit' AND l."IsActive") <> 1) THEN
                        RAISE EXCEPTION 'AddStockLocations: every company must have exactly one active Transit (V2)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockLocations" l JOIN "Branches" b ON b."Id" = l."BranchId" WHERE b."CompanyId" <> l."CompanyId") THEN
                        RAISE EXCEPTION 'AddStockLocations: location and branch must belong to the same company (V3)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Companies" WHERE "Id" = '0c0a1f00-0000-4000-8000-000000000001')
                       AND NOT (EXISTS (SELECT 1 FROM "StockLocations" WHERE "Id" = '0c0a1f00-0000-4000-8000-000000000003'
                                        AND "BranchId" = '0c0a1f00-0000-4000-8000-000000000002' AND "LocationType" = 'BranchStore')
                                AND EXISTS (SELECT 1 FROM "StockLocations" WHERE "Id" = '0c0a1f00-0000-4000-8000-000000000004'
                                            AND "LocationType" = 'Transit')) THEN
                        RAISE EXCEPTION 'AddStockLocations: the default company must own the reserved Main store and Transit';
                    END IF;
                END $s$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G3: locations of other types (Phase 4 warehouses/vehicles) cannot be folded back.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "StockLocations" WHERE "LocationType" NOT IN ('BranchStore', 'Transit')) THEN
                        RAISE EXCEPTION 'AddStockLocations Down: locations of another type exist; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropTable(
                name: "StockLocations");
        }
    }
}
