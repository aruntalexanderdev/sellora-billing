using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 4 - Warehouses and vehicles (D1-D4): subtype tables 1:1 with a StockLocation (one migration, one transaction).
     *   guard    : G6 (before anything is written) raises when a StockLocations row of type CompanyWarehouse/BranchWarehouse/Vehicle
     *              already exists (Phase 3 creates none; such a row would be an orphan subtype).
     *   additive : AK_StockLocations_CompanyId_Id_LocationType on StockLocations (target of the subtype FKs).
     *              Warehouses (Id PK, CompanyId, StockLocationId, LocationType, audit) and Vehicles (Id PK, CompanyId, StockLocationId,
     *              LocationType, VehicleNumber, VehicleName, VehicleType, AssignedBranchId NULL, IsActive, audit).
     *              Composite navigation-less FKs (CompanyId, StockLocationId, LocationType) -> the AK, Restrict; the FK columns carry a
     *              UNIQUE index (IX_Warehouses_StockLocation / IX_Vehicles_StockLocation) = 1:1 with the location.
     *              Vehicles (CompanyId, AssignedBranchId) -> Branches (CompanyId, Id), MATCH SIMPLE. Unique (CompanyId, VehicleNumber).
     *              CHECKs: CK_Warehouses_LocationType, CK_Vehicles_LocationType, CK_Vehicles_VehicleType, Id_NotEmpty x2.
     *   backfill : none (no existing data). Code/Name/IsActive mirroring and name uniqueness per company are service rules.
     *
     * RELEASE NOTE: new tables plus one unique constraint on a 2-row table, instant. Old application builds ignore them. Production must
     * add the 8 Company.Warehouses.* / Company.Vehicles.* permission rows after deploy (scripts/sql/seed-permissions.sql). No stock table is touched.
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P1 locations by type (expect BranchStore = branches, Transit = companies, nothing else)
     * SELECT "LocationType", COUNT(*) FROM "StockLocations" GROUP BY 1;
     * -- P2 stock reconciliation SQL (skill stock-ledger-invariants): record results, must be identical after.
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying (and after services wrote rows, for V1-V4); every query must return no rows.
     *
     * -- V1 every warehouse location has exactly one Warehouses row
     * SELECT l."Id" FROM "StockLocations" l WHERE l."LocationType" IN ('CompanyWarehouse', 'BranchWarehouse') AND (SELECT COUNT(*) FROM "Warehouses" w WHERE w."StockLocationId" = l."Id") <> 1;
     * -- V2 every vehicle location has exactly one Vehicles row
     * SELECT l."Id" FROM "StockLocations" l WHERE l."LocationType" = 'Vehicle' AND (SELECT COUNT(*) FROM "Vehicles" v WHERE v."StockLocationId" = l."Id") <> 1;
     * -- V3 vehicle and location agree (code, name, status; vehicle locations carry no branch)
     * SELECT v."Id" FROM "Vehicles" v JOIN "StockLocations" l ON l."Id" = v."StockLocationId" WHERE l."Code" <> v."VehicleNumber" OR l."Name" <> v."VehicleName" OR l."IsActive" <> v."IsActive" OR l."BranchId" IS NOT NULL;
     * -- V4 assigned branch belongs to the vehicle's company
     * SELECT v."Id" FROM "Vehicles" v JOIN "Branches" b ON b."Id" = v."AssignedBranchId" WHERE b."CompanyId" <> v."CompanyId";
     * -- V5 permission rows (after the release step adds them)
     * SELECT c FROM unnest(ARRAY['Company.Warehouses.View','Company.Warehouses.Create','Company.Warehouses.Edit','Company.Warehouses.Disable','Company.Vehicles.View','Company.Vehicles.Create','Company.Vehicles.Edit','Company.Vehicles.Disable']) c WHERE c NOT IN (SELECT "Code" FROM "Permissions");
     * -- V6 regression: Phase 3 V1-V3 and the stock reconciliation SQL unchanged vs P2.
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G4 first: raises when Warehouses or Vehicles hold any row - restore the pre-deploy snapshot instead):
     * drops Vehicles, Warehouses and the StockLocations alternate key.
     */
    /// <inheritdoc />
    public partial class AddWarehousesAndVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard G6: Phase 3 creates no warehouse/vehicle locations; one found now would be an orphan subtype.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "StockLocations" WHERE "LocationType" IN ('CompanyWarehouse', 'BranchWarehouse', 'Vehicle')) THEN
                        RAISE EXCEPTION 'AddWarehousesAndVehicles: warehouse/vehicle locations already exist without subtype rows (G6)';
                    END IF;
                END $g$;
                """);

            // ===== 1. additive =====
            migrationBuilder.AddUniqueConstraint(
                name: "AK_StockLocations_CompanyId_Id_LocationType",
                table: "StockLocations",
                columns: new[] { "CompanyId", "Id", "LocationType" });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StockLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VehicleNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VehicleName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    VehicleType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssignedBranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.CheckConstraint("CK_Vehicles_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_Vehicles_LocationType", "\"LocationType\" = 'Vehicle'");
                    table.CheckConstraint("CK_Vehicles_VehicleType", "\"VehicleType\" IN ('Van','Truck','Car','Motorcycle','ThreeWheeler','Other')");
                    table.ForeignKey(
                        name: "FK_Vehicles_Branches_CompanyId_AssignedBranchId",
                        columns: x => new { x.CompanyId, x.AssignedBranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vehicles_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vehicles_StockLocations_Typed",
                        columns: x => new { x.CompanyId, x.StockLocationId, x.LocationType },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id", "LocationType" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vehicles_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vehicles_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StockLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.Id);
                    table.CheckConstraint("CK_Warehouses_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_Warehouses_LocationType", "\"LocationType\" IN ('CompanyWarehouse','BranchWarehouse')");
                    table.ForeignKey(
                        name: "FK_Warehouses_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Warehouses_StockLocations_Typed",
                        columns: x => new { x.CompanyId, x.StockLocationId, x.LocationType },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id", "LocationType" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Warehouses_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Warehouses_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_CompanyId_AssignedBranchId",
                table: "Vehicles",
                columns: new[] { "CompanyId", "AssignedBranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_CompanyId_VehicleNumber",
                table: "Vehicles",
                columns: new[] { "CompanyId", "VehicleNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_CreatedByUserId",
                table: "Vehicles",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_StockLocation",
                table: "Vehicles",
                columns: new[] { "CompanyId", "StockLocationId", "LocationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_UpdatedByUserId",
                table: "Vehicles",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_CreatedByUserId",
                table: "Warehouses",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_StockLocation",
                table: "Warehouses",
                columns: new[] { "CompanyId", "StockLocationId", "LocationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_UpdatedByUserId",
                table: "Warehouses",
                column: "UpdatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G4: warehouse/vehicle rows cannot be folded back.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Warehouses") OR EXISTS (SELECT 1 FROM "Vehicles") THEN
                        RAISE EXCEPTION 'AddWarehousesAndVehicles Down: warehouses or vehicles exist; restore the pre-deploy snapshot instead (D-G4).';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "Warehouses");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_StockLocations_CompanyId_Id_LocationType",
                table: "StockLocations");
        }
    }
}
