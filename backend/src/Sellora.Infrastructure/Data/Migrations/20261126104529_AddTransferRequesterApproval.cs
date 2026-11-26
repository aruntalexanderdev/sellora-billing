using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferRequesterApproval : Migration
    {
        // Two-level transfer approval (FI-D48..FI-D51). Additive only:
        //   BusinessSettings.TransferRequesterApprovalThreshold (numeric(18,2), null = off for every company until it opts in);
        //   StockTransfers.EstimatedValue (numeric(18,2), null for requests submitted before this release), RequiresRequesterApproval (DEFAULT false),
        //   RequesterApprovedAt/RequesterApprovedByUserId (paired, added to CK_StockTransfers_Actors_Paired); Status widened varchar(20) -> varchar(30)
        //   (metadata-only on PostgreSQL) and CK_StockTransfers_Status_Valid += 'AwaitingRequesterApproval'; CK_StockTransfers_RequesterApproval.
        //   Permission StockTransfers.ApproveRequest, granted to every role that holds StockTransfers.Approve (FI-D50). No backfill.
        // Validation (right after Up):
        //   SELECT COUNT(*) FROM "BusinessSettings" WHERE "TransferRequesterApprovalThreshold" IS NOT NULL;                                   -- 0
        //   SELECT COUNT(*) FROM "StockTransfers" WHERE "RequiresRequesterApproval" OR "RequesterApprovedAt" IS NOT NULL OR "EstimatedValue" IS NOT NULL
        //     OR "Status" = 'AwaitingRequesterApproval';                                                                                     -- 0
        //   SELECT COUNT(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" AND p."Code" = 'StockTransfers.Approve'
        //     WHERE NOT EXISTS (SELECT 1 FROM "RolePermissions" x JOIN "Permissions" n ON n."Id" = x."PermissionId" AND n."Code" = 'StockTransfers.ApproveRequest'
        //     WHERE x."RoleId" = rp."RoleId");                                                                                               -- 0
        // Down refuses while any request is AwaitingRequesterApproval or carries a level-1 stamp (that state cannot be represented without these
        // columns); otherwise it drops the additions and the permission rows.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "StockTransfers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedValue",
                table: "StockTransfers",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequesterApprovedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RequesterApprovedByUserId",
                table: "StockTransfers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresRequesterApproval",
                table: "StockTransfers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TransferRequesterApprovalThreshold",
                table: "BusinessSettings",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers",
                sql: "(\"SubmittedAt\" IS NULL) = (\"SubmittedByUserId\" IS NULL) AND (\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"RejectedAt\" IS NULL) = (\"RejectedByUserId\" IS NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL) AND (\"CompletedAt\" IS NULL) = (\"CompletedByUserId\" IS NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL) AND (\"ClosedAt\" IS NULL) = (\"ClosedByUserId\" IS NULL) AND (\"RequesterApprovedAt\" IS NULL) = (\"RequesterApprovedByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_RequesterApproval",
                table: "StockTransfers",
                sql: "(\"Status\" <> 'AwaitingRequesterApproval' OR (\"RequiresRequesterApproval\" AND \"RequesterApprovedAt\" IS NULL)) AND (\"RequiresRequesterApproval\" OR \"RequesterApprovedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers",
                sql: "\"Status\" IN ('Draft', 'Requested', 'Approved', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled', 'PartiallyFulfilled', 'Fulfilled', 'Closed', 'AwaitingRequesterApproval')");

            // FI-D50: the seeder adds the same row and grant on startup (ON CONFLICT keeps this id).
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES
                  (md5('perm:StockTransfers.ApproveRequest')::uuid, 'StockTransfers.ApproveRequest', 'Inventory', 'Approve requests (requesting branch)',
                   'Approve or reject high-value transfer requests of the requesting branch before the source branch decides.')
                ON CONFLICT ("Code") DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId" AND ep."Code" = 'StockTransfers.Approve'
                CROSS JOIN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'StockTransfers.ApproveRequest') np
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM "StockTransfers" WHERE "Status" = 'AwaitingRequesterApproval' OR "RequesterApprovedAt" IS NOT NULL) THEN
                    RAISE EXCEPTION 'AddTransferRequesterApproval Down: requests awaiting or carrying a requesting-branch approval exist; restore the pre-deploy snapshot instead.';
                  END IF;
                END $$;

                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'StockTransfers.ApproveRequest');
                DELETE FROM "Permissions" WHERE "Code" = 'StockTransfers.ApproveRequest';
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_RequesterApproval",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "EstimatedValue",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "RequesterApprovedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "RequesterApprovedByUserId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "RequiresRequesterApproval",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "TransferRequesterApprovalThreshold",
                table: "BusinessSettings");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "StockTransfers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Actors_Paired",
                table: "StockTransfers",
                sql: "(\"SubmittedAt\" IS NULL) = (\"SubmittedByUserId\" IS NULL) AND (\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"RejectedAt\" IS NULL) = (\"RejectedByUserId\" IS NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL) AND (\"CompletedAt\" IS NULL) = (\"CompletedByUserId\" IS NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL) AND (\"ClosedAt\" IS NULL) = (\"ClosedByUserId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransfers_Status_Valid",
                table: "StockTransfers",
                sql: "\"Status\" IN ('Draft', 'Requested', 'Approved', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled', 'PartiallyFulfilled', 'Fulfilled', 'Closed')");
        }
    }
}
