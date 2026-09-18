using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiCurrency : Migration
    {
        private static readonly Guid InrCurrencyId = new("7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Currencies must exist (with INR seeded) before any column that references it is added, so
            // every existing supplier / purchase / the settings row can be backfilled to INR in one step.
            // INR is inserted here rather than in DbSeeder because migrations run in every environment
            // while DbSeeder is development-only.
            migrationBuilder.CreateTable(
                name: "Currencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    DecimalPrecision = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currencies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Currencies_Code",
                table: "Currencies",
                column: "Code",
                unique: true);

            migrationBuilder.InsertData(
                table: "Currencies",
                columns: new[] { "Id", "Code", "Name", "Symbol", "DecimalPrecision", "IsActive" },
                values: new object[] { InrCurrencyId, "INR", "Indian Rupee", "\u20B9", 2, true });

            migrationBuilder.AddColumn<Guid>(
                name: "BaseCurrencyId",
                table: "BusinessSettings",
                type: "uuid",
                nullable: false,
                defaultValue: InrCurrencyId);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrencyId",
                table: "Suppliers",
                type: "uuid",
                nullable: false,
                defaultValue: InrCurrencyId);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrencyId",
                table: "PurchaseOrders",
                type: "uuid",
                nullable: false,
                defaultValue: InrCurrencyId);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRateToBase",
                table: "PurchaseOrders",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalGrandTotal",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            // Every pre-existing purchase was implicitly 1:1 INR, so its original total is its grand total.
            migrationBuilder.Sql("UPDATE \"PurchaseOrders\" SET \"OriginalGrandTotal\" = \"GrandTotal\";");

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RateToBase = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangeRates_Currencies_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExchangeRates_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_CurrencyId",
                table: "Suppliers",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CurrencyId",
                table: "PurchaseOrders",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSettings_BaseCurrencyId",
                table: "BusinessSettings",
                column: "BaseCurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_CreatedByUserId",
                table: "ExchangeRates",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_CurrencyId_EffectiveDate",
                table: "ExchangeRates",
                columns: new[] { "CurrencyId", "EffectiveDate" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BusinessSettings_Currencies_BaseCurrencyId",
                table: "BusinessSettings",
                column: "BaseCurrencyId",
                principalTable: "Currencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrders_Currencies_CurrencyId",
                table: "PurchaseOrders",
                column: "CurrencyId",
                principalTable: "Currencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Suppliers_Currencies_CurrencyId",
                table: "Suppliers",
                column: "CurrencyId",
                principalTable: "Currencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BusinessSettings_Currencies_BaseCurrencyId",
                table: "BusinessSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrders_Currencies_CurrencyId",
                table: "PurchaseOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_Suppliers_Currencies_CurrencyId",
                table: "Suppliers");

            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "Currencies");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_CurrencyId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CurrencyId",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_BusinessSettings_BaseCurrencyId",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "CurrencyId",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ExchangeRateToBase",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "OriginalGrandTotal",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyId",
                table: "BusinessSettings");
        }
    }
}
