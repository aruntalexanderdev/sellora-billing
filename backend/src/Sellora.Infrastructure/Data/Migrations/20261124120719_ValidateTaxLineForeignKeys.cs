using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidateTaxLineForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AddTaxProfiles added the four line FKs NOT VALID so that its transaction never scanned the line tables. VALIDATE takes only
            // SHARE UPDATE EXCLUSIVE (inserts and updates continue) and checks the rows that existed before the constraint. Idempotent.
            migrationBuilder.Sql("ALTER TABLE \"SalesInvoiceLineTaxes\" VALIDATE CONSTRAINT \"FK_SalesInvoiceLineTaxes_SalesInvoiceLines\";");
            migrationBuilder.Sql("ALTER TABLE \"PurchaseOrderLineTaxes\" VALIDATE CONSTRAINT \"FK_PurchaseOrderLineTaxes_PurchaseOrderLines\";");
            migrationBuilder.Sql("ALTER TABLE \"SalesReturnLineTaxes\" VALIDATE CONSTRAINT \"FK_SalesReturnLineTaxes_SalesReturnLines\";");
            migrationBuilder.Sql("ALTER TABLE \"PurchaseReturnLineTaxes\" VALIDATE CONSTRAINT \"FK_PurchaseReturnLineTaxes_PurchaseReturnLines\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: a validated constraint cannot be made "not validated" and stays correct either way; AddTaxProfiles.Down drops the tables.
        }
    }
}
