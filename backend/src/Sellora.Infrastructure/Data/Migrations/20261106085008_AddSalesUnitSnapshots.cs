using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Phase 5 / M6 (sales part) - Sales and sales-return unit snapshots, price snapshot, cancellation relabel
     * (one migration, one transaction).
     *   additive : SalesInvoiceLines.ConversionFactorToBase widens numeric(18,3) -> numeric(28,10); SalesInvoiceLines gains
     *              UnitId uuid, UnitCode varchar(10), ListUnitPrice numeric(18,4) NULL, PriceOverridden boolean NOT NULL
     *              DEFAULT false; SalesReturnLines gains ProductUnitId uuid, UnitId uuid, UnitCode varchar(10),
     *              ConversionFactorToBase numeric(28,10) (all nullable first).
     *   guard    : raises (and rolls back) on G1 a sales line whose ProductUnit belongs to another product (P1), G2 a sales
     *              line with Qty <= 0 or Qty x factor <> BaseQty (P2), G3 a return line whose ProductId differs from its
     *              parent line's (P3), G4 a return line with Qty x parent factor <> BaseQty (P4), G5 a sales line with
     *              Discount < 0 or Discount > Qty x UnitPrice (P6; decision Q5: fix the data first, the CHECK stays).
     *   backfill : sales line UnitId / UnitCode from its ProductUnit / Units.ShortCode; a return line takes its parent line's
     *              ProductUnitId, UnitId, UnitCode and factor (legacy returns were entered in the sold line's unit).
     *              ListUnitPrice stays NULL and PriceOverridden false for legacy rows (DV-2: "not recorded").
     *              Legacy cancellations: positive 'Sale' movements with ReferenceType 'SalesInvoice' (only written by
     *              CancelSaleAsync) are relabelled 'SaleCancellation'. Assertions raise on any row-count change, NULL
     *              snapshot, broken Qty x factor = BaseQty or a positive 'Sale' movement left over.
     *   enforce  : NOT NULL on the six new columns; the simple FK SalesInvoiceLines.ProductUnitId is replaced by the
     *              composite FK (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id) via AK_ProductUnits_ProductId_Id,
     *              the same composite FK is added on return lines, FKs UnitId -> Units (Restrict); CHECKs on sales lines:
     *              Qty > 0, factor > 0, char_length(UnitCode) > 0, Qty x factor = BaseQty, override requires a list price,
     *              price equals list unless overridden, 0 <= Discount <= Qty x UnitPrice; on return lines: factor > 0,
     *              char_length(UnitCode) > 0, Qty x factor = BaseQty. IX_*_ProductId (and IX_SalesInvoiceLines_ProductUnitId)
     *              fold into IX_*_ProductId_ProductUnitId, plus IX_*_UnitId.
     * Not expressible in SQL (service layer): the return unit is active, entered decimals fit Unit.DecimalPlaces, base qty
     * fits 3 dp, the price/override permission rule.
     *
     * RELEASE NOTE / MAINTENANCE WINDOW: widening SalesInvoiceLines.ConversionFactorToBase rewrites the whole table (and its
     * indexes) under ACCESS EXCLUSIVE, and the SET NOT NULL / ADD CONSTRAINT steps then scan SalesInvoiceLines and
     * SalesReturnLines. SalesInvoiceLines is the largest transactional table, so plan a maintenance window (POS offline)
     * sized from the rehearsal on a production copy (record the timing). Legacy UnitCode is the CURRENT Units.ShortCode (units
     * referenced by history are immutable). The sales GET/POST contracts change, so API and SWA ship together. Existing roles
     * do NOT receive Sales.PriceOverride (Q2). Order:
     *   1. snapshot / Neon branch; 2. run the P-block on it (P1-P4, P6 must be empty; record P0, P5, P7);
     *   3. apply the idempotent script (this migration; then any later ones); 4. run the V-block (V1 = P0; V2-V8 empty);
     *   5. deploy API and SWA back-to-back; 6. smoke test (sell 2 Box, return 3 Piece, cancel a sale, scan a barcode);
     *   7. also run before release: sellable units without an open price (POS refuses them from now on).
     * Startup auto-migrate must stay off in production (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P0 volume (record; compare in V1)
     * SELECT (SELECT COUNT(*) FROM "SalesInvoiceLines"), (SELECT COUNT(*) FROM "SalesReturnLines"), (SELECT COUNT(*) FROM "StockMovements");
     * -- P1 (G1) sales line whose ProductUnit belongs to another product (must be empty)
     * SELECT l."Id" FROM "SalesInvoiceLines" l JOIN "ProductUnits" pu ON pu."Id" = l."ProductUnitId" WHERE pu."ProductId" <> l."ProductId";
     * -- P2 (G2) non-positive or inexact sales line quantity (must be empty)
     * SELECT "Id", "Qty", "ConversionFactorToBase", "BaseQty" FROM "SalesInvoiceLines" WHERE "Qty" <= 0 OR "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * -- P3 (G3) return line of another product than its parent (must be empty)
     * SELECT r."Id" FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId" WHERE r."ProductId" <> l."ProductId";
     * -- P4 (G4) return line arithmetic against the parent factor (must be empty)
     * SELECT r."Id" FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId" WHERE r."Qty" * l."ConversionFactorToBase" <> r."BaseQty";
     * -- P5 informational: sales lines whose price differs from the open list price (record the count; NOT a guard)
     * SELECT COUNT(*) FROM "SalesInvoiceLines" l JOIN "SellingPrices" p ON p."ProductUnitId" = l."ProductUnitId" AND p."EffectiveTo" IS NULL
     * WHERE p."Price" <> l."UnitPrice";
     * -- P6 (G5) discount outside [0, gross] (must be empty; fix the data, the CHECK stays)
     * SELECT "Id", "Discount", "Qty" * "UnitPrice" AS gross FROM "SalesInvoiceLines" WHERE "Discount" < 0 OR "Discount" > "Qty" * "UnitPrice";
     * -- P7 positive Sale movements (these become SaleCancellation) vs cancelled invoices
     * SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'Sale' AND "ReferenceType" = 'SalesInvoice' AND "QuantityBaseUnits" > 0;
     * SELECT COUNT(*) FROM "SalesInvoiceLines" l JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId" WHERE i."Status" = 'Cancelled';
     * -- P8 reconciliation (must be empty)
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     * -- P9 sellable units without an open price (POS will refuse them; price them first)
     * SELECT pu."Id", pu."ProductId" FROM "ProductUnits" pu WHERE pu."IsActive" AND pu."IsSellable"
     * AND NOT EXISTS (SELECT 1 FROM "SellingPrices" p WHERE p."ProductUnitId" = pu."Id" AND p."EffectiveTo" IS NULL);
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying. V1 equals P0; V2-V8 must return no rows.
     *
     * -- V1 row counts (same as P0)
     * SELECT (SELECT COUNT(*) FROM "SalesInvoiceLines"), (SELECT COUNT(*) FROM "SalesReturnLines"), (SELECT COUNT(*) FROM "StockMovements");
     * -- V2 arithmetic, both tables
     * SELECT "Id" FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * SELECT "Id" FROM "SalesReturnLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * -- V3 sales line snapshot matches the master
     * SELECT l."Id" FROM "SalesInvoiceLines" l JOIN "ProductUnits" pu ON pu."Id" = l."ProductUnitId"
     * WHERE pu."ProductId" <> l."ProductId" OR pu."UnitId" <> l."UnitId" OR pu."ConversionFactorToBase" <> l."ConversionFactorToBase";
     * -- V4 legacy return line uses its parent's unit (run before any new return is posted)
     * SELECT r."Id" FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId"
     * WHERE r."ProductUnitId" <> l."ProductUnitId" OR r."ConversionFactorToBase" <> l."ConversionFactorToBase";
     * -- V5 no over-return
     * SELECT l."Id" FROM "SalesInvoiceLines" l
     * JOIN (SELECT "SalesInvoiceLineId" id, SUM("BaseQty") q FROM "SalesReturnLines" GROUP BY 1) r ON r.id = l."Id"
     * WHERE r.q > l."BaseQty";
     * -- V6 consumption returned equals returned base qty per line
     * SELECT l."Id" FROM "SalesInvoiceLines" l
     * WHERE COALESCE((SELECT SUM("ReturnedQtyBaseUnits") FROM "SalesInvoiceLineBatchConsumptions" c WHERE c."SalesInvoiceLineId" = l."Id"), 0)
     *    <> COALESCE((SELECT SUM("BaseQty") FROM "SalesReturnLines" r WHERE r."SalesInvoiceLineId" = l."Id"), 0);
     * -- V7 no positive 'Sale' movement remains; SaleCancellation count = distinct (cancelled invoice, product) pairs
     * SELECT "Id" FROM "StockMovements" WHERE "Type" = 'Sale' AND "QuantityBaseUnits" > 0;
     * SELECT (SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'SaleCancellation')
     *      - (SELECT COUNT(*) FROM (SELECT DISTINCT l."SalesInvoiceId", l."ProductId" FROM "SalesInvoiceLines" l
     *         JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId" WHERE i."Status" = 'Cancelled') x) AS difference;  -- 0 (cancel writes one movement per product)
     * -- V8 reconciliation: the two StockItems queries of P8
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down() restores the simple FK and narrows the factor to numeric(18,3) (a RAISE protects against any factor with more
     * than 3 dp). Return lines posted in a unit other than their sold line's unit are rewritten to Qty = BaseQty / parent
     * factor when that fits 3 dp, otherwise Down RAISES ("restore the snapshot"). SaleCancellation movements are relabelled
     * 'Sale' again. ListUnitPrice, PriceOverridden, UnitId and UnitCode are LOST on Down. The pre-deploy snapshot is the
     * full rollback.
     */
    /// <inheritdoc />
    public partial class AddSalesUnitSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 1. additive =====
            migrationBuilder.AlterColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "SalesInvoiceLines",
                type: "numeric(28,10)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)");

            migrationBuilder.AddColumn<Guid>(name: "UnitId", table: "SalesInvoiceLines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "UnitCode", table: "SalesInvoiceLines", type: "character varying(10)", maxLength: 10, nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "ListUnitPrice", table: "SalesInvoiceLines", type: "numeric(18,4)", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "PriceOverridden", table: "SalesInvoiceLines", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<Guid>(name: "ProductUnitId", table: "SalesReturnLines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "UnitId", table: "SalesReturnLines", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "UnitCode", table: "SalesReturnLines", type: "character varying(10)", maxLength: 10, nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "ConversionFactorToBase", table: "SalesReturnLines", type: "numeric(28,10)", nullable: true);

            // ===== 2. guard, backfill, assertions =====
            // p5_count is a TEMP table created ON COMMIT DROP, so it vanishes with the migration transaction.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "SalesInvoiceLines" l JOIN "ProductUnits" pu ON pu."Id" = l."ProductUnitId"
                               WHERE pu."ProductId" <> l."ProductId") THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots: sales line whose ProductUnit belongs to another product (run P1).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "SalesInvoiceLines" WHERE "Qty" <= 0 OR "Qty" * "ConversionFactorToBase" <> "BaseQty") THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots: sales line with Qty <= 0 or Qty x factor <> BaseQty (run P2).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId"
                               WHERE r."ProductId" <> l."ProductId") THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots: return line of another product than its parent line (run P3).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId"
                               WHERE r."Qty" * l."ConversionFactorToBase" <> r."BaseQty") THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots: return line Qty x parent factor <> BaseQty (run P4).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "SalesInvoiceLines" WHERE "Discount" < 0 OR "Discount" > "Qty" * "UnitPrice") THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots: sales line with Discount < 0 or Discount > Qty x UnitPrice; fix the data first (run P6).';
                    END IF;
                END $g$;

                CREATE TEMP TABLE p5_count ON COMMIT DROP AS
                SELECT (SELECT COUNT(*) FROM "SalesInvoiceLines") AS sil, (SELECT COUNT(*) FROM "SalesReturnLines") AS srl,
                       (SELECT COUNT(*) FROM "StockMovements") AS mv;

                UPDATE "SalesInvoiceLines" l
                SET "UnitId" = pu."UnitId", "UnitCode" = u."ShortCode"
                FROM "ProductUnits" pu JOIN "Units" u ON u."Id" = pu."UnitId"
                WHERE pu."Id" = l."ProductUnitId";

                UPDATE "SalesReturnLines" r
                SET "ProductUnitId" = l."ProductUnitId", "UnitId" = l."UnitId",
                    "UnitCode" = l."UnitCode", "ConversionFactorToBase" = l."ConversionFactorToBase"
                FROM "SalesInvoiceLines" l
                WHERE l."Id" = r."SalesInvoiceLineId";

                UPDATE "StockMovements" SET "Type" = 'SaleCancellation'
                WHERE "Type" = 'Sale' AND "ReferenceType" = 'SalesInvoice' AND "QuantityBaseUnits" > 0;

                DO $a$
                BEGIN
                    IF (SELECT sil FROM p5_count) <> (SELECT COUNT(*) FROM "SalesInvoiceLines")
                    OR (SELECT srl FROM p5_count) <> (SELECT COUNT(*) FROM "SalesReturnLines")
                    OR (SELECT mv FROM p5_count) <> (SELECT COUNT(*) FROM "StockMovements")
                    OR EXISTS (SELECT 1 FROM "SalesInvoiceLines"
                               WHERE "UnitId" IS NULL OR "UnitCode" IS NULL OR "Qty" * "ConversionFactorToBase" <> "BaseQty")
                    OR EXISTS (SELECT 1 FROM "SalesReturnLines"
                               WHERE "ProductUnitId" IS NULL OR "UnitId" IS NULL OR "UnitCode" IS NULL
                                  OR "ConversionFactorToBase" IS NULL OR "Qty" * "ConversionFactorToBase" <> "BaseQty")
                    OR EXISTS (SELECT 1 FROM "StockMovements" WHERE "Type" = 'Sale' AND "QuantityBaseUnits" > 0)
                    THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots: backfill incomplete';
                    END IF;
                END $a$;
                """);

            // ===== 3. enforce =====
            migrationBuilder.AlterColumn<Guid>(name: "UnitId", table: "SalesInvoiceLines", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "UnitCode", table: "SalesInvoiceLines", type: "character varying(10)", maxLength: 10, nullable: false, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10, oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "ProductUnitId", table: "SalesReturnLines", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "UnitId", table: "SalesReturnLines", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "UnitCode", table: "SalesReturnLines", type: "character varying(10)", maxLength: 10, nullable: false, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10, oldNullable: true);
            migrationBuilder.AlterColumn<decimal>(name: "ConversionFactorToBase", table: "SalesReturnLines", type: "numeric(28,10)", nullable: false, oldClrType: typeof(decimal), oldType: "numeric(28,10)", oldNullable: true);

            migrationBuilder.DropForeignKey(name: "FK_SalesInvoiceLines_ProductUnits_ProductUnitId", table: "SalesInvoiceLines");
            migrationBuilder.DropIndex(name: "IX_SalesReturnLines_ProductId", table: "SalesReturnLines");
            migrationBuilder.DropIndex(name: "IX_SalesInvoiceLines_ProductId", table: "SalesInvoiceLines");
            migrationBuilder.DropIndex(name: "IX_SalesInvoiceLines_ProductUnitId", table: "SalesInvoiceLines");

            migrationBuilder.CreateIndex(name: "IX_SalesReturnLines_ProductId_ProductUnitId", table: "SalesReturnLines", columns: new[] { "ProductId", "ProductUnitId" });
            migrationBuilder.CreateIndex(name: "IX_SalesReturnLines_UnitId", table: "SalesReturnLines", column: "UnitId");
            migrationBuilder.CreateIndex(name: "IX_SalesInvoiceLines_ProductId_ProductUnitId", table: "SalesInvoiceLines", columns: new[] { "ProductId", "ProductUnitId" });
            migrationBuilder.CreateIndex(name: "IX_SalesInvoiceLines_UnitId", table: "SalesInvoiceLines", column: "UnitId");

            migrationBuilder.AddCheckConstraint(name: "CK_SalesInvoiceLines_Qty_Positive", table: "SalesInvoiceLines", sql: "\"Qty\" > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesInvoiceLines_Factor_Positive", table: "SalesInvoiceLines", sql: "\"ConversionFactorToBase\" > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesInvoiceLines_UnitCode_NotEmpty", table: "SalesInvoiceLines", sql: "char_length(\"UnitCode\") > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesInvoiceLines_BaseQty_Arithmetic", table: "SalesInvoiceLines", sql: "\"Qty\" * \"ConversionFactorToBase\" = \"BaseQty\"");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesInvoiceLines_Override_HasList", table: "SalesInvoiceLines", sql: "NOT \"PriceOverridden\" OR \"ListUnitPrice\" IS NOT NULL");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesInvoiceLines_Price_MatchesList", table: "SalesInvoiceLines", sql: "\"PriceOverridden\" OR \"ListUnitPrice\" IS NULL OR \"UnitPrice\" = \"ListUnitPrice\"");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesInvoiceLines_Discount_WithinGross", table: "SalesInvoiceLines", sql: "\"Discount\" >= 0 AND \"Discount\" <= \"Qty\" * \"UnitPrice\"");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesReturnLines_Factor_Positive", table: "SalesReturnLines", sql: "\"ConversionFactorToBase\" > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesReturnLines_UnitCode_NotEmpty", table: "SalesReturnLines", sql: "char_length(\"UnitCode\") > 0");
            migrationBuilder.AddCheckConstraint(name: "CK_SalesReturnLines_BaseQty_Arithmetic", table: "SalesReturnLines", sql: "\"Qty\" * \"ConversionFactorToBase\" = \"BaseQty\"");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoiceLines_ProductUnits_ProductId_ProductUnitId",
                table: "SalesInvoiceLines",
                columns: new[] { "ProductId", "ProductUnitId" },
                principalTable: "ProductUnits",
                principalColumns: new[] { "ProductId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoiceLines_Units_UnitId",
                table: "SalesInvoiceLines",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReturnLines_ProductUnits_ProductId_ProductUnitId",
                table: "SalesReturnLines",
                columns: new[] { "ProductId", "ProductUnitId" },
                principalTable: "ProductUnits",
                principalColumns: new[] { "ProductId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReturnLines_Units_UnitId",
                table: "SalesReturnLines",
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
                    IF EXISTS (SELECT 1 FROM "SalesInvoiceLines" WHERE "ConversionFactorToBase" <> ROUND("ConversionFactorToBase", 3)) THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots Down: a sales line factor has more than 3 decimals; restore the snapshot.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId"
                               WHERE r."ProductUnitId" <> l."ProductUnitId"
                                 AND ROUND(r."BaseQty" / l."ConversionFactorToBase", 3) * l."ConversionFactorToBase" <> r."BaseQty") THEN
                        RAISE EXCEPTION 'AddSalesUnitSnapshots Down: a return line posted in another unit cannot be expressed in its sold line unit at 3 dp; restore the snapshot.';
                    END IF;
                END $d$;
                """);

            migrationBuilder.DropCheckConstraint(name: "CK_SalesReturnLines_BaseQty_Arithmetic", table: "SalesReturnLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesReturnLines_Factor_Positive", table: "SalesReturnLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesReturnLines_UnitCode_NotEmpty", table: "SalesReturnLines");

            // Re-express other-unit returns in the sold line's unit (legacy semantics of Qty), and relabel cancellations.
            migrationBuilder.Sql("""
                UPDATE "SalesReturnLines" r
                SET "Qty" = ROUND(r."BaseQty" / l."ConversionFactorToBase", 3)
                FROM "SalesInvoiceLines" l
                WHERE l."Id" = r."SalesInvoiceLineId" AND r."ProductUnitId" <> l."ProductUnitId";

                UPDATE "StockMovements" SET "Type" = 'Sale' WHERE "Type" = 'SaleCancellation';
                """);

            migrationBuilder.DropForeignKey(name: "FK_SalesInvoiceLines_ProductUnits_ProductId_ProductUnitId", table: "SalesInvoiceLines");
            migrationBuilder.DropForeignKey(name: "FK_SalesInvoiceLines_Units_UnitId", table: "SalesInvoiceLines");
            migrationBuilder.DropForeignKey(name: "FK_SalesReturnLines_ProductUnits_ProductId_ProductUnitId", table: "SalesReturnLines");
            migrationBuilder.DropForeignKey(name: "FK_SalesReturnLines_Units_UnitId", table: "SalesReturnLines");

            migrationBuilder.DropIndex(name: "IX_SalesReturnLines_ProductId_ProductUnitId", table: "SalesReturnLines");
            migrationBuilder.DropIndex(name: "IX_SalesReturnLines_UnitId", table: "SalesReturnLines");
            migrationBuilder.DropIndex(name: "IX_SalesInvoiceLines_ProductId_ProductUnitId", table: "SalesInvoiceLines");
            migrationBuilder.DropIndex(name: "IX_SalesInvoiceLines_UnitId", table: "SalesInvoiceLines");

            migrationBuilder.DropCheckConstraint(name: "CK_SalesInvoiceLines_BaseQty_Arithmetic", table: "SalesInvoiceLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesInvoiceLines_Discount_WithinGross", table: "SalesInvoiceLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesInvoiceLines_Factor_Positive", table: "SalesInvoiceLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesInvoiceLines_Override_HasList", table: "SalesInvoiceLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesInvoiceLines_Price_MatchesList", table: "SalesInvoiceLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesInvoiceLines_Qty_Positive", table: "SalesInvoiceLines");
            migrationBuilder.DropCheckConstraint(name: "CK_SalesInvoiceLines_UnitCode_NotEmpty", table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(name: "ConversionFactorToBase", table: "SalesReturnLines");
            migrationBuilder.DropColumn(name: "ProductUnitId", table: "SalesReturnLines");
            migrationBuilder.DropColumn(name: "UnitCode", table: "SalesReturnLines");
            migrationBuilder.DropColumn(name: "UnitId", table: "SalesReturnLines");
            migrationBuilder.DropColumn(name: "ListUnitPrice", table: "SalesInvoiceLines");
            migrationBuilder.DropColumn(name: "PriceOverridden", table: "SalesInvoiceLines");
            migrationBuilder.DropColumn(name: "UnitCode", table: "SalesInvoiceLines");
            migrationBuilder.DropColumn(name: "UnitId", table: "SalesInvoiceLines");

            migrationBuilder.AlterColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "SalesInvoiceLines",
                type: "numeric(18,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(28,10)");

            migrationBuilder.CreateIndex(name: "IX_SalesReturnLines_ProductId", table: "SalesReturnLines", column: "ProductId");
            migrationBuilder.CreateIndex(name: "IX_SalesInvoiceLines_ProductId", table: "SalesInvoiceLines", column: "ProductId");
            migrationBuilder.CreateIndex(name: "IX_SalesInvoiceLines_ProductUnitId", table: "SalesInvoiceLines", column: "ProductUnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoiceLines_ProductUnits_ProductUnitId",
                table: "SalesInvoiceLines",
                column: "ProductUnitId",
                principalTable: "ProductUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
