using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShipmentsConsignmentsAndReservations : Migration
    {
        // Phase 04 (FI-D4, FI-D5, FI-D29..D31). Purely additive: five new tables, StockItems.ReservedQuantity (NOT NULL DEFAULT 0, so existing rows reconcile with zero
        // reservations), AK_Vehicles_CompanyId_Id (unique over an already-unique Id, cannot fail) and four permission rows. No existing row is rewritten.
        // Validation (each must return 0 / no rows right after Up; the same queries live in scripts/sql/stock-reservations-reconciliation.sql):
        //   SELECT COUNT(*) FROM "StockItems" WHERE "ReservedQuantity" <> 0;
        //   SELECT COUNT(*) FROM "Shipments"; SELECT COUNT(*) FROM "Consignments"; SELECT COUNT(*) FROM "ConsignmentLines"; SELECT COUNT(*) FROM "StockReservations";
        //   SELECT s."StockLocationId", s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "SourceLocationId", "ProductId", SUM("BaseQty") q FROM "StockReservations" WHERE "Status" = 'Active' GROUP BY 1, 2) r
        //     ON r."SourceLocationId" = s."StockLocationId" AND r."ProductId" = s."ProductId" WHERE s."ReservedQuantity" <> COALESCE(r.q, 0);   -- FI-D5 reconciliation, forever empty
        //   SELECT "Code" FROM "Permissions" WHERE "Code" IN ('Shipments.View', 'Shipments.Plan', 'Shipments.Load', 'Reservations.Release');   -- 4 rows
        // Down refuses while any shipment/consignment/reservation row exists or any ReservedQuantity > 0 (dropping would lose planning data); otherwise it reverses everything.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReservedQuantity",
                table: "StockItems",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Vehicles_CompanyId_Id",
                table: "Vehicles",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransportMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransportVia = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ProviderContact = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    TransportReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PlannedDispatchDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.UniqueConstraint("AK_Shipments_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Shipments_Provider_Required", "\"TransportVia\" <> 'ExternalProvider' OR COALESCE(char_length(\"ProviderName\"), 0) > 0");
                    table.CheckConstraint("CK_Shipments_Status_Cancellation", "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL) AND (\"CancellationReason\" IS NULL OR \"Status\" = 'Cancelled')");
                    table.CheckConstraint("CK_Shipments_Status_Valid", "\"Status\" IN ('Draft', 'Loading', 'ReadyToDispatch', 'Cancelled')");
                    table.CheckConstraint("CK_Shipments_TransportMode_Valid", "\"TransportMode\" IN ('Road', 'Air', 'Sea', 'Rail', 'Courier', 'HandCarry')");
                    table.CheckConstraint("CK_Shipments_TransportVia_Valid", "\"TransportVia\" IN ('OwnFleet', 'ExternalProvider')");
                    table.ForeignKey(
                        name: "FK_Shipments_Branches_BranchId",
                        columns: x => new { x.CompanyId, x.BranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_StockLocations_Source",
                        columns: x => new { x.CompanyId, x.SourceLocationId },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_Users_CancelledBy",
                        column: x => x.CancelledByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_Users_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_Users_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentVehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalVehicleNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ExternalDescription = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DriverName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DriverPhone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentVehicles", x => x.Id);
                    table.UniqueConstraint("AK_ShipmentVehicles_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_ShipmentVehicles_ShipmentId_Id", x => new { x.ShipmentId, x.Id });
                    table.CheckConstraint("CK_ShipmentVehicles_Sequence_Positive", "\"Sequence\" > 0");
                    table.CheckConstraint("CK_ShipmentVehicles_Vehicle_Xor_External", "(\"VehicleId\" IS NULL) <> (\"ExternalVehicleNumber\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_ShipmentVehicles_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentVehicles_Shipments",
                        columns: x => new { x.CompanyId, x.ShipmentId },
                        principalTable: "Shipments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentVehicles_Vehicles",
                        columns: x => new { x.CompanyId, x.VehicleId },
                        principalTable: "Vehicles",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Consignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsignmentNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DestinationLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationBranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    ShipmentVehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LoadedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Consignments", x => x.Id);
                    table.UniqueConstraint("AK_Consignments_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Consignments_Status_Cancellation", "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL) AND (\"CancellationReason\" IS NULL OR \"Status\" = 'Cancelled')");
                    table.CheckConstraint("CK_Consignments_Status_Loaded", "(\"Status\" <> 'Loaded' OR \"LoadedAt\" IS NOT NULL) AND (\"Status\" <> 'Planned' OR \"LoadedAt\" IS NULL) AND (\"LoadedAt\" IS NULL) = (\"LoadedByUserId\" IS NULL)");
                    table.CheckConstraint("CK_Consignments_Status_Valid", "\"Status\" IN ('Planned', 'Loaded', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_Consignments_Branches_DestinationBranchId",
                        columns: x => new { x.CompanyId, x.DestinationBranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_ShipmentVehicles_SameShipment",
                        columns: x => new { x.ShipmentId, x.ShipmentVehicleId },
                        principalTable: "ShipmentVehicles",
                        principalColumns: new[] { "ShipmentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_Shipments",
                        columns: x => new { x.CompanyId, x.ShipmentId },
                        principalTable: "Shipments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_StockLocations_Destination",
                        columns: x => new { x.CompanyId, x.DestinationLocationId },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_Users_CancelledBy",
                        column: x => x.CancelledByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_Users_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_Users_LoadedBy",
                        column: x => x.LoadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Consignments_Users_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsignmentLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StockTransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    StockTransferLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ConversionFactorToBase = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    EnteredQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    AllocatedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    LoadedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false, defaultValue: 0m),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsignmentLines", x => x.Id);
                    table.UniqueConstraint("AK_ConsignmentLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ConsignmentLines_Allocated_Arithmetic", "\"EnteredQty\" >= 0 AND \"EnteredQty\" * \"ConversionFactorToBase\" = \"AllocatedBaseQty\"");
                    table.CheckConstraint("CK_ConsignmentLines_Allocated_Positive", "\"Status\" <> 'Active' OR \"AllocatedBaseQty\" > 0");
                    table.CheckConstraint("CK_ConsignmentLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
                    table.CheckConstraint("CK_ConsignmentLines_Loaded_Valid", "\"LoadedBaseQty\" >= 0 AND \"LoadedBaseQty\" <= \"AllocatedBaseQty\"");
                    table.CheckConstraint("CK_ConsignmentLines_Status_Cancellation", "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_ConsignmentLines_Status_Valid", "\"Status\" IN ('Active', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_ConsignmentLines_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentLines_Consignments",
                        columns: x => new { x.CompanyId, x.ConsignmentId },
                        principalTable: "Consignments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentLines_ProductUnits",
                        columns: x => new { x.ProductId, x.ProductUnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentLines_Products",
                        columns: x => new { x.CompanyId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentLines_StockTransferLines",
                        columns: x => new { x.CompanyId, x.StockTransferLineId },
                        principalTable: "StockTransferLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentLines_StockTransfers",
                        columns: x => new { x.CompanyId, x.StockTransferId },
                        principalTable: "StockTransfers",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentLines_Units",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    ConsignmentLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservations", x => x.Id);
                    table.CheckConstraint("CK_StockReservations_BaseQty_Positive", "\"BaseQty\" > 0");
                    table.CheckConstraint("CK_StockReservations_Status_Consumed", "(\"Status\" = 'Consumed') = (\"ConsumedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_StockReservations_Status_Released", "(\"Status\" = 'Released') = (\"ReleasedAt\" IS NOT NULL) AND (\"ReleasedAt\" IS NULL) = (\"ReleasedByUserId\" IS NULL) AND (\"ReleaseReason\" IS NULL OR \"Status\" = 'Released')");
                    table.CheckConstraint("CK_StockReservations_Status_Valid", "\"Status\" IN ('Active', 'Released', 'Consumed')");
                    table.ForeignKey(
                        name: "FK_StockReservations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockReservations_ConsignmentLines",
                        columns: x => new { x.CompanyId, x.ConsignmentLineId },
                        principalTable: "ConsignmentLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockReservations_Products",
                        columns: x => new { x.CompanyId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockReservations_StockItems",
                        columns: x => new { x.SourceLocationId, x.ProductId },
                        principalTable: "StockItems",
                        principalColumns: new[] { "StockLocationId", "ProductId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockReservations_StockLocations_Source",
                        columns: x => new { x.CompanyId, x.SourceLocationId },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockReservations_Users_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockReservations_Users_ReleasedBy",
                        column: x => x.ReleasedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockItems_Reserved_Valid",
                table: "StockItems",
                sql: "\"ReservedQuantity\" >= 0 AND \"ReservedQuantity\" <= \"QuantityOnHand\"");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentLines_CompanyId_ConsignmentId",
                table: "ConsignmentLines",
                columns: new[] { "CompanyId", "ConsignmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentLines_CompanyId_ProductId",
                table: "ConsignmentLines",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentLines_CompanyId_StockTransferId",
                table: "ConsignmentLines",
                columns: new[] { "CompanyId", "StockTransferId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentLines_CompanyId_StockTransferLineId",
                table: "ConsignmentLines",
                columns: new[] { "CompanyId", "StockTransferLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentLines_ConsignmentId_TransferLine_Active",
                table: "ConsignmentLines",
                columns: new[] { "ConsignmentId", "StockTransferLineId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentLines_ProductId_ProductUnitId",
                table: "ConsignmentLines",
                columns: new[] { "ProductId", "ProductUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentLines_UnitId",
                table: "ConsignmentLines",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_CancelledByUserId",
                table: "Consignments",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_CompanyId_ConsignmentNo",
                table: "Consignments",
                columns: new[] { "CompanyId", "ConsignmentNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_CompanyId_DestinationBranchId",
                table: "Consignments",
                columns: new[] { "CompanyId", "DestinationBranchId" },
                filter: "\"DestinationBranchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_CompanyId_DestinationLocationId",
                table: "Consignments",
                columns: new[] { "CompanyId", "DestinationLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_CompanyId_ShipmentId",
                table: "Consignments",
                columns: new[] { "CompanyId", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_CreatedByUserId",
                table: "Consignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_LoadedByUserId",
                table: "Consignments",
                column: "LoadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_ShipmentId_Destination_Active",
                table: "Consignments",
                columns: new[] { "ShipmentId", "DestinationLocationId" },
                unique: true,
                filter: "\"Status\" <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_ShipmentId_ShipmentVehicleId",
                table: "Consignments",
                columns: new[] { "ShipmentId", "ShipmentVehicleId" },
                filter: "\"ShipmentVehicleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Consignments_UpdatedByUserId",
                table: "Consignments",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CancelledByUserId",
                table: "Shipments",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CompanyId_BranchId_CreatedAt",
                table: "Shipments",
                columns: new[] { "CompanyId", "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CompanyId_ShipmentNo",
                table: "Shipments",
                columns: new[] { "CompanyId", "ShipmentNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CompanyId_SourceLocationId",
                table: "Shipments",
                columns: new[] { "CompanyId", "SourceLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CompanyId_Status",
                table: "Shipments",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CreatedByUserId",
                table: "Shipments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_UpdatedByUserId",
                table: "Shipments",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentVehicles_CompanyId_ShipmentId",
                table: "ShipmentVehicles",
                columns: new[] { "CompanyId", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentVehicles_CompanyId_VehicleId",
                table: "ShipmentVehicles",
                columns: new[] { "CompanyId", "VehicleId" },
                filter: "\"VehicleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentVehicles_ShipmentId_Sequence",
                table: "ShipmentVehicles",
                columns: new[] { "ShipmentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentVehicles_ShipmentId_VehicleId",
                table: "ShipmentVehicles",
                columns: new[] { "ShipmentId", "VehicleId" },
                unique: true,
                filter: "\"VehicleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_CompanyId_ConsignmentLineId",
                table: "StockReservations",
                columns: new[] { "CompanyId", "ConsignmentLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_CompanyId_CreatedAt_Active",
                table: "StockReservations",
                columns: new[] { "CompanyId", "CreatedAt" },
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_CompanyId_ProductId",
                table: "StockReservations",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_ConsignmentLineId_Active",
                table: "StockReservations",
                column: "ConsignmentLineId",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_CreatedByUserId",
                table: "StockReservations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_Location_Product_Active",
                table: "StockReservations",
                columns: new[] { "CompanyId", "SourceLocationId", "ProductId" },
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_ReleasedByUserId",
                table: "StockReservations",
                column: "ReleasedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_SourceLocationId_ProductId",
                table: "StockReservations",
                columns: new[] { "SourceLocationId", "ProductId" });

            // FI-D31: Administrator gets every code implicitly; existing StockTransfers.Dispatch holders get View/Plan/Load, Approve holders View only; Reservations.Release is granted to nobody.
            // The seed script (scripts/sql/seed-permissions.sql) inserts the same rows (ON CONFLICT ("Code") DO NOTHING keeps these ids).
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES
                  (md5('perm:Shipments.View')::uuid, 'Shipments.View', 'Inventory', 'View shipments', 'View shipments, consignments and their allocations.'),
                  (md5('perm:Shipments.Plan')::uuid, 'Shipments.Plan', 'Inventory', 'Plan shipments', 'Create, edit, allocate stock to, assign vehicles to and cancel shipments and consignments.'),
                  (md5('perm:Shipments.Load')::uuid, 'Shipments.Load', 'Inventory', 'Load shipments', 'Record loading of consignments and mark shipments ready to dispatch.'),
                  (md5('perm:Reservations.Release')::uuid, 'Reservations.Release', 'Inventory', 'Release reservations', 'Release stock reservations that are no longer needed (company-wide only).')
                ON CONFLICT ("Code") DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId" AND ep."Code" = 'StockTransfers.Dispatch'
                CROSS JOIN (SELECT "Id" FROM "Permissions" WHERE "Code" IN ('Shipments.View', 'Shipments.Plan', 'Shipments.Load')) np
                ON CONFLICT DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId" AND ep."Code" = 'StockTransfers.Approve'
                CROSS JOIN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'Shipments.View') np
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $g$
                DECLARE s bigint; c bigint; l bigint; r bigint; q bigint;
                BEGIN
                    SELECT COUNT(*) INTO s FROM "Shipments";
                    SELECT COUNT(*) INTO c FROM "Consignments";
                    SELECT COUNT(*) INTO l FROM "ConsignmentLines";
                    SELECT COUNT(*) INTO r FROM "StockReservations";
                    SELECT COUNT(*) INTO q FROM "StockItems" WHERE "ReservedQuantity" > 0;
                    IF s > 0 OR c > 0 OR l > 0 OR r > 0 OR q > 0 THEN
                        RAISE EXCEPTION 'AddShipmentsConsignmentsAndReservations cannot be reverted: % shipment(s), % consignment(s), % consignment line(s), % reservation(s) and % stock row(s) with ReservedQuantity > 0 exist. Dropping would lose data.', s, c, l, r, q;
                    END IF;
                END
                $g$;

                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" IN ('Shipments.View', 'Shipments.Plan', 'Shipments.Load', 'Reservations.Release'));
                DELETE FROM "Permissions" WHERE "Code" IN ('Shipments.View', 'Shipments.Plan', 'Shipments.Load', 'Reservations.Release');
                """);

            migrationBuilder.DropTable(
                name: "StockReservations");

            migrationBuilder.DropTable(
                name: "ConsignmentLines");

            migrationBuilder.DropTable(
                name: "Consignments");

            migrationBuilder.DropTable(
                name: "ShipmentVehicles");

            migrationBuilder.DropTable(
                name: "Shipments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Vehicles_CompanyId_Id",
                table: "Vehicles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockItems_Reserved_Valid",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "ReservedQuantity",
                table: "StockItems");
        }
    }
}
