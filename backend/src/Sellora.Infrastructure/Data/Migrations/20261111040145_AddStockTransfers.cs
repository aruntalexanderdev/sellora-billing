using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 9 WP1 - Stock transfers schema (phase-9-design D1/D4/D7/D8, section 5; owner approval 2026-11-11): one migration, one transaction. Additive.
     *   guard    : G0 StockTransfers does not exist yet. G1 every company has exactly one Transit location. G2 no StockItems / StockMovements /
     *              BatchLocationBalances row sits at a Transit location (before Phase 9 nothing may be posted there). G3 the model is at the
     *              AddStockLocationDimension head. The migration REFUSES (RAISE) otherwise.
     *   profiling: P1 companies / Transit locations, P2 Permissions rows (RAISE NOTICE only).
     *   step 1   : StockTransfers, StockTransferLines, StockTransferLineBatches, StockTransferReceipts, StockTransferReceiptLines (PK, same-company composite FKs,
     *              CHECKs, indexes; open-transfer indexes are filtered on Status NOT IN (Completed, Cancelled, Rejected)).
     *   step 2   : CK_StockMovements_Type_Sign rebuilt with TransferIn > 0 and TransferOut < 0 (validation scan of StockMovements only, no rewrite).
     *   step 3   : the 7 StockTransfers.* permission rows (md5 ids, same text as PermissionCatalog and scripts/sql/seed-permissions.sql). No role backfill (O5):
     *              Administrator holds them implicitly.
     *   no backfill: no existing row changes.
     *
     * RELEASE NOTE: ships together with Phase 8b (Phase 8 O1 = A). Deploy per D10: snapshot -> idempotent script -> V-block -> deploy -> smoke test.
     * Down is only valid while no transfer or TransferIn/TransferOut movement exists (guard); otherwise rollback = restore the pre-deploy snapshot + previous build.
     * Not expressible in SQL (service layer, WP2): receipt line and transfer line belong to the same transfer; a receipt line's counters stay within the line's
     * outstanding; line counters = SUM of LineBatches = SUM of receipt lines; Source/Destination branch snapshots match the locations' effective branches;
     * Transit location is the company's Transit; status transitions. These are asserted by V5-V8 below.
     * Receipt-line Kind consistency: the line carries a Kind copy tied to the header by the composite FK (CompanyId, ReceiptId, Kind); the CHECK
     * CK_StockTransferReceiptLines_Kind_Quantities then allows only accepted/damaged on Receipt lines and written-off/returned on Resolution lines.
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release (also scripts/sql/stock-transfers-validation.sql).
     *
     * -- P1 companies and their Transit locations (expected: 1 per company)
     * SELECT c."Id", (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit') AS transit FROM "Companies" c;
     * -- P2 existing StockTransfers permission rows (expected: none)
     * SELECT "Code" FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%';
     * -- P3 guard probes (expected: no rows)
     * SELECT c."Id" FROM "Companies" c WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit') <> 1;
     * SELECT s."CompanyId" FROM "StockItems" s JOIN "StockLocations" l ON l."CompanyId" = s."CompanyId" AND l."Id" = s."StockLocationId" AND l."LocationType" = 'Transit';
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying and after every transfer flow in tests/smoke; every query must return no rows (V4 is spelled out in the design; all in
     * scripts/sql/stock-transfers-validation.sql).
     *
     * V1 per (location, product): QOH = SUM(movements) incl. Transit.      V2 per (location, product): QOH = SUM(BatchLocationBalances).
     * V3 per batch: SUM(balances) = PurchaseBatches.RemainingBaseQty.      V4 Transit balance per batch = SUM(Dispatched - Received - Damaged - WrittenOff - Returned) of LineBatches.
     * V5 line counters = SUM(LineBatches) = SUM(receipt lines).            V6 Completed => outstanding 0; status outside {Dispatched, PartiallyReceived, Completed} => no LineBatches, no movements.
     * V7 per transfer: SUM('StockTransfer' movements) = -(SUM Damaged + WrittenOff) base.   V8 no 'StockTransfer' movement outside the transfer's source / destination / Transit.
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard first, RAISE: any StockTransfers row or any TransferIn / TransferOut movement). Else drops the 5 tables, restores the previous
     * CK_StockMovements_Type_Sign and deletes the 7 permission rows with their RolePermissions rows.
     */
    /// <inheritdoc />
    public partial class AddStockTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guards G0-G3 and profiling notices (before anything is written).
            migrationBuilder.Sql("""
                DO $g$
                DECLARE n bigint; sample text;
                BEGIN
                    IF to_regclass('"StockTransfers"') IS NOT NULL THEN
                        RAISE EXCEPTION 'AddStockTransfers: table StockTransfers already exists (G0)';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261110114731_AddStockLocationDimension') THEN
                        RAISE EXCEPTION 'AddStockTransfers: the model is not at the AddStockLocationDimension head (G3)';
                    END IF;

                    SELECT COUNT(*), string_agg(x."Id"::text, ', ') INTO n, sample FROM (
                        SELECT c."Id" FROM "Companies" c
                        WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit') <> 1 LIMIT 20) x;
                    IF n > 0 THEN
                        RAISE EXCEPTION 'AddStockTransfers: company(ies) % do not have exactly one Transit location (G1)', sample;
                    END IF;

                    SELECT COUNT(*) INTO n FROM (
                        SELECT 1 FROM "StockItems" t JOIN "StockLocations" l ON l."CompanyId" = t."CompanyId" AND l."Id" = t."StockLocationId" WHERE l."LocationType" = 'Transit'
                        UNION ALL SELECT 1 FROM "StockMovements" t JOIN "StockLocations" l ON l."CompanyId" = t."CompanyId" AND l."Id" = t."StockLocationId" WHERE l."LocationType" = 'Transit'
                        UNION ALL SELECT 1 FROM "BatchLocationBalances" t JOIN "StockLocations" l ON l."CompanyId" = t."CompanyId" AND l."Id" = t."StockLocationId" WHERE l."LocationType" = 'Transit') x;
                    IF n > 0 THEN
                        RAISE EXCEPTION 'AddStockTransfers: % stock rows already sit at a Transit location (G2)', n;
                    END IF;

                    -- P1 / P2 profiling
                    SELECT COUNT(*) INTO n FROM "Companies";
                    RAISE NOTICE 'AddStockTransfers P1: % companies, % Transit locations', n, (SELECT COUNT(*) FROM "StockLocations" WHERE "LocationType" = 'Transit');
                    RAISE NOTICE 'AddStockTransfers P2: % Permissions rows, % already StockTransfers.*', (SELECT COUNT(*) FROM "Permissions"), (SELECT COUNT(*) FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%');
                END $g$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type_Sign",
                table: "StockMovements");

            migrationBuilder.CreateTable(
                name: "StockTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransitLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceBranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    DestinationBranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DispatchedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfers", x => x.Id);
                    table.UniqueConstraint("AK_StockTransfers_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_StockTransfers_Actors_Paired", "(\"SubmittedAt\" IS NULL) = (\"SubmittedByUserId\" IS NULL) AND (\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"RejectedAt\" IS NULL) = (\"RejectedByUserId\" IS NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL) AND (\"CompletedAt\" IS NULL) = (\"CompletedByUserId\" IS NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL)");
                    table.CheckConstraint("CK_StockTransfers_Endpoints_Differ", "\"SourceLocationId\" <> \"DestinationLocationId\"");
                    table.CheckConstraint("CK_StockTransfers_Endpoints_NotTransit", "\"SourceLocationId\" <> \"TransitLocationId\" AND \"DestinationLocationId\" <> \"TransitLocationId\"");
                    table.CheckConstraint("CK_StockTransfers_Status_Approval", "(\"Status\" NOT IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed') OR \"ApprovedAt\" IS NOT NULL) AND (\"Status\" IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled') OR \"ApprovedAt\" IS NULL)");
                    table.CheckConstraint("CK_StockTransfers_Status_Cancellation", "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL) AND (\"Status\" <> 'Cancelled' OR char_length(\"CancellationReason\") > 0)");
                    table.CheckConstraint("CK_StockTransfers_Status_Completion", "(\"Status\" = 'Completed') = (\"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_StockTransfers_Status_Dispatch", "(\"Status\" IN ('Dispatched', 'PartiallyReceived', 'Completed')) = (\"DispatchedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_StockTransfers_Status_Rejection", "(\"Status\" = 'Rejected') = (\"RejectedAt\" IS NOT NULL) AND (\"Status\" <> 'Rejected' OR char_length(\"RejectionReason\") > 0)");
                    table.CheckConstraint("CK_StockTransfers_Status_Stamps", "(\"Status\" = 'Draft') = (\"SubmittedAt\" IS NULL) OR \"Status\" = 'Cancelled'");
                    table.CheckConstraint("CK_StockTransfers_Status_Valid", "\"Status\" IN ('Draft', 'Requested', 'Approved', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_StockTransfers_Branches_BranchId",
                        columns: x => new { x.CompanyId, x.BranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Branches_DestinationBranchId",
                        columns: x => new { x.CompanyId, x.DestinationBranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Branches_SourceBranchId",
                        columns: x => new { x.CompanyId, x.SourceBranchId },
                        principalTable: "Branches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_StockLocations_Destination",
                        columns: x => new { x.CompanyId, x.DestinationLocationId },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_StockLocations_Source",
                        columns: x => new { x.CompanyId, x.SourceLocationId },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_StockLocations_Transit",
                        columns: x => new { x.CompanyId, x.TransitLocationId },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Users_ApprovedBy",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Users_CancelledBy",
                        column: x => x.CancelledByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Users_CompletedBy",
                        column: x => x.CompletedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Users_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Users_DispatchedBy",
                        column: x => x.DispatchedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Users_RejectedBy",
                        column: x => x.RejectedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Users_SubmittedBy",
                        column: x => x.SubmittedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ConversionFactorToBase = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    RequestedQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    RequestedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    DispatchProductUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    DispatchUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    DispatchUnitCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    DispatchFactor = table.Column<decimal>(type: "numeric(28,10)", nullable: true),
                    DispatchedQty = table.Column<decimal>(type: "numeric(18,3)", nullable: true),
                    DispatchedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    ReceivedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    DamagedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    WrittenOffBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    ReturnedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferLines", x => x.Id);
                    table.UniqueConstraint("AK_StockTransferLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_StockTransferLines_Counters_Max", "\"ReceivedBaseQty\" + \"DamagedBaseQty\" + \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" <= \"DispatchedBaseQty\"");
                    table.CheckConstraint("CK_StockTransferLines_Counters_NonNegative", "\"ReceivedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0");
                    table.CheckConstraint("CK_StockTransferLines_Dispatch_AllOrNone", "(\"DispatchProductUnitId\" IS NULL) = (\"DispatchUnitId\" IS NULL) AND (\"DispatchProductUnitId\" IS NULL) = (\"DispatchUnitCode\" IS NULL) AND (\"DispatchProductUnitId\" IS NULL) = (\"DispatchFactor\" IS NULL) AND (\"DispatchProductUnitId\" IS NULL) = (\"DispatchedQty\" IS NULL)");
                    table.CheckConstraint("CK_StockTransferLines_Dispatch_Arithmetic", "\"DispatchedQty\" IS NULL OR (\"DispatchedQty\" >= 0 AND \"DispatchFactor\" > 0 AND \"DispatchedQty\" * \"DispatchFactor\" = \"DispatchedBaseQty\")");
                    table.CheckConstraint("CK_StockTransferLines_Dispatch_Required", "\"DispatchProductUnitId\" IS NOT NULL OR (\"DispatchedBaseQty\" = 0 AND \"ReceivedBaseQty\" = 0 AND \"DamagedBaseQty\" = 0 AND \"WrittenOffBaseQty\" = 0 AND \"ReturnedBaseQty\" = 0)");
                    table.CheckConstraint("CK_StockTransferLines_Dispatched_Max", "\"DispatchedBaseQty\" >= 0 AND \"DispatchedBaseQty\" <= \"RequestedBaseQty\"");
                    table.CheckConstraint("CK_StockTransferLines_LineNo_Positive", "\"LineNo\" > 0");
                    table.CheckConstraint("CK_StockTransferLines_Requested_Arithmetic", "\"RequestedQty\" * \"ConversionFactorToBase\" = \"RequestedBaseQty\"");
                    table.CheckConstraint("CK_StockTransferLines_Requested_Positive", "\"RequestedQty\" > 0 AND \"ConversionFactorToBase\" > 0 AND \"RequestedBaseQty\" > 0");
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_ProductUnits_Dispatch",
                        columns: x => new { x.ProductId, x.DispatchProductUnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_ProductUnits_Request",
                        columns: x => new { x.ProductId, x.ProductUnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Products",
                        columns: x => new { x.CompanyId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_StockTransfers",
                        columns: x => new { x.CompanyId, x.TransferId },
                        principalTable: "StockTransfers",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Units_Dispatch",
                        column: x => x.DispatchUnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Units_Request",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferReceipts", x => x.Id);
                    table.UniqueConstraint("AK_StockTransferReceipts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_StockTransferReceipts_CompanyId_Id_Kind", x => new { x.CompanyId, x.Id, x.Kind });
                    table.CheckConstraint("CK_StockTransferReceipts_Kind_Valid", "\"Kind\" IN ('Receipt', 'Resolution')");
                    table.CheckConstraint("CK_StockTransferReceipts_Sequence_Positive", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_StockTransferReceipts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferReceipts_StockTransfers",
                        columns: x => new { x.CompanyId, x.TransferId },
                        principalTable: "StockTransfers",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferReceipts_Users_By",
                        column: x => x.ByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferLineBatches",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DispatchedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ReceivedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    DamagedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    WrittenOffBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    ReturnedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferLineBatches", x => new { x.LineId, x.PurchaseBatchId });
                    table.CheckConstraint("CK_StockTransferLineBatches_Counters_Max", "\"ReceivedBaseQty\" + \"DamagedBaseQty\" + \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" <= \"DispatchedBaseQty\"");
                    table.CheckConstraint("CK_StockTransferLineBatches_Counters_NonNegative", "\"ReceivedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0");
                    table.CheckConstraint("CK_StockTransferLineBatches_Dispatched_Positive", "\"DispatchedBaseQty\" > 0 AND \"UnitCost\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockTransferLineBatches_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLineBatches_Lines",
                        columns: x => new { x.CompanyId, x.LineId },
                        principalTable: "StockTransferLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLineBatches_PurchaseBatches",
                        columns: x => new { x.CompanyId, x.PurchaseBatchId },
                        principalTable: "PurchaseBatches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferReceiptLines",
                columns: table => new
                {
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ConversionFactorToBase = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    AcceptedQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    AcceptedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    DamagedQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    DamagedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    WrittenOffBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    ReturnedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferReceiptLines", x => new { x.ReceiptId, x.TransferLineId });
                    table.CheckConstraint("CK_StockTransferReceiptLines_Arithmetic", "\"AcceptedQty\" * \"ConversionFactorToBase\" = \"AcceptedBaseQty\" AND \"DamagedQty\" * \"ConversionFactorToBase\" = \"DamagedBaseQty\"");
                    table.CheckConstraint("CK_StockTransferReceiptLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
                    table.CheckConstraint("CK_StockTransferReceiptLines_Kind_Quantities", "(\"Kind\" = 'Receipt' AND \"WrittenOffBaseQty\" = 0 AND \"ReturnedBaseQty\" = 0 AND \"AcceptedBaseQty\" + \"DamagedBaseQty\" > 0) OR (\"Kind\" = 'Resolution' AND \"AcceptedBaseQty\" = 0 AND \"DamagedBaseQty\" = 0 AND \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" > 0)");
                    table.CheckConstraint("CK_StockTransferReceiptLines_Quantities_NonNegative", "\"AcceptedQty\" >= 0 AND \"AcceptedBaseQty\" >= 0 AND \"DamagedQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockTransferReceiptLines_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferReceiptLines_Lines",
                        columns: x => new { x.CompanyId, x.TransferLineId },
                        principalTable: "StockTransferLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferReceiptLines_ProductUnits",
                        columns: x => new { x.ProductId, x.ProductUnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferReceiptLines_Products",
                        columns: x => new { x.CompanyId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferReceiptLines_Receipts",
                        columns: x => new { x.CompanyId, x.ReceiptId, x.Kind },
                        principalTable: "StockTransferReceipts",
                        principalColumns: new[] { "CompanyId", "Id", "Kind" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferReceiptLines_Units",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type_Sign",
                table: "StockMovements",
                sql: "(\"Type\" NOT IN ('Purchase', 'SaleReturn', 'SaleCancellation', 'OpeningStock', 'TransferIn') OR \"QuantityBaseUnits\" > 0) AND (\"Type\" NOT IN ('Sale', 'PurchaseReturn', 'PurchaseCancellation', 'TransferOut') OR \"QuantityBaseUnits\" < 0)");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLineBatches_CompanyId_LineId",
                table: "StockTransferLineBatches",
                columns: new[] { "CompanyId", "LineId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLineBatches_CompanyId_PurchaseBatchId",
                table: "StockTransferLineBatches",
                columns: new[] { "CompanyId", "PurchaseBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLineBatches_PurchaseBatchId",
                table: "StockTransferLineBatches",
                column: "PurchaseBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_CompanyId_ProductId",
                table: "StockTransferLines",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_CompanyId_TransferId_LineNo",
                table: "StockTransferLines",
                columns: new[] { "CompanyId", "TransferId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_DispatchUnitId",
                table: "StockTransferLines",
                column: "DispatchUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_ProductId_DispatchProductUnitId",
                table: "StockTransferLines",
                columns: new[] { "ProductId", "DispatchProductUnitId" },
                filter: "\"DispatchProductUnitId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_ProductId_ProductUnitId",
                table: "StockTransferLines",
                columns: new[] { "ProductId", "ProductUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_UnitId",
                table: "StockTransferLines",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferReceiptLines_CompanyId_ProductId",
                table: "StockTransferReceiptLines",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferReceiptLines_CompanyId_ReceiptId_Kind",
                table: "StockTransferReceiptLines",
                columns: new[] { "CompanyId", "ReceiptId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferReceiptLines_CompanyId_TransferLineId",
                table: "StockTransferReceiptLines",
                columns: new[] { "CompanyId", "TransferLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferReceiptLines_ProductId_ProductUnitId",
                table: "StockTransferReceiptLines",
                columns: new[] { "ProductId", "ProductUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferReceiptLines_UnitId",
                table: "StockTransferReceiptLines",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferReceipts_ByUserId",
                table: "StockTransferReceipts",
                column: "ByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferReceipts_CompanyId_TransferId_Sequence",
                table: "StockTransferReceipts",
                columns: new[] { "CompanyId", "TransferId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_ApprovedByUserId",
                table: "StockTransfers",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CancelledByUserId",
                table: "StockTransfers",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CompanyId_BranchId_CreatedAt",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CompanyId_DestinationBranchId",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "DestinationBranchId" },
                filter: "\"DestinationBranchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CompanyId_SourceBranchId",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "SourceBranchId" },
                filter: "\"SourceBranchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CompanyId_Status",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CompanyId_TransferNo",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "TransferNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CompanyId_TransitLocationId",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "TransitLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CompletedByUserId",
                table: "StockTransfers",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CreatedByUserId",
                table: "StockTransfers",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_Destination_Open",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "DestinationLocationId" },
                filter: "\"Status\" NOT IN ('Completed', 'Cancelled', 'Rejected')");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_DispatchedByUserId",
                table: "StockTransfers",
                column: "DispatchedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_RejectedByUserId",
                table: "StockTransfers",
                column: "RejectedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_Source_Open",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "SourceLocationId" },
                filter: "\"Status\" NOT IN ('Completed', 'Cancelled', 'Rejected')");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_SubmittedByUserId",
                table: "StockTransfers",
                column: "SubmittedByUserId");

            // Step 3: the 7 catalogue rows (same text as PermissionCatalog / scripts/sql/seed-permissions.sql). No role backfill (O5).
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description")
                SELECT md5('perm:' || v."Code")::uuid, v."Code", v."Module", v."Action", v."Description"
                FROM (VALUES
                  ('StockTransfers.View', 'Inventory', 'View transfers', 'View stock transfers.'),
                  ('StockTransfers.Create', 'Inventory', 'Create transfers', 'Create, edit and submit stock transfer requests.'),
                  ('StockTransfers.Approve', 'Inventory', 'Approve transfers', 'Approve or reject stock transfer requests.'),
                  ('StockTransfers.Dispatch', 'Inventory', 'Dispatch transfers', 'Dispatch approved stock transfers from the source location.'),
                  ('StockTransfers.Receive', 'Inventory', 'Receive transfers', 'Receive dispatched stock transfers at the destination location.'),
                  ('StockTransfers.Cancel', 'Inventory', 'Cancel transfers', 'Cancel stock transfers before they are dispatched.'),
                  ('StockTransfers.Resolve', 'Inventory', 'Resolve transfers', 'Resolve in-transit shortages by writing them off or returning them to the source (company-wide only).')
                ) AS v("Code", "Module", "Action", "Description")
                ON CONFLICT ("Code") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard: refuse once any transfer or transfer movement exists (restore the pre-deploy snapshot instead).
            migrationBuilder.Sql("""
                DO $g$
                DECLARE n bigint;
                BEGIN
                    SELECT COUNT(*) INTO n FROM "StockTransfers";
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockTransfers Down refused: % StockTransfers rows exist (restore the pre-deploy snapshot)', n; END IF;
                    SELECT COUNT(*) INTO n FROM "StockMovements" WHERE "Type" IN ('TransferIn', 'TransferOut');
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockTransfers Down refused: % TransferIn/TransferOut movements exist (restore the pre-deploy snapshot)', n; END IF;
                END $g$;
                """);

            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%');
                DELETE FROM "Permissions" WHERE "Code" LIKE 'StockTransfers.%';
                """);

            migrationBuilder.DropTable(
                name: "StockTransferLineBatches");

            migrationBuilder.DropTable(
                name: "StockTransferReceiptLines");

            migrationBuilder.DropTable(
                name: "StockTransferLines");

            migrationBuilder.DropTable(
                name: "StockTransferReceipts");

            migrationBuilder.DropTable(
                name: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type_Sign",
                table: "StockMovements");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type_Sign",
                table: "StockMovements",
                sql: "(\"Type\" NOT IN ('Purchase', 'SaleReturn', 'SaleCancellation', 'OpeningStock') OR \"QuantityBaseUnits\" > 0) AND (\"Type\" NOT IN ('Sale', 'PurchaseReturn', 'PurchaseCancellation') OR \"QuantityBaseUnits\" < 0)");
        }
    }
}
