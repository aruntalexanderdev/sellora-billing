using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCashAndBankAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_OneSource",
                table: "FinancialTransactions");

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "FinancialTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "FinancialTransactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReconciledAt",
                table: "FinancialTransactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReconciledByUserId",
                table: "FinancialTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TransferGroupId",
                table: "FinancialTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FinancialAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinancialAccounts_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_AccountId_TransactionDate",
                table: "FinancialTransactions",
                columns: new[] { "AccountId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_ReconciledByUserId",
                table: "FinancialTransactions",
                column: "ReconciledByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_ManualHasAccount",
                table: "FinancialTransactions",
                sql: "\"Type\" NOT IN ('OpeningBalance', 'Transfer') OR \"AccountId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_Source",
                table: "FinancialTransactions",
                sql: "(CASE WHEN \"PurchasePaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SalesPaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"ExpenseId\" IS NULL THEN 0 ELSE 1 END) = CASE WHEN \"Type\" IN ('OpeningBalance', 'Transfer') THEN 0 ELSE 1 END");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAccounts_CreatedByUserId",
                table: "FinancialAccounts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAccounts_Name",
                table: "FinancialAccounts",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialTransactions_FinancialAccounts_AccountId",
                table: "FinancialTransactions",
                column: "AccountId",
                principalTable: "FinancialAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialTransactions_Users_ReconciledByUserId",
                table: "FinancialTransactions",
                column: "ReconciledByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FinancialTransactions_FinancialAccounts_AccountId",
                table: "FinancialTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_FinancialTransactions_Users_ReconciledByUserId",
                table: "FinancialTransactions");

            migrationBuilder.DropTable(
                name: "FinancialAccounts");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_AccountId_TransactionDate",
                table: "FinancialTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_ReconciledByUserId",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_ManualHasAccount",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_Source",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "ReconciledAt",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "ReconciledByUserId",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "TransferGroupId",
                table: "FinancialTransactions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_OneSource",
                table: "FinancialTransactions",
                sql: "(CASE WHEN \"PurchasePaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SalesPaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"ExpenseId\" IS NULL THEN 0 ELSE 1 END) = 1");
        }
    }
}
