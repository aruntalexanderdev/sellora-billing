using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Phase 4 / M6 (purchase part) - Purchase and purchase-return unit snapshots (one migration, one transaction).
     *   additive : PurchaseOrderLines.ConversionFactorToBase widens numeric(18,3) -> numeric(28,10); PurchaseOrderLines
     *              gains UnitId uuid and UnitCode varchar(10); PurchaseReturnLines gains ProductUnitId uuid, UnitId uuid,
     *              UnitCode varchar(10), ConversionFactorToBase numeric(28,10) (all nullable first).
     *   guard    : raises (and rolls back) on G1 a PO line whose ProductUnit belongs to another product (P2), G2 a PO line
     *              with Qty <= 0 or Qty x factor <> BaseQty (P3), G3 a return line whose ProductId differs from its parent
     *              line's (P4), G4 a return line with Qty x parent factor <> BaseQty (P5).
     *   backfill : PO line UnitId / UnitCode from its ProductUnit / Units.ShortCode; a return line takes its parent line's
     *              ProductUnitId, UnitId, UnitCode and factor (legacy returns were entered in the purchase line's unit).
     *              Assertions raise on any row-count change, NULL snapshot or broken Qty x factor = BaseQty.
     *   enforce  : NOT NULL on the six new columns; the simple FK PurchaseOrderLines.ProductUnitId is replaced by the
     *              composite FK (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id) via AK_ProductUnits_ProductId_Id,
     *              the same composite FK is added on return lines, FKs UnitId -> Units (Restrict); CHECKs Qty > 0 (PO),
     *              factor > 0, Qty x factor = BaseQty and char_length(UnitCode) > 0 on both tables. EF's index diff is kept: IX_*_ProductId and
     *              IX_PurchaseOrderLines_ProductUnitId fold into IX_*_ProductId_ProductUnitId.
     * Not expressible in SQL (service layer): the return unit belongs to the same product as the PURCHASE line (V5; the
     * service copies ProductId), the unit is active, entered decimals fit Unit.DecimalPlaces, base qty fits 3 dp.
     * RatePerBaseUnit is deliberately NOT stored: it is exactly Rate / ConversionFactorToBase (two immutable snapshots).
     *
     * RELEASE NOTE: widening PurchaseOrderLines.ConversionFactorToBase rewrites the table under ACCESS EXCLUSIVE; measure
     * on a production copy. The new NOT NULL columns are added nullable and SET NOT NULL scans (no rewrite). Legacy
     * UnitCode is the CURRENT Units.ShortCode (not necessarily the code at posting time; the unit row has been immutable
     * since it was first used). The GET purchase DTO and the return POST change, so the API and the SWA must ship together.
     * Apply WidenCostPrecision (M7) right after, in the same release:
     *   1. snapshot / Neon branch; 2. run the P-block on it (P2-P5 must be empty; record P1; P7 < 1e12);
     *   3. apply the idempotent script; 4. run the V-block (V1 = P1; V2-V7 empty; V4 before any new return);
     *   5. deploy API and SWA back-to-back; 6. smoke test (purchase in a pack unit, return 37 pc against a carton line).
     * Startup auto-migrate must stay off in production (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P1 volume (record; compare in V1)
     * SELECT (SELECT COUNT(*) FROM "PurchaseOrderLines"), (SELECT COUNT(*) FROM "PurchaseReturnLines");
     * -- P2 (G1) PO line whose ProductUnit belongs to another product (must be empty)
     * SELECT l."Id" FROM "PurchaseOrderLines" l JOIN "ProductUnits" pu ON pu."Id" = l."ProductUnitId" WHERE pu."ProductId" <> l."ProductId";
     * -- P3 (G2) non-positive or inexact PO line quantity (must be empty; fix Qty := BaseQty / factor where exact)
     * SELECT "Id", "Qty", "ConversionFactorToBase", "BaseQty" FROM "PurchaseOrderLines" WHERE "Qty" <= 0 OR "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * -- P4 (G3) return line of another product than its parent (must be empty)
     * SELECT r."Id" FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId" WHERE r."ProductId" <> l."ProductId";
     * -- P5 (G4) return line arithmetic against the parent factor (must be empty)
     * SELECT r."Id" FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId" WHERE r."Qty" * l."ConversionFactorToBase" <> r."BaseQty";
     * -- P6 reconciliation (must be empty)
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     * -- P7 (M7 range, must be < 1e12): see WidenCostPrecision
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying. V1 equals P1; V2-V7 must return no rows.
     *
     * -- V1 row counts (same as P1)
     * SELECT (SELECT COUNT(*) FROM "PurchaseOrderLines"), (SELECT COUNT(*) FROM "PurchaseReturnLines");
     * -- V2 arithmetic, both tables
     * SELECT "Id" FROM "PurchaseOrderLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * SELECT "Id" FROM "PurchaseReturnLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * -- V3 snapshot matches the master, both tables
     * SELECT x."Id" FROM "PurchaseOrderLines" x JOIN "ProductUnits" pu ON pu."Id" = x."ProductUnitId"
     * WHERE pu."ProductId" <> x."ProductId" OR pu."UnitId" <> x."UnitId" OR x."ConversionFactorToBase" <> pu."ConversionFactorToBase";
     * SELECT x."Id" FROM "PurchaseReturnLines" x JOIN "ProductUnits" pu ON pu."Id" = x."ProductUnitId"
     * WHERE pu."ProductId" <> x."ProductId" OR pu."UnitId" <> x."UnitId";
     * -- V4 legacy return line uses its parent's unit (run before any new return is posted)
     * SELECT r."Id" FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId"
     * WHERE r."ProductUnitId" <> l."ProductUnitId" OR r."ConversionFactorToBase" <> l."ConversionFactorToBase";
     * -- V5 return line product equals its parent's
     * SELECT r."Id" FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId" WHERE r."ProductId" <> l."ProductId";
     * -- V6 no over-return
     * SELECT l."Id" FROM "PurchaseOrderLines" l
     * JOIN (SELECT "PurchaseOrderLineId" id, SUM("BaseQty") q FROM "PurchaseReturnLines" GROUP BY 1) r ON r.id = l."Id"
     * WHERE r.q > l."BaseQty";
     * -- V7 reconciliation: the two StockItems queries of P6 and
     * SELECT "Id" FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down() restores the simple PO FK and narrows the factor to numeric(18,3) (exact: Phase 2 rule R5 keeps factors
     * within the base unit's decimals; a RAISE protects against any factor with more than 3 dp). Return lines posted in a
     * unit other than their purchase line's unit are rewritten to Qty = BaseQty / parent factor when that fits 3 dp,
     * otherwise Down RAISES ("restore the snapshot"). Stock state is untouched. The pre-deploy snapshot is the full rollback.
     */
    /// <inheritdoc />
    public partial class AddPurchaseUnitSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 1. additive =====
            migrationBuilder.AlterColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "PurchaseOrderLines",
                type: "numeric(28,10)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)");

            migrationBuilder.AddColumn<Guid>(name: "UnitId", table: "PurchaseOrderLines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "UnitCode", table: "PurchaseOrderLines", type: "character varying(10)", maxLength: 10, nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "ProductUnitId", table: "PurchaseReturnLines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "UnitId", table: "PurchaseReturnLines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "UnitCode", table: "PurchaseReturnLines", type: "character varying(10)", maxLength: 10, nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "ConversionFactorToBase", table: "PurchaseReturnLines", type: "numeric(28,10)", nullable: true);

            // ===== 2. guard, backfill, assertions =====
            // p4_count is a TEMP table created ON COMMIT DROP, so it vanishes with the migration transaction.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "PurchaseOrderLines" l JOIN "ProductUnits" pu ON pu."Id" = l."ProductUnitId"
                               WHERE pu."ProductId" <> l."ProductId") THEN
                        RAISE EXCEPTION 'AddPurchaseUnitSnapshots: PO line whose ProductUnit belongs to another product (run P2).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PurchaseOrderLines" WHERE "Qty" <= 0 OR "Qty" * "ConversionFactorToBase" <> "BaseQty") THEN
                        RAISE EXCEPTION 'AddPurchaseUnitSnapshots: PO line with Qty <= 0 or Qty x factor <> BaseQty (run P3).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId"
                               WHERE r."ProductId" <> l."ProductId") THEN
                        RAISE EXCEPTION 'AddPurchaseUnitSnapshots: return line of another product than its parent line (run P4).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId"
                               WHERE r."Qty" * l."ConversionFactorToBase" <> r."BaseQty") THEN
                        RAISE EXCEPTION 'AddPurchaseUnitSnapshots: return line Qty x parent factor <> BaseQty (run P5).';
                    END IF;
                END $g$;

                CREATE TEMP TABLE p4_count ON COMMIT DROP AS
                SELECT (SELECT COUNT(*) FROM "PurchaseOrderLines") AS pol, (SELECT COUNT(*) FROM "PurchaseReturnLines") AS prl;

                UPDATE "PurchaseOrderLines" l
                SET "UnitId" = pu."UnitId", "UnitCode" = u."ShortCode"
                FROM "ProductUnits" pu JOIN "Units" u ON u."Id" = pu."UnitId"
                WHERE pu."Id" = l."ProductUnitId";

                UPDATE "PurchaseReturnLines" r
                SET "ProductUnitId" = l."ProductUnitId", "UnitId" = l."UnitId",
                    "UnitCode" = l."UnitCode", "ConversionFactorToBase" = l."ConversionFactorToBase"
                FROM "PurchaseOrderLines" l
                WHERE l."Id" = r."PurchaseOrderLineId";

                DO $a$
                BEGIN
                    IF (SELECT pol FROM p4_count) <> (SELECT COUNT(*) FROM "PurchaseOrderLines")
                    OR (SELECT prl FROM p4_count) <> (SELECT COUNT(*) FROM "PurchaseReturnLines")
                    OR EXISTS (SELECT 1 FROM "PurchaseOrderLines"
                               WHERE "UnitId" IS NULL OR "UnitCode" IS NULL OR "Qty" * "ConversionFactorToBase" <> "BaseQty")
                    OR EXISTS (SELECT 1 FROM "PurchaseReturnLines"
                               WHERE "ProductUnitId" IS NULL OR "UnitId" IS NULL OR "UnitCode" IS NULL
                                  OR "ConversionFactorToBase" IS NULL OR "Qty" * "ConversionFactorToBase" <> "BaseQty")
                    THEN
                        RAISE EXCEPTION 'AddPurchaseUnitSnapshots: backfill incomplete';
                    END IF;
                END $a$;
                """);

            // ===== 3. enforce =====
            migrationBuilder.AlterColumn<Guid>(name: "UnitId", table: "PurchaseOrderLines", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "UnitCode", table: "PurchaseOrderLines", type: "character varying(10)", maxLength: 10, nullable: false, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10, oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "ProductUnitId", table: "PurchaseReturnLines", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "UnitId", table: "PurchaseReturnLines", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "UnitCode", table: "PurchaseReturnLines", type: "character varying(10)", maxLength: 10, nullable: false, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10, oldNullable: true);
            migrationBuilder.AlterColumn<decimal>(name: "ConversionFactorToBase", table: "PurchaseReturnLines", type: "numeric(28,10)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(28,10)", oldNullable: true);

            migrationBuilder.DropForeignKey(name: "FK_PurchaseOrderLines_ProductUnits_ProductUnitId", table: "PurchaseOrderLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseReturnLines_ProductId", table: "PurchaseReturnLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrderLines_ProductId", table: "PurchaseOrderLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrderLines_ProductUnitId", table: "PurchaseOrderLines");

            migrationBuilder.CreateIndex(name: "IX_PurchaseReturnLines_ProductId_ProductUnitId", table: "PurchaseReturnLines", columns: new[] { "ProductId", "ProductUnitId" });
            migrationBuilder.CreateIndex(name: "IX_PurchaseReturnLines_UnitId", table: "PurchaseReturnLines", column: "UnitId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrderLines_ProductId_ProductUnitId", table: "PurchaseOrderLines", columns: new[] { "ProductId", "ProductUnitId" });
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrderLines_UnitId", table: "PurchaseOrderLines", column: "UnitId");

            migrationBuilder.AddCheckConstraint(name: "CK_PurchaseOrderLines_Qty_Positive", table: "PurchaseOrderLines", sql: "\"Qty\" > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_PurchaseOrderLines_Factor_Positive", table: "PurchaseOrderLines", sql: "\"ConversionFactorToBase\" > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_PurchaseOrderLines_BaseQty_Arithmetic", table: "PurchaseOrderLines", sql: "\"Qty\" * \"ConversionFactorToBase\" = \"BaseQty\"");
            migrationBuilder.AddCheckConstraint(name: "CK_PurchaseReturnLines_Factor_Positive", table: "PurchaseReturnLines", sql: "\"ConversionFactorToBase\" > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_PurchaseReturnLines_BaseQty_Arithmetic", table: "PurchaseReturnLines", sql: "\"Qty\" * \"ConversionFactorToBase\" = \"BaseQty\"");
            migrationBuilder.AddCheckConstraint(name: "CK_PurchaseOrderLines_UnitCode_NotEmpty", table: "PurchaseOrderLines", sql: "char_length(\"UnitCode\") > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_PurchaseReturnLines_UnitCode_NotEmpty", table: "PurchaseReturnLines", sql: "char_length(\"UnitCode\") > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderLines_ProductUnits_ProductId_ProductUnitId",
                table: "PurchaseOrderLines",
                columns: new[] { "ProductId", "ProductUnitId" },
                principalTable: "ProductUnits",
                principalColumns: new[] { "ProductId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderLines_Units_UnitId",
                table: "PurchaseOrderLines",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturnLines_ProductUnits_ProductId_ProductUnitId",
                table: "PurchaseReturnLines",
                columns: new[] { "ProductId", "ProductUnitId" },
                principalTable: "ProductUnits",
                principalColumns: new[] { "ProductId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturnLines_Units_UnitId",
                table: "PurchaseReturnLines",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Pre-checks first (RAISE rolls the whole Down back). See header for the lossy cases.
            migrationBuilder.Sql("""
                DO $d$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "PurchaseOrderLines" WHERE "ConversionFactorToBase" <> ROUND("ConversionFactorToBase", 3)) THEN
                        RAISE EXCEPTION 'AddPurchaseUnitSnapshots Down: a PO line factor has more than 3 decimals; restore the snapshot.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId"
                               WHERE r."ProductUnitId" <> l."ProductUnitId"
                                 AND ROUND(r."BaseQty" / l."ConversionFactorToBase", 3) * l."ConversionFactorToBase" <> r."BaseQty") THEN
                        RAISE EXCEPTION 'AddPurchaseUnitSnapshots Down: a return line posted in another unit cannot be expressed in its purchase line unit at 3 dp; restore the snapshot.';
                    END IF;
                END $d$;
                """);

            migrationBuilder.DropCheckConstraint(name: "CK_PurchaseReturnLines_BaseQty_Arithmetic", table: "PurchaseReturnLines");
            migrationBuilder.DropCheckConstraint(name: "CK_PurchaseReturnLines_Factor_Positive", table: "PurchaseReturnLines");
            migrationBuilder.DropCheckConstraint(name: "CK_PurchaseReturnLines_UnitCode_NotEmpty", table: "PurchaseReturnLines");

            // Re-express other-unit returns in the purchase line's unit (legacy semantics of Qty).
            migrationBuilder.Sql("""
                UPDATE "PurchaseReturnLines" r
                SET "Qty" = ROUND(r."BaseQty" / l."ConversionFactorToBase", 3)
                FROM "PurchaseOrderLines" l
                WHERE l."Id" = r."PurchaseOrderLineId" AND r."ProductUnitId" <> l."ProductUnitId";
                """);

            migrationBuilder.DropForeignKey(name: "FK_PurchaseOrderLines_ProductUnits_ProductId_ProductUnitId", table: "PurchaseOrderLines");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseOrderLines_Units_UnitId", table: "PurchaseOrderLines");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseReturnLines_ProductUnits_ProductId_ProductUnitId", table: "PurchaseReturnLines");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseReturnLines_Units_UnitId", table: "PurchaseReturnLines");

            migrationBuilder.DropIndex(name: "IX_PurchaseReturnLines_ProductId_ProductUnitId", table: "PurchaseReturnLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseReturnLines_UnitId", table: "PurchaseReturnLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrderLines_ProductId_ProductUnitId", table: "PurchaseOrderLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrderLines_UnitId", table: "PurchaseOrderLines");

            migrationBuilder.DropCheckConstraint(name: "CK_PurchaseOrderLines_BaseQty_Arithmetic", table: "PurchaseOrderLines");
            migrationBuilder.DropCheckConstraint(name: "CK_PurchaseOrderLines_Factor_Positive", table: "PurchaseOrderLines");
            migrationBuilder.DropCheckConstraint(name: "CK_PurchaseOrderLines_UnitCode_NotEmpty", table: "PurchaseOrderLines");
            migrationBuilder.DropCheckConstraint(name: "CK_PurchaseOrderLines_Qty_Positive", table: "PurchaseOrderLines");

            migrationBuilder.DropColumn(name: "ConversionFactorToBase", table: "PurchaseReturnLines");
            migrationBuilder.DropColumn(name: "ProductUnitId", table: "PurchaseReturnLines");
            migrationBuilder.DropColumn(name: "UnitCode", table: "PurchaseReturnLines");
            migrationBuilder.DropColumn(name: "UnitId", table: "PurchaseReturnLines");
            migrationBuilder.DropColumn(name: "UnitCode", table: "PurchaseOrderLines");
            migrationBuilder.DropColumn(name: "UnitId", table: "PurchaseOrderLines");

            migrationBuilder.AlterColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "PurchaseOrderLines",
                type: "numeric(18,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(28,10)");

            migrationBuilder.CreateIndex(name: "IX_PurchaseReturnLines_ProductId", table: "PurchaseReturnLines", column: "ProductId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrderLines_ProductId", table: "PurchaseOrderLines", column: "ProductId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrderLines_ProductUnitId", table: "PurchaseOrderLines", column: "ProductUnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderLines_ProductUnits_ProductUnitId",
                table: "PurchaseOrderLines",
                column: "ProductUnitId",
                principalTable: "ProductUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
