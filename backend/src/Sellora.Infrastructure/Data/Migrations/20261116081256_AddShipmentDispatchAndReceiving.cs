using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShipmentDispatchAndReceiving : Migration
    {
        // Phase 05 (FI-D34..D37). Purely additive: Shipments.DispatchedAt/DispatchedByUserId, Consignments.DispatchedAt, six ConsignmentLines counters (NOT NULL DEFAULT 0),
        // StockTransferLineBatches.ConsignmentLineId (nullable), ConsignmentReceipts + ConsignmentReceiptLines. The two status CHECKs are widened (every existing value stays valid);
        // the new status/stamp, counter and receipt CHECKs hold trivially for existing rows (no dispatched rows, all counters 0). No existing row is rewritten.
        // Validation (each must return 0 / no rows right after Up; ongoing versions live in scripts/sql/transit-reconciliation.sql):
        //   SELECT COUNT(*) FROM "ConsignmentReceipts"; SELECT COUNT(*) FROM "ConsignmentReceiptLines";                                   -- 0, 0
        //   SELECT COUNT(*) FROM "ConsignmentLines" WHERE "DispatchedBaseQty" <> 0 OR "AcceptedBaseQty" <> 0 OR "DamagedBaseQty" <> 0 OR "RejectedBaseQty" <> 0 OR "MissingBaseQty" <> 0 OR "PostedBaseQty" <> 0;  -- 0
        //   SELECT COUNT(*) FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NOT NULL;                                         -- 0
        //   SELECT COUNT(*) FROM "Shipments" WHERE "DispatchedAt" IS NOT NULL;  SELECT COUNT(*) FROM "Consignments" WHERE "DispatchedAt" IS NOT NULL;  -- 0, 0
        //   -- Steady state (after dispatches exist): consumed reservations = dispatched per consignment line, and receipt lines = line counters:
        //   SELECT l."Id" FROM "ConsignmentLines" l LEFT JOIN (SELECT "ConsignmentLineId", SUM("BaseQty") q FROM "StockReservations" WHERE "Status" = 'Consumed' GROUP BY 1) r ON r."ConsignmentLineId" = l."Id"
        //     WHERE l."DispatchedBaseQty" <> COALESCE(r.q, 0);
        //   SELECT l."Id" FROM "ConsignmentLines" l LEFT JOIN (SELECT "ConsignmentLineId", SUM("AcceptedBaseQty") a, SUM("DamagedBaseQty") d, SUM("RejectedBaseQty") j, SUM("MissingBaseQty") m FROM "ConsignmentReceiptLines" GROUP BY 1) x ON x."ConsignmentLineId" = l."Id"
        //     WHERE (l."AcceptedBaseQty", l."DamagedBaseQty", l."RejectedBaseQty", l."MissingBaseQty") IS DISTINCT FROM (COALESCE(x.a, 0), COALESCE(x.d, 0), COALESCE(x.j, 0), COALESCE(x.m, 0));
        // StockTransferLineBatches gets a surrogate Id PK (FI-D34, one row per batch per consignment line): existing rows are backfilled with the deterministic id
        //   md5(LineId || PurchaseBatchId)::uuid (unique because the old PK was (LineId, PurchaseBatchId)); the old PK is replaced by two partial unique indexes
        //   (legacy: (LineId, PurchaseBatchId) WHERE ConsignmentLineId IS NULL; shipment: (ConsignmentLineId, PurchaseBatchId) WHERE NOT NULL). Validation (both must be 0 rows / 0):
        //   SELECT COUNT(*) - COUNT(DISTINCT "Id") FROM "StockTransferLineBatches";   SELECT "LineId", "PurchaseBatchId" FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NULL GROUP BY 1, 2 HAVING COUNT(*) > 1;
        // Down refuses when any shipment/consignment is in a new status or has a DispatchedAt, any new counter is > 0, any receipt exists, or any batch row is traced to a consignment line.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_StockTransferLineBatches",
                table: "StockTransferLineBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shipments_Status_Valid",
                table: "Shipments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Consignments_Status_Valid",
                table: "Consignments");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "StockTransferLineBatches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql("""UPDATE "StockTransferLineBatches" SET "Id" = md5("LineId"::text || "PurchaseBatchId"::text)::uuid;""");

            migrationBuilder.AddColumn<Guid>(
                name: "ConsignmentLineId",
                table: "StockTransferLineBatches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "Shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DispatchedByUserId",
                table: "Shipments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchedAt",
                table: "Consignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AcceptedBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DamagedBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DispatchedBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MissingBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PostedBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RejectedBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddPrimaryKey(
                name: "PK_StockTransferLineBatches",
                table: "StockTransferLineBatches",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ConsignmentReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PostedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsignmentReceipts", x => x.Id);
                    table.UniqueConstraint("AK_ConsignmentReceipts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ConsignmentReceipts_Sequence_Positive", "\"Sequence\" > 0");
                    table.CheckConstraint("CK_ConsignmentReceipts_Status_Posted", "(\"Status\" = 'Posted') = (\"PostedAt\" IS NOT NULL) AND (\"PostedAt\" IS NULL) = (\"PostedByUserId\" IS NULL)");
                    table.CheckConstraint("CK_ConsignmentReceipts_Status_Valid", "\"Status\" IN ('Verified', 'Posted')");
                    table.ForeignKey(
                        name: "FK_ConsignmentReceipts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceipts_Consignments",
                        columns: x => new { x.CompanyId, x.ConsignmentId },
                        principalTable: "Consignments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceipts_Users_PostedBy",
                        column: x => x.PostedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceipts_Users_VerifiedBy",
                        column: x => x.VerifiedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsignmentReceiptLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsignmentLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ConversionFactorToBase = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    AcceptedEnteredQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    AcceptedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    DamagedEnteredQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    DamagedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    RejectedEnteredQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    RejectedBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    MissingEnteredQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    MissingBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsignmentReceiptLines", x => x.Id);
                    table.UniqueConstraint("AK_ConsignmentReceiptLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ConsignmentReceiptLines_AnyPositive", "\"AcceptedBaseQty\" + \"DamagedBaseQty\" + \"RejectedBaseQty\" + \"MissingBaseQty\" > 0");
                    table.CheckConstraint("CK_ConsignmentReceiptLines_Arithmetic", "\"AcceptedEnteredQty\" * \"ConversionFactorToBase\" = \"AcceptedBaseQty\" AND \"DamagedEnteredQty\" * \"ConversionFactorToBase\" = \"DamagedBaseQty\" AND \"RejectedEnteredQty\" * \"ConversionFactorToBase\" = \"RejectedBaseQty\" AND \"MissingEnteredQty\" * \"ConversionFactorToBase\" = \"MissingBaseQty\"");
                    table.CheckConstraint("CK_ConsignmentReceiptLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
                    table.CheckConstraint("CK_ConsignmentReceiptLines_Qty_NonNegative", "\"AcceptedEnteredQty\" >= 0 AND \"AcceptedBaseQty\" >= 0 AND \"DamagedEnteredQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"RejectedEnteredQty\" >= 0 AND \"RejectedBaseQty\" >= 0 AND \"MissingEnteredQty\" >= 0 AND \"MissingBaseQty\" >= 0");
                    table.ForeignKey(
                        name: "FK_ConsignmentReceiptLines_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceiptLines_ConsignmentLines",
                        columns: x => new { x.CompanyId, x.ConsignmentLineId },
                        principalTable: "ConsignmentLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceiptLines_ProductUnits",
                        columns: x => new { x.ProductId, x.ProductUnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceiptLines_Products",
                        columns: x => new { x.CompanyId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceiptLines_Receipts",
                        columns: x => new { x.CompanyId, x.ReceiptId },
                        principalTable: "ConsignmentReceipts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentReceiptLines_Units",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLineBatches_CompanyId_ConsignmentLineId",
                table: "StockTransferLineBatches",
                columns: new[] { "CompanyId", "ConsignmentLineId" },
                filter: "\"ConsignmentLineId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLineBatches_ConsignmentLineId_PurchaseBatchId",
                table: "StockTransferLineBatches",
                columns: new[] { "ConsignmentLineId", "PurchaseBatchId" },
                unique: true,
                filter: "\"ConsignmentLineId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLineBatches_LineId_PurchaseBatchId_Legacy",
                table: "StockTransferLineBatches",
                columns: new[] { "LineId", "PurchaseBatchId" },
                unique: true,
                filter: "\"ConsignmentLineId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_DispatchedByUserId",
                table: "Shipments",
                column: "DispatchedByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipments_Status_Dispatched",
                table: "Shipments",
                sql: "(\"Status\" IN ('Dispatched', 'PartiallyDelivered', 'Delivered')) = (\"DispatchedAt\" IS NOT NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipments_Status_Valid",
                table: "Shipments",
                sql: "\"Status\" IN ('Draft', 'Loading', 'ReadyToDispatch', 'Cancelled', 'Dispatched', 'PartiallyDelivered', 'Delivered')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Consignments_Status_Dispatched",
                table: "Consignments",
                sql: "(\"Status\" IN ('InTransit', 'PartiallyReceived', 'Received')) = (\"DispatchedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Consignments_Status_Valid",
                table: "Consignments",
                sql: "\"Status\" IN ('Planned', 'Loaded', 'Cancelled', 'InTransit', 'PartiallyReceived', 'Received')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentLines_Dispatched_Valid",
                table: "ConsignmentLines",
                sql: "\"DispatchedBaseQty\" >= 0 AND \"DispatchedBaseQty\" <= \"AllocatedBaseQty\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentLines_Posted_Valid",
                table: "ConsignmentLines",
                sql: "\"PostedBaseQty\" <= \"AcceptedBaseQty\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentLines_Receiving_Max",
                table: "ConsignmentLines",
                sql: "\"AcceptedBaseQty\" + \"DamagedBaseQty\" + \"RejectedBaseQty\" + \"MissingBaseQty\" <= \"DispatchedBaseQty\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentLines_Receiving_NonNegative",
                table: "ConsignmentLines",
                sql: "\"AcceptedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"RejectedBaseQty\" >= 0 AND \"MissingBaseQty\" >= 0 AND \"PostedBaseQty\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceiptLines_CompanyId_ConsignmentLineId",
                table: "ConsignmentReceiptLines",
                columns: new[] { "CompanyId", "ConsignmentLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceiptLines_CompanyId_ProductId",
                table: "ConsignmentReceiptLines",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceiptLines_CompanyId_ReceiptId",
                table: "ConsignmentReceiptLines",
                columns: new[] { "CompanyId", "ReceiptId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceiptLines_ProductId_ProductUnitId",
                table: "ConsignmentReceiptLines",
                columns: new[] { "ProductId", "ProductUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceiptLines_ReceiptId_ConsignmentLineId",
                table: "ConsignmentReceiptLines",
                columns: new[] { "ReceiptId", "ConsignmentLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceiptLines_UnitId",
                table: "ConsignmentReceiptLines",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceipts_CompanyId_ConsignmentId",
                table: "ConsignmentReceipts",
                columns: new[] { "CompanyId", "ConsignmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceipts_CompanyId_Status_Verified",
                table: "ConsignmentReceipts",
                columns: new[] { "CompanyId", "Status" },
                filter: "\"Status\" = 'Verified'");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceipts_ConsignmentId_Sequence",
                table: "ConsignmentReceipts",
                columns: new[] { "ConsignmentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceipts_PostedByUserId",
                table: "ConsignmentReceipts",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceipts_VerifiedByUserId",
                table: "ConsignmentReceipts",
                column: "VerifiedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_Users_DispatchedBy",
                table: "Shipments",
                column: "DispatchedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransferLineBatches_ConsignmentLines",
                table: "StockTransferLineBatches",
                columns: new[] { "CompanyId", "ConsignmentLineId" },
                principalTable: "ConsignmentLines",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $g$
                DECLARE sh bigint; co bigint; cl bigint; rc bigint; bt bigint; dp bigint;
                BEGIN
                    SELECT COUNT(*) INTO sh FROM "Shipments" WHERE "Status" IN ('Dispatched', 'PartiallyDelivered', 'Delivered') OR "DispatchedAt" IS NOT NULL;
                    SELECT COUNT(*) INTO co FROM "Consignments" WHERE "Status" IN ('InTransit', 'PartiallyReceived', 'Received') OR "DispatchedAt" IS NOT NULL;
                    SELECT COUNT(*) INTO cl FROM "ConsignmentLines" WHERE "DispatchedBaseQty" > 0 OR "AcceptedBaseQty" > 0 OR "DamagedBaseQty" > 0 OR "RejectedBaseQty" > 0 OR "MissingBaseQty" > 0 OR "PostedBaseQty" > 0;
                    SELECT COUNT(*) INTO rc FROM "ConsignmentReceipts";
                    SELECT COUNT(*) INTO bt FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" IS NOT NULL;
                    SELECT COUNT(*) INTO dp FROM (SELECT 1 FROM "StockTransferLineBatches" GROUP BY "LineId", "PurchaseBatchId" HAVING COUNT(*) > 1) x;
                    IF dp > 0 OR sh > 0 OR co > 0 OR cl > 0 OR rc > 0 OR bt > 0 THEN
                        RAISE EXCEPTION 'AddShipmentDispatchAndReceiving cannot be reverted: % dispatched shipment(s), % in-transit/received consignment(s), % consignment line(s) with dispatch/receiving counters, % receipt(s) and % transfer batch row(s) traced to a consignment line exist, and % batch share(s) have duplicate (LineId, PurchaseBatchId). Reverting would lose data.', sh, co, cl, rc, bt, dp;
                    END IF;
                END
                $g$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_Users_DispatchedBy",
                table: "Shipments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockTransferLineBatches_ConsignmentLines",
                table: "StockTransferLineBatches");

            migrationBuilder.DropTable(
                name: "ConsignmentReceiptLines");

            migrationBuilder.DropTable(
                name: "ConsignmentReceipts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StockTransferLineBatches",
                table: "StockTransferLineBatches");

            migrationBuilder.DropIndex(
                name: "IX_StockTransferLineBatches_CompanyId_ConsignmentLineId",
                table: "StockTransferLineBatches");

            migrationBuilder.DropIndex(
                name: "IX_StockTransferLineBatches_ConsignmentLineId_PurchaseBatchId",
                table: "StockTransferLineBatches");

            migrationBuilder.DropIndex(
                name: "IX_StockTransferLineBatches_LineId_PurchaseBatchId_Legacy",
                table: "StockTransferLineBatches");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_DispatchedByUserId",
                table: "Shipments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shipments_Status_Dispatched",
                table: "Shipments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shipments_Status_Valid",
                table: "Shipments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Consignments_Status_Dispatched",
                table: "Consignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Consignments_Status_Valid",
                table: "Consignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentLines_Dispatched_Valid",
                table: "ConsignmentLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentLines_Posted_Valid",
                table: "ConsignmentLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentLines_Receiving_Max",
                table: "ConsignmentLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentLines_Receiving_NonNegative",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "StockTransferLineBatches");

            migrationBuilder.DropColumn(
                name: "ConsignmentLineId",
                table: "StockTransferLineBatches");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "DispatchedByUserId",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "Consignments");

            migrationBuilder.DropColumn(
                name: "AcceptedBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "DamagedBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "DispatchedBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "MissingBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "PostedBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "RejectedBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StockTransferLineBatches",
                table: "StockTransferLineBatches",
                columns: new[] { "LineId", "PurchaseBatchId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipments_Status_Valid",
                table: "Shipments",
                sql: "\"Status\" IN ('Draft', 'Loading', 'ReadyToDispatch', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Consignments_Status_Valid",
                table: "Consignments",
                sql: "\"Status\" IN ('Planned', 'Loaded', 'Cancelled')");
        }
    }
}
