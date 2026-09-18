using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOriginalPaidAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OriginalPaidAmount",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            // Every pre-existing purchase was paid 1:1 in the base currency.
            migrationBuilder.Sql("UPDATE \"PurchaseOrders\" SET \"OriginalPaidAmount\" = \"PaidAmount\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalPaidAmount",
                table: "PurchaseOrders");
        }
    }
}
