using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferRequestFulfilment : Migration
    {
        // FI-D2/D3/D26/D27 (Phase 03). Additive + enforce only: no data is modified, every existing row satisfies the new checks
        // (new columns are NULL / default 0; legacy statuses keep their stamps; Closed 0 keeps Dispatched + Closed <= Requested == old rule).
        // Validation SQL (each must return 0 after Up):
        //   SELECT COUNT(*) FROM "StockTransfers" WHERE "Status" NOT IN ('Draft','Requested','Approved','Rejected','Dispatched','PartiallyReceived','Completed','Cancelled','PartiallyFulfilled','Fulfilled','Closed');
        //   SELECT COUNT(*) FROM "StockTransferLines" WHERE "AllocatedBaseQty" < 0 OR "ClosedBaseQty" < 0 OR "AllocatedBaseQty" + "ClosedBaseQty" > "RequestedBaseQty" OR "DispatchedBaseQty" + "ClosedBaseQty" > "RequestedBaseQty";
        //   SELECT COUNT(*) FROM "StockTransfers" WHERE "ClosedAt" IS NOT NULL OR "ClosedByUserId" IS NOT NULL OR "ClosureReason" IS NOT NULL;   -- 0 right after Up
        // Down REFUSES while any transfer is PartiallyFulfilled/Fulfilled/Closed or any line has Allocated/Closed > 0; otherwise restores the previous schema exactly.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_Destination_Open",
                table: "StockTransfers");

            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_Source_Open",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Status_Approval",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLines_Dispatched_Max",
                table: "StockTransferLines");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClosedByUserId",
                table: "StockTransfers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosureReason",
                table: "StockTransfers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AllocatedBaseQty",
                table: "StockTransferLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ClosedBaseQty",
                table: "StockTransferLines",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_ClosedByUserId",
                table: "StockTransfers",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_Destination_Open",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "DestinationLocationId" },
                filter: "\"Status\" NOT IN ('Completed', 'Cancelled', 'Rejected', 'Fulfilled', 'Closed')");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_Source_Open",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "SourceLocationId" },
                filter: "\"Status\" NOT IN ('Completed', 'Cancelled', 'Rejected', 'Fulfilled', 'Closed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers",
                sql: "(\"SubmittedAt\" IS NULL) = (\"SubmittedByUserId\" IS NULL) AND (\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"RejectedAt\" IS NULL) = (\"RejectedByUserId\" IS NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL) AND (\"CompletedAt\" IS NULL) = (\"CompletedByUserId\" IS NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL) AND (\"ClosedAt\" IS NULL) = (\"ClosedByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Status_Approval",
                table: "StockTransfers",
                sql: "(\"Status\" NOT IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed', 'PartiallyFulfilled', 'Fulfilled', 'Closed') OR \"ApprovedAt\" IS NOT NULL) AND (\"Status\" IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed', 'PartiallyFulfilled', 'Fulfilled', 'Closed', 'Cancelled') OR \"ApprovedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Status_Closure",
                table: "StockTransfers",
                sql: "(\"Status\" = 'Closed') = (\"ClosedAt\" IS NOT NULL) AND (\"Status\" <> 'Closed' OR COALESCE(char_length(\"ClosureReason\"), 0) > 0) AND (\"Status\" = 'Closed' OR \"ClosureReason\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers",
                sql: "\"Status\" IN ('Draft', 'Requested', 'Approved', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled', 'PartiallyFulfilled', 'Fulfilled', 'Closed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLines_Allocation_Valid",
                table: "StockTransferLines",
                sql: "\"AllocatedBaseQty\" >= 0 AND \"ClosedBaseQty\" >= 0 AND \"AllocatedBaseQty\" + \"ClosedBaseQty\" <= \"RequestedBaseQty\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLines_Dispatched_Max",
                table: "StockTransferLines",
                sql: "\"DispatchedBaseQty\" >= 0 AND \"DispatchedBaseQty\" + \"ClosedBaseQty\" <= \"RequestedBaseQty\"");

            migrationBuilder.AddForeignKey(
                name: "FK_StockTransfers_Users_ClosedBy",
                table: "StockTransfers",
                column: "ClosedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $g$
                DECLARE t bigint; l bigint;
                BEGIN
                    SELECT COUNT(*) INTO t FROM "StockTransfers" WHERE "Status" IN ('PartiallyFulfilled', 'Fulfilled', 'Closed') OR "ClosedAt" IS NOT NULL;
                    SELECT COUNT(*) INTO l FROM "StockTransferLines" WHERE "AllocatedBaseQty" > 0 OR "ClosedBaseQty" > 0;
                    IF t > 0 OR l > 0 THEN
                        RAISE EXCEPTION 'AddTransferRequestFulfilment cannot be reverted: % transfer(s) are in a fulfilment status and % line(s) carry allocated/closed quantities. Dropping would lose data.', t, l;
                    END IF;
                END
                $g$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_StockTransfers_Users_ClosedBy",
                table: "StockTransfers");

            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_ClosedByUserId",
                table: "StockTransfers");

            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_Destination_Open",
                table: "StockTransfers");

            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_Source_Open",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Status_Approval",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Status_Closure",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLines_Allocation_Valid",
                table: "StockTransferLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferLines_Dispatched_Max",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ClosedByUserId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ClosureReason",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "AllocatedBaseQty",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "ClosedBaseQty",
                table: "StockTransferLines");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_Destination_Open",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "DestinationLocationId" },
                filter: "\"Status\" NOT IN ('Completed', 'Cancelled', 'Rejected')");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_Source_Open",
                table: "StockTransfers",
                columns: new[] { "CompanyId", "SourceLocationId" },
                filter: "\"Status\" NOT IN ('Completed', 'Cancelled', 'Rejected')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers",
                sql: "(\"SubmittedAt\" IS NULL) = (\"SubmittedByUserId\" IS NULL) AND (\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"RejectedAt\" IS NULL) = (\"RejectedByUserId\" IS NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL) AND (\"CompletedAt\" IS NULL) = (\"CompletedByUserId\" IS NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Status_Approval",
                table: "StockTransfers",
                sql: "(\"Status\" NOT IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed') OR \"ApprovedAt\" IS NOT NULL) AND (\"Status\" IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled') OR \"ApprovedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers",
                sql: "\"Status\" IN ('Draft', 'Requested', 'Approved', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferLines_Dispatched_Max",
                table: "StockTransferLines",
                sql: "\"DispatchedBaseQty\" >= 0 AND \"DispatchedBaseQty\" <= \"RequestedBaseQty\"");
        }
    }
}
