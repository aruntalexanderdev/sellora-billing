using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Phase 6a / M8 - Stock ledger enrichment: new movement types, reason code, entered-unit snapshot (one migration, one
     * transaction).
     *   additive : StockMovements gains ReasonCode varchar(20), EnteredQuantity numeric(18,3), EnteredProductUnitId uuid,
     *              EnteredUnitCode varchar(10), ConversionFactorSnapshot numeric(28,10) (all nullable; metadata-only in PG).
     *              Movement types 'PurchaseCancellation' and 'OpeningStock' need no DDL (string enum).
     *   guard    : raises (and rolls back) on G1 an 'Adjustment' movement without its StockAdjustment (P1), G2 a negative
     *              'Purchase' movement that is not the cancellation of a cancelled purchase (P2), G3 a movement whose sign
     *              contradicts its type (P3). P4 (reconciliation) is recorded, not a failure: products already out of
     *              balance are remembered so the V-block only fails on NEW breakage.
     *   backfill : every statement is idempotent (relabels filter on the old Type, entered updates on EnteredProductUnitId IS
     *              NULL, ReasonCode on ReasonCode IS NULL).
     *              B1 negative 'Purchase' on a PurchaseOrder -> 'PurchaseCancellation'.
     *              B2 ReasonCode = StockAdjustments.Type for ReferenceType 'StockAdjustment' rows.
     *              B3 'Adjustment' rows with ReasonCode 'OpeningStock' -> 'OpeningStock'.
     *              B4 adjustment rows take the adjustment's unit snapshot; EnteredQuantity only when the row carries the whole
     *                 adjustment (a decrease split over batches keeps the unit context only; 6a-D2).
     *              B5 purchase / cancellation rows with a batch: exact via PurchaseBatches.PurchaseOrderLineId.
     *              B6 every other row (purchase without batch, purchase return, sale, sale cancellation, sale return): filled
     *                 only when ALL candidate source lines (same document, same product, BaseQty = |movement qty|) agree on
     *                 unit, qty and factor; ambiguous rows and legacy aggregated sale cancellations stay NULL (history is
     *                 never guessed).
     *   enforce  : composite FK (ProductId, EnteredProductUnitId) -> ProductUnits (ProductId, Id), IX_StockMovements_
     *              ProductId_EnteredProductUnitId and IX_StockMovements_ReferenceType_ReferenceId, CHECKs: unit fields
     *              all-or-none, EnteredQuantity requires a unit and is positive, factor positive, code not empty,
     *              EnteredQuantity x factor = |QuantityBaseUnits|, reason-code rules, sign per movement type.
     * Not expressible in SQL (service layer): entered decimals fit Unit.DecimalPlaces; new rows always carry the unit
     * context (decision Q1: no NOT VALID constraint; tests + post-deploy query V9 instead).
     *
     * RELEASE NOTE: CREATE INDEX on StockMovements takes a write lock and the CHECKs / FK scan the table once; volumes are
     * small, but time the rehearsal on a production copy. Sale cancellations written after this release are one row PER
     * SALES LINE (legacy rows stay one per product); no consumer counts rows. The stock-detail and stock-movement report
     * contracts gain ReasonCode, EnteredQuantity, EnteredProductUnitId, EnteredUnitCode, ConversionFactorSnapshot,
     * EnteredDecimalPlaces, so API and SWA ship together. Order:
     *   1. snapshot / Neon branch; 2. run the P-block on it (P1-P3 must be empty; record P0, P4);
     *   3. apply the idempotent script; 4. run the V-block (V1 = P0; V2-V8 empty); record the coverage query (V10);
     *   5. deploy API and SWA back-to-back; 6. smoke test (purchase 2 Box, cancel it, sell, return, cancel a sale, post an
     *   opening stock and a damage adjustment) and run V9 (must be 0).
     * Startup auto-migrate must stay off in production (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P0 volume
     * SELECT COUNT(*) FROM "StockMovements";
     * -- P1 adjustment movement without its adjustment (must be empty)
     * SELECT m."Id" FROM "StockMovements" m WHERE m."Type" = 'Adjustment'
     *   AND (m."ReferenceType" <> 'StockAdjustment' OR NOT EXISTS (SELECT 1 FROM "StockAdjustments" a WHERE a."Id" = m."ReferenceId"));
     * -- P2 negative purchase movement that is not a cancellation (must be empty)
     * SELECT m."Id" FROM "StockMovements" m WHERE m."Type" = 'Purchase' AND m."QuantityBaseUnits" < 0
     *   AND (m."ReferenceType" <> 'PurchaseOrder'
     *        OR NOT EXISTS (SELECT 1 FROM "PurchaseOrders" p WHERE p."Id" = m."ReferenceId" AND p."Status" = 'Cancelled'));
     * -- P3 sign violations (must be empty)
     * SELECT "Id", "Type", "QuantityBaseUnits" FROM "StockMovements"
     * WHERE ("Type" = 'Sale' AND "QuantityBaseUnits" >= 0) OR ("Type" IN ('SaleReturn', 'SaleCancellation') AND "QuantityBaseUnits" <= 0)
     *    OR ("Type" = 'PurchaseReturn' AND "QuantityBaseUnits" >= 0) OR ("Type" = 'Purchase' AND "QuantityBaseUnits" = 0)
     *    OR ("Type" = 'Adjustment' AND "QuantityBaseUnits" <= 0 AND EXISTS (SELECT 1 FROM "StockAdjustments" a WHERE a."Id" = "ReferenceId" AND a."Type" = 'OpeningStock'));
     * -- P4 reconciliation (record the product ids; V8 only fails on products NOT listed here)
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     * -- P5 informational: legacy row counts per type (compare with the V10 coverage)
     * SELECT "Type", COUNT(*) FROM "StockMovements" GROUP BY 1;
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying. V1 equals P0; V2-V8 must return no rows.
     *
     * -- V1 row count (same as P0)
     * SELECT COUNT(*) FROM "StockMovements";
     * -- V2 no negative 'Purchase' movement remains
     * SELECT "Id" FROM "StockMovements" WHERE "Type" = 'Purchase' AND "QuantityBaseUnits" < 0;
     * -- V3 every stock-adjustment movement has a reason code; none is 'Adjustment' with reason 'OpeningStock'
     * SELECT "Id" FROM "StockMovements" WHERE "ReferenceType" = 'StockAdjustment' AND "ReasonCode" IS NULL;
     * SELECT "Id" FROM "StockMovements" WHERE "Type" = 'Adjustment' AND "ReasonCode" = 'OpeningStock';
     * -- V4 entered arithmetic
     * SELECT "Id" FROM "StockMovements" WHERE "EnteredQuantity" IS NOT NULL AND "EnteredQuantity" * "ConversionFactorSnapshot" <> abs("QuantityBaseUnits");
     * -- V5 unit fields all-or-none
     * SELECT "Id" FROM "StockMovements" WHERE ("EnteredProductUnitId" IS NULL) <> ("EnteredUnitCode" IS NULL)
     *    OR ("EnteredProductUnitId" IS NULL) <> ("ConversionFactorSnapshot" IS NULL);
     * -- V6 entered unit belongs to the movement's product
     * SELECT m."Id" FROM "StockMovements" m JOIN "ProductUnits" pu ON pu."Id" = m."EnteredProductUnitId" WHERE pu."ProductId" <> m."ProductId";
     * -- V7 per-product sum unchanged: run the ledger-vs-StockItems query of P4
     * -- V8 reconciliation (must be empty except products already listed by P4): the two queries of P4
     * -- V9 post-deploy acceptance (must be 0; replace the timestamp with the deploy time)
     * SELECT COUNT(*) FROM "StockMovements" WHERE "OccurredAt" >= TIMESTAMPTZ '<deploy time>' AND "EnteredProductUnitId" IS NULL;
     * -- V10 coverage report per type (record; not a failure)
     * SELECT "Type", COUNT(*) FILTER (WHERE "EnteredProductUnitId" IS NULL) AS without_unit,
     *        COUNT(*) FILTER (WHERE "EnteredQuantity" IS NULL) AS without_qty, COUNT(*) AS total
     * FROM "StockMovements" GROUP BY 1 ORDER BY 1;
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down() relabels 'OpeningStock' -> 'Adjustment' and 'PurchaseCancellation' -> 'Purchase' (the pre-6a labels; lossless),
     * then removes the CHECKs, FK, both indexes and the five columns. ReasonCode and entered fields are LOST on Down but are
     * recomputed by Up. Per-line SaleCancellation rows keep their label here (Phase 5 Down relabels them).
     */
    /// <inheritdoc />
    public partial class AddMovementTypesAndReasonCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 1. additive =====
            migrationBuilder.AddColumn<decimal>(name: "ConversionFactorSnapshot", table: "StockMovements", type: "numeric(28,10)", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "EnteredProductUnitId", table: "StockMovements", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "EnteredQuantity", table: "StockMovements", type: "numeric(18,3)", nullable: true);
            migrationBuilder.AddColumn<string>(name: "EnteredUnitCode", table: "StockMovements", type: "character varying(10)", maxLength: 10, nullable: true);
            migrationBuilder.AddColumn<string>(name: "ReasonCode", table: "StockMovements", type: "character varying(20)", maxLength: 20, nullable: true);

            // ===== 2. guard, backfill, assertions =====
            // The temp tables are ON COMMIT DROP, so they vanish with the migration transaction.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "StockMovements" m WHERE m."Type" = 'Adjustment'
                               AND (m."ReferenceType" <> 'StockAdjustment'
                                    OR NOT EXISTS (SELECT 1 FROM "StockAdjustments" a WHERE a."Id" = m."ReferenceId"))) THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: an Adjustment movement has no StockAdjustment (run P1).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockMovements" m WHERE m."Type" = 'Purchase' AND m."QuantityBaseUnits" < 0
                               AND (m."ReferenceType" <> 'PurchaseOrder'
                                    OR NOT EXISTS (SELECT 1 FROM "PurchaseOrders" p WHERE p."Id" = m."ReferenceId" AND p."Status" = 'Cancelled'))) THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: a negative Purchase movement is not the cancellation of a cancelled purchase (run P2).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockMovements"
                               WHERE ("Type" = 'Sale' AND "QuantityBaseUnits" >= 0)
                                  OR ("Type" IN ('SaleReturn', 'SaleCancellation') AND "QuantityBaseUnits" <= 0)
                                  OR ("Type" = 'PurchaseReturn' AND "QuantityBaseUnits" >= 0)
                                  OR ("Type" = 'Purchase' AND "QuantityBaseUnits" = 0)
                                  OR ("Type" = 'Adjustment' AND "QuantityBaseUnits" <= 0 AND EXISTS (SELECT 1 FROM "StockAdjustments" a WHERE a."Id" = "ReferenceId" AND a."Type" = 'OpeningStock'))) THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: a movement sign contradicts its type (run P3).';
                    END IF;
                END $g$;

                CREATE TEMP TABLE p6_count ON COMMIT DROP AS SELECT COUNT(*) AS mv FROM "StockMovements";

                CREATE TEMP TABLE p6_sum ON COMMIT DROP AS
                SELECT "ProductId", SUM("QuantityBaseUnits") AS q FROM "StockMovements" GROUP BY 1;

                -- P4: products already out of balance before this migration (recorded, not a failure)
                CREATE TEMP TABLE p6_unbalanced ON COMMIT DROP AS
                SELECT s."ProductId" FROM "StockItems" s
                LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
                WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
                UNION
                SELECT s."ProductId" FROM "StockItems" s
                LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
                WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);

                -- B1: cancellation of a purchase was written as a negative 'Purchase' movement
                UPDATE "StockMovements" SET "Type" = 'PurchaseCancellation'
                WHERE "Type" = 'Purchase' AND "QuantityBaseUnits" < 0 AND "ReferenceType" = 'PurchaseOrder';

                -- B2: reason code from the adjustment
                UPDATE "StockMovements" m SET "ReasonCode" = a."Type"
                FROM "StockAdjustments" a
                WHERE m."ReferenceType" = 'StockAdjustment' AND a."Id" = m."ReferenceId" AND m."ReasonCode" IS NULL;

                -- B3: opening stock gets its own movement type
                UPDATE "StockMovements" SET "Type" = 'OpeningStock'
                WHERE "Type" = 'Adjustment' AND "ReferenceType" = 'StockAdjustment' AND "ReasonCode" = 'OpeningStock';

                -- B4: adjustments: unit context on every row, entered quantity only when the row carries the whole adjustment
                UPDATE "StockMovements" m
                SET "EnteredProductUnitId" = a."ProductUnitId", "EnteredUnitCode" = a."UnitCode",
                    "ConversionFactorSnapshot" = a."ConversionFactorToBase",
                    "EnteredQuantity" = CASE WHEN a."QuantityBaseUnits" = abs(m."QuantityBaseUnits") THEN a."EnteredQuantity" END
                FROM "StockAdjustments" a
                WHERE m."ReferenceType" = 'StockAdjustment' AND a."Id" = m."ReferenceId" AND a."ProductId" = m."ProductId"
                  AND a."EnteredQuantity" * a."ConversionFactorToBase" = a."QuantityBaseUnits"
                  AND m."EnteredProductUnitId" IS NULL;

                -- B5: purchase and purchase cancellation rows with a batch: exact through batch -> purchase line
                UPDATE "StockMovements" m
                SET "EnteredProductUnitId" = l."ProductUnitId", "EnteredUnitCode" = l."UnitCode",
                    "ConversionFactorSnapshot" = l."ConversionFactorToBase",
                    "EnteredQuantity" = CASE WHEN l."BaseQty" = abs(m."QuantityBaseUnits") THEN l."Qty" END
                FROM "PurchaseBatches" b JOIN "PurchaseOrderLines" l ON l."Id" = b."PurchaseOrderLineId"
                WHERE m."Type" IN ('Purchase', 'PurchaseCancellation') AND m."ReferenceType" = 'PurchaseOrder'
                  AND b."Id" = m."PurchaseBatchId" AND l."PurchaseOrderId" = m."ReferenceId" AND l."ProductId" = m."ProductId"
                  AND l."Qty" * l."ConversionFactorToBase" = l."BaseQty"
                  AND m."EnteredProductUnitId" IS NULL;

                -- B6: the rest, only where every candidate source line agrees (same document, product, BaseQty = |qty|)
                UPDATE "StockMovements" m
                SET "EnteredProductUnitId" = x.pu, "EnteredUnitCode" = x.code, "ConversionFactorSnapshot" = x.factor, "EnteredQuantity" = x.qty
                FROM (SELECT m2."Id" AS mid, (array_agg(l."ProductUnitId"))[1] AS pu, (array_agg(l."UnitCode"))[1] AS code,
                             (array_agg(l."ConversionFactorToBase"))[1] AS factor, (array_agg(l."Qty"))[1] AS qty
                      FROM "StockMovements" m2 JOIN "PurchaseOrderLines" l ON l."PurchaseOrderId" = m2."ReferenceId"
                           AND l."ProductId" = m2."ProductId" AND l."BaseQty" = abs(m2."QuantityBaseUnits") AND l."Qty" * l."ConversionFactorToBase" = l."BaseQty"
                      WHERE m2."Type" IN ('Purchase', 'PurchaseCancellation') AND m2."ReferenceType" = 'PurchaseOrder'
                        AND m2."EnteredProductUnitId" IS NULL
                      GROUP BY m2."Id"
                      HAVING COUNT(DISTINCT (l."ProductUnitId", l."UnitCode", l."ConversionFactorToBase", l."Qty")) = 1) x
                WHERE m."Id" = x.mid;

                UPDATE "StockMovements" m
                SET "EnteredProductUnitId" = x.pu, "EnteredUnitCode" = x.code, "ConversionFactorSnapshot" = x.factor, "EnteredQuantity" = x.qty
                FROM (SELECT m2."Id" AS mid, (array_agg(r."ProductUnitId"))[1] AS pu, (array_agg(r."UnitCode"))[1] AS code,
                             (array_agg(r."ConversionFactorToBase"))[1] AS factor, (array_agg(r."Qty"))[1] AS qty
                      FROM "StockMovements" m2 JOIN "PurchaseReturnLines" r ON r."PurchaseReturnId" = m2."ReferenceId"
                           AND r."ProductId" = m2."ProductId" AND r."BaseQty" = abs(m2."QuantityBaseUnits") AND r."Qty" * r."ConversionFactorToBase" = r."BaseQty"
                      LEFT JOIN "PurchaseBatches" b ON b."Id" = m2."PurchaseBatchId"
                      WHERE m2."Type" = 'PurchaseReturn' AND m2."ReferenceType" = 'PurchaseReturn'
                        AND m2."EnteredProductUnitId" IS NULL
                        AND (m2."PurchaseBatchId" IS NULL OR r."PurchaseOrderLineId" = b."PurchaseOrderLineId")
                      GROUP BY m2."Id"
                      HAVING COUNT(DISTINCT (r."ProductUnitId", r."UnitCode", r."ConversionFactorToBase", r."Qty")) = 1) x
                WHERE m."Id" = x.mid;

                UPDATE "StockMovements" m
                SET "EnteredProductUnitId" = x.pu, "EnteredUnitCode" = x.code, "ConversionFactorSnapshot" = x.factor, "EnteredQuantity" = x.qty
                FROM (SELECT m2."Id" AS mid, (array_agg(l."ProductUnitId"))[1] AS pu, (array_agg(l."UnitCode"))[1] AS code,
                             (array_agg(l."ConversionFactorToBase"))[1] AS factor, (array_agg(l."Qty"))[1] AS qty
                      FROM "StockMovements" m2 JOIN "SalesInvoiceLines" l ON l."SalesInvoiceId" = m2."ReferenceId"
                           AND l."ProductId" = m2."ProductId" AND l."BaseQty" = abs(m2."QuantityBaseUnits") AND l."Qty" * l."ConversionFactorToBase" = l."BaseQty"
                      WHERE m2."Type" IN ('Sale', 'SaleCancellation') AND m2."ReferenceType" = 'SalesInvoice'
                        AND m2."EnteredProductUnitId" IS NULL
                      GROUP BY m2."Id"
                      HAVING COUNT(DISTINCT (l."ProductUnitId", l."UnitCode", l."ConversionFactorToBase", l."Qty")) = 1) x
                WHERE m."Id" = x.mid;

                UPDATE "StockMovements" m
                SET "EnteredProductUnitId" = x.pu, "EnteredUnitCode" = x.code, "ConversionFactorSnapshot" = x.factor, "EnteredQuantity" = x.qty
                FROM (SELECT m2."Id" AS mid, (array_agg(r."ProductUnitId"))[1] AS pu, (array_agg(r."UnitCode"))[1] AS code,
                             (array_agg(r."ConversionFactorToBase"))[1] AS factor, (array_agg(r."Qty"))[1] AS qty
                      FROM "StockMovements" m2 JOIN "SalesReturnLines" r ON r."SalesReturnId" = m2."ReferenceId"
                           AND r."ProductId" = m2."ProductId" AND r."BaseQty" = abs(m2."QuantityBaseUnits") AND r."Qty" * r."ConversionFactorToBase" = r."BaseQty"
                      WHERE m2."Type" = 'SaleReturn' AND m2."ReferenceType" = 'SalesReturn'
                        AND m2."EnteredProductUnitId" IS NULL
                      GROUP BY m2."Id"
                      HAVING COUNT(DISTINCT (r."ProductUnitId", r."UnitCode", r."ConversionFactorToBase", r."Qty")) = 1) x
                WHERE m."Id" = x.mid;

                DO $a$
                BEGIN
                    IF (SELECT mv FROM p6_count) <> (SELECT COUNT(*) FROM "StockMovements") THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: movement row count changed';
                    END IF;
                    IF EXISTS (SELECT 1 FROM p6_sum o FULL JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") AS q FROM "StockMovements" GROUP BY 1) n
                                   ON n."ProductId" = o."ProductId"
                               WHERE o.q IS DISTINCT FROM n.q) THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: a per-product movement sum changed';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockMovements" WHERE "Type" = 'Purchase' AND "QuantityBaseUnits" < 0) THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: a negative Purchase movement is left (V2)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockMovements" WHERE "ReferenceType" = 'StockAdjustment' AND "ReasonCode" IS NULL)
                    OR EXISTS (SELECT 1 FROM "StockMovements" WHERE "Type" = 'Adjustment' AND "ReasonCode" = 'OpeningStock') THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: adjustment reason code backfill incomplete (V3)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockMovements" WHERE "EnteredQuantity" IS NOT NULL
                                  AND "EnteredQuantity" * "ConversionFactorSnapshot" <> abs("QuantityBaseUnits")) THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: entered quantity x factor <> base quantity (V4)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockMovements" m JOIN "ProductUnits" pu ON pu."Id" = m."EnteredProductUnitId"
                               WHERE pu."ProductId" <> m."ProductId") THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: entered unit belongs to another product (V6)';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "StockItems" s
                               LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
                               WHERE s."QuantityOnHand" <> COALESCE(m.q, 0) AND s."ProductId" NOT IN (SELECT "ProductId" FROM p6_unbalanced))
                    OR EXISTS (SELECT 1 FROM "StockItems" s
                               LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
                               WHERE s."QuantityOnHand" <> COALESCE(b.q, 0) AND s."ProductId" NOT IN (SELECT "ProductId" FROM p6_unbalanced)) THEN
                        RAISE EXCEPTION 'AddMovementTypesAndReasonCode: reconciliation broke for a product that was balanced before (V8)';
                    END IF;
                END $a$;
                """);

            // ===== 3. enforce =====
            migrationBuilder.CreateIndex(name: "IX_StockMovements_ProductId_EnteredProductUnitId", table: "StockMovements", columns: new[] { "ProductId", "EnteredProductUnitId" });
            migrationBuilder.CreateIndex(name: "IX_StockMovements_ReferenceType_ReferenceId", table: "StockMovements", columns: new[] { "ReferenceType", "ReferenceId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Entered_Arithmetic",
                table: "StockMovements",
                sql: "\"EnteredQuantity\" IS NULL OR \"EnteredQuantity\" * \"ConversionFactorSnapshot\" = abs(\"QuantityBaseUnits\")");
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_EnteredQuantity_Positive",
                table: "StockMovements",
                sql: "\"EnteredQuantity\" IS NULL OR \"EnteredQuantity\" > 0");
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_EnteredQuantity_RequiresUnit",
                table: "StockMovements",
                sql: "\"EnteredQuantity\" IS NULL OR \"EnteredProductUnitId\" IS NOT NULL");
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_EnteredUnit_AllOrNone",
                table: "StockMovements",
                sql: "(\"EnteredProductUnitId\" IS NULL) = (\"EnteredUnitCode\" IS NULL) AND (\"EnteredProductUnitId\" IS NULL) = (\"ConversionFactorSnapshot\" IS NULL)");
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Factor_Positive",
                table: "StockMovements",
                sql: "\"ConversionFactorSnapshot\" IS NULL OR \"ConversionFactorSnapshot\" > 0");
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_ReasonCode",
                table: "StockMovements",
                sql: "(\"Type\" NOT IN ('Adjustment', 'OpeningStock') OR \"ReasonCode\" IS NOT NULL) AND (\"Type\" <> 'OpeningStock' OR \"ReasonCode\" = 'OpeningStock') AND (\"Type\" <> 'Adjustment' OR \"ReasonCode\" <> 'OpeningStock')");
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type_Sign",
                table: "StockMovements",
                sql: "(\"Type\" NOT IN ('Purchase', 'SaleReturn', 'SaleCancellation', 'OpeningStock') OR \"QuantityBaseUnits\" > 0) AND (\"Type\" NOT IN ('Sale', 'PurchaseReturn', 'PurchaseCancellation') OR \"QuantityBaseUnits\" < 0)");
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_UnitCode_NotEmpty",
                table: "StockMovements",
                sql: "\"EnteredUnitCode\" IS NULL OR char_length(\"EnteredUnitCode\") > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_ProductUnits_ProductId_EnteredProductUnitId",
                table: "StockMovements",
                columns: new[] { "ProductId", "EnteredProductUnitId" },
                principalTable: "ProductUnits",
                principalColumns: new[] { "ProductId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_StockMovements_ProductUnits_ProductId_EnteredProductUnitId", table: "StockMovements");
            migrationBuilder.DropIndex(name: "IX_StockMovements_ProductId_EnteredProductUnitId", table: "StockMovements");
            migrationBuilder.DropIndex(name: "IX_StockMovements_ReferenceType_ReferenceId", table: "StockMovements");

            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_Entered_Arithmetic", table: "StockMovements");
            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_EnteredQuantity_Positive", table: "StockMovements");
            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_EnteredQuantity_RequiresUnit", table: "StockMovements");
            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_EnteredUnit_AllOrNone", table: "StockMovements");
            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_Factor_Positive", table: "StockMovements");
            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_ReasonCode", table: "StockMovements");
            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_Type_Sign", table: "StockMovements");
            migrationBuilder.DropCheckConstraint(name: "CK_StockMovements_UnitCode_NotEmpty", table: "StockMovements");

            // Restore the pre-6a labels (after the CHECKs are gone: a negative 'Purchase' would violate the sign CHECK).
            migrationBuilder.Sql("""
                UPDATE "StockMovements" SET "Type" = 'Adjustment' WHERE "Type" = 'OpeningStock';
                UPDATE "StockMovements" SET "Type" = 'Purchase' WHERE "Type" = 'PurchaseCancellation';
                """);

            migrationBuilder.DropColumn(name: "ConversionFactorSnapshot", table: "StockMovements");
            migrationBuilder.DropColumn(name: "EnteredProductUnitId", table: "StockMovements");
            migrationBuilder.DropColumn(name: "EnteredQuantity", table: "StockMovements");
            migrationBuilder.DropColumn(name: "EnteredUnitCode", table: "StockMovements");
            migrationBuilder.DropColumn(name: "ReasonCode", table: "StockMovements");
        }
    }
}
