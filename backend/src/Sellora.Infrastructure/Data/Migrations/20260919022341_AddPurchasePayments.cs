using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchasePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchasePayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeRateToBase = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchasePayments", x => x.Id);
                    table.CheckConstraint("CK_PurchasePayments_Amount_Positive", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_PurchasePayments_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchasePayments_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchasePayments_CreatedByUserId",
                table: "PurchasePayments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchasePayments_PurchaseOrderId_CreatedAt",
                table: "PurchasePayments",
                columns: new[] { "PurchaseOrderId", "CreatedAt" });

            // Each purchase's existing lump-sum paid amount becomes its first ledger row, so paid totals and
            // history agree from day one. Nothing on PurchaseOrders is changed.
            migrationBuilder.Sql(@"
                INSERT INTO ""PurchasePayments"" (""Id"", ""PurchaseOrderId"", ""PaymentDate"", ""Amount"", ""ExchangeRateToBase"", ""BaseAmount"", ""PaymentMethod"", ""CreatedByUserId"", ""CreatedAt"")
                SELECT gen_random_uuid(), ""Id"", ""PurchaseDate"", ""OriginalPaidAmount"", ""ExchangeRateToBase"", ""PaidAmount"", ""PaymentMethod"", ""CreatedByUserId"", ""CreatedAt""
                FROM ""PurchaseOrders""
                WHERE ""OriginalPaidAmount"" > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchasePayments");
        }
    }
}
