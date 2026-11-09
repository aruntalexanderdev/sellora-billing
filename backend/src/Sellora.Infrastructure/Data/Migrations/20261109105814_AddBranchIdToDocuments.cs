using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 2 / B2 - BranchId on documents (D3, D8): one migration, one transaction.
     *   guard G3 : any company without a default branch -> RAISE (B1 must have run).
     *   additive : "BranchId" uuid NULL on PurchaseOrders, PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments (to be
     *              NOT NULL) and Expenses, FinancialAccounts (stay nullable, NULL = company-level). Metadata-only.
     *   backfill : the 5 headers get their company's default branch (documented default mapping: the business was single-office).
     *              Expenses/FinancialAccounts are NOT tagged (nothing fabricated).
     *   assert   : no NULL BranchId on the 5 headers; row counts unchanged (same DO block, no temp table).
     *   enforce  : SET NOT NULL (5); composite navigation-less FKs FK_<T>_Branches_CompanyId_BranchId
     *              (CompanyId, BranchId) -> Branches (CompanyId, Id) on all 7; indexes (CompanyId, BranchId, <date>) on the 5
     *              headers (they also serve the FK; no redundant FK index) and (CompanyId, BranchId) on Expenses/FinancialAccounts.
     *
     * RELEASE NOTE: old API + new schema is incompatible (inserts without BranchId fail) - stop the API, apply B1-B3, run the
     * V-block, deploy the API. The backfill rewrites all rows of the 5 headers and the index builds lock writes on them (maintenance
     * window; time it on a prod copy, P3).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P0 row counts (compare with V1)
     * SELECT 'PurchaseOrders', COUNT(*) FROM "PurchaseOrders" UNION ALL SELECT 'PurchaseReturns', COUNT(*) FROM "PurchaseReturns"
     * UNION ALL SELECT 'SalesInvoices', COUNT(*) FROM "SalesInvoices" UNION ALL SELECT 'SalesReturns', COUNT(*) FROM "SalesReturns"
     * UNION ALL SELECT 'StockAdjustments', COUNT(*) FROM "StockAdjustments" UNION ALL SELECT 'Expenses', COUNT(*) FROM "Expenses"
     * UNION ALL SELECT 'FinancialAccounts', COUNT(*) FROM "FinancialAccounts";
     * -- P3 time this migration on the copy (SalesInvoices backfill + two index builds dominate)
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; V2, V3, V6 must return no rows (V6 as COUNT = 0), V1 = P0.
     *
     * -- V2 every header on the default branch (single-office legacy data); repeat for the other 4 headers
     * SELECT 'SalesInvoices', "Id" FROM "SalesInvoices" WHERE "BranchId" IS NULL OR "BranchId" <> '0c0a1f00-0000-4000-8000-000000000002';
     * -- V3 same company (the FK makes this empty); repeat for the other 6 tables
     * SELECT i."Id" FROM "SalesInvoices" i JOIN "Branches" b ON b."Id" = i."BranchId" WHERE b."CompanyId" <> i."CompanyId";
     * -- V6 no invented tags
     * SELECT (SELECT COUNT(*) FROM "Expenses" WHERE "BranchId" IS NOT NULL) + (SELECT COUNT(*) FROM "FinancialAccounts" WHERE "BranchId" IS NOT NULL);
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G2 first: raises when a company has more than one branch or a header sits on a branch other than its
     * company's default): drops FKs, indexes and columns. Optional Main tags on Expenses/FinancialAccounts are lost (nothing
     * is derived from them; acceptable). The backfill is derived, hence reversible.
     */
    /// <inheritdoc />
    public partial class AddBranchIdToDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard G3: B1 must have created the default branch of every company.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Companies" c WHERE NOT EXISTS
                               (SELECT 1 FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault")) THEN
                        RAISE EXCEPTION 'AddBranchIdToDocuments: a company has no default branch (run AddBranches first, G3)';
                    END IF;
                END $g$;
                """);

            // ===== 1. additive =====
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "SalesReturns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "SalesInvoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "PurchaseReturns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "FinancialAccounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Expenses",
                type: "uuid",
                nullable: true);

            // ===== 2. backfill + assertions (headers only; Expenses/FinancialAccounts stay untagged) =====
            migrationBuilder.Sql("""
                DO $s$
                DECLARE
                    t text;
                    before_count bigint;
                    after_count bigint;
                    unassigned bigint;
                BEGIN
                    FOREACH t IN ARRAY ARRAY['PurchaseOrders', 'PurchaseReturns', 'SalesInvoices', 'SalesReturns', 'StockAdjustments'] LOOP
                        EXECUTE format('SELECT COUNT(*) FROM %I', t) INTO before_count;
                        EXECUTE format($f$
                            UPDATE %1$I d SET "BranchId" = b."Id"
                            FROM "Branches" b
                            WHERE b."CompanyId" = d."CompanyId" AND b."IsDefault" AND d."BranchId" IS NULL
                            $f$, t);
                        EXECUTE format('SELECT COUNT(*) FROM %I', t) INTO after_count;
                        EXECUTE format('SELECT COUNT(*) FROM %I WHERE "BranchId" IS NULL', t) INTO unassigned;
                        IF unassigned > 0 THEN
                            RAISE EXCEPTION 'AddBranchIdToDocuments: % row(s) of % have no branch after the backfill (V2)', unassigned, t;
                        END IF;
                        IF before_count <> after_count THEN
                            RAISE EXCEPTION 'AddBranchIdToDocuments: row count of % changed (% -> %)', t, before_count, after_count;
                        END IF;
                    END LOOP;
                END $s$;
                """);

            // ===== 3. enforce =====
            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "PurchaseReturns",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "SalesInvoices",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "SalesReturns",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "StockAdjustments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);


            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CompanyId_BranchId_CreatedAt",
                table: "StockAdjustments",
                columns: new[] { "CompanyId", "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CompanyId_BranchId_ReturnDate",
                table: "SalesReturns",
                columns: new[] { "CompanyId", "BranchId", "ReturnDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CompanyId_BranchId_InvoiceDate",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "BranchId", "InvoiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_CompanyId_BranchId_ReturnDate",
                table: "PurchaseReturns",
                columns: new[] { "CompanyId", "BranchId", "ReturnDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CompanyId_BranchId_PurchaseDate",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "BranchId", "PurchaseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAccounts_CompanyId_BranchId",
                table: "FinancialAccounts",
                columns: new[] { "CompanyId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CompanyId_BranchId",
                table: "Expenses",
                columns: new[] { "CompanyId", "BranchId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_Branches_CompanyId_BranchId",
                table: "Expenses",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialAccounts_Branches_CompanyId_BranchId",
                table: "FinancialAccounts",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrders_Branches_CompanyId_BranchId",
                table: "PurchaseOrders",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturns_Branches_CompanyId_BranchId",
                table: "PurchaseReturns",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_Branches_CompanyId_BranchId",
                table: "SalesInvoices",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReturns_Branches_CompanyId_BranchId",
                table: "SalesReturns",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockAdjustments_Branches_CompanyId_BranchId",
                table: "StockAdjustments",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G2: with more than one branch the BranchId columns cannot be dropped without losing information.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Branches" GROUP BY "CompanyId" HAVING COUNT(*) > 1)
                       OR EXISTS (SELECT 1 FROM "PurchaseOrders" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault")
                       OR EXISTS (SELECT 1 FROM "PurchaseReturns" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault")
                       OR EXISTS (SELECT 1 FROM "SalesInvoices" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault")
                       OR EXISTS (SELECT 1 FROM "SalesReturns" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault")
                       OR EXISTS (SELECT 1 FROM "StockAdjustments" d JOIN "Branches" b ON b."Id" = d."BranchId" WHERE NOT b."IsDefault") THEN
                        RAISE EXCEPTION 'AddBranchIdToDocuments Down: documents exist on a non-default branch; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Branches_CompanyId_BranchId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_FinancialAccounts_Branches_CompanyId_BranchId",
                table: "FinancialAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrders_Branches_CompanyId_BranchId",
                table: "PurchaseOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturns_Branches_CompanyId_BranchId",
                table: "PurchaseReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_Branches_CompanyId_BranchId",
                table: "SalesInvoices");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReturns_Branches_CompanyId_BranchId",
                table: "SalesReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_StockAdjustments_Branches_CompanyId_BranchId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_CompanyId_BranchId_CreatedAt",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_CompanyId_BranchId_ReturnDate",
                table: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_CompanyId_BranchId_InvoiceDate",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_CompanyId_BranchId_ReturnDate",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CompanyId_BranchId_PurchaseDate",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_FinancialAccounts_CompanyId_BranchId",
                table: "FinancialAccounts");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_CompanyId_BranchId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "SalesReturns");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "FinancialAccounts");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Expenses");
        }
    }
}
