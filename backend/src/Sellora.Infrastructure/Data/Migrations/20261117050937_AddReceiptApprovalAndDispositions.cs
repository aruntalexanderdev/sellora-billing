using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptApprovalAndDispositions : Migration
    {
        // Phase 06 (FI-D39..D41). Additive: BusinessSettings.ReceiptPostingMode (NOT NULL DEFAULT 'Direct') + ReceiptSeparationOfDuties (DEFAULT false); ConsignmentReceipts.PolicyMode/RequireSeparation
        // (same defaults = the Direct / no-separation snapshot of every existing receipt) + RejectedAt/RejectedByUserId/RejectionReason (Posted stamps double as approval stamps);
        // ConsignmentLines.Dispositioned{Damaged,Rejected,Missing}BaseQty (DEFAULT 0); StockTransferLineBatches.AcceptedCorrectionBaseQty (DEFAULT 0, added to the Counters_Max sum so
        // Received + Damaged + WrittenOff + Returned + AcceptedCorrection <= Dispatched); new append-only tables ConsignmentDispositions + ConsignmentDispositionLines.
        // Widened CHECKs: Shipments.Status (+Completed), Consignments.Status (+Reconciled), ConsignmentReceipts.Status (+PendingApproval, RejectedForCorrection; column widened varchar(20) -> varchar(30) for RejectedForCorrection); every existing value stays valid.
        // Backfill (end of Up, after the widened CHECKs): Received consignments with no damaged/rejected/missing quantity on any line -> Reconciled; Delivered shipments with >= 1 non-cancelled consignment, all Reconciled -> Completed.
        // Validation (each must return 0 / no rows right after Up):
        //   SELECT COUNT(*) FROM "ConsignmentDispositions"; SELECT COUNT(*) FROM "ConsignmentDispositionLines";                                                    -- 0, 0
        //   SELECT COUNT(*) FROM "ConsignmentReceipts" WHERE "PolicyMode" <> 'Direct' OR "RequireSeparation" OR "RejectedAt" IS NOT NULL;                           -- 0
        //   SELECT COUNT(*) FROM "BusinessSettings" WHERE "ReceiptPostingMode" <> 'Direct' OR "ReceiptSeparationOfDuties";                                          -- 0
        //   SELECT COUNT(*) FROM "Consignments" c WHERE c."Status" = 'Reconciled' AND EXISTS (SELECT 1 FROM "ConsignmentLines" l WHERE l."ConsignmentId" = c."Id" AND l."DamagedBaseQty" + l."RejectedBaseQty" + l."MissingBaseQty" > 0);  -- 0
        //   SELECT COUNT(*) FROM "Consignments" c WHERE c."Status" = 'Received' AND NOT EXISTS (SELECT 1 FROM "ConsignmentLines" l WHERE l."ConsignmentId" = c."Id" AND l."DamagedBaseQty" + l."RejectedBaseQty" + l."MissingBaseQty" > 0);  -- 0
        //   SELECT COUNT(*) FROM "Shipments" s WHERE s."Status" = 'Completed' AND EXISTS (SELECT 1 FROM "Consignments" c WHERE c."ShipmentId" = s."Id" AND c."Status" NOT IN ('Cancelled', 'Reconciled'));  -- 0
        //   SELECT COUNT(*) FROM "StockTransferLineBatches" WHERE "AcceptedCorrectionBaseQty" <> 0;  SELECT COUNT(*) FROM "ConsignmentLines" WHERE "DispositionedDamagedBaseQty" + "DispositionedRejectedBaseQty" + "DispositionedMissingBaseQty" <> 0;  -- 0, 0
        //   Ongoing: scripts/sql/transit-reconciliation.sql (queries 7-9 cover dispositions).
        // Down refuses when any disposition, PendingApproval/RejectedForCorrection receipt, rejection stamp, non-default receipt policy snapshot, non-default BusinessSettings policy, disposition counter or
        // AcceptedCorrection quantity exists; otherwise it reverses the backfill (Reconciled -> Received, Completed -> Delivered) and drops the additions.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "ConsignmentReceipts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_Max",
                table: "StockTransferLineBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_NonNegative",
                table: "StockTransferLineBatches");

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
                name: "CK_ConsignmentReceipts_Status_Valid",
                table: "ConsignmentReceipts");

            migrationBuilder.AddColumn<decimal>(
                name: "AcceptedCorrectionBaseQty",
                table: "StockTransferLineBatches",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PolicyMode",
                table: "ConsignmentReceipts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Direct");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RejectedAt",
                table: "ConsignmentReceipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RejectedByUserId",
                table: "ConsignmentReceipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "ConsignmentReceipts",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSeparation",
                table: "ConsignmentReceipts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "DispositionedDamagedBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DispositionedMissingBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DispositionedRejectedBaseQty",
                table: "ConsignmentLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptPostingMode",
                table: "BusinessSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Direct");

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptSeparationOfDuties",
                table: "BusinessSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ConsignmentDispositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsignmentDispositions", x => x.Id);
                    table.UniqueConstraint("AK_ConsignmentDispositions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ConsignmentDispositions_Reason_Required", "char_length(btrim(\"Reason\")) > 0");
                    table.CheckConstraint("CK_ConsignmentDispositions_Sequence_Positive", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositions_Consignments",
                        columns: x => new { x.CompanyId, x.ConsignmentId },
                        principalTable: "Consignments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositions_Users_By",
                        column: x => x.ByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsignmentDispositionLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DispositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsignmentLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ConversionFactorToBase = table.Column<decimal>(type: "numeric(28,10)", nullable: false),
                    Bucket = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    EnteredQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    BaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsignmentDispositionLines", x => x.Id);
                    table.UniqueConstraint("AK_ConsignmentDispositionLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ConsignmentDispositionLines_Action_Valid", "\"Action\" IN ('WriteOff', 'ReturnToSource', 'AcceptToDestination')");
                    table.CheckConstraint("CK_ConsignmentDispositionLines_Arithmetic", "\"EnteredQty\" * \"ConversionFactorToBase\" = \"BaseQty\"");
                    table.CheckConstraint("CK_ConsignmentDispositionLines_BaseQty_Positive", "\"BaseQty\" > 0");
                    table.CheckConstraint("CK_ConsignmentDispositionLines_Bucket_Valid", "\"Bucket\" IN ('Damaged', 'Rejected', 'Missing')");
                    table.CheckConstraint("CK_ConsignmentDispositionLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositionLines_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositionLines_ConsignmentLines",
                        columns: x => new { x.CompanyId, x.ConsignmentLineId },
                        principalTable: "ConsignmentLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositionLines_Dispositions",
                        columns: x => new { x.CompanyId, x.DispositionId },
                        principalTable: "ConsignmentDispositions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositionLines_ProductUnits",
                        columns: x => new { x.ProductId, x.ProductUnitId },
                        principalTable: "ProductUnits",
                        principalColumns: new[] { "ProductId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositionLines_Products",
                        columns: x => new { x.CompanyId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsignmentDispositionLines_Units",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_Max",
                table: "StockTransferLineBatches",
                sql: "\"ReceivedBaseQty\" + \"DamagedBaseQty\" + \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" + \"AcceptedCorrectionBaseQty\" <= \"DispatchedBaseQty\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_NonNegative",
                table: "StockTransferLineBatches",
                sql: "\"ReceivedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0 AND \"AcceptedCorrectionBaseQty\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipments_Status_Dispatched",
                table: "Shipments",
                sql: "(\"Status\" IN ('Dispatched', 'PartiallyDelivered', 'Delivered', 'Completed')) = (\"DispatchedAt\" IS NOT NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipments_Status_Valid",
                table: "Shipments",
                sql: "\"Status\" IN ('Draft', 'Loading', 'ReadyToDispatch', 'Cancelled', 'Dispatched', 'PartiallyDelivered', 'Delivered', 'Completed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Consignments_Status_Dispatched",
                table: "Consignments",
                sql: "(\"Status\" IN ('InTransit', 'PartiallyReceived', 'Received', 'Reconciled')) = (\"DispatchedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Consignments_Status_Valid",
                table: "Consignments",
                sql: "\"Status\" IN ('Planned', 'Loaded', 'Cancelled', 'InTransit', 'PartiallyReceived', 'Received', 'Reconciled')");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceipts_CompanyId_Status_PendingApproval",
                table: "ConsignmentReceipts",
                columns: new[] { "CompanyId", "Status" },
                filter: "\"Status\" = 'PendingApproval'");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentReceipts_RejectedByUserId",
                table: "ConsignmentReceipts",
                column: "RejectedByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentReceipts_PolicyMode_Valid",
                table: "ConsignmentReceipts",
                sql: "\"PolicyMode\" IN ('Direct', 'SupervisorApproval')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentReceipts_Separation",
                table: "ConsignmentReceipts",
                sql: "NOT \"RequireSeparation\" OR \"PostedByUserId\" IS NULL OR \"PostedByUserId\" <> \"VerifiedByUserId\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentReceipts_Status_Policy",
                table: "ConsignmentReceipts",
                sql: "(\"Status\" <> 'Verified' OR \"PolicyMode\" = 'Direct') AND (\"Status\" <> 'PendingApproval' OR \"PolicyMode\" = 'SupervisorApproval')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentReceipts_Status_Rejected",
                table: "ConsignmentReceipts",
                sql: "(\"Status\" = 'RejectedForCorrection') = (\"RejectedAt\" IS NOT NULL) AND (\"RejectedAt\" IS NULL) = (\"RejectedByUserId\" IS NULL) AND (\"Status\" <> 'RejectedForCorrection' OR COALESCE(char_length(btrim(\"RejectionReason\")), 0) > 0) AND (\"RejectionReason\" IS NULL OR \"Status\" = 'RejectedForCorrection')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentReceipts_Status_Valid",
                table: "ConsignmentReceipts",
                sql: "\"Status\" IN ('Verified', 'Posted', 'PendingApproval', 'RejectedForCorrection')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConsignmentLines_Dispositioned_Valid",
                table: "ConsignmentLines",
                sql: "\"DispositionedDamagedBaseQty\" >= 0 AND \"DispositionedDamagedBaseQty\" <= \"DamagedBaseQty\" AND \"DispositionedRejectedBaseQty\" >= 0 AND \"DispositionedRejectedBaseQty\" <= \"RejectedBaseQty\" AND \"DispositionedMissingBaseQty\" >= 0 AND \"DispositionedMissingBaseQty\" <= \"MissingBaseQty\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BusinessSettings_ReceiptPostingMode_Valid",
                table: "BusinessSettings",
                sql: "\"ReceiptPostingMode\" IN ('Direct', 'SupervisorApproval')");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositionLines_CompanyId_ConsignmentLineId",
                table: "ConsignmentDispositionLines",
                columns: new[] { "CompanyId", "ConsignmentLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositionLines_CompanyId_DispositionId",
                table: "ConsignmentDispositionLines",
                columns: new[] { "CompanyId", "DispositionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositionLines_CompanyId_ProductId",
                table: "ConsignmentDispositionLines",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositionLines_Disposition_Line_Bucket_Action",
                table: "ConsignmentDispositionLines",
                columns: new[] { "DispositionId", "ConsignmentLineId", "Bucket", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositionLines_ProductId_ProductUnitId",
                table: "ConsignmentDispositionLines",
                columns: new[] { "ProductId", "ProductUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositionLines_UnitId",
                table: "ConsignmentDispositionLines",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositions_ByUserId",
                table: "ConsignmentDispositions",
                column: "ByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositions_CompanyId_At",
                table: "ConsignmentDispositions",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositions_CompanyId_ConsignmentId",
                table: "ConsignmentDispositions",
                columns: new[] { "CompanyId", "ConsignmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsignmentDispositions_ConsignmentId_Sequence",
                table: "ConsignmentDispositions",
                columns: new[] { "ConsignmentId", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ConsignmentReceipts_Users_RejectedBy",
                table: "ConsignmentReceipts",
                column: "RejectedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                UPDATE "Consignments" c SET "Status" = 'Reconciled'
                WHERE c."Status" = 'Received'
                  AND NOT EXISTS (SELECT 1 FROM "ConsignmentLines" l WHERE l."ConsignmentId" = c."Id" AND l."DamagedBaseQty" + l."RejectedBaseQty" + l."MissingBaseQty" > 0);
                """);

            migrationBuilder.Sql("""
                UPDATE "Shipments" s SET "Status" = 'Completed'
                WHERE s."Status" = 'Delivered'
                  AND EXISTS (SELECT 1 FROM "Consignments" c WHERE c."ShipmentId" = s."Id" AND c."Status" <> 'Cancelled')
                  AND NOT EXISTS (SELECT 1 FROM "Consignments" c WHERE c."ShipmentId" = s."Id" AND c."Status" NOT IN ('Cancelled', 'Reconciled'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $g$
                DECLARE dp bigint; rc bigint; pol bigint; st bigint; cl bigint; bt bigint;
                BEGIN
                    SELECT COUNT(*) INTO dp FROM "ConsignmentDispositions";
                    SELECT COUNT(*) INTO rc FROM "ConsignmentReceipts" WHERE "Status" IN ('PendingApproval', 'RejectedForCorrection') OR "RejectedAt" IS NOT NULL;
                    SELECT COUNT(*) INTO pol FROM "ConsignmentReceipts" WHERE "PolicyMode" <> 'Direct' OR "RequireSeparation";
                    SELECT COUNT(*) INTO st FROM "BusinessSettings" WHERE "ReceiptPostingMode" <> 'Direct' OR "ReceiptSeparationOfDuties";
                    SELECT COUNT(*) INTO cl FROM "ConsignmentLines" WHERE "DispositionedDamagedBaseQty" <> 0 OR "DispositionedRejectedBaseQty" <> 0 OR "DispositionedMissingBaseQty" <> 0;
                    SELECT COUNT(*) INTO bt FROM "StockTransferLineBatches" WHERE "AcceptedCorrectionBaseQty" <> 0;
                    IF dp > 0 OR rc > 0 OR pol > 0 OR st > 0 OR cl > 0 OR bt > 0 THEN
                        RAISE EXCEPTION 'AddReceiptApprovalAndDispositions cannot be reverted: % disposition(s), % pending/rejected receipt(s), % receipt(s) with a non-default policy snapshot, % company setting(s) using supervisor approval or separation of duties, % consignment line(s) with disposition counters and % batch row(s) with accepted-correction quantity exist. Reverting would lose data.', dp, rc, pol, st, cl, bt;
                    END IF;
                END
                $g$;
                """);

            // Reverse the backfill while the widened CHECKs are still in place.
            migrationBuilder.Sql("""UPDATE "Consignments" SET "Status" = 'Received' WHERE "Status" = 'Reconciled';""");
            migrationBuilder.Sql("""UPDATE "Shipments" SET "Status" = 'Delivered' WHERE "Status" = 'Completed';""");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "ConsignmentReceipts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.DropForeignKey(
                name: "FK_ConsignmentReceipts_Users_RejectedBy",
                table: "ConsignmentReceipts");

            migrationBuilder.DropTable(
                name: "ConsignmentDispositionLines");

            migrationBuilder.DropTable(
                name: "ConsignmentDispositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_Max",
                table: "StockTransferLineBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_NonNegative",
                table: "StockTransferLineBatches");

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

            migrationBuilder.DropIndex(
                name: "IX_ConsignmentReceipts_CompanyId_Status_PendingApproval",
                table: "ConsignmentReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ConsignmentReceipts_RejectedByUserId",
                table: "ConsignmentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentReceipts_PolicyMode_Valid",
                table: "ConsignmentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentReceipts_Separation",
                table: "ConsignmentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentReceipts_Status_Policy",
                table: "ConsignmentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentReceipts_Status_Rejected",
                table: "ConsignmentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentReceipts_Status_Valid",
                table: "ConsignmentReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConsignmentLines_Dispositioned_Valid",
                table: "ConsignmentLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BusinessSettings_ReceiptPostingMode_Valid",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "AcceptedCorrectionBaseQty",
                table: "StockTransferLineBatches");

            migrationBuilder.DropColumn(
                name: "PolicyMode",
                table: "ConsignmentReceipts");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "ConsignmentReceipts");

            migrationBuilder.DropColumn(
                name: "RejectedByUserId",
                table: "ConsignmentReceipts");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "ConsignmentReceipts");

            migrationBuilder.DropColumn(
                name: "RequireSeparation",
                table: "ConsignmentReceipts");

            migrationBuilder.DropColumn(
                name: "DispositionedDamagedBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "DispositionedMissingBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "DispositionedRejectedBaseQty",
                table: "ConsignmentLines");

            migrationBuilder.DropColumn(
                name: "ReceiptPostingMode",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "ReceiptSeparationOfDuties",
                table: "BusinessSettings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_Max",
                table: "StockTransferLineBatches",
                sql: "\"ReceivedBaseQty\" + \"DamagedBaseQty\" + \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" <= \"DispatchedBaseQty\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLineBatches_Counters_NonNegative",
                table: "StockTransferLineBatches",
                sql: "\"ReceivedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0");

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
                name: "CK_ConsignmentReceipts_Status_Valid",
                table: "ConsignmentReceipts",
                sql: "\"Status\" IN ('Verified', 'Posted')");
        }
    }
}
