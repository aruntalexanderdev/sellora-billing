using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 1 / M3 - company-scoped uniques, (CompanyId, Id) alternate keys, same-company composite FKs (one
     * migration, one transaction).
     *   guard    : G2 raises (and rolls back) when a dependent row references a principal that does not exist in the same company
     *              (P4: composite-pair orphans). With the single default company only true orphans can fail; the existing
     *              single-column FKs make it empty in practice.
     *   uniques  : IX_Products_Sku -> IX_Products_CompanyId_Sku; IX_ProductUnits_Barcode -> IX_ProductUnits_CompanyId_Barcode (same
     *              filter "Barcode" IS NOT NULL AND "IsActive"); IX_Taxes_Name / IX_ExpenseCategories_Name /
     *              IX_FinancialAccounts_Name -> IX_<T>_CompanyId_Name; IX_ExchangeRates_CurrencyId_EffectiveDate ->
     *              IX_ExchangeRates_CompanyId_CurrencyId_EffectiveDate; IX_PurchaseOrders_PurchaseNo, IX_PurchaseReturns_ReturnNo,
     *              IX_SalesInvoices_InvoiceNo, IX_SalesReturns_ReturnNo, IX_StockAdjustments_AdjustmentNo, IX_Expenses_ExpenseNo ->
     *              IX_<T>_CompanyId_<No>; IX_IdempotencyKeys_UserId_Scope_Key -> IX_IdempotencyKeys_CompanyId_UserId_Scope_Key.
     *              Stay global: Users.Username/Email, Permissions.Code, Currencies.Code, Units.Name/ShortCode,
     *              RefreshTokens.TokenHash, PurchaseBatches.BatchSequence, Roles.Name (Phase 7).
     *   keys     : 9 alternate keys AK_<T>_CompanyId_Id on Products, Taxes, Suppliers, Customers, PurchaseOrders, SalesInvoices,
     *              PurchaseBatches, ExpenseCategories, FinancialAccounts.
     *   enforce  : 13 composite FKs (Restrict; StockItems -> Products cascades like its single-column FK): ProductUnits->Products,
     *              Products->Taxes, StockItems->Products, PurchaseBatches->Products, StockMovements->Products,
     *              StockMovements->PurchaseBatches (nullable, MATCH SIMPLE), StockAdjustments->Products, PurchaseOrders->Suppliers,
     *              PurchaseReturns->PurchaseOrders, SalesInvoices->Customers, SalesReturns->SalesInvoices, Expenses->
     *              ExpenseCategories, FinancialTransactions->FinancialAccounts (nullable). EF also creates the supporting
     *              IX_<T>_CompanyId_<FkColumn> indexes; the 19 IX_<T>_CompanyId from M2 are dropped where a company-leading unique
     *              or alternate key covers them (IX_ExchangeRates_CurrencyId replaces the FK index the old unique provided).
     * Not here: line-level company columns (lines inherit; services resolve every master through filtered sets), no
     * (CompanyId, date) report indexes (Phase 2 builds (CompanyId, BranchId, date) once).
     *
     * RELEASE NOTE (R-a): index builds and 13 FK validations scan the big tables (StockMovements, PurchaseBatches, SalesInvoices,
     * FinancialTransactions) under SHARE / SHARE ROW EXCLUSIVE locks; time on the production copy. Startup auto-migrate must stay
     * off in production (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * release  : runs in ONE transaction with M2 locks still relevant: building the scoped unique indexes and validating the 13
     *            composite FKs scans StockMovements, SalesInvoices, PurchaseBatches and the other document tables under
     *            SHARE / SHARE ROW EXCLUSIVE locks (writes blocked until commit). Apply M1-M4 in a maintenance window sized from
     *            the P0 row counts and timed on a production-size copy (R5).
     *
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P4 composite-pair orphans (must be empty); the dependent / column / principal triples are in the guard below. Example:
     * SELECT pu."Id" FROM "ProductUnits" pu LEFT JOIN "Products" p ON p."Id" = pu."ProductId" WHERE p."Id" IS NULL;
     * -- P6 duplicates the new uniques would reject (must be empty; with one company equal to the old uniques)
     * SELECT "Sku" FROM "Products" GROUP BY "Sku" HAVING COUNT(*) > 1;
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; every query must return no rows.
     *
     * -- V4 cross-company pairs (12 document -> master joins, same shape; e.g.)
     * SELECT pu."Id" FROM "ProductUnits" pu JOIN "Products" p ON p."Id" = pu."ProductId" WHERE pu."CompanyId" <> p."CompanyId";
     * SELECT o."Id" FROM "PurchaseOrders" o JOIN "Suppliers" s ON s."Id" = o."SupplierId" WHERE o."CompanyId" <> s."CompanyId";
     * SELECT i."Id" FROM "SalesInvoices" i JOIN "Customers" c ON c."Id" = i."CustomerId" WHERE i."CompanyId" <> c."CompanyId";
     * SELECT m."Id" FROM "StockMovements" m JOIN "PurchaseBatches" b ON b."Id" = m."PurchaseBatchId" WHERE m."CompanyId" <> b."CompanyId";
     * -- V4 children (lines have no CompanyId; their product must be in the header's company)
     * SELECT l."Id" FROM "SalesInvoiceLines" l JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId" JOIN "Products" p ON p."Id" = l."ProductId"
     *   WHERE p."CompanyId" <> i."CompanyId";
     * SELECT l."Id" FROM "PurchaseOrderLines" l JOIN "PurchaseOrders" o ON o."Id" = l."PurchaseOrderId" JOIN "Products" p ON p."Id" = l."ProductId"
     *   WHERE p."CompanyId" <> o."CompanyId";
     * -- (same shape for SalesReturnLines / PurchaseReturnLines; consumptions: batch company vs invoice company)
     * -- V6 reconciliation unchanged: the two queries of M2 P3
     * -- V7 uniques are company-scoped: IX_Products_CompanyId_Sku exists and IX_Products_Sku does not
     * SELECT indexname FROM pg_indexes WHERE tablename = 'Products' AND indexname IN ('IX_Products_Sku', 'IX_Products_CompanyId_Sku');
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G first: raises when more than one company exists or any company-owned row belongs to another company):
     * drops the composite FKs, AKs and company-scoped uniques and recreates the global uniques and single-column
     * IX_<T>_CompanyId indexes. Safe only with one company; otherwise restore the pre-deploy snapshot.
     */
    /// <inheritdoc />
    public partial class ScopeUniquesAndCompositeKeysToCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== guard G2 =====
            migrationBuilder.Sql("""
                DO $g$
                DECLARE
                    r record;
                    orphans bigint;
                BEGIN
                    FOR r IN SELECT * FROM (VALUES
                        ('ProductUnits', 'ProductId', 'Products'),
                        ('Products', 'TaxId', 'Taxes'),
                        ('StockItems', 'ProductId', 'Products'),
                        ('PurchaseBatches', 'ProductId', 'Products'),
                        ('StockMovements', 'ProductId', 'Products'),
                        ('StockMovements', 'PurchaseBatchId', 'PurchaseBatches'),
                        ('StockAdjustments', 'ProductId', 'Products'),
                        ('PurchaseOrders', 'SupplierId', 'Suppliers'),
                        ('PurchaseReturns', 'PurchaseOrderId', 'PurchaseOrders'),
                        ('SalesInvoices', 'CustomerId', 'Customers'),
                        ('SalesReturns', 'SalesInvoiceId', 'SalesInvoices'),
                        ('Expenses', 'CategoryId', 'ExpenseCategories'),
                        ('FinancialTransactions', 'AccountId', 'FinancialAccounts')
                    ) AS v(dep, col, pri) LOOP
                        EXECUTE format('SELECT COUNT(*) FROM %I d WHERE d.%I IS NOT NULL AND NOT EXISTS (SELECT 1 FROM %I p WHERE p."Id" = d.%I AND p."CompanyId" = d."CompanyId")',
                                       r.dep, r.col, r.pri, r.col) INTO orphans;
                        IF orphans > 0 THEN
                            RAISE EXCEPTION 'ScopeUniquesAndCompositeKeysToCompany: % row(s) of %.% have no principal in the same company (run P4).', orphans, r.dep, r.col;
                        END IF;
                    END LOOP;
                END $g$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Taxes_CompanyId",
                table: "Taxes");

            migrationBuilder.DropIndex(
                name: "IX_Taxes_Name",
                table: "Taxes");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_CompanyId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_CompanyId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockItems_CompanyId",
                table: "StockItems");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_AdjustmentNo",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_CompanyId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_CompanyId",
                table: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_ReturnNo",
                table: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_CompanyId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_InvoiceNo",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_CompanyId",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_ReturnNo",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CompanyId",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_PurchaseNo",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseBatches_CompanyId",
                table: "PurchaseBatches");

            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_Barcode",
                table: "ProductUnits");

            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_CompanyId",
                table: "ProductUnits");

            migrationBuilder.DropIndex(
                name: "IX_Products_CompanyId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_Sku",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_IdempotencyKeys_CompanyId",
                table: "IdempotencyKeys");

            migrationBuilder.DropIndex(
                name: "IX_IdempotencyKeys_UserId_Scope_Key",
                table: "IdempotencyKeys");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_CompanyId",
                table: "FinancialTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinancialAccounts_CompanyId",
                table: "FinancialAccounts");

            migrationBuilder.DropIndex(
                name: "IX_FinancialAccounts_Name",
                table: "FinancialAccounts");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_CompanyId",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_ExpenseNo",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseCategories_CompanyId",
                table: "ExpenseCategories");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseCategories_Name",
                table: "ExpenseCategories");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_CompanyId",
                table: "ExchangeRates");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_CurrencyId_EffectiveDate",
                table: "ExchangeRates");

            migrationBuilder.DropIndex(
                name: "IX_Customers_CompanyId",
                table: "Customers");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Taxes_CompanyId_Id",
                table: "Taxes",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Suppliers_CompanyId_Id",
                table: "Suppliers",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SalesInvoices_CompanyId_Id",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PurchaseOrders_CompanyId_Id",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PurchaseBatches_CompanyId_Id",
                table: "PurchaseBatches",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Products_CompanyId_Id",
                table: "Products",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_FinancialAccounts_CompanyId_Id",
                table: "FinancialAccounts",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ExpenseCategories_CompanyId_Id",
                table: "ExpenseCategories",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Customers_CompanyId_Id",
                table: "Customers",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Taxes_CompanyId_Name",
                table: "Taxes",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId_ProductId",
                table: "StockMovements",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId_PurchaseBatchId",
                table: "StockMovements",
                columns: new[] { "CompanyId", "PurchaseBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_CompanyId_ProductId",
                table: "StockItems",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CompanyId_AdjustmentNo",
                table: "StockAdjustments",
                columns: new[] { "CompanyId", "AdjustmentNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CompanyId_ProductId",
                table: "StockAdjustments",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CompanyId_ReturnNo",
                table: "SalesReturns",
                columns: new[] { "CompanyId", "ReturnNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CompanyId_SalesInvoiceId",
                table: "SalesReturns",
                columns: new[] { "CompanyId", "SalesInvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CompanyId_CustomerId",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CompanyId_InvoiceNo",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "InvoiceNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_CompanyId_PurchaseOrderId",
                table: "PurchaseReturns",
                columns: new[] { "CompanyId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_CompanyId_ReturnNo",
                table: "PurchaseReturns",
                columns: new[] { "CompanyId", "ReturnNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CompanyId_PurchaseNo",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "PurchaseNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CompanyId_SupplierId",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseBatches_CompanyId_ProductId",
                table: "PurchaseBatches",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_CompanyId_Barcode",
                table: "ProductUnits",
                columns: new[] { "CompanyId", "Barcode" },
                unique: true,
                filter: "\"Barcode\" IS NOT NULL AND \"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_CompanyId_ProductId",
                table: "ProductUnits",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_CompanyId_Sku",
                table: "Products",
                columns: new[] { "CompanyId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_CompanyId_TaxId",
                table: "Products",
                columns: new[] { "CompanyId", "TaxId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_CompanyId_UserId_Scope_Key",
                table: "IdempotencyKeys",
                columns: new[] { "CompanyId", "UserId", "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_CompanyId_AccountId",
                table: "FinancialTransactions",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAccounts_CompanyId_Name",
                table: "FinancialAccounts",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CompanyId_CategoryId",
                table: "Expenses",
                columns: new[] { "CompanyId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CompanyId_ExpenseNo",
                table: "Expenses",
                columns: new[] { "CompanyId", "ExpenseNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseCategories_CompanyId_Name",
                table: "ExpenseCategories",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_CompanyId_CurrencyId_EffectiveDate",
                table: "ExchangeRates",
                columns: new[] { "CompanyId", "CurrencyId", "EffectiveDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_CurrencyId",
                table: "ExchangeRates",
                column: "CurrencyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_ExpenseCategories_CompanyId_CategoryId",
                table: "Expenses",
                columns: new[] { "CompanyId", "CategoryId" },
                principalTable: "ExpenseCategories",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialTransactions_FinancialAccounts_CompanyId_AccountId",
                table: "FinancialTransactions",
                columns: new[] { "CompanyId", "AccountId" },
                principalTable: "FinancialAccounts",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Taxes_CompanyId_TaxId",
                table: "Products",
                columns: new[] { "CompanyId", "TaxId" },
                principalTable: "Taxes",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductUnits_Products_CompanyId_ProductId",
                table: "ProductUnits",
                columns: new[] { "CompanyId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseBatches_Products_CompanyId_ProductId",
                table: "PurchaseBatches",
                columns: new[] { "CompanyId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrders_Suppliers_CompanyId_SupplierId",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "SupplierId" },
                principalTable: "Suppliers",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturns_PurchaseOrders_CompanyId_PurchaseOrderId",
                table: "PurchaseReturns",
                columns: new[] { "CompanyId", "PurchaseOrderId" },
                principalTable: "PurchaseOrders",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_Customers_CompanyId_CustomerId",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "CustomerId" },
                principalTable: "Customers",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReturns_SalesInvoices_CompanyId_SalesInvoiceId",
                table: "SalesReturns",
                columns: new[] { "CompanyId", "SalesInvoiceId" },
                principalTable: "SalesInvoices",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockAdjustments_Products_CompanyId_ProductId",
                table: "StockAdjustments",
                columns: new[] { "CompanyId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockItems_Products_CompanyId_ProductId",
                table: "StockItems",
                columns: new[] { "CompanyId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Products_CompanyId_ProductId",
                table: "StockMovements",
                columns: new[] { "CompanyId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_PurchaseBatches_CompanyId_PurchaseBatchId",
                table: "StockMovements",
                columns: new[] { "CompanyId", "PurchaseBatchId" },
                principalTable: "PurchaseBatches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G: a second tenant cannot be folded back into global uniques.
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
                        RAISE EXCEPTION 'ScopeUniquesAndCompositeKeysToCompany Down: more than one company exists; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_ExpenseCategories_CompanyId_CategoryId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_FinancialTransactions_FinancialAccounts_CompanyId_AccountId",
                table: "FinancialTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_Taxes_CompanyId_TaxId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductUnits_Products_CompanyId_ProductId",
                table: "ProductUnits");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseBatches_Products_CompanyId_ProductId",
                table: "PurchaseBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrders_Suppliers_CompanyId_SupplierId",
                table: "PurchaseOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturns_PurchaseOrders_CompanyId_PurchaseOrderId",
                table: "PurchaseReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_Customers_CompanyId_CustomerId",
                table: "SalesInvoices");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReturns_SalesInvoices_CompanyId_SalesInvoiceId",
                table: "SalesReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_StockAdjustments_Products_CompanyId_ProductId",
                table: "StockAdjustments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockItems_Products_CompanyId_ProductId",
                table: "StockItems");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Products_CompanyId_ProductId",
                table: "StockMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_PurchaseBatches_CompanyId_PurchaseBatchId",
                table: "StockMovements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Taxes_CompanyId_Id",
                table: "Taxes");

            migrationBuilder.DropIndex(
                name: "IX_Taxes_CompanyId_Name",
                table: "Taxes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Suppliers_CompanyId_Id",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_CompanyId_ProductId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_CompanyId_PurchaseBatchId",
                table: "StockMovements");

            migrationBuilder.DropIndex(
                name: "IX_StockItems_CompanyId_ProductId",
                table: "StockItems");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_CompanyId_AdjustmentNo",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_CompanyId_ProductId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_CompanyId_ReturnNo",
                table: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_CompanyId_SalesInvoiceId",
                table: "SalesReturns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SalesInvoices_CompanyId_Id",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_CompanyId_CustomerId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_CompanyId_InvoiceNo",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_CompanyId_PurchaseOrderId",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_CompanyId_ReturnNo",
                table: "PurchaseReturns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PurchaseOrders_CompanyId_Id",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CompanyId_PurchaseNo",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CompanyId_SupplierId",
                table: "PurchaseOrders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PurchaseBatches_CompanyId_Id",
                table: "PurchaseBatches");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseBatches_CompanyId_ProductId",
                table: "PurchaseBatches");

            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_CompanyId_Barcode",
                table: "ProductUnits");

            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_CompanyId_ProductId",
                table: "ProductUnits");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Products_CompanyId_Id",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_CompanyId_Sku",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_CompanyId_TaxId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_IdempotencyKeys_CompanyId_UserId_Scope_Key",
                table: "IdempotencyKeys");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_CompanyId_AccountId",
                table: "FinancialTransactions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_FinancialAccounts_CompanyId_Id",
                table: "FinancialAccounts");

            migrationBuilder.DropIndex(
                name: "IX_FinancialAccounts_CompanyId_Name",
                table: "FinancialAccounts");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_CompanyId_CategoryId",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_CompanyId_ExpenseNo",
                table: "Expenses");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ExpenseCategories_CompanyId_Id",
                table: "ExpenseCategories");

            migrationBuilder.DropIndex(
                name: "IX_ExpenseCategories_CompanyId_Name",
                table: "ExpenseCategories");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_CompanyId_CurrencyId_EffectiveDate",
                table: "ExchangeRates");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeRates_CurrencyId",
                table: "ExchangeRates");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Customers_CompanyId_Id",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Taxes_CompanyId",
                table: "Taxes",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Taxes_Name",
                table: "Taxes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_CompanyId",
                table: "Suppliers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId",
                table: "StockMovements",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_CompanyId",
                table: "StockItems",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_AdjustmentNo",
                table: "StockAdjustments",
                column: "AdjustmentNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CompanyId",
                table: "StockAdjustments",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CompanyId",
                table: "SalesReturns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_ReturnNo",
                table: "SalesReturns",
                column: "ReturnNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CompanyId",
                table: "SalesInvoices",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_InvoiceNo",
                table: "SalesInvoices",
                column: "InvoiceNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_CompanyId",
                table: "PurchaseReturns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_ReturnNo",
                table: "PurchaseReturns",
                column: "ReturnNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CompanyId",
                table: "PurchaseOrders",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_PurchaseNo",
                table: "PurchaseOrders",
                column: "PurchaseNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseBatches_CompanyId",
                table: "PurchaseBatches",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_Barcode",
                table: "ProductUnits",
                column: "Barcode",
                unique: true,
                filter: "\"Barcode\" IS NOT NULL AND \"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_CompanyId",
                table: "ProductUnits",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_CompanyId",
                table: "Products",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Sku",
                table: "Products",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_CompanyId",
                table: "IdempotencyKeys",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_UserId_Scope_Key",
                table: "IdempotencyKeys",
                columns: new[] { "UserId", "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_CompanyId",
                table: "FinancialTransactions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAccounts_CompanyId",
                table: "FinancialAccounts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAccounts_Name",
                table: "FinancialAccounts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CompanyId",
                table: "Expenses",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_ExpenseNo",
                table: "Expenses",
                column: "ExpenseNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseCategories_CompanyId",
                table: "ExpenseCategories",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseCategories_Name",
                table: "ExpenseCategories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_CompanyId",
                table: "ExchangeRates",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_CurrencyId_EffectiveDate",
                table: "ExchangeRates",
                columns: new[] { "CurrencyId", "EffectiveDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CompanyId",
                table: "Customers",
                column: "CompanyId");
        }
    }
}
