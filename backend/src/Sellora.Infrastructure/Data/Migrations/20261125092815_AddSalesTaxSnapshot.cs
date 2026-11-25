using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesTaxSnapshot : Migration
    {
        private const string PlaceOfSupplyCheck = "\"PlaceOfSupply\" IS NULL OR \"PlaceOfSupply\" IN ('IntraState','InterState','NotApplicable')";
        private const string ProfilePairCheck = "(\"TaxProfileId\" IS NULL) = (\"TaxProfileVersionId\" IS NULL)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TP-4 design (docs/features/tax-profiles.md): purely additive, one transaction, no table rewrite (every column is nullable or has a CONSTANT default), so the
            // TP-3 build can keep inserting during the app swap and can be redeployed without a database rollback. Rollback is application-only; Down() is dev-only.
            // Lock order: ONE LOCK TABLE (SalesInvoices, SalesInvoiceLines, SalesReturnLines: the order the sales service takes its row locks), then only metadata ALTERs.
            // Every constraint is added NOT VALID (no scan under the lock); ValidateSalesTaxConstraints validates them and creates the indexes. The *LineTaxes tables are
            // untouched (Treatment exists since AddPurchaseTaxSnapshot).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // D2 catch-up: lines saved by the TP-3 build between the TP-1 commit and now have no tax row. The frozen, SHA-pinned, idempotent file runs FIRST, before the lock
            // (it takes only row-level locks and runs exactly as released); re-running it is a no-op. Do not edit it.
            migrationBuilder.Sql(MigrationSql.Read("20261124120429_AddTaxProfiles.legacy-rows.sql"));

            // The single ACCESS EXCLUSIVE window. Everything below is catalog-only (constant defaults, nullable columns, NOT VALID constraints).
            migrationBuilder.Sql("LOCK TABLE \"SalesInvoices\", \"SalesInvoiceLines\", \"SalesReturnLines\" IN ACCESS EXCLUSIVE MODE;");

            // 1. SalesInvoices (new header money columns are numeric(18,4); the existing numeric(18,2) ones are untouched).
            migrationBuilder.AddColumn<decimal>(
                name: "BillCharges",
                table: "SalesInvoices",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BillDiscountAmount",
                table: "SalesInvoices",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BillDiscountPercent",
                table: "SalesInvoices",
                type: "numeric(9,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PartyTaxStatus",
                table: "SalesInvoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupply",
                table: "SalesInvoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlaceOfSupplyStateId",
                table: "SalesInvoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SellerStateId",
                table: "SalesInvoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxableTotal",
                table: "SalesInvoices",
                type: "numeric(18,4)",
                nullable: true);

            NotValidCheck(migrationBuilder, "SalesInvoices", "CK_SalesInvoices_PlaceOfSupply", PlaceOfSupplyCheck);
            NotValidCheck(migrationBuilder, "SalesInvoices", "CK_SalesInvoices_PartyTaxStatus", "\"PartyTaxStatus\" IN ('Unspecified')");
            NotValidCheck(migrationBuilder, "SalesInvoices", "CK_SalesInvoices_BillDiscountPercent_Range", "\"BillDiscountPercent\" IS NULL OR (\"BillDiscountPercent\" >= 0 AND \"BillDiscountPercent\" <= 100)");
            NotValidCheck(migrationBuilder, "SalesInvoices", "CK_SalesInvoices_BillAmounts_NonNegative", "\"BillDiscountAmount\" >= 0 AND \"BillCharges\" >= 0");
            NotValidFk(migrationBuilder, "SalesInvoices", "FK_SalesInvoices_States_PlaceOfSupplyStateId", "PlaceOfSupplyStateId", "States");
            NotValidFk(migrationBuilder, "SalesInvoices", "FK_SalesInvoices_States_SellerStateId", "SellerStateId", "States");

            // 2. SalesInvoiceLines (PriceIncludesTax defaults to false: history lines were tax-exclusive).
            migrationBuilder.AddColumn<decimal>(
                name: "BillChargeShare",
                table: "SalesInvoiceLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BillDiscountShare",
                table: "SalesInvoiceLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "HsnCode",
                table: "SalesInvoiceLines",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupply",
                table: "SalesInvoiceLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PriceIncludesTax",
                table: "SalesInvoiceLines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileId",
                table: "SalesInvoiceLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileVersionId",
                table: "SalesInvoiceLines",
                type: "uuid",
                nullable: true);

            NotValidCheck(migrationBuilder, "SalesInvoiceLines", "CK_SalesInvoiceLines_PlaceOfSupply", PlaceOfSupplyCheck);
            NotValidCheck(migrationBuilder, "SalesInvoiceLines", "CK_SalesInvoiceLines_TaxProfile_Pair", ProfilePairCheck);
            NotValidCheck(migrationBuilder, "SalesInvoiceLines", "CK_SalesInvoiceLines_HsnCode_Format", "\"HsnCode\" IS NULL OR \"HsnCode\" ~ '^[0-9]{4}([0-9]{2}){0,2}$'");
            NotValidCheck(migrationBuilder, "SalesInvoiceLines", "CK_SalesInvoiceLines_BillShares_NonNegative", "\"BillDiscountShare\" >= 0 AND \"BillChargeShare\" >= 0");
            NotValidFk(migrationBuilder, "SalesInvoiceLines", "FK_SalesInvoiceLines_TaxProfiles_TaxProfileId", "TaxProfileId", "TaxProfiles");
            NotValidFk(migrationBuilder, "SalesInvoiceLines", "FK_SalesInvoiceLines_TaxProfileVersions_TaxProfileVersionId", "TaxProfileVersionId", "TaxProfileVersions");

            // 3. SalesReturnLines
            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupply",
                table: "SalesReturnLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileId",
                table: "SalesReturnLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileVersionId",
                table: "SalesReturnLines",
                type: "uuid",
                nullable: true);

            NotValidCheck(migrationBuilder, "SalesReturnLines", "CK_SalesReturnLines_PlaceOfSupply", PlaceOfSupplyCheck);
            NotValidCheck(migrationBuilder, "SalesReturnLines", "CK_SalesReturnLines_TaxProfile_Pair", ProfilePairCheck);
            NotValidFk(migrationBuilder, "SalesReturnLines", "FK_SalesReturnLines_TaxProfiles_TaxProfileId", "TaxProfileId", "TaxProfiles");
            NotValidFk(migrationBuilder, "SalesReturnLines", "FK_SalesReturnLines_TaxProfileVersions_TaxProfileVersionId", "TaxProfileVersionId", "TaxProfileVersions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev-only (design: rollback is application-only). Drops every column added by Up with its constraints and indexes; the data in them (tax snapshots of sales
            // saved by the TP-4 build) is lost, the TP-1 legacy rows and the TaxId mirrors are untouched. The catch-up rows inserted by Up stay (they are the legacy rows
            // the TP-1 build would have written). Re-running Up afterwards works.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.Sql("""
                ALTER TABLE "SalesInvoices"
                  DROP CONSTRAINT IF EXISTS "FK_SalesInvoices_States_PlaceOfSupplyStateId",
                  DROP CONSTRAINT IF EXISTS "FK_SalesInvoices_States_SellerStateId",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoices_PlaceOfSupply",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoices_PartyTaxStatus",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoices_BillDiscountPercent_Range",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoices_BillAmounts_NonNegative";
                ALTER TABLE "SalesInvoiceLines"
                  DROP CONSTRAINT IF EXISTS "FK_SalesInvoiceLines_TaxProfiles_TaxProfileId",
                  DROP CONSTRAINT IF EXISTS "FK_SalesInvoiceLines_TaxProfileVersions_TaxProfileVersionId",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoiceLines_PlaceOfSupply",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoiceLines_TaxProfile_Pair",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoiceLines_HsnCode_Format",
                  DROP CONSTRAINT IF EXISTS "CK_SalesInvoiceLines_BillShares_NonNegative";
                ALTER TABLE "SalesReturnLines"
                  DROP CONSTRAINT IF EXISTS "FK_SalesReturnLines_TaxProfiles_TaxProfileId",
                  DROP CONSTRAINT IF EXISTS "FK_SalesReturnLines_TaxProfileVersions_TaxProfileVersionId",
                  DROP CONSTRAINT IF EXISTS "CK_SalesReturnLines_PlaceOfSupply",
                  DROP CONSTRAINT IF EXISTS "CK_SalesReturnLines_TaxProfile_Pair";
                """);

            foreach (var column in new[] { "BillCharges", "BillDiscountAmount", "BillDiscountPercent", "PartyTaxStatus", "PlaceOfSupply", "PlaceOfSupplyStateId", "SellerStateId", "TaxableTotal" })
            {
                migrationBuilder.DropColumn(name: column, table: "SalesInvoices");
            }

            foreach (var column in new[] { "BillChargeShare", "BillDiscountShare", "HsnCode", "PlaceOfSupply", "PriceIncludesTax", "TaxProfileId", "TaxProfileVersionId" })
            {
                migrationBuilder.DropColumn(name: column, table: "SalesInvoiceLines");
            }

            foreach (var column in new[] { "PlaceOfSupply", "TaxProfileId", "TaxProfileVersionId" })
            {
                migrationBuilder.DropColumn(name: column, table: "SalesReturnLines");
            }
        }

        private static void NotValidCheck(MigrationBuilder migrationBuilder, string table, string name, string expression) =>
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"{name}\" CHECK ({expression}) NOT VALID;");

        private static void NotValidFk(MigrationBuilder migrationBuilder, string table, string name, string column, string principalTable) =>
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"{name}\" FOREIGN KEY (\"{column}\") REFERENCES \"{principalTable}\" (\"Id\") ON DELETE RESTRICT NOT VALID;");
    }
}
