using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferApprovalControls : Migration
    {
        // Transfer approval controls (FI-D46/FI-D47). Additive only:
        //   BusinessSettings.TransferApprovalSeparationOfDuties (NOT NULL DEFAULT true: maker-checker on for every existing company, owner decision);
        //   StockTransfers.RequireApprovalSeparation (NOT NULL DEFAULT false = the snapshot of every existing request, so pending requests are unaffected)
        //   + ApprovalAdjustmentReason (varchar 500, null); StockTransferLines.ApprovedQty / ApprovedBaseQty (numeric(18,3), null = approved before this
        //   release or not yet approved) with CK_StockTransferLines_Approved_Valid. No backfill: history is not recomputed.
        // Validation (right after Up):
        //   SELECT COUNT(*) FROM "BusinessSettings" WHERE NOT "TransferApprovalSeparationOfDuties";                                   -- 0
        //   SELECT COUNT(*) FROM "StockTransfers" WHERE "RequireApprovalSeparation" OR "ApprovalAdjustmentReason" IS NOT NULL;      -- 0
        //   SELECT COUNT(*) FROM "StockTransferLines" WHERE "ApprovedQty" IS NOT NULL OR "ApprovedBaseQty" IS NOT NULL;              -- 0
        // Down drops the additions; approval cuts already written stay as ClosedBaseQty (remaining quantities are unchanged), only the approved
        // snapshot and reason are lost — restore the pre-deploy snapshot if they must be kept.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalAdjustmentReason",
                table: "StockTransfers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireApprovalSeparation",
                table: "StockTransfers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ApprovedBaseQty",
                table: "StockTransferLines",
                type: "numeric(18,3)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ApprovedQty",
                table: "StockTransferLines",
                type: "numeric(18,3)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TransferApprovalSeparationOfDuties",
                table: "BusinessSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true); // owner decision: on for existing companies too (EF model has no default, so the app always writes the value)

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLines_Approved_Valid",
                table: "StockTransferLines",
                sql: "(\"ApprovedQty\" IS NULL AND \"ApprovedBaseQty\" IS NULL) OR (\"ApprovedQty\" >= 0 AND \"ApprovedBaseQty\" >= 0 AND \"ApprovedBaseQty\" <= \"RequestedBaseQty\" AND \"ApprovedQty\" * \"ConversionFactorToBase\" = \"ApprovedBaseQty\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLines_Approved_Valid",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "ApprovalAdjustmentReason",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "RequireApprovalSeparation",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ApprovedBaseQty",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "ApprovedQty",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "TransferApprovalSeparationOfDuties",
                table: "BusinessSettings");
        }
    }
}
