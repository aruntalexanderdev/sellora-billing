using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinancialTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeRateToBase = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PurchasePaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SalesPaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpenseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialTransactions", x => x.Id);
                    table.CheckConstraint("CK_FinancialTransactions_Amount_Positive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_FinancialTransactions_OneSource", "(CASE WHEN \"PurchasePaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SalesPaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"ExpenseId\" IS NULL THEN 0 ELSE 1 END) = 1");
                    table.ForeignKey(
                        name: "FK_FinancialTransactions_Currencies_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialTransactions_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialTransactions_PurchasePayments_PurchasePaymentId",
                        column: x => x.PurchasePaymentId,
                        principalTable: "PurchasePayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialTransactions_SalesPayments_SalesPaymentId",
                        column: x => x.SalesPaymentId,
                        principalTable: "SalesPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialTransactions_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_CreatedByUserId",
                table: "FinancialTransactions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_CurrencyId",
                table: "FinancialTransactions",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_ExpenseId",
                table: "FinancialTransactions",
                column: "ExpenseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_PurchasePaymentId",
                table: "FinancialTransactions",
                column: "PurchasePaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_SalesPaymentId",
                table: "FinancialTransactions",
                column: "SalesPaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_TransactionDate_CreatedAt",
                table: "FinancialTransactions",
                columns: new[] { "TransactionDate", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_Type_Status",
                table: "FinancialTransactions",
                columns: new[] { "Type", "Status" });

            // One transaction per existing source record, so history agrees with the ledgers from day one.
            // Nothing on the source tables is changed.
            migrationBuilder.Sql(@"
                INSERT INTO ""FinancialTransactions"" (""Id"", ""Type"", ""Direction"", ""Status"", ""TransactionDate"", ""CurrencyId"", ""Amount"", ""ExchangeRateToBase"", ""BaseAmount"", ""PaymentMethod"", ""PurchasePaymentId"", ""CreatedByUserId"", ""CreatedAt"")
                SELECT gen_random_uuid(), 'SupplierPayment', 'Out', 'Active', pp.""PaymentDate"", po.""CurrencyId"", pp.""Amount"", pp.""ExchangeRateToBase"", pp.""BaseAmount"", pp.""PaymentMethod"", pp.""Id"", pp.""CreatedByUserId"", pp.""CreatedAt""
                FROM ""PurchasePayments"" pp JOIN ""PurchaseOrders"" po ON po.""Id"" = pp.""PurchaseOrderId"";");
            migrationBuilder.Sql(@"
                INSERT INTO ""FinancialTransactions"" (""Id"", ""Type"", ""Direction"", ""Status"", ""TransactionDate"", ""CurrencyId"", ""Amount"", ""ExchangeRateToBase"", ""BaseAmount"", ""PaymentMethod"", ""SalesPaymentId"", ""CreatedByUserId"", ""CreatedAt"")
                SELECT gen_random_uuid(), 'CustomerPayment', 'In', 'Active', sp.""PaymentDate"", (SELECT ""BaseCurrencyId"" FROM ""BusinessSettings"" LIMIT 1), sp.""Amount"", sp.""ExchangeRateToBase"", sp.""BaseAmount"", sp.""PaymentMethod"", sp.""Id"", sp.""CreatedByUserId"", sp.""CreatedAt""
                FROM ""SalesPayments"" sp
                WHERE EXISTS (SELECT 1 FROM ""BusinessSettings"");");
            migrationBuilder.Sql(@"
                INSERT INTO ""FinancialTransactions"" (""Id"", ""Type"", ""Direction"", ""Status"", ""TransactionDate"", ""CurrencyId"", ""Amount"", ""ExchangeRateToBase"", ""BaseAmount"", ""PaymentMethod"", ""ExpenseId"", ""CreatedByUserId"", ""CreatedAt"")
                SELECT gen_random_uuid(), 'ExpensePayment', 'Out', CASE WHEN e.""Status"" = 'Cancelled' THEN 'Cancelled' ELSE 'Active' END, e.""ExpenseDate"", e.""CurrencyId"", e.""Amount"", e.""ExchangeRateToBase"", e.""BaseAmount"", e.""PaymentMethod"", e.""Id"", e.""CreatedByUserId"", e.""CreatedAt""
                FROM ""Expenses"" e;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinancialTransactions");
        }
    }
}
