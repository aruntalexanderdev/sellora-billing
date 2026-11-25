using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidatePurchaseTaxConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // AddPurchaseTaxSnapshot added every constraint NOT VALID so its transaction never scanned a purchase table. VALIDATE CONSTRAINT takes only SHARE UPDATE EXCLUSIVE
            // (inserts and updates continue) and checks the rows that existed before. Idempotent (validating a valid constraint is a no-op).
            migrationBuilder.Sql("""
                ALTER TABLE "PurchaseReturnLines" VALIDATE CONSTRAINT "CK_PurchaseReturnLines_PlaceOfSupply";
                ALTER TABLE "PurchaseReturnLines" VALIDATE CONSTRAINT "CK_PurchaseReturnLines_TaxProfile_Pair";
                ALTER TABLE "PurchaseReturnLines" VALIDATE CONSTRAINT "FK_PurchaseReturnLines_TaxProfiles_TaxProfileId";
                ALTER TABLE "PurchaseReturnLines" VALIDATE CONSTRAINT "FK_PurchaseReturnLines_TaxProfileVersions_TaxProfileVersionId";
                ALTER TABLE "PurchaseOrderLines" VALIDATE CONSTRAINT "CK_PurchaseOrderLines_PlaceOfSupply";
                ALTER TABLE "PurchaseOrderLines" VALIDATE CONSTRAINT "CK_PurchaseOrderLines_TaxProfile_Pair";
                ALTER TABLE "PurchaseOrderLines" VALIDATE CONSTRAINT "CK_PurchaseOrderLines_HsnCode_Format";
                ALTER TABLE "PurchaseOrderLines" VALIDATE CONSTRAINT "CK_PurchaseOrderLines_BillShares_NonNegative";
                ALTER TABLE "PurchaseOrderLines" VALIDATE CONSTRAINT "FK_PurchaseOrderLines_TaxProfiles_TaxProfileId";
                ALTER TABLE "PurchaseOrderLines" VALIDATE CONSTRAINT "FK_PurchaseOrderLines_TaxProfileVersions_TaxProfileVersionId";
                ALTER TABLE "PurchaseOrders" VALIDATE CONSTRAINT "CK_PurchaseOrders_PlaceOfSupply";
                ALTER TABLE "PurchaseOrders" VALIDATE CONSTRAINT "CK_PurchaseOrders_PartyTaxStatus";
                ALTER TABLE "PurchaseOrders" VALIDATE CONSTRAINT "CK_PurchaseOrders_BillDiscountPercent_Range";
                ALTER TABLE "PurchaseOrders" VALIDATE CONSTRAINT "CK_PurchaseOrders_BillAmounts_NonNegative";
                ALTER TABLE "PurchaseOrders" VALIDATE CONSTRAINT "FK_PurchaseOrders_States_PlaceOfSupplyStateId";
                ALTER TABLE "PurchaseOrders" VALIDATE CONSTRAINT "FK_PurchaseOrders_States_SupplierStateId";
                ALTER TABLE "PurchaseOrderLineTaxes" VALIDATE CONSTRAINT "CK_PurchaseOrderLineTaxes_Treatment";
                ALTER TABLE "PurchaseReturnLineTaxes" VALIDATE CONSTRAINT "CK_PurchaseReturnLineTaxes_Treatment";
                ALTER TABLE "SalesReturnLineTaxes" VALIDATE CONSTRAINT "CK_SalesReturnLineTaxes_Treatment";
                ALTER TABLE "SalesInvoiceLineTaxes" VALIDATE CONSTRAINT "CK_SalesInvoiceLineTaxes_Treatment";
                """);

            // FK indexes (a plain CREATE INDEX blocks writes of that table for its duration: these are mostly-NULL uuid columns on the purchase tables only, so it is brief).
            migrationBuilder.CreateIndex(name: "IX_PurchaseReturnLines_TaxProfileId", table: "PurchaseReturnLines", column: "TaxProfileId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseReturnLines_TaxProfileVersionId", table: "PurchaseReturnLines", column: "TaxProfileVersionId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrderLines_TaxProfileId", table: "PurchaseOrderLines", column: "TaxProfileId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrderLines_TaxProfileVersionId", table: "PurchaseOrderLines", column: "TaxProfileVersionId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrders_PlaceOfSupplyStateId", table: "PurchaseOrders", column: "PlaceOfSupplyStateId");
            migrationBuilder.CreateIndex(name: "IX_PurchaseOrders_SupplierStateId", table: "PurchaseOrders", column: "SupplierStateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drops the indexes; a validated constraint cannot be made "not validated" and stays correct either way (AddPurchaseTaxSnapshot.Down drops the columns).
            migrationBuilder.DropIndex(name: "IX_PurchaseReturnLines_TaxProfileId", table: "PurchaseReturnLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseReturnLines_TaxProfileVersionId", table: "PurchaseReturnLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrderLines_TaxProfileId", table: "PurchaseOrderLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrderLines_TaxProfileVersionId", table: "PurchaseOrderLines");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrders_PlaceOfSupplyStateId", table: "PurchaseOrders");
            migrationBuilder.DropIndex(name: "IX_PurchaseOrders_SupplierStateId", table: "PurchaseOrders");
        }
    }
}
