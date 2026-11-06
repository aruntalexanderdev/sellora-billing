using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Phase 3 / M6 (adjustment part only) - Stock adjustment unit snapshots (one migration, runs in one transaction).
     *   additive : StockAdjustments gains EnteredQuantity numeric(18,3), ProductUnitId uuid, UnitId uuid,
     *              UnitCode varchar(10), ConversionFactorToBase numeric(28,10) (all nullable first) and
     *              EnteredUnitCost numeric(18,4) NULL (cost per ENTERED unit; increases only).
     *   guard    : raises (and rolls back) if an adjusted product has no base ProductUnit row (P2) or an adjustment
     *              quantity is <= 0 (P3).
     *   backfill : every legacy adjustment was entered in base units, so ProductUnitId = the product's base row,
     *              UnitId = that row's UnitId, UnitCode = Units.ShortCode, factor = 1, EnteredQuantity =
     *              QuantityBaseUnits, EnteredUnitCost = UnitCost. The base row's UnitId equals Product.BaseUnitId and
     *              the base unit cannot change once stock history exists (ProductService.UpdateProductAsync), so it
     *              is the unit in force when each adjustment was posted. In-transaction assertions raise on any
     *              row-count change, NULL snapshot or broken EnteredQuantity x factor = QuantityBaseUnits.
     *   enforce  : NOT NULL on the five snapshot columns; composite FK (ProductId, ProductUnitId) -> ProductUnits
     *              (ProductId, Id) via AK_ProductUnits_ProductId_Id (same-product guaranteed by the DB), FK UnitId ->
     *              Units, both Restrict; IX_StockAdjustments_CreatedAt (list ordering); CHECKs EnteredQuantity > 0,
     *              factor > 0, EnteredQuantity x factor = QuantityBaseUnits, (UnitCost IS NULL) = (EnteredUnitCost
     *              IS NULL). IX_StockAdjustments_ProductId is folded into IX_StockAdjustments_ProductId_ProductUnitId.
     * StockAdjustments.UnitCost keeps its meaning (per BASE unit, equals the batch UnitCost) and stays numeric(18,4):
     * it JOINS THE M7 WIDENING LIST (Phase 4, -> numeric(18,6)) together with PurchaseBatches.UnitCost,
     * StockItems.AverageCost, consumptions and PO lines. Constraints SQL cannot express (the entered unit must be
     * active, entered decimals within Unit.DecimalPlaces, base qty storable at 3 dp) are service-level
     * (StockAdjustmentService / StockQuantity).
     *
     * RELEASE NOTE: StockAdjustments is a small table; ADD COLUMN (nullable, no default) and SET NOT NULL do not
     * rewrite it, no large-table rewrite happens. The POST api/v1/stock-adjustments shape breaks (quantity +
     * productUnitId replace quantityBaseUnits), so the API and the SWA must ship together:
     *   1. take a snapshot / Neon branch of the database; 2. run the P-block on it (P2, P3, P5 must be empty; record
     *   P1); 3. apply the idempotent script; 4. run the V-block (V1 must match P1, V2-V6 must return no rows; run
     *   V4 before any new adjustment is posted); 5. deploy the API and the SWA back-to-back; 6. smoke test (opening
     *   stock in a pack unit, damage decrease, adjustment list/detail, stock detail).
     * Rehearse on a Neon branch of prod first. Startup auto-migrate must stay off in production (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: profiling, read-only, run on a prod copy BEFORE the release.
     *
     * -- P1 volume (record; compare in V1)
     * SELECT "Type", "Direction", COUNT(*) FROM "StockAdjustments" GROUP BY 1, 2 ORDER BY 1, 2;
     * -- P2 adjusted product without a base ProductUnit (blocks the migration; must be empty)
     * SELECT a."AdjustmentNo" FROM "StockAdjustments" a
     * WHERE NOT EXISTS (SELECT 1 FROM "ProductUnits" pu WHERE pu."ProductId" = a."ProductId" AND pu."IsBaseUnit");
     * -- P3 non-positive quantity (blocks the migration; must be empty)
     * SELECT "AdjustmentNo" FROM "StockAdjustments" WHERE "QuantityBaseUnits" <= 0;
     * -- P4 cost anomalies (review: increases without cost / decreases with cost; the pairing CHECK still holds
     * --    because EnteredUnitCost is copied from UnitCost)
     * SELECT "AdjustmentNo", "Direction", "UnitCost" FROM "StockAdjustments"
     * WHERE ("Direction" = 'Increase') <> ("UnitCost" IS NOT NULL);
     * -- P5 stock history without a StockItem (must be empty)
     * SELECT DISTINCT m."ProductId" FROM "StockMovements" m LEFT JOIN "StockItems" s USING ("ProductId")
     * WHERE s."ProductId" IS NULL;
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: run AFTER applying. V1 compares with P1; V2-V6 must return no rows.
     *
     * -- V1 row count unchanged (same result as P1)
     * SELECT "Type", "Direction", COUNT(*) FROM "StockAdjustments" GROUP BY 1, 2 ORDER BY 1, 2;
     * -- V2 line arithmetic
     * SELECT "Id" FROM "StockAdjustments" WHERE "EnteredQuantity" * "ConversionFactorToBase" <> "QuantityBaseUnits";
     * -- V3 unit snapshot belongs to the product and matches the ProductUnit
     * SELECT a."Id" FROM "StockAdjustments" a JOIN "ProductUnits" pu ON pu."Id" = a."ProductUnitId"
     * WHERE pu."ProductId" <> a."ProductId" OR pu."UnitId" <> a."UnitId";
     * -- V4 legacy rows are base-unit entries (run before any new posting)
     * SELECT a."Id" FROM "StockAdjustments" a JOIN "ProductUnits" pu ON pu."Id" = a."ProductUnitId"
     * WHERE NOT pu."IsBaseUnit" OR a."ConversionFactorToBase" <> 1;
     * -- V5 increase batches match their adjustment
     * SELECT a."Id" FROM "StockAdjustments" a JOIN "PurchaseBatches" b ON b."StockAdjustmentId" = a."Id"
     * WHERE b."PurchasedBaseQty" <> a."QuantityBaseUnits" OR b."UnitCost" <> a."UnitCost";
     * -- V6 stock invariants and line arithmetic (unchanged by this migration)
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     * SELECT "Id" FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * SELECT "Id" FROM "PurchaseOrderLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down() drops the CHECKs, FKs, indexes and the six columns and restores IX_StockAdjustments_ProductId. Legacy
     * rows lose nothing (every snapshot was derived from QuantityBaseUnits / UnitCost). Adjustments posted in a pack
     * unit after this release lose their entered unit, entered quantity and entered cost; QuantityBaseUnits,
     * UnitCost, batches, movements and StockItems stay intact. The pre-deploy snapshot is the full rollback.
     */
    /// <inheritdoc />
    public partial class AddStockAdjustmentUnitSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 1. additive (nullable) =====
            migrationBuilder.AddColumn<decimal>(
                name: "EnteredQuantity",
                table: "StockAdjustments",
                type: "numeric(18,3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProductUnitId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitCode",
                table: "StockAdjustments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "StockAdjustments",
                type: "numeric(28,10)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EnteredUnitCost",
                table: "StockAdjustments",
                type: "numeric(18,4)",
                nullable: true);

            // ===== 2. guard, backfill, assertions =====
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "StockAdjustments" a WHERE NOT EXISTS
                        (SELECT 1 FROM "ProductUnits" pu WHERE pu."ProductId" = a."ProductId" AND pu."IsBaseUnit")) THEN
                        RAISE EXCEPTION 'AddStockAdjustmentUnitSnapshots: adjustment product without base ProductUnit (run P2).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockAdjustments" WHERE "QuantityBaseUnits" <= 0) THEN
                        RAISE EXCEPTION 'AddStockAdjustmentUnitSnapshots: non-positive adjustment quantity (run P3).';
                    END IF;
                END $g$;

                CREATE TEMP TABLE p3_count AS SELECT COUNT(*) AS n FROM "StockAdjustments";

                UPDATE "StockAdjustments" a
                SET "ProductUnitId" = pu."Id",
                    "UnitId" = pu."UnitId",
                    "UnitCode" = u."ShortCode",
                    "ConversionFactorToBase" = 1,
                    "EnteredQuantity" = a."QuantityBaseUnits",
                    "EnteredUnitCost" = a."UnitCost"
                FROM "ProductUnits" pu
                JOIN "Units" u ON u."Id" = pu."UnitId"
                WHERE pu."ProductId" = a."ProductId" AND pu."IsBaseUnit";

                DO $a$
                DECLARE cnt bigint;
                BEGIN
                    IF (SELECT n FROM p3_count) <> (SELECT COUNT(*) FROM "StockAdjustments") THEN
                        RAISE EXCEPTION 'AddStockAdjustmentUnitSnapshots: row count changed';
                    END IF;
                    SELECT COUNT(*) INTO cnt FROM "StockAdjustments"
                    WHERE "ProductUnitId" IS NULL OR "UnitId" IS NULL OR "UnitCode" IS NULL OR "EnteredQuantity" IS NULL
                       OR "ConversionFactorToBase" IS NULL
                       OR "EnteredQuantity" * "ConversionFactorToBase" <> "QuantityBaseUnits"
                       OR ("UnitCost" IS NULL) <> ("EnteredUnitCost" IS NULL);
                    IF cnt > 0 THEN
                        RAISE EXCEPTION 'AddStockAdjustmentUnitSnapshots: backfill incomplete (% rows)', cnt;
                    END IF;
                END $a$;

                DROP TABLE p3_count;
                """);

            // ===== 3. enforce =====
            migrationBuilder.AlterColumn<decimal>(
                name: "EnteredQuantity",
                table: "StockAdjustments",
                type: "numeric(18,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductUnitId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "UnitId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UnitCode",
                table: "StockAdjustments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "StockAdjustments",
                type: "numeric(28,10)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(28,10)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_ProductId_ProductUnitId",
                table: "StockAdjustments",
                columns: new[] { "ProductId", "ProductUnitId" });

            // Folded into IX_StockAdjustments_ProductId_ProductUnitId (leading column ProductId).
            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_ProductId",
                table: "StockAdjustments");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_UnitId",
                table: "StockAdjustments",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CreatedAt",
                table: "StockAdjustments",
                column: "CreatedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_StockAdjustments_ProductUnits_ProductId_ProductUnitId",
                table: "StockAdjustments",
                columns: new[] { "ProductId", "ProductUnitId" },
                principalTable: "ProductUnits",
                principalColumns: new[] { "ProductId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockAdjustments_Units_UnitId",
                table: "StockAdjustments",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockAdjustments_EnteredQuantity_Positive",
                table: "StockAdjustments",
                sql: "\"EnteredQuantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockAdjustments_Factor_Positive",
                table: "StockAdjustments",
                sql: "\"ConversionFactorToBase\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockAdjustments_BaseQty_Arithmetic",
                table: "StockAdjustments",
                sql: "\"EnteredQuantity\" * \"ConversionFactorToBase\" = \"QuantityBaseUnits\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockAdjustments_Cost_Pairing",
                table: "StockAdjustments",
                sql: "(\"UnitCost\" IS NULL) = (\"EnteredUnitCost\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy only for pack-unit adjustments posted after this release (entered unit/qty/cost); base-unit
            // state (QuantityBaseUnits, UnitCost, batches, movements, StockItems) is untouched. See header.
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockAdjustments_Cost_Pairing",
                table: "StockAdjustments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockAdjustments_BaseQty_Arithmetic",
                table: "StockAdjustments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockAdjustments_Factor_Positive",
                table: "StockAdjustments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockAdjustments_EnteredQuantity_Positive",
                table: "StockAdjustments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockAdjustments_ProductUnits_ProductId_ProductUnitId",
                table: "StockAdjustments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockAdjustments_Units_UnitId",
                table: "StockAdjustments");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_ProductId",
                table: "StockAdjustments",
                column: "ProductId");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_ProductId_ProductUnitId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_UnitId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_CreatedAt",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "EnteredUnitCost",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "ConversionFactorToBase",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "UnitCode",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "ProductUnitId",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "EnteredQuantity",
                table: "StockAdjustments");
        }
    }
}
