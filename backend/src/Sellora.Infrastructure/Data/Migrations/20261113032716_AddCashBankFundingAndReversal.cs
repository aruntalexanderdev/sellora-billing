using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCashBankFundingAndReversal : Migration
    {
        /// <inheritdoc />
        // Additive: new nullable columns and a default-0 OverdraftLimit; existing rows satisfy every new check (they are none of Funding / Reversal / CustomerId).
        // Validation: scripts/sql/cash-bank-funding-validation.sql (must be empty); info: scripts/sql/cash-bank-negative-balances.sql.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_ManualHasAccount",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_Source",
                table: "FinancialTransactions");

            migrationBuilder.AddColumn<string>(
                name: "Counterparty",
                table: "FinancialTransactions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "FinancialTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FundingSource",
                table: "FinancialTransactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "FinancialTransactions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalOfTransactionId",
                table: "FinancialTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceReference",
                table: "FinancialTransactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OverdraftLimit",
                table: "FinancialAccounts",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_CompanyId_CustomerId",
                table: "FinancialTransactions",
                columns: new[] { "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialTransactions_ReversalOfTransactionId",
                table: "FinancialTransactions",
                column: "ReversalOfTransactionId",
                unique: true,
                filter: "\"ReversalOfTransactionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_CustomerReceipt",
                table: "FinancialTransactions",
                sql: "(\"FundingSource\" IS DISTINCT FROM 'CustomerReceipt' OR (\"CustomerId\" IS NOT NULL AND \"SourceReference\" IS NOT NULL)) AND (\"CustomerId\" IS NULL OR \"FundingSource\" = 'CustomerReceipt')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_Funding",
                table: "FinancialTransactions",
                sql: "(\"Type\" = 'Funding') = (\"FundingSource\" IS NOT NULL) AND (\"Type\" <> 'Funding' OR \"Direction\" = 'In')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_ManualHasAccount",
                table: "FinancialTransactions",
                sql: "\"Type\" NOT IN ('OpeningBalance', 'Transfer', 'Funding', 'Reversal') OR \"AccountId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_OtherReason",
                table: "FinancialTransactions",
                sql: "\"FundingSource\" IS DISTINCT FROM 'Other' OR \"Reason\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_Reversal",
                table: "FinancialTransactions",
                sql: "(\"Type\" = 'Reversal') = (\"ReversalOfTransactionId\" IS NOT NULL) AND (\"Type\" <> 'Reversal' OR \"Reason\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_Source",
                table: "FinancialTransactions",
                sql: "(CASE WHEN \"PurchasePaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SalesPaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"ExpenseId\" IS NULL THEN 0 ELSE 1 END) = CASE WHEN \"Type\" IN ('OpeningBalance', 'Transfer', 'Funding', 'Reversal') THEN 0 ELSE 1 END");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAccounts_Overdraft",
                table: "FinancialAccounts",
                sql: "\"OverdraftLimit\" >= 0 AND (\"Kind\" <> 'Cash' OR \"OverdraftLimit\" = 0)");

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialTransactions_Customers_CompanyId_CustomerId",
                table: "FinancialTransactions",
                columns: new[] { "CompanyId", "CustomerId" },
                principalTable: "Customers",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FinancialTransactions_FinancialTransactions_ReversalOfTrans~",
                table: "FinancialTransactions",
                column: "ReversalOfTransactionId",
                principalTable: "FinancialTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // FI-D19/D21: the new company-only permissions go to every role that holds CashBank.Manage (Phase 08 precedent); the seed script inserts the same rows.
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES
                  (md5('perm:CashBank.Fund')::uuid, 'CashBank.Fund', 'Accounts', 'Fund', 'Record funding (capital, loan, deposit, customer receipt) into cash and bank accounts.'),
                  (md5('perm:CashBank.Reverse')::uuid, 'CashBank.Reverse', 'Accounts', 'Reverse', 'Reverse funding, transfers and opening balances posted in error.')
                ON CONFLICT ("Code") DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId" AND ep."Code" = 'CashBank.Manage'
                CROSS JOIN (SELECT "Id" FROM "Permissions" WHERE "Code" IN ('CashBank.Fund', 'CashBank.Reverse')) np
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Funding / Reversal entries and overdraft limits have no representation before this migration: refuse rather than lose money history.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM "FinancialTransactions" WHERE "Type" IN ('Funding', 'Reversal')) THEN
                    RAISE EXCEPTION 'AddCashBankFundingAndReversal Down refused: Funding/Reversal ledger entries exist. Restore the pre-deploy snapshot instead.';
                  END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" IN ('CashBank.Fund', 'CashBank.Reverse'));
                DELETE FROM "Permissions" WHERE "Code" IN ('CashBank.Fund', 'CashBank.Reverse');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_FinancialTransactions_Customers_CompanyId_CustomerId",
                table: "FinancialTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_FinancialTransactions_FinancialTransactions_ReversalOfTrans~",
                table: "FinancialTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_CompanyId_CustomerId",
                table: "FinancialTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FinancialTransactions_ReversalOfTransactionId",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_CustomerReceipt",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_Funding",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_ManualHasAccount",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_OtherReason",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_Reversal",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialTransactions_Source",
                table: "FinancialTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAccounts_Overdraft",
                table: "FinancialAccounts");

            migrationBuilder.DropColumn(
                name: "Counterparty",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "FundingSource",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "ReversalOfTransactionId",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "SourceReference",
                table: "FinancialTransactions");

            migrationBuilder.DropColumn(
                name: "OverdraftLimit",
                table: "FinancialAccounts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_ManualHasAccount",
                table: "FinancialTransactions",
                sql: "\"Type\" NOT IN ('OpeningBalance', 'Transfer') OR \"AccountId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialTransactions_Source",
                table: "FinancialTransactions",
                sql: "(CASE WHEN \"PurchasePaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SalesPaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"ExpenseId\" IS NULL THEN 0 ELSE 1 END) = CASE WHEN \"Type\" IN ('OpeningBalance', 'Transfer') THEN 0 ELSE 1 END");
        }
    }
}
