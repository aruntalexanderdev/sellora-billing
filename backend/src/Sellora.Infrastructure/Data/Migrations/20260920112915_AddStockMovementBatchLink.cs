using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockMovementBatchLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseBatchId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            // Link existing movements to their batch where that is unambiguous: purchase receipts/cancellations
            // when the purchase has a single line for the product, and adjustment increases. Purchase returns
            // and anything ambiguous stay null rather than guessed.
            migrationBuilder.Sql("""
                UPDATE "StockMovements" m
                SET "PurchaseBatchId" = x."BatchId"
                FROM (
                    SELECT pol."PurchaseOrderId", pol."ProductId", MIN(b."Id"::text)::uuid AS "BatchId"
                    FROM "PurchaseOrderLines" pol
                    JOIN "PurchaseBatches" b ON b."PurchaseOrderLineId" = pol."Id"
                    GROUP BY pol."PurchaseOrderId", pol."ProductId"
                    HAVING COUNT(*) = 1
                ) x
                WHERE m."ReferenceType" = 'PurchaseOrder' AND m."Type" = 'Purchase'
                  AND m."ReferenceId" = x."PurchaseOrderId" AND m."ProductId" = x."ProductId";

                UPDATE "StockMovements" m
                SET "PurchaseBatchId" = b."Id"
                FROM "PurchaseBatches" b
                WHERE m."ReferenceType" = 'StockAdjustment' AND m."QuantityBaseUnits" > 0
                  AND b."StockAdjustmentId" = m."ReferenceId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_PurchaseBatchId",
                table: "StockMovements",
                column: "PurchaseBatchId");

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_PurchaseBatches_PurchaseBatchId",
                table: "StockMovements",
                column: "PurchaseBatchId",
                principalTable: "PurchaseBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_PurchaseBatches_PurchaseBatchId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_PurchaseBatchId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "PurchaseBatchId",
                table: "StockMovements");
        }
    }
}
