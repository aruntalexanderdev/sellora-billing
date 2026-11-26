using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseTaxSnapshot : Migration
    {
        private const string TreatmentCheck = "\"Treatment\" IS NULL OR \"Treatment\" IN ('Taxable','NilRated','Exempt','ZeroRated','NonTaxable')";
        private const string PlaceOfSupplyCheck = "\"PlaceOfSupply\" IS NULL OR \"PlaceOfSupply\" IN ('IntraState','InterState','NotApplicable')";
        private const string ProfilePairCheck = "(\"TaxProfileId\" IS NULL) = (\"TaxProfileVersionId\" IS NULL)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TP-3 design (docs/features/tax-profiles.md): purely additive, one transaction, no table rewrite (every column is nullable or has a CONSTANT default), so the
            // TP-2 build can keep inserting during the app swap and can be redeployed without a database rollback. Rollback is application-only; Down() is dev-only.
            // Lock order: PurchaseReturnLines, PurchaseOrderLines, PurchaseOrders, then the four line tax tables LAST (shortest ACCESS EXCLUSIVE window on the sales tables).
            // Every constraint is added NOT VALID (no scan under the lock); ValidatePurchaseTaxConstraints validates them and creates the FK indexes.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // D2 catch-up: lines saved by the TP-2 build between the TP-1 commit and now have no tax row. The frozen, SHA-pinned, idempotent file runs first (before the
            // new Treatment column exists, exactly as released); re-running it is a no-op. Do not edit it.
            migrationBuilder.Sql(MigrationSql.Read("20261124120429_AddTaxProfiles.legacy-rows.sql"));

            // 1. PurchaseReturnLines
            migrationBuilder.AddColumn<decimal>(
                name: "OriginalTaxAmount",
                table: "PurchaseReturnLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalTaxableValue",
                table: "PurchaseReturnLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupply",
                table: "PurchaseReturnLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileId",
                table: "PurchaseReturnLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileVersionId",
                table: "PurchaseReturnLines",
                type: "uuid",
                nullable: true);

            NotValidCheck(migrationBuilder, "PurchaseReturnLines", "CK_PurchaseReturnLines_PlaceOfSupply", PlaceOfSupplyCheck);
            NotValidCheck(migrationBuilder, "PurchaseReturnLines", "CK_PurchaseReturnLines_TaxProfile_Pair", ProfilePairCheck);
            NotValidFk(migrationBuilder, "PurchaseReturnLines", "FK_PurchaseReturnLines_TaxProfiles_TaxProfileId", "TaxProfileId", "TaxProfiles");
            NotValidFk(migrationBuilder, "PurchaseReturnLines", "FK_PurchaseReturnLines_TaxProfileVersions_TaxProfileVersionId", "TaxProfileVersionId", "TaxProfileVersions");

            // 2. PurchaseOrderLines
            migrationBuilder.AddColumn<decimal>(
                name: "BillChargeShare",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BillDiscountShare",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "HsnCode",
                table: "PurchaseOrderLines",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalBillChargeShare",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalBillDiscountShare",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalDiscount",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalLineTotal",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalOtherCharges",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalRate",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalTaxAmount",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalTaxableValue",
                table: "PurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupply",
                table: "PurchaseOrderLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PriceIncludesTax",
                table: "PurchaseOrderLines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileId",
                table: "PurchaseOrderLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxProfileVersionId",
                table: "PurchaseOrderLines",
                type: "uuid",
                nullable: true);

            NotValidCheck(migrationBuilder, "PurchaseOrderLines", "CK_PurchaseOrderLines_PlaceOfSupply", PlaceOfSupplyCheck);
            NotValidCheck(migrationBuilder, "PurchaseOrderLines", "CK_PurchaseOrderLines_TaxProfile_Pair", ProfilePairCheck);
            NotValidCheck(migrationBuilder, "PurchaseOrderLines", "CK_PurchaseOrderLines_HsnCode_Format", "\"HsnCode\" IS NULL OR \"HsnCode\" ~ '^[0-9]{4}([0-9]{2}){0,2}$'");
            NotValidCheck(migrationBuilder, "PurchaseOrderLines", "CK_PurchaseOrderLines_BillShares_NonNegative", "\"BillDiscountShare\" >= 0 AND \"BillChargeShare\" >= 0");
            NotValidFk(migrationBuilder, "PurchaseOrderLines", "FK_PurchaseOrderLines_TaxProfiles_TaxProfileId", "TaxProfileId", "TaxProfiles");
            NotValidFk(migrationBuilder, "PurchaseOrderLines", "FK_PurchaseOrderLines_TaxProfileVersions_TaxProfileVersionId", "TaxProfileVersionId", "TaxProfileVersions");

            // 3. PurchaseOrders (TP3-Q2: the new header money columns are numeric(18,4); the existing numeric(18,2) ones are untouched).
            migrationBuilder.AddColumn<decimal>(
                name: "BillCharges",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BillDiscountAmount",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BillDiscountPercent",
                table: "PurchaseOrders",
                type: "numeric(9,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalBillCharges",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalBillDiscountAmount",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PartyTaxStatus",
                table: "PurchaseOrders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupply",
                table: "PurchaseOrders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlaceOfSupplyStateId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RatesIncludeTax",
                table: "PurchaseOrders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierStateId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxableTotal",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: true);

            NotValidCheck(migrationBuilder, "PurchaseOrders", "CK_PurchaseOrders_PlaceOfSupply", PlaceOfSupplyCheck);
            NotValidCheck(migrationBuilder, "PurchaseOrders", "CK_PurchaseOrders_PartyTaxStatus", "\"PartyTaxStatus\" IN ('Unspecified')");
            NotValidCheck(migrationBuilder, "PurchaseOrders", "CK_PurchaseOrders_BillDiscountPercent_Range", "\"BillDiscountPercent\" IS NULL OR (\"BillDiscountPercent\" >= 0 AND \"BillDiscountPercent\" <= 100)");
            NotValidCheck(migrationBuilder, "PurchaseOrders", "CK_PurchaseOrders_BillAmounts_NonNegative",
                "\"BillDiscountAmount\" >= 0 AND \"BillCharges\" >= 0 AND \"OriginalBillDiscountAmount\" >= 0 AND \"OriginalBillCharges\" >= 0");
            NotValidFk(migrationBuilder, "PurchaseOrders", "FK_PurchaseOrders_States_PlaceOfSupplyStateId", "PlaceOfSupplyStateId", "States");
            NotValidFk(migrationBuilder, "PurchaseOrders", "FK_PurchaseOrders_States_SupplierStateId", "SupplierStateId", "States");

            // 4. Treatment (DQ-TP2-3; NULL = legacy row) on the four line tax tables, LAST. The sales tables are touched only here.
            migrationBuilder.AddColumn<string>(
                name: "Treatment",
                table: "PurchaseOrderLineTaxes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Treatment",
                table: "PurchaseReturnLineTaxes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Treatment",
                table: "SalesReturnLineTaxes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Treatment",
                table: "SalesInvoiceLineTaxes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            foreach (var table in new[] { "PurchaseOrderLineTaxes", "PurchaseReturnLineTaxes", "SalesReturnLineTaxes", "SalesInvoiceLineTaxes" })
            {
                NotValidCheck(migrationBuilder, table, $"CK_{table}_Treatment", TreatmentCheck);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev-only (design: rollback is application-only). Drops every column added by Up with its constraints and indexes; the data in them (tax snapshots of purchases
            // saved by the TP-3 build) is lost, the TP-1 legacy rows and the TaxId mirrors are untouched. The catch-up rows inserted by Up stay (they are the legacy rows
            // the TP-1 build would have written). Re-running Up afterwards works.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            foreach (var table in new[] { "SalesInvoiceLineTaxes", "SalesReturnLineTaxes", "PurchaseReturnLineTaxes", "PurchaseOrderLineTaxes" })
            {
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" DROP CONSTRAINT IF EXISTS \"CK_{table}_Treatment\";");
                migrationBuilder.DropColumn(name: "Treatment", table: table);
            }

            migrationBuilder.Sql("""
                ALTER TABLE "PurchaseOrders"
                  DROP CONSTRAINT IF EXISTS "FK_PurchaseOrders_States_PlaceOfSupplyStateId",
                  DROP CONSTRAINT IF EXISTS "FK_PurchaseOrders_States_SupplierStateId",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrders_PlaceOfSupply",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrders_PartyTaxStatus",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrders_BillDiscountPercent_Range",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrders_BillAmounts_NonNegative";
                ALTER TABLE "PurchaseOrderLines"
                  DROP CONSTRAINT IF EXISTS "FK_PurchaseOrderLines_TaxProfiles_TaxProfileId",
                  DROP CONSTRAINT IF EXISTS "FK_PurchaseOrderLines_TaxProfileVersions_TaxProfileVersionId",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrderLines_PlaceOfSupply",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrderLines_TaxProfile_Pair",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrderLines_HsnCode_Format",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseOrderLines_BillShares_NonNegative";
                ALTER TABLE "PurchaseReturnLines"
                  DROP CONSTRAINT IF EXISTS "FK_PurchaseReturnLines_TaxProfiles_TaxProfileId",
                  DROP CONSTRAINT IF EXISTS "FK_PurchaseReturnLines_TaxProfileVersions_TaxProfileVersionId",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseReturnLines_PlaceOfSupply",
                  DROP CONSTRAINT IF EXISTS "CK_PurchaseReturnLines_TaxProfile_Pair";
                """);

            foreach (var column in new[] { "BillCharges", "BillDiscountAmount", "BillDiscountPercent", "OriginalBillCharges", "OriginalBillDiscountAmount", "PartyTaxStatus", "PlaceOfSupply",
                         "PlaceOfSupplyStateId", "RatesIncludeTax", "SupplierStateId", "TaxableTotal" })
            {
                migrationBuilder.DropColumn(name: column, table: "PurchaseOrders");
            }

            foreach (var column in new[] { "BillChargeShare", "BillDiscountShare", "HsnCode", "OriginalBillChargeShare", "OriginalBillDiscountShare", "OriginalDiscount", "OriginalLineTotal",
                         "OriginalOtherCharges", "OriginalRate", "OriginalTaxAmount", "OriginalTaxableValue", "PlaceOfSupply", "PriceIncludesTax", "TaxProfileId", "TaxProfileVersionId" })
            {
                migrationBuilder.DropColumn(name: column, table: "PurchaseOrderLines");
            }

            foreach (var column in new[] { "OriginalTaxAmount", "OriginalTaxableValue", "PlaceOfSupply", "TaxProfileId", "TaxProfileVersionId" })
            {
                migrationBuilder.DropColumn(name: column, table: "PurchaseReturnLines");
            }
        }

        private static void NotValidCheck(MigrationBuilder migrationBuilder, string table, string name, string expression) =>
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"{name}\" CHECK ({expression}) NOT VALID;");

        private static void NotValidFk(MigrationBuilder migrationBuilder, string table, string name, string column, string principalTable) =>
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"{name}\" FOREIGN KEY (\"{column}\") REFERENCES \"{principalTable}\" (\"Id\") ON DELETE RESTRICT NOT VALID;");
    }
}
