using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnsRefundsAndReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SalesPayments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Payment");

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "SalesPayments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversesPaymentId",
                table: "SalesPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesReturnId",
                table: "SalesPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedAmount",
                table: "SalesInvoices",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedQtyBaseUnits",
                table: "SalesInvoiceLineBatchConsumptions",
                type: "numeric(18,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PurchasePayments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Payment");

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseReturnId",
                table: "PurchasePayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "PurchasePayments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversesPaymentId",
                table: "PurchasePayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalReturnedAmount",
                table: "PurchaseOrders",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedAmount",
                table: "PurchaseOrders",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "PurchaseReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TaxableTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TaxTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    OriginalTotal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReturns", x => x.Id);
                    table.CheckConstraint("CK_PurchaseReturns_Total_Positive", "\"Total\" >= 0");
                    table.ForeignKey(
                        name: "FK_PurchaseReturns_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReturns_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SalesInvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TaxableTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TaxTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturns", x => x.Id);
                    table.CheckConstraint("CK_SalesReturns_Total_Positive", "\"Total\" >= 0");
                    table.ForeignKey(
                        name: "FK_SalesReturns_SalesInvoices_SalesInvoiceId",
                        column: x => x.SalesInvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturns_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReturnLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    BaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    TaxableValue = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CostRemoved = table.Column<decimal>(type: "numeric(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReturnLines", x => x.Id);
                    table.CheckConstraint("CK_PurchaseReturnLines_Qty_Positive", "\"Qty\" > 0");
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLines_PurchaseOrderLines_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLines_PurchaseReturns_PurchaseReturnId",
                        column: x => x.PurchaseReturnId,
                        principalTable: "PurchaseReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SalesReturnLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesInvoiceLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    BaseQty = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    TaxableValue = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CostRestored = table.Column<decimal>(type: "numeric(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturnLines", x => x.Id);
                    table.CheckConstraint("CK_SalesReturnLines_Qty_Positive", "\"Qty\" > 0");
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_SalesInvoiceLines_SalesInvoiceLineId",
                        column: x => x.SalesInvoiceLineId,
                        principalTable: "SalesInvoiceLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_SalesReturns_SalesReturnId",
                        column: x => x.SalesReturnId,
                        principalTable: "SalesReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesPayments_ReversesPaymentId",
                table: "SalesPayments",
                column: "ReversesPaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesPayments_SalesReturnId",
                table: "SalesPayments",
                column: "SalesReturnId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SalesPayments_ReversalLink",
                table: "SalesPayments",
                sql: "(\"Kind\" = 'Reversal') = (\"ReversesPaymentId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SalesConsumption_ReturnedWithinConsumed",
                table: "SalesInvoiceLineBatchConsumptions",
                sql: "\"ReturnedQtyBaseUnits\" >= 0 AND \"ReturnedQtyBaseUnits\" <= \"QtyBaseUnits\"");

            migrationBuilder.CreateIndex(
                name: "IX_PurchasePayments_PurchaseReturnId",
                table: "PurchasePayments",
                column: "PurchaseReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchasePayments_ReversesPaymentId",
                table: "PurchasePayments",
                column: "ReversesPaymentId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchasePayments_ReversalLink",
                table: "PurchasePayments",
                sql: "(\"Kind\" = 'Reversal') = (\"ReversesPaymentId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_ProductId",
                table: "PurchaseReturnLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_PurchaseOrderLineId",
                table: "PurchaseReturnLines",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_PurchaseReturnId",
                table: "PurchaseReturnLines",
                column: "PurchaseReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_CreatedByUserId",
                table: "PurchaseReturns",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_PurchaseOrderId_CreatedAt",
                table: "PurchaseReturns",
                columns: new[] { "PurchaseOrderId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_ReturnNo",
                table: "PurchaseReturns",
                column: "ReturnNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLines_ProductId",
                table: "SalesReturnLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLines_SalesInvoiceLineId",
                table: "SalesReturnLines",
                column: "SalesInvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLines_SalesReturnId",
                table: "SalesReturnLines",
                column: "SalesReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CreatedByUserId",
                table: "SalesReturns",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_ReturnNo",
                table: "SalesReturns",
                column: "ReturnNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_SalesInvoiceId_CreatedAt",
                table: "SalesReturns",
                columns: new[] { "SalesInvoiceId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_PurchasePayments_PurchasePayments_ReversesPaymentId",
                table: "PurchasePayments",
                column: "ReversesPaymentId",
                principalTable: "PurchasePayments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchasePayments_PurchaseReturns_PurchaseReturnId",
                table: "PurchasePayments",
                column: "PurchaseReturnId",
                principalTable: "PurchaseReturns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesPayments_SalesPayments_ReversesPaymentId",
                table: "SalesPayments",
                column: "ReversesPaymentId",
                principalTable: "SalesPayments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesPayments_SalesReturns_SalesReturnId",
                table: "SalesPayments",
                column: "SalesReturnId",
                principalTable: "SalesReturns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchasePayments_PurchasePayments_ReversesPaymentId",
                table: "PurchasePayments");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchasePayments_PurchaseReturns_PurchaseReturnId",
                table: "PurchasePayments");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesPayments_SalesPayments_ReversesPaymentId",
                table: "SalesPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesPayments_SalesReturns_SalesReturnId",
                table: "SalesPayments");

            migrationBuilder.DropTable(
                name: "PurchaseReturnLines");

            migrationBuilder.DropTable(
                name: "SalesReturnLines");

            migrationBuilder.DropTable(
                name: "PurchaseReturns");

            migrationBuilder.DropTable(
                name: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesPayments_ReversesPaymentId",
                table: "SalesPayments");

            migrationBuilder.DropIndex(
                name: "IX_SalesPayments_SalesReturnId",
                table: "SalesPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesPayments_ReversalLink",
                table: "SalesPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesConsumption_ReturnedWithinConsumed",
                table: "SalesInvoiceLineBatchConsumptions");

            migrationBuilder.DropIndex(
                name: "IX_PurchasePayments_PurchaseReturnId",
                table: "PurchasePayments");

            migrationBuilder.DropIndex(
                name: "IX_PurchasePayments_ReversesPaymentId",
                table: "PurchasePayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchasePayments_ReversalLink",
                table: "PurchasePayments");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SalesPayments");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "SalesPayments");

            migrationBuilder.DropColumn(
                name: "ReversesPaymentId",
                table: "SalesPayments");

            migrationBuilder.DropColumn(
                name: "SalesReturnId",
                table: "SalesPayments");

            migrationBuilder.DropColumn(
                name: "ReturnedAmount",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "ReturnedQtyBaseUnits",
                table: "SalesInvoiceLineBatchConsumptions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PurchasePayments");

            migrationBuilder.DropColumn(
                name: "PurchaseReturnId",
                table: "PurchasePayments");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "PurchasePayments");

            migrationBuilder.DropColumn(
                name: "ReversesPaymentId",
                table: "PurchasePayments");

            migrationBuilder.DropColumn(
                name: "OriginalReturnedAmount",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReturnedAmount",
                table: "PurchaseOrders");
        }
    }
}
