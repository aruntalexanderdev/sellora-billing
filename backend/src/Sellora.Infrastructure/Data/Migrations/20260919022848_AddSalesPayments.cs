using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesInvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ExchangeRateToBase = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesPayments", x => x.Id);
                    table.CheckConstraint("CK_SalesPayments_Amount_Positive", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_SalesPayments_SalesInvoices_SalesInvoiceId",
                        column: x => x.SalesInvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesPayments_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesPayments_CreatedByUserId",
                table: "SalesPayments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesPayments_SalesInvoiceId_CreatedAt",
                table: "SalesPayments",
                columns: new[] { "SalesInvoiceId", "CreatedAt" });

            // POS used to store cash tendered above the total as "paid". Change handed back is not money
            // received, so cap it at the invoice total (due and status are unaffected), then turn each
            // invoice's paid amount into its first ledger row.
            migrationBuilder.Sql(@"
                UPDATE ""SalesInvoices"" SET ""PaidAmount"" = ""GrandTotal"" WHERE ""PaidAmount"" > ""GrandTotal"";");
            migrationBuilder.Sql(@"
                INSERT INTO ""SalesPayments"" (""Id"", ""SalesInvoiceId"", ""PaymentDate"", ""Amount"", ""ExchangeRateToBase"", ""BaseAmount"", ""PaymentMethod"", ""CreatedByUserId"", ""CreatedAt"")
                SELECT gen_random_uuid(), ""Id"", ""InvoiceDate"", ""PaidAmount"", 1, ""PaidAmount"", ""PaymentMethod"", ""CreatedByUserId"", ""CreatedAt""
                FROM ""SalesInvoices""
                WHERE ""PaidAmount"" > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesPayments");
        }
    }
}
