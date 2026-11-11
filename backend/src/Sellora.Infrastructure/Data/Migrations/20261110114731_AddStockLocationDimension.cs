using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 8a - Stock location dimension (phase-8-design D1/D2, section 5; owner approval 2026-11-10, O5): one migration, one transaction.
     *   guard    : G1 StockItems.StockLocationId does not exist yet and the history contains AddScopedRoles (model at its head). G2 every company owning rows in
     *              StockItems / StockMovements / PurchaseBatches / the 5 document headers has exactly one IsDefault branch that has a BranchStore location.
     *              G3 (O5) existing stock reconciles per product: QuantityOnHand = SUM(StockMovements.QuantityBaseUnits) = SUM(PurchaseBatches.RemainingBaseQty)
     *              (stock-ledger-invariants), including products that have movements/batches but no StockItems row. The migration REFUSES otherwise.
     *   additive : StockLocationId (nullable) on StockItems, StockMovements, PurchaseOrders, PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments;
     *              OriginStockLocationId (nullable) on PurchaseBatches.
     *   backfill : every legacy row of a company -> that company's default store (the BranchStore of its IsDefault branch; ...0003 for the default company),
     *              including documents of non-default branches (D2: before Phase 8 stock was one shared pool). Header BranchId is NOT changed.
     *   enforce  : NOT NULL; PK_StockItems -> (StockLocationId, ProductId); same-company composite FKs to StockLocations (AK_StockLocations_CompanyId_Id); indexes.
     *   create   : BatchLocationBalances (PK (PurchaseBatchId, StockLocationId); FKs to PurchaseBatches (CompanyId, Id), StockLocations (CompanyId, Id) and
     *              StockItems (StockLocationId, ProductId); CK remaining >= 0). One row per batch with RemainingBaseQty > 0, at the batch's origin location.
     *   assert   : V1-V8 run in the same transaction and RAISE on any violation.
     *
     * RELEASE NOTE (maintenance window): rewrites StockMovements (largest: an UPDATE of every row plus 2 new indexes) and touches every PurchaseBatches and
     * document row; the StockItems primary key swap takes ACCESS EXCLUSIVE on a small table. Run with the application stopped. Behaviour is unchanged for the
     * application until the writers are location-aware (WP2): an OLD application build breaks on StockItems (no ProductId key) - rollback = restore the pre-deploy
     * snapshot + previous build (D10); Down is only valid while every row still sits in the default store (guard). AverageCost stays per row (now per location).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P1 row counts of the touched tables
     * SELECT (SELECT COUNT(*) FROM "StockItems") AS stock_items, (SELECT COUNT(*) FROM "StockMovements") AS stock_movements, (SELECT COUNT(*) FROM "PurchaseBatches") AS batches,
     *        (SELECT COUNT(*) FROM "PurchaseOrders") AS purchases, (SELECT COUNT(*) FROM "PurchaseReturns") AS purchase_returns, (SELECT COUNT(*) FROM "SalesInvoices") AS invoices,
     *        (SELECT COUNT(*) FROM "SalesReturns") AS sales_returns, (SELECT COUNT(*) FROM "StockAdjustments") AS adjustments;
     * -- P2 documents of non-default branches (they map to the default store, D2)
     * SELECT 'PurchaseOrders' AS t, COUNT(*) FROM "PurchaseOrders" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault"
     * UNION ALL SELECT 'SalesInvoices', COUNT(*) FROM "SalesInvoices" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault"
     * UNION ALL SELECT 'StockAdjustments', COUNT(*) FROM "StockAdjustments" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault";
     * -- P3 server version
     * SELECT version();
     * -- P4 guard probes (expected: no rows): reconciliation per product (QOH vs ledger, QOH vs batches), companies without exactly one default branch store
     * SELECT s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId") WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
     * SELECT s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId") WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     * SELECT c."Id" FROM "Companies" c WHERE (SELECT COUNT(*) FROM "Branches" b JOIN "StockLocations" l ON l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore'
     *                                         WHERE b."CompanyId" = c."Id" AND b."IsDefault") <> 1;
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; every query must return no rows.
     *
     * -- V1 NULL location in any of the 8 columns (also NOT NULL-enforced)
     * SELECT 1 WHERE EXISTS (SELECT 1 FROM "StockMovements" WHERE "StockLocationId" IS NULL) OR EXISTS (SELECT 1 FROM "PurchaseBatches" WHERE "OriginStockLocationId" IS NULL);
     * -- V2 row counts equal P1 (compare manually); BatchLocationBalances rows = batches with RemainingBaseQty > 0
     * SELECT 1 WHERE (SELECT COUNT(*) FROM "BatchLocationBalances") <> (SELECT COUNT(*) FROM "PurchaseBatches" WHERE "RemainingBaseQty" > 0);
     * -- V3 per (location, product): QOH = ledger
     * SELECT s."StockLocationId", s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1, 2) m
     *   USING ("StockLocationId", "ProductId") WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
     * UNION ALL SELECT m."StockLocationId", m."ProductId" FROM "StockMovements" m LEFT JOIN "StockItems" s USING ("StockLocationId", "ProductId")
     *   WHERE s."ProductId" IS NULL GROUP BY 1, 2 HAVING SUM(m."QuantityBaseUnits") <> 0;
     * -- V4 per (location, product): QOH = SUM(balances)
     * SELECT s."StockLocationId", s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2) b
     *   USING ("StockLocationId", "ProductId") WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     * -- V5 dual-write: batch remaining = SUM(balances)
     * SELECT pb."Id" FROM "PurchaseBatches" pb LEFT JOIN (SELECT "PurchaseBatchId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1) b ON b."PurchaseBatchId" = pb."Id"
     *   WHERE pb."RemainingBaseQty" <> COALESCE(b.q, 0);
     * -- V6 balance product = batch product
     * SELECT bl."PurchaseBatchId" FROM "BatchLocationBalances" bl JOIN "PurchaseBatches" pb ON pb."Id" = bl."PurchaseBatchId" WHERE bl."ProductId" <> pb."ProductId";
     * -- V7 every row at its company's default store (Up only: later postings may use other locations)
     * SELECT s."ProductId" FROM "StockItems" s JOIN (@MAP@) m ON m."CompanyId" = s."CompanyId" WHERE s."StockLocationId" <> m."StockLocationId";
     * -- V8 at most one StockItems row per (company, product) (Up only)
     * SELECT "CompanyId", "ProductId" FROM "StockItems" GROUP BY 1, 2 HAVING COUNT(*) > 1;
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard first, RAISE: any (CompanyId, ProductId) with more than one StockItems row; any StockItems / StockMovements / PurchaseBatches origin / document /
     * balance row outside its company's default store; any batch whose remaining differs from SUM(balances)). Else drops BatchLocationBalances, the FKs, indexes and
     * columns and restores PK_StockItems on ProductId. Balances are derivable from PurchaseBatches while the guard holds, so nothing is lost.
     */
    /// <inheritdoc />
    public partial class AddStockLocationDimension : Migration
    {
        // company -> its default store (BranchStore of the IsDefault branch). Zero or several rows for a company are caught by guard G2 / V-block.
        private const string DefaultStoreMap = """
            SELECT b."CompanyId", l."Id" AS "StockLocationId" FROM "Branches" b
            JOIN "StockLocations" l ON l."CompanyId" = b."CompanyId" AND l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore'
            WHERE b."IsDefault"
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guards G1-G3 and profiling notices (before anything is written). The 8 row counts are kept in a transaction-local setting for V2.
            migrationBuilder.Sql("""
                DO $g$
                DECLARE n bigint; sample text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'StockItems' AND column_name = 'StockLocationId') THEN
                        RAISE EXCEPTION 'AddStockLocationDimension: StockItems.StockLocationId already exists (G1)';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261110093408_AddScopedRoles') THEN
                        RAISE EXCEPTION 'AddStockLocationDimension: the model is not at the AddScopedRoles head (G1)';
                    END IF;

                    SELECT COUNT(*) INTO n FROM (
                        SELECT "CompanyId" FROM "StockItems" UNION SELECT "CompanyId" FROM "StockMovements" UNION SELECT "CompanyId" FROM "PurchaseBatches"
                        UNION SELECT "CompanyId" FROM "PurchaseOrders" UNION SELECT "CompanyId" FROM "PurchaseReturns" UNION SELECT "CompanyId" FROM "SalesInvoices"
                        UNION SELECT "CompanyId" FROM "SalesReturns" UNION SELECT "CompanyId" FROM "StockAdjustments") c
                    WHERE (SELECT COUNT(*) FROM (@MAP@) m WHERE m."CompanyId" = c."CompanyId") <> 1;
                    IF n > 0 THEN
                        RAISE EXCEPTION 'AddStockLocationDimension: % company(ies) with stock or documents lack exactly one default branch with a BranchStore location (G2)', n;
                    END IF;

                    SELECT COUNT(*), string_agg(x."ProductId"::text, ', ') INTO n, sample FROM (
                        SELECT s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
                          WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
                        UNION SELECT m."ProductId" FROM "StockMovements" m LEFT JOIN "StockItems" s USING ("ProductId") WHERE s."ProductId" IS NULL GROUP BY 1 HAVING SUM(m."QuantityBaseUnits") <> 0
                        LIMIT 20) x;
                    IF n > 0 THEN
                        RAISE EXCEPTION 'AddStockLocationDimension: quantity on hand differs from the movement ledger for product(s) % (G3, O5)', sample;
                    END IF;
                    SELECT COUNT(*), string_agg(x."ProductId"::text, ', ') INTO n, sample FROM (
                        SELECT s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
                          WHERE s."QuantityOnHand" <> COALESCE(b.q, 0)
                        UNION SELECT b."ProductId" FROM "PurchaseBatches" b LEFT JOIN "StockItems" s USING ("ProductId") WHERE s."ProductId" IS NULL GROUP BY 1 HAVING SUM(b."RemainingBaseQty") <> 0
                        LIMIT 20) x;
                    IF n > 0 THEN
                        RAISE EXCEPTION 'AddStockLocationDimension: quantity on hand differs from the batch remainders for product(s) % (G3, O5)', sample;
                    END IF;

                    PERFORM set_config('sellora.p8a_counts',
                        (SELECT COUNT(*) FROM "StockItems")::text || ',' || (SELECT COUNT(*) FROM "StockMovements") || ',' || (SELECT COUNT(*) FROM "PurchaseBatches") || ','
                        || (SELECT COUNT(*) FROM "PurchaseOrders") || ',' || (SELECT COUNT(*) FROM "PurchaseReturns") || ',' || (SELECT COUNT(*) FROM "SalesInvoices") || ','
                        || (SELECT COUNT(*) FROM "SalesReturns") || ',' || (SELECT COUNT(*) FROM "StockAdjustments"), true);
                    RAISE NOTICE 'AddStockLocationDimension P1 counts (StockItems,StockMovements,PurchaseBatches,PurchaseOrders,PurchaseReturns,SalesInvoices,SalesReturns,StockAdjustments): %', current_setting('sellora.p8a_counts', true);
                    RAISE NOTICE 'AddStockLocationDimension P2 documents of non-default branches (purchases, invoices, adjustments): %, %, %',
                        (SELECT COUNT(*) FROM "PurchaseOrders" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault"),
                        (SELECT COUNT(*) FROM "SalesInvoices" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault"),
                        (SELECT COUNT(*) FROM "StockAdjustments" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault");
                    RAISE NOTICE 'AddStockLocationDimension P3 server: %', version();
                END $g$;
                """.Replace("@MAP@", DefaultStoreMap));

            // ===== 1. additive: nullable columns; the StockItems key is dropped here and re-created on (StockLocationId, ProductId) after the backfill =====
            migrationBuilder.DropPrimaryKey(
                name: "PK_StockItems",
                table: "StockItems");

            migrationBuilder.AddColumn<Guid>(
                name: "StockLocationId",
                table: "StockMovements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockLocationId",
                table: "StockItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockLocationId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockLocationId",
                table: "SalesReturns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockLocationId",
                table: "SalesInvoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockLocationId",
                table: "PurchaseReturns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockLocationId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginStockLocationId",
                table: "PurchaseBatches",
                type: "uuid",
                nullable: true);


            // ===== 2. backfill: every legacy row -> its company's default store (D2) =====
            foreach (var (table, column) in new[]
            {
                ("StockItems", "StockLocationId"), ("StockMovements", "StockLocationId"), ("PurchaseBatches", "OriginStockLocationId"),
                ("PurchaseOrders", "StockLocationId"), ("PurchaseReturns", "StockLocationId"), ("SalesInvoices", "StockLocationId"),
                ("SalesReturns", "StockLocationId"), ("StockAdjustments", "StockLocationId"),
            })
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" t SET \"{column}\" = m.\"StockLocationId\" FROM ({DefaultStoreMap}) m WHERE m.\"CompanyId\" = t.\"CompanyId\";");
            }

            // ===== 3. enforce =====
            migrationBuilder.AlterColumn<Guid>(
                name: "StockLocationId",
                table: "StockMovements",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "StockLocationId",
                table: "StockItems",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "StockLocationId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "StockLocationId",
                table: "SalesReturns",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "StockLocationId",
                table: "SalesInvoices",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "StockLocationId",
                table: "PurchaseReturns",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "StockLocationId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OriginStockLocationId",
                table: "PurchaseBatches",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_StockItems",
                table: "StockItems",
                columns: new[] { "StockLocationId", "ProductId" });

            migrationBuilder.CreateTable(
                name: "BatchLocationBalances",
                columns: table => new
                {
                    PurchaseBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    StockLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemainingBaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchLocationBalances", x => new { x.PurchaseBatchId, x.StockLocationId });
                    table.CheckConstraint("CK_BatchLocationBalances_Remaining_NonNegative", "\"RemainingBaseQty\" >= 0");
                    table.ForeignKey(
                        name: "FK_BatchLocationBalances_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BatchLocationBalances_PurchaseBatches",
                        columns: x => new { x.CompanyId, x.PurchaseBatchId },
                        principalTable: "PurchaseBatches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BatchLocationBalances_StockItems",
                        columns: x => new { x.StockLocationId, x.ProductId },
                        principalTable: "StockItems",
                        principalColumns: new[] { "StockLocationId", "ProductId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BatchLocationBalances_StockLocations",
                        columns: x => new { x.CompanyId, x.StockLocationId },
                        principalTable: "StockLocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId_StockLocationId",
                table: "StockMovements",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_StockLocationId_ProductId_OccurredAt",
                table: "StockMovements",
                columns: new[] { "StockLocationId", "ProductId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_CompanyId_StockLocationId",
                table: "StockItems",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_ProductId",
                table: "StockItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CompanyId_StockLocationId",
                table: "StockAdjustments",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CompanyId_StockLocationId",
                table: "SalesReturns",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CompanyId_StockLocationId",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_CompanyId_StockLocationId",
                table: "PurchaseReturns",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CompanyId_StockLocationId",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseBatches_CompanyId_OriginStockLocationId",
                table: "PurchaseBatches",
                columns: new[] { "CompanyId", "OriginStockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BatchLocationBalances_CompanyId_PurchaseBatchId",
                table: "BatchLocationBalances",
                columns: new[] { "CompanyId", "PurchaseBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_BatchLocationBalances_CompanyId_StockLocationId",
                table: "BatchLocationBalances",
                columns: new[] { "CompanyId", "StockLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BatchLocationBalances_Location_Product",
                table: "BatchLocationBalances",
                columns: new[] { "StockLocationId", "ProductId" },
                filter: "\"RemainingBaseQty\" > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseBatches_StockLocations_Origin",
                table: "PurchaseBatches",
                columns: new[] { "CompanyId", "OriginStockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrders_StockLocations_CompanyId_StockLocationId",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "StockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturns_StockLocations_CompanyId_StockLocationId",
                table: "PurchaseReturns",
                columns: new[] { "CompanyId", "StockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_StockLocations_CompanyId_StockLocationId",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "StockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReturns_StockLocations_CompanyId_StockLocationId",
                table: "SalesReturns",
                columns: new[] { "CompanyId", "StockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockAdjustments_StockLocations_CompanyId_StockLocationId",
                table: "StockAdjustments",
                columns: new[] { "CompanyId", "StockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockItems_StockLocations_CompanyId_StockLocationId",
                table: "StockItems",
                columns: new[] { "CompanyId", "StockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_StockLocations_CompanyId_StockLocationId",
                table: "StockMovements",
                columns: new[] { "CompanyId", "StockLocationId" },
                principalTable: "StockLocations",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        

            // ===== 4. per-location batch balances: one row per batch with stock, at its origin location (the legacy single-pool state) =====
            migrationBuilder.Sql("""
                INSERT INTO "BatchLocationBalances" ("PurchaseBatchId", "StockLocationId", "CompanyId", "ProductId", "RemainingBaseQty")
                SELECT pb."Id", pb."OriginStockLocationId", pb."CompanyId", pb."ProductId", pb."RemainingBaseQty" FROM "PurchaseBatches" pb WHERE pb."RemainingBaseQty" > 0;
                """);

            // ===== 5. V-block (V1-V8), same transaction =====
            migrationBuilder.Sql("""
                DO $v$
                DECLARE n bigint; expected text;
                BEGIN
                    SELECT (SELECT COUNT(*) FROM "StockMovements" WHERE "StockLocationId" IS NULL) + (SELECT COUNT(*) FROM "PurchaseBatches" WHERE "OriginStockLocationId" IS NULL) INTO n;
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension V1: % rows without a location', n; END IF;

                    expected := current_setting('sellora.p8a_counts', true);
                    IF expected IS NOT NULL AND expected <> '' AND expected <>
                        (SELECT COUNT(*) FROM "StockItems")::text || ',' || (SELECT COUNT(*) FROM "StockMovements") || ',' || (SELECT COUNT(*) FROM "PurchaseBatches") || ','
                        || (SELECT COUNT(*) FROM "PurchaseOrders") || ',' || (SELECT COUNT(*) FROM "PurchaseReturns") || ',' || (SELECT COUNT(*) FROM "SalesInvoices") || ','
                        || (SELECT COUNT(*) FROM "SalesReturns") || ',' || (SELECT COUNT(*) FROM "StockAdjustments") THEN
                        RAISE EXCEPTION 'AddStockLocationDimension V2: row counts changed (before %)', expected;
                    END IF;
                    IF (SELECT COUNT(*) FROM "BatchLocationBalances") <> (SELECT COUNT(*) FROM "PurchaseBatches" WHERE "RemainingBaseQty" > 0) THEN
                        RAISE EXCEPTION 'AddStockLocationDimension V2: balance rows <> batches with remaining stock';
                    END IF;

                    SELECT COUNT(*) INTO n FROM (
                        SELECT s."StockLocationId", s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1, 2) m
                          USING ("StockLocationId", "ProductId") WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
                        UNION ALL SELECT m."StockLocationId", m."ProductId" FROM "StockMovements" m LEFT JOIN "StockItems" s USING ("StockLocationId", "ProductId")
                          WHERE s."ProductId" IS NULL GROUP BY 1, 2 HAVING SUM(m."QuantityBaseUnits") <> 0) x;
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension V3: % (location, product) pairs where quantity on hand <> ledger', n; END IF;

                    SELECT COUNT(*) INTO n FROM (
                        SELECT s."StockLocationId", s."ProductId" FROM "StockItems" s LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2) b
                          USING ("StockLocationId", "ProductId") WHERE s."QuantityOnHand" <> COALESCE(b.q, 0)
                        UNION ALL SELECT b."StockLocationId", b."ProductId" FROM "BatchLocationBalances" b LEFT JOIN "StockItems" s USING ("StockLocationId", "ProductId")
                          WHERE s."ProductId" IS NULL GROUP BY 1, 2 HAVING SUM(b."RemainingBaseQty") <> 0) x;
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension V4: % (location, product) pairs where quantity on hand <> balances', n; END IF;

                    SELECT COUNT(*) INTO n FROM "PurchaseBatches" pb
                        LEFT JOIN (SELECT "PurchaseBatchId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1) b ON b."PurchaseBatchId" = pb."Id"
                        WHERE pb."RemainingBaseQty" <> COALESCE(b.q, 0);
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension V5: % batches whose remaining <> sum of balances', n; END IF;

                    SELECT COUNT(*) INTO n FROM "BatchLocationBalances" bl JOIN "PurchaseBatches" pb ON pb."Id" = bl."PurchaseBatchId" WHERE bl."ProductId" <> pb."ProductId";
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension V6: % balances whose product differs from the batch product', n; END IF;

                    SELECT COUNT(*) INTO n FROM (
                        SELECT 1 FROM "StockItems" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."StockLocationId" <> m."StockLocationId"
                        UNION ALL SELECT 1 FROM "StockMovements" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."StockLocationId" <> m."StockLocationId"
                        UNION ALL SELECT 1 FROM "PurchaseBatches" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."OriginStockLocationId" <> m."StockLocationId"
                        UNION ALL SELECT 1 FROM "PurchaseOrders" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."StockLocationId" <> m."StockLocationId"
                        UNION ALL SELECT 1 FROM "PurchaseReturns" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."StockLocationId" <> m."StockLocationId"
                        UNION ALL SELECT 1 FROM "SalesInvoices" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."StockLocationId" <> m."StockLocationId"
                        UNION ALL SELECT 1 FROM "SalesReturns" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."StockLocationId" <> m."StockLocationId"
                        UNION ALL SELECT 1 FROM "StockAdjustments" t JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE t."StockLocationId" <> m."StockLocationId") x;
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension V7: % rows not at their company''s default store', n; END IF;

                    SELECT COUNT(*) INTO n FROM (SELECT 1 FROM "StockItems" GROUP BY "CompanyId", "ProductId" HAVING COUNT(*) > 1) x;
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension V8: % products with more than one StockItems row', n; END IF;
                END $v$;
                """.Replace("@MAP@", DefaultStoreMap));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard: refuse while any stock or document lives outside the default store (post-8b data), or while a product has stock rows at several locations.
            migrationBuilder.Sql("""
                DO $g$
                DECLARE n bigint;
                BEGIN
                    SELECT COUNT(*) INTO n FROM (SELECT 1 FROM "StockItems" GROUP BY "CompanyId", "ProductId" HAVING COUNT(*) > 1) x;
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension Down refused: % products have stock rows at more than one location', n; END IF;

                    SELECT COUNT(*) INTO n FROM (
                        SELECT 1 FROM "StockItems" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."StockLocationId"
                        UNION ALL SELECT 1 FROM "StockMovements" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."StockLocationId"
                        UNION ALL SELECT 1 FROM "PurchaseBatches" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."OriginStockLocationId"
                        UNION ALL SELECT 1 FROM "PurchaseOrders" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."StockLocationId"
                        UNION ALL SELECT 1 FROM "PurchaseReturns" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."StockLocationId"
                        UNION ALL SELECT 1 FROM "SalesInvoices" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."StockLocationId"
                        UNION ALL SELECT 1 FROM "SalesReturns" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."StockLocationId"
                        UNION ALL SELECT 1 FROM "StockAdjustments" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId" WHERE m."StockLocationId" IS DISTINCT FROM t."StockLocationId"
                        UNION ALL SELECT 1 FROM "BatchLocationBalances" t LEFT JOIN (@MAP@) m ON m."CompanyId" = t."CompanyId"
                            WHERE t."RemainingBaseQty" <> 0 AND m."StockLocationId" IS DISTINCT FROM t."StockLocationId") x;
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension Down refused: % stock or document rows are outside the default store (restore the pre-deploy snapshot)', n; END IF;

                    SELECT COUNT(*) INTO n FROM "PurchaseBatches" pb
                        LEFT JOIN (SELECT "PurchaseBatchId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1) b ON b."PurchaseBatchId" = pb."Id"
                        WHERE pb."RemainingBaseQty" <> COALESCE(b.q, 0);
                    IF n > 0 THEN RAISE EXCEPTION 'AddStockLocationDimension Down refused: % batches whose remaining differs from the sum of balances', n; END IF;
                END $g$;
                """.Replace("@MAP@", DefaultStoreMap));

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseBatches_StockLocations_Origin",
                table: "PurchaseBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrders_StockLocations_CompanyId_StockLocationId",
                table: "PurchaseOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturns_StockLocations_CompanyId_StockLocationId",
                table: "PurchaseReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_StockLocations_CompanyId_StockLocationId",
                table: "SalesInvoices");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReturns_StockLocations_CompanyId_StockLocationId",
                table: "SalesReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_StockAdjustments_StockLocations_CompanyId_StockLocationId",
                table: "StockAdjustments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockItems_StockLocations_CompanyId_StockLocationId",
                table: "StockItems");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_StockLocations_CompanyId_StockLocationId",
                table: "StockMovements");

            migrationBuilder.DropTable(
                name: "BatchLocationBalances");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_CompanyId_StockLocationId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_StockLocationId_ProductId_OccurredAt",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockItems_CompanyId_StockLocationId",
                table: "StockItems");

            migrationBuilder.DropIndex(
                name: "IX_StockItems_ProductId",
                table: "StockItems");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_CompanyId_StockLocationId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_CompanyId_StockLocationId",
                table: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_CompanyId_StockLocationId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_CompanyId_StockLocationId",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CompanyId_StockLocationId",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseBatches_CompanyId_OriginStockLocationId",
                table: "PurchaseBatches");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StockItems",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "StockLocationId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "StockLocationId",
                table: "StockItems");

            migrationBuilder.DropColumn(
                name: "StockLocationId",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "StockLocationId",
                table: "SalesReturns");

            migrationBuilder.DropColumn(
                name: "StockLocationId",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "StockLocationId",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "StockLocationId",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "OriginStockLocationId",
                table: "PurchaseBatches");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StockItems",
                table: "StockItems",
                column: "ProductId");
        }
    }
}
