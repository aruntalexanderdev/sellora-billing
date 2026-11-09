using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 1 / M2 - CompanyId on the 19 company-owned tables (one migration, one transaction).
     *   tables   : Taxes, ExchangeRates, Suppliers, Customers, ExpenseCategories, Expenses, FinancialAccounts,
     *              FinancialTransactions, Products, ProductUnits, PurchaseOrders, PurchaseReturns, PurchaseBatches, SalesInvoices,
     *              SalesReturns, StockAdjustments, StockItems, StockMovements, IdempotencyKeys. Lines, payments, consumptions and
     *              SellingPrices inherit the company through their parent (query filter, no column). Roles stay global (D4).
     *   additive : "CompanyId" uuid NULL on each table (metadata-only in PG).
     *   backfill : every row -> the default company 0c0a1f00-0000-4000-8000-000000000001 (created by M1; derived, idempotent:
     *              WHERE "CompanyId" IS NULL).
     *   assert   : in the same transaction, before enforcing: no NULL / foreign CompanyId, per-table row counts equal the
     *              pre-counts (ON COMMIT DROP temp table), the set of products out of balance (stock-ledger reconciliation) is
     *              unchanged.
     *   enforce  : SET NOT NULL, FK_<T>_Companies_CompanyId (Restrict), IX_<T>_CompanyId. M3 drops the indexes that a
     *              company-leading unique makes redundant.
     * Composite same-company FKs and company-scoped uniques follow in M3. No (CompanyId, date) report indexes in Phase 1.
     *
     * RELEASE NOTE (R-a): the backfill rewrites every row of the 19 tables (StockMovements, FinancialTransactions, SalesInvoices
     * are the largest) and SET NOT NULL / FK validation scan them under an ACCESS EXCLUSIVE / SHARE ROW EXCLUSIVE lock. Time
     * M2 on the production copy; if too slow split it into M2a (add + batched backfill, CHECK ("CompanyId" IS NOT NULL) NOT
     * VALID) and M2b (VALIDATE, SET NOT NULL, FK) - decide after timing. Startup auto-migrate must stay off in production (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P0 volume of the 19 tables (record; V1 must match)
     * SELECT 'Taxes' t, COUNT(*) FROM "Taxes" UNION ALL SELECT 'Products', COUNT(*) FROM "Products" UNION ALL
     * SELECT 'StockMovements', COUNT(*) FROM "StockMovements" UNION ALL SELECT 'SalesInvoices', COUNT(*) FROM "SalesInvoices"
     * UNION ALL SELECT 'FinancialTransactions', COUNT(*) FROM "FinancialTransactions"; -- ... and the other 14 tables
     * -- P3 reconciliation (record the product ids; V6 must return exactly this set)
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying. V1 equals P0; V2 must return no rows; V6 equals P3.
     *
     * -- V1 volume: repeat P0 for all 19 tables (identical numbers)
     * -- V2 rows not on the default company (run for each of the 19 tables; must be empty)
     * SELECT 'Products' FROM "Products" WHERE "CompanyId" IS NULL OR "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001' LIMIT 1;
     * -- (same predicate for Taxes, ExchangeRates, Suppliers, Customers, ExpenseCategories, Expenses, FinancialAccounts,
     * --  FinancialTransactions, ProductUnits, PurchaseOrders, PurchaseReturns, PurchaseBatches, SalesInvoices, SalesReturns,
     * --  StockAdjustments, StockItems, StockMovements, IdempotencyKeys)
     * -- V6 reconciliation: the two queries of P3 (exactly the P3 set)
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G first: raises when more than one company exists or any of the 19 tables holds another company's row): drops
     * the indexes, FKs and the 19 columns. The backfilled value is derived; Up recreates it.
     */
    /// <inheritdoc />
    public partial class AddCompanyIdToCompanyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 1. additive =====
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "Taxes", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "ExchangeRates", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "Suppliers", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "Customers", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "ExpenseCategories", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "Expenses", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "FinancialAccounts", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "FinancialTransactions", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "Products", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "ProductUnits", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "PurchaseOrders", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "PurchaseReturns", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "PurchaseBatches", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "SalesInvoices", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "SalesReturns", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "StockAdjustments", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "StockItems", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "StockMovements", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "CompanyId", table: "IdempotencyKeys", type: "uuid", nullable: true);

            // ===== 2. backfill + assertions =====
            // The temp tables are ON COMMIT DROP, so they vanish with the migration transaction.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE m2_counts ON COMMIT DROP AS
                SELECT 'Taxes'::text AS t, COUNT(*) AS n FROM "Taxes"
                UNION ALL SELECT 'ExchangeRates', COUNT(*) FROM "ExchangeRates"
                UNION ALL SELECT 'Suppliers', COUNT(*) FROM "Suppliers"
                UNION ALL SELECT 'Customers', COUNT(*) FROM "Customers"
                UNION ALL SELECT 'ExpenseCategories', COUNT(*) FROM "ExpenseCategories"
                UNION ALL SELECT 'Expenses', COUNT(*) FROM "Expenses"
                UNION ALL SELECT 'FinancialAccounts', COUNT(*) FROM "FinancialAccounts"
                UNION ALL SELECT 'FinancialTransactions', COUNT(*) FROM "FinancialTransactions"
                UNION ALL SELECT 'Products', COUNT(*) FROM "Products"
                UNION ALL SELECT 'ProductUnits', COUNT(*) FROM "ProductUnits"
                UNION ALL SELECT 'PurchaseOrders', COUNT(*) FROM "PurchaseOrders"
                UNION ALL SELECT 'PurchaseReturns', COUNT(*) FROM "PurchaseReturns"
                UNION ALL SELECT 'PurchaseBatches', COUNT(*) FROM "PurchaseBatches"
                UNION ALL SELECT 'SalesInvoices', COUNT(*) FROM "SalesInvoices"
                UNION ALL SELECT 'SalesReturns', COUNT(*) FROM "SalesReturns"
                UNION ALL SELECT 'StockAdjustments', COUNT(*) FROM "StockAdjustments"
                UNION ALL SELECT 'StockItems', COUNT(*) FROM "StockItems"
                UNION ALL SELECT 'StockMovements', COUNT(*) FROM "StockMovements"
                UNION ALL SELECT 'IdempotencyKeys', COUNT(*) FROM "IdempotencyKeys";

                CREATE TEMP TABLE m2_unbalanced ON COMMIT DROP AS
                SELECT s."ProductId" FROM "StockItems" s
                LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
                WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
                UNION
                SELECT s."ProductId" FROM "StockItems" s
                LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
                WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);

                UPDATE "Taxes" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "ExchangeRates" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "Suppliers" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "Customers" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "ExpenseCategories" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "Expenses" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "FinancialAccounts" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "FinancialTransactions" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "Products" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "ProductUnits" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "PurchaseOrders" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "PurchaseReturns" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "PurchaseBatches" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "SalesInvoices" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "SalesReturns" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "StockAdjustments" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "StockItems" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "StockMovements" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;
                UPDATE "IdempotencyKeys" SET "CompanyId" = '0c0a1f00-0000-4000-8000-000000000001' WHERE "CompanyId" IS NULL;

                DO $a$
                DECLARE
                    tbl text;
                    bad bigint;
                    expected bigint;
                    actual bigint;
                BEGIN
                    FOREACH tbl IN ARRAY ARRAY['Taxes', 'ExchangeRates', 'Suppliers', 'Customers', 'ExpenseCategories', 'Expenses', 'FinancialAccounts', 'FinancialTransactions', 'Products', 'ProductUnits', 'PurchaseOrders', 'PurchaseReturns', 'PurchaseBatches', 'SalesInvoices', 'SalesReturns', 'StockAdjustments', 'StockItems', 'StockMovements', 'IdempotencyKeys'] LOOP
                        EXECUTE format('SELECT COUNT(*) FROM %I WHERE "CompanyId" IS NULL OR "CompanyId" <> %L', tbl, '0c0a1f00-0000-4000-8000-000000000001') INTO bad;
                        IF bad <> 0 THEN
                            RAISE EXCEPTION 'AddCompanyIdToCompanyTables: % has % row(s) without the default company (V2)', tbl, bad;
                        END IF;
                        EXECUTE format('SELECT COUNT(*) FROM %I', tbl) INTO actual;
                        SELECT n INTO expected FROM m2_counts WHERE m2_counts.t = tbl;
                        IF actual <> expected THEN
                            RAISE EXCEPTION 'AddCompanyIdToCompanyTables: % row count changed from % to % (V1)', tbl, expected, actual;
                        END IF;
                    END LOOP;
                    IF EXISTS (SELECT 1 FROM "StockItems" s
                               LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
                               WHERE s."QuantityOnHand" <> COALESCE(m.q, 0) AND s."ProductId" NOT IN (SELECT "ProductId" FROM m2_unbalanced))
                    OR EXISTS (SELECT 1 FROM "StockItems" s
                               LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
                               WHERE s."QuantityOnHand" <> COALESCE(b.q, 0) AND s."ProductId" NOT IN (SELECT "ProductId" FROM m2_unbalanced)) THEN
                        RAISE EXCEPTION 'AddCompanyIdToCompanyTables: reconciliation broke for a product that was balanced before (V6)';
                    END IF;
                END $a$;
                """);

            // ===== 3. enforce =====
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "Taxes", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "ExchangeRates", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "Suppliers", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "Customers", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "ExpenseCategories", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "Expenses", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "FinancialAccounts", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "FinancialTransactions", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "Products", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "ProductUnits", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "PurchaseOrders", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "PurchaseReturns", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "PurchaseBatches", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "SalesInvoices", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "SalesReturns", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "StockAdjustments", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "StockItems", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "StockMovements", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "CompanyId", table: "IdempotencyKeys", type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.AddForeignKey(name: "FK_Taxes_Companies_CompanyId", table: "Taxes", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_ExchangeRates_Companies_CompanyId", table: "ExchangeRates", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Suppliers_Companies_CompanyId", table: "Suppliers", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Customers_Companies_CompanyId", table: "Customers", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_ExpenseCategories_Companies_CompanyId", table: "ExpenseCategories", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Expenses_Companies_CompanyId", table: "Expenses", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_FinancialAccounts_Companies_CompanyId", table: "FinancialAccounts", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_FinancialTransactions_Companies_CompanyId", table: "FinancialTransactions", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Products_Companies_CompanyId", table: "Products", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_ProductUnits_Companies_CompanyId", table: "ProductUnits", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_PurchaseOrders_Companies_CompanyId", table: "PurchaseOrders", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_PurchaseReturns_Companies_CompanyId", table: "PurchaseReturns", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_PurchaseBatches_Companies_CompanyId", table: "PurchaseBatches", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_SalesInvoices_Companies_CompanyId", table: "SalesInvoices", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_SalesReturns_Companies_CompanyId", table: "SalesReturns", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_StockAdjustments_Companies_CompanyId", table: "StockAdjustments", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_StockItems_Companies_CompanyId", table: "StockItems", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_StockMovements_Companies_CompanyId", table: "StockMovements", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_IdempotencyKeys_Companies_CompanyId", table: "IdempotencyKeys", column: "CompanyId", principalTable: "Companies", principalColumn: "Id", onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(name: "IX_Taxes_CompanyId", table: "Taxes", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_ExchangeRates_CompanyId", table: "ExchangeRates", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_Suppliers_CompanyId", table: "Suppliers", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_Customers_CompanyId", table: "Customers", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_ExpenseCategories_CompanyId", table: "ExpenseCategories", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_Expenses_CompanyId", table: "Expenses", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_FinancialAccounts_CompanyId", table: "FinancialAccounts", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_FinancialTransactions_CompanyId", table: "FinancialTransactions", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_Products_CompanyId", table: "Products", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_ProductUnits_CompanyId", table: "ProductUnits", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrders_CompanyId", table: "PurchaseOrders", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseReturns_CompanyId", table: "PurchaseReturns", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseBatches_CompanyId", table: "PurchaseBatches", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_SalesInvoices_CompanyId", table: "SalesInvoices", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_SalesReturns_CompanyId", table: "SalesReturns", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_StockAdjustments_CompanyId", table: "StockAdjustments", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_StockItems_CompanyId", table: "StockItems", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_StockMovements_CompanyId", table: "StockMovements", column: "CompanyId");
            migrationBuilder.CreateIndex(name: "IX_IdempotencyKeys_CompanyId", table: "IdempotencyKeys", column: "CompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G: a second tenant cannot be folded back into the single-tenant schema.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF (SELECT COUNT(*) FROM "Companies") > 1
                       OR EXISTS (SELECT 1 FROM "Taxes" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "ExchangeRates" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "Suppliers" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "Customers" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "ExpenseCategories" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "Expenses" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "FinancialAccounts" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "FinancialTransactions" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "Products" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "ProductUnits" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "PurchaseOrders" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "PurchaseReturns" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "PurchaseBatches" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "SalesInvoices" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "SalesReturns" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "StockAdjustments" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "StockItems" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "StockMovements" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001')
                       OR EXISTS (SELECT 1 FROM "IdempotencyKeys" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001') THEN
                        RAISE EXCEPTION 'AddCompanyIdToCompanyTables Down: more than one company exists; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropIndex(name: "IX_IdempotencyKeys_CompanyId", table: "IdempotencyKeys");
            migrationBuilder.DropIndex(name: "IX_StockMovements_CompanyId", table: "StockMovements");
            migrationBuilder.DropIndex(name: "IX_StockItems_CompanyId", table: "StockItems");
            migrationBuilder.DropIndex(name: "IX_StockAdjustments_CompanyId", table: "StockAdjustments");
            migrationBuilder.DropIndex(name: "IX_SalesReturns_CompanyId", table: "SalesReturns");
            migrationBuilder.DropIndex(name: "IX_SalesInvoices_CompanyId", table: "SalesInvoices");
            migrationBuilder.DropIndex(name: "IX_PurchaseBatches_CompanyId", table: "PurchaseBatches");
            migrationBuilder.DropIndex(name: "IX_PurchaseReturns_CompanyId", table: "PurchaseReturns");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrders_CompanyId", table: "PurchaseOrders");
            migrationBuilder.DropIndex(name: "IX_ProductUnits_CompanyId", table: "ProductUnits");
            migrationBuilder.DropIndex(name: "IX_Products_CompanyId", table: "Products");
            migrationBuilder.DropIndex(name: "IX_FinancialTransactions_CompanyId", table: "FinancialTransactions");
            migrationBuilder.DropIndex(name: "IX_FinancialAccounts_CompanyId", table: "FinancialAccounts");
            migrationBuilder.DropIndex(name: "IX_Expenses_CompanyId", table: "Expenses");
            migrationBuilder.DropIndex(name: "IX_ExpenseCategories_CompanyId", table: "ExpenseCategories");
            migrationBuilder.DropIndex(name: "IX_Customers_CompanyId", table: "Customers");
            migrationBuilder.DropIndex(name: "IX_Suppliers_CompanyId", table: "Suppliers");
            migrationBuilder.DropIndex(name: "IX_ExchangeRates_CompanyId", table: "ExchangeRates");
            migrationBuilder.DropIndex(name: "IX_Taxes_CompanyId", table: "Taxes");

            migrationBuilder.DropForeignKey(name: "FK_IdempotencyKeys_Companies_CompanyId", table: "IdempotencyKeys");
            migrationBuilder.DropForeignKey(name: "FK_StockMovements_Companies_CompanyId", table: "StockMovements");
            migrationBuilder.DropForeignKey(name: "FK_StockItems_Companies_CompanyId", table: "StockItems");
            migrationBuilder.DropForeignKey(name: "FK_StockAdjustments_Companies_CompanyId", table: "StockAdjustments");
            migrationBuilder.DropForeignKey(name: "FK_SalesReturns_Companies_CompanyId", table: "SalesReturns");
            migrationBuilder.DropForeignKey(name: "FK_SalesInvoices_Companies_CompanyId", table: "SalesInvoices");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseBatches_Companies_CompanyId", table: "PurchaseBatches");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseReturns_Companies_CompanyId", table: "PurchaseReturns");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseOrders_Companies_CompanyId", table: "PurchaseOrders");
            migrationBuilder.DropForeignKey(name: "FK_ProductUnits_Companies_CompanyId", table: "ProductUnits");
            migrationBuilder.DropForeignKey(name: "FK_Products_Companies_CompanyId", table: "Products");
            migrationBuilder.DropForeignKey(name: "FK_FinancialTransactions_Companies_CompanyId", table: "FinancialTransactions");
            migrationBuilder.DropForeignKey(name: "FK_FinancialAccounts_Companies_CompanyId", table: "FinancialAccounts");
            migrationBuilder.DropForeignKey(name: "FK_Expenses_Companies_CompanyId", table: "Expenses");
            migrationBuilder.DropForeignKey(name: "FK_ExpenseCategories_Companies_CompanyId", table: "ExpenseCategories");
            migrationBuilder.DropForeignKey(name: "FK_Customers_Companies_CompanyId", table: "Customers");
            migrationBuilder.DropForeignKey(name: "FK_Suppliers_Companies_CompanyId", table: "Suppliers");
            migrationBuilder.DropForeignKey(name: "FK_ExchangeRates_Companies_CompanyId", table: "ExchangeRates");
            migrationBuilder.DropForeignKey(name: "FK_Taxes_Companies_CompanyId", table: "Taxes");

            migrationBuilder.DropColumn(name: "CompanyId", table: "IdempotencyKeys");
            migrationBuilder.DropColumn(name: "CompanyId", table: "StockMovements");
            migrationBuilder.DropColumn(name: "CompanyId", table: "StockItems");
            migrationBuilder.DropColumn(name: "CompanyId", table: "StockAdjustments");
            migrationBuilder.DropColumn(name: "CompanyId", table: "SalesReturns");
            migrationBuilder.DropColumn(name: "CompanyId", table: "SalesInvoices");
            migrationBuilder.DropColumn(name: "CompanyId", table: "PurchaseBatches");
            migrationBuilder.DropColumn(name: "CompanyId", table: "PurchaseReturns");
            migrationBuilder.DropColumn(name: "CompanyId", table: "PurchaseOrders");
            migrationBuilder.DropColumn(name: "CompanyId", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "CompanyId", table: "Products");
            migrationBuilder.DropColumn(name: "CompanyId", table: "FinancialTransactions");
            migrationBuilder.DropColumn(name: "CompanyId", table: "FinancialAccounts");
            migrationBuilder.DropColumn(name: "CompanyId", table: "Expenses");
            migrationBuilder.DropColumn(name: "CompanyId", table: "ExpenseCategories");
            migrationBuilder.DropColumn(name: "CompanyId", table: "Customers");
            migrationBuilder.DropColumn(name: "CompanyId", table: "Suppliers");
            migrationBuilder.DropColumn(name: "CompanyId", table: "ExchangeRates");
            migrationBuilder.DropColumn(name: "CompanyId", table: "Taxes");
        }
    }
}
