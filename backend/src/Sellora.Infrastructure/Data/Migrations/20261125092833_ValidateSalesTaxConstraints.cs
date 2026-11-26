using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidateSalesTaxConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // AddSalesTaxSnapshot added every constraint NOT VALID so its transaction never scanned a sales table. VALIDATE CONSTRAINT takes only SHARE UPDATE EXCLUSIVE
            // (inserts and updates continue) and checks the rows that existed before. Idempotent (validating a valid constraint is a no-op).
            migrationBuilder.Sql("""
                ALTER TABLE "SalesInvoices" VALIDATE CONSTRAINT "CK_SalesInvoices_PlaceOfSupply";
                ALTER TABLE "SalesInvoices" VALIDATE CONSTRAINT "CK_SalesInvoices_PartyTaxStatus";
                ALTER TABLE "SalesInvoices" VALIDATE CONSTRAINT "CK_SalesInvoices_BillDiscountPercent_Range";
                ALTER TABLE "SalesInvoices" VALIDATE CONSTRAINT "CK_SalesInvoices_BillAmounts_NonNegative";
                ALTER TABLE "SalesInvoices" VALIDATE CONSTRAINT "FK_SalesInvoices_States_PlaceOfSupplyStateId";
                ALTER TABLE "SalesInvoices" VALIDATE CONSTRAINT "FK_SalesInvoices_States_SellerStateId";
                ALTER TABLE "SalesInvoiceLines" VALIDATE CONSTRAINT "CK_SalesInvoiceLines_PlaceOfSupply";
                ALTER TABLE "SalesInvoiceLines" VALIDATE CONSTRAINT "CK_SalesInvoiceLines_TaxProfile_Pair";
                ALTER TABLE "SalesInvoiceLines" VALIDATE CONSTRAINT "CK_SalesInvoiceLines_HsnCode_Format";
                ALTER TABLE "SalesInvoiceLines" VALIDATE CONSTRAINT "CK_SalesInvoiceLines_BillShares_NonNegative";
                ALTER TABLE "SalesInvoiceLines" VALIDATE CONSTRAINT "FK_SalesInvoiceLines_TaxProfiles_TaxProfileId";
                ALTER TABLE "SalesInvoiceLines" VALIDATE CONSTRAINT "FK_SalesInvoiceLines_TaxProfileVersions_TaxProfileVersionId";
                ALTER TABLE "SalesReturnLines" VALIDATE CONSTRAINT "CK_SalesReturnLines_PlaceOfSupply";
                ALTER TABLE "SalesReturnLines" VALIDATE CONSTRAINT "CK_SalesReturnLines_TaxProfile_Pair";
                ALTER TABLE "SalesReturnLines" VALIDATE CONSTRAINT "FK_SalesReturnLines_TaxProfiles_TaxProfileId";
                ALTER TABLE "SalesReturnLines" VALIDATE CONSTRAINT "FK_SalesReturnLines_TaxProfileVersions_TaxProfileVersionId";
                """);

            // SalesInvoiceLines is the big table (TP4-Q9): partial indexes (the columns are NULL on history), raw IF NOT EXISTS so the runbook can pre-create them with
            // CREATE INDEX CONCURRENTLY (scripts/sql/tax-sales-release-notes.sql, section "Runbook") and the migration then finds them. The model declares the same filter,
            // so has-pending-model-changes stays clean. A failed CONCURRENTLY build leaves an INVALID index of the same name which IF NOT EXISTS would silently accept:
            // refuse that here (drop it and re-run the runbook statement).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                             WHERE c.relname IN ('IX_SalesInvoiceLines_TaxProfileId', 'IX_SalesInvoiceLines_TaxProfileVersionId') AND NOT i.indisvalid) THEN
                    RAISE EXCEPTION 'An INVALID index IX_SalesInvoiceLines_TaxProfile(Version)Id exists (failed CREATE INDEX CONCURRENTLY): DROP INDEX it and re-run the migration.';
                  END IF;
                END $$;
                CREATE INDEX IF NOT EXISTS "IX_SalesInvoiceLines_TaxProfileId" ON "SalesInvoiceLines" ("TaxProfileId") WHERE "TaxProfileId" IS NOT NULL;
                CREATE INDEX IF NOT EXISTS "IX_SalesInvoiceLines_TaxProfileVersionId" ON "SalesInvoiceLines" ("TaxProfileVersionId") WHERE "TaxProfileVersionId" IS NOT NULL;
                """);

            // The other FK indexes are plain: mostly-NULL uuid columns on the small tables (a plain CREATE INDEX blocks writes of that table for its brief duration).
            migrationBuilder.CreateIndex(name: "IX_SalesReturnLines_TaxProfileId", table: "SalesReturnLines", column: "TaxProfileId");
            migrationBuilder.CreateIndex(name: "IX_SalesReturnLines_TaxProfileVersionId", table: "SalesReturnLines", column: "TaxProfileVersionId");
            migrationBuilder.CreateIndex(name: "IX_SalesInvoices_PlaceOfSupplyStateId", table: "SalesInvoices", column: "PlaceOfSupplyStateId");
            migrationBuilder.CreateIndex(name: "IX_SalesInvoices_SellerStateId", table: "SalesInvoices", column: "SellerStateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the indexes (also ones pre-created by the runbook, same names); a validated constraint cannot be made "not validated" and stays correct either way
            // (AddSalesTaxSnapshot.Down drops the columns).
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_SalesInvoiceLines_TaxProfileId";
                DROP INDEX IF EXISTS "IX_SalesInvoiceLines_TaxProfileVersionId";
                """);
            migrationBuilder.DropIndex(name: "IX_SalesReturnLines_TaxProfileId", table: "SalesReturnLines");
            migrationBuilder.DropIndex(name: "IX_SalesReturnLines_TaxProfileVersionId", table: "SalesReturnLines");
            migrationBuilder.DropIndex(name: "IX_SalesInvoices_PlaceOfSupplyStateId", table: "SalesInvoices");
            migrationBuilder.DropIndex(name: "IX_SalesInvoices_SellerStateId", table: "SalesInvoices");
        }
    }
}
