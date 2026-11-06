using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Phase 4 / M7 - Widen the five per-BASE-unit cost columns numeric(18,4) -> numeric(18,6)
     * (StockItems.AverageCost, StockAdjustments.UnitCost, SalesInvoiceLineBatchConsumptions.UnitCost,
     * PurchaseOrderLines.UnitCost, PurchaseBatches.UnitCost). Pure DDL, no backfill: existing values are exact at 6 dp.
     * Money columns and per-ENTERED-unit values (Rate, EnteredUnitCost, CostRemoved, CostRestored, CostOfGoodsSold) stay 18,4.
     *
     * RELEASE NOTE: every ALTER rewrites its table under ACCESS EXCLUSIVE; SalesInvoiceLineBatchConsumptions and
     * PurchaseBatches are likely the largest - measure on a production copy. The integer range drops from 14 to 12
     * digits (check P7). Apply right after AddPurchaseUnitSnapshots in the same release; services round through
     * StockCost.PerBaseUnit so the DB never rounds silently.
     *
     * P7 (read-only, before; each value must be < 1e12):
     * SELECT GREATEST((SELECT MAX(ABS("AverageCost")) FROM "StockItems"),
     *                 (SELECT MAX(ABS("UnitCost")) FROM "StockAdjustments"),
     *                 (SELECT MAX(ABS("UnitCost")) FROM "SalesInvoiceLineBatchConsumptions"),
     *                 (SELECT MAX(ABS("UnitCost")) FROM "PurchaseOrderLines"),
     *                 (SELECT MAX(ABS("UnitCost")) FROM "PurchaseBatches"));
     * V (after; must return 5 rows, all numeric_scale = 6):
     * SELECT table_name, column_name, numeric_scale FROM information_schema.columns
     * WHERE (table_name, column_name) IN (('StockItems','AverageCost'), ('StockAdjustments','UnitCost'),
     *   ('SalesInvoiceLineBatchConsumptions','UnitCost'), ('PurchaseOrderLines','UnitCost'), ('PurchaseBatches','UnitCost'))
     *   AND numeric_scale = 6;
     *
     * Down() narrows back to numeric(18,4) but first RAISES (naming table.column) if any value has more than 4 decimals,
     * i.e. anything written at 6 dp after M7; then the only rollback is the pre-deploy snapshot.
     */
    /// <inheritdoc />
    public partial class WidenCostPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "AverageCost",
                table: "StockItems",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "StockAdjustments",
                type: "numeric(18,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "SalesInvoiceLineBatchConsumptions",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "PurchaseOrderLines",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "PurchaseBatches",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse a lossy narrowing (RAISE rolls the whole Down back); the exact rollback is the snapshot.
            migrationBuilder.Sql("""
                DO $d$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "StockItems" WHERE "AverageCost" <> ROUND("AverageCost", 4)) THEN
                        RAISE EXCEPTION 'WidenCostPrecision Down: StockItems.AverageCost has values with more than 4 decimals; narrowing would round them. Restore the snapshot.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockAdjustments" WHERE "UnitCost" <> ROUND("UnitCost", 4)) THEN
                        RAISE EXCEPTION 'WidenCostPrecision Down: StockAdjustments.UnitCost has values with more than 4 decimals; narrowing would round them. Restore the snapshot.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "SalesInvoiceLineBatchConsumptions" WHERE "UnitCost" <> ROUND("UnitCost", 4)) THEN
                        RAISE EXCEPTION 'WidenCostPrecision Down: SalesInvoiceLineBatchConsumptions.UnitCost has values with more than 4 decimals; narrowing would round them. Restore the snapshot.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PurchaseOrderLines" WHERE "UnitCost" <> ROUND("UnitCost", 4)) THEN
                        RAISE EXCEPTION 'WidenCostPrecision Down: PurchaseOrderLines.UnitCost has values with more than 4 decimals; narrowing would round them. Restore the snapshot.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PurchaseBatches" WHERE "UnitCost" <> ROUND("UnitCost", 4)) THEN
                        RAISE EXCEPTION 'WidenCostPrecision Down: PurchaseBatches.UnitCost has values with more than 4 decimals; narrowing would round them. Restore the snapshot.';
                    END IF;
                END $d$;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "AverageCost",
                table: "StockItems",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "StockAdjustments",
                type: "numeric(18,4)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "SalesInvoiceLineBatchConsumptions",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "PurchaseBatches",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");
        }
    }
}
