using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PurchaseOrderLineId",
                table: "PurchaseBatches",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "StockAdjustmentId",
                table: "PurchaseBatches",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StockAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdjustmentNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    QuantityBaseUnits = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BeforeQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    AfterQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockAdjustments_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockAdjustments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseBatches_StockAdjustmentId",
                table: "PurchaseBatches",
                column: "StockAdjustmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_AdjustmentNo",
                table: "StockAdjustments",
                column: "AdjustmentNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_ProductId",
                table: "StockAdjustments",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_UserId",
                table: "StockAdjustments",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseBatches_StockAdjustments_StockAdjustmentId",
                table: "PurchaseBatches",
                column: "StockAdjustmentId",
                principalTable: "StockAdjustments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseBatches_StockAdjustments_StockAdjustmentId",
                table: "PurchaseBatches");

            migrationBuilder.DropTable(
                name: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseBatches_StockAdjustmentId",
                table: "PurchaseBatches");

            migrationBuilder.DropColumn(
                name: "StockAdjustmentId",
                table: "PurchaseBatches");

            migrationBuilder.AlterColumn<Guid>(
                name: "PurchaseOrderLineId",
                table: "PurchaseBatches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
