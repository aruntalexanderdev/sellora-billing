using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidateTaxHistoryBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // PrepareTaxHistoryBackfill added the "version needs profile" constraints NOT VALID so its transaction never scanned a line table. VALIDATE CONSTRAINT takes only
            // SHARE UPDATE EXCLUSIVE (inserts and updates continue). Idempotent (validating a valid constraint is a no-op). Run it BEFORE the backfill script: it then scans the
            // lines once without the script's updates racing it (the constraint cannot fail either way, it is weaker than the pair it replaces).
            migrationBuilder.Sql("""
                ALTER TABLE "PurchaseReturnLines" VALIDATE CONSTRAINT "CK_PurchaseReturnLines_TaxProfile_VersionNeedsProfile";
                ALTER TABLE "PurchaseOrderLines" VALIDATE CONSTRAINT "CK_PurchaseOrderLines_TaxProfile_VersionNeedsProfile";
                ALTER TABLE "SalesInvoiceLines" VALIDATE CONSTRAINT "CK_SalesInvoiceLines_TaxProfile_VersionNeedsProfile";
                ALTER TABLE "SalesReturnLines" VALIDATE CONSTRAINT "CK_SalesReturnLines_TaxProfile_VersionNeedsProfile";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: a validated constraint cannot be made "not validated" and stays correct (PrepareTaxHistoryBackfill.Down drops it).
        }
    }
}
