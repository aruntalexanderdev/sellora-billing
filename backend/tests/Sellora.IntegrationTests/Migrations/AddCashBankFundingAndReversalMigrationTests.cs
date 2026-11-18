using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 07 <c>AddCashBankFundingAndReversal</c>: existing ledger rows survive untouched, each new CHECK / unique index rejects bad rows and accepts good ones,
/// the shipped validation script is clean, and Down restores the previous schema (and refuses while Funding / Reversal rows exist).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddCashBankFundingAndReversalMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261112114131_AddProductCategoryMasters";
    private const string This = "20261113032716_AddCashBankFundingAndReversal";
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";
    private static readonly string Till = G(402), User = G(12), Customer = G(13), Expense = G(403), Currency = "7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01";

    private static string Tx(int id, string type, string direction, string? account, string extraCols = "", string extraVals = "", string? expense = null) => $"""
        INSERT INTO "FinancialTransactions" ("Id", "CompanyId", "Type", "Direction", "Status", "TransactionDate", "CurrencyId", "Amount", "ExchangeRateToBase", "BaseAmount", "PaymentMethod", "AccountId", "ExpenseId", "CreatedByUserId", "CreatedAt"{extraCols})
        VALUES ('{G(id)}', '{DefaultCompany}', '{type}', '{direction}', 'Active', DATE '2026-11-01', '{Currency}', 10, 1, 10, 'Cash', {(account is null ? "NULL" : $"'{account}'")}, {(expense is null ? "NULL" : $"'{expense}'")}, '{User}', now(){extraVals});
        """;

    private static string Funding(int id, string source, string cols = "", string vals = "", string direction = "In") =>
        Tx(id, "Funding", direction, Till, ", \"FundingSource\"" + cols, $", '{source}'" + vals);

    private static string Reversal(int id, int of, string? reason = "oops") =>
        Tx(id, "Reversal", "Out", Till, ", \"ReversalOfTransactionId\", \"Reason\"", $", '{G(of)}', {(reason is null ? "NULL" : $"'{reason}'")}");

    private async Task<TestDatabase> CreateAtPreviousAsync() => await CreateAtAsync(postgres, Previous);

    private async Task<TestDatabase> CreateWithLedgerAtPreviousAsync()
    {
        var database = await CreateAtPreviousAsync();
        await ExecuteAsync(database, Tx(1, "ExpensePayment", "Out", Till, expense: Expense) + Tx(2, "OpeningBalance", "In", Till));
        return database;
    }

    private static async Task AssertRejectedAsync(TestDatabase database, string sql, string constraint) =>
        await AssertRaisesAsync(() => ExecuteAsync(database, sql), constraint);

    [Fact]
    public async Task Up_ExistingLedgerAndAccountsSurvive_AndValidationIsClean()
    {
        await using var database = await CreateWithLedgerAtPreviousAsync();
        var before = await QueryAsync(database, """SELECT "Id"::text || "Type" || "Direction" || "BaseAmount" FROM "FinancialTransactions" ORDER BY 1""", r => r.GetString(0));
        var accounts = await RowCountsAsync(database, "FinancialAccounts", "FinancialTransactions");

        await MigrateToAsync(database, This);

        Assert.Equal(before, await QueryAsync(database, """SELECT "Id"::text || "Type" || "Direction" || "BaseAmount" FROM "FinancialTransactions" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(accounts, await RowCountsAsync(database, "FinancialAccounts", "FinancialTransactions"));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "FinancialAccounts" WHERE "OverdraftLimit" <> 0"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "FinancialTransactions" WHERE "FundingSource" IS NOT NULL OR "CustomerId" IS NOT NULL OR "ReversalOfTransactionId" IS NOT NULL"""));
        await AssertValidationCleanAsync(database);
    }

    [Fact]
    public async Task Constraints_AcceptGoodRows_AndRejectBadOnes()
    {
        await using var database = await CreateWithLedgerAtPreviousAsync();
        await MigrateToAsync(database, This);
        const string cols = ", \"CustomerId\", \"SourceReference\"";

        // Funding: good rows
        await ExecuteAsync(database, Funding(10, "OwnerCapital") + Funding(11, "Loan", ", \"Counterparty\"", ", 'HDFC'") + Funding(12, "CashDeposit"));
        await ExecuteAsync(database, Funding(13, "CustomerReceipt", cols, $", '{Customer}', 'RCPT-1'"));
        await ExecuteAsync(database, Funding(14, "Other", ", \"Reason\"", ", 'misc'"));

        // CK_Funding: Funding must be In; FundingSource only with Funding; Funding needs a source.
        await AssertRejectedAsync(database, Funding(20, "Loan", direction: "Out"), "CK_FinancialTransactions_Funding");
        await AssertRejectedAsync(database, Tx(21, "Funding", "In", Till), "CK_FinancialTransactions_Funding");
        await AssertRejectedAsync(database, Tx(22, "OpeningBalance", "In", Till, ", \"FundingSource\"", ", 'Loan'"), "CK_FinancialTransactions_Funding");
        // Funding / Reversal are manual: need an account and must not name a source document.
        await AssertRejectedAsync(database, Tx(23, "Funding", "In", null, ", \"FundingSource\"", ", 'Loan'"), "CK_FinancialTransactions_ManualHasAccount");
        await AssertRejectedAsync(database, Tx(24, "Funding", "In", Till, ", \"FundingSource\"", ", 'Loan'", expense: Expense), "CK_FinancialTransactions_Source");

        // CK_CustomerReceipt
        await AssertRejectedAsync(database, Funding(30, "CustomerReceipt", ", \"SourceReference\"", ", 'R'"), "CK_FinancialTransactions_CustomerReceipt");
        await AssertRejectedAsync(database, Funding(31, "CustomerReceipt", ", \"CustomerId\"", $", '{Customer}'"), "CK_FinancialTransactions_CustomerReceipt");
        await AssertRejectedAsync(database, Funding(32, "Loan", ", \"CustomerId\"", $", '{Customer}'"), "CK_FinancialTransactions_CustomerReceipt");

        // CK_OtherReason
        await AssertRejectedAsync(database, Funding(33, "Other"), "CK_FinancialTransactions_OtherReason");

        // Customer FK is same-company: an unknown customer is rejected.
        await AssertRaisesAsync(() => ExecuteAsync(database, Funding(34, "CustomerReceipt", cols, $", '{G(9999)}', 'R'")), "FK_FinancialTransactions_Customers");

        // Reversal: good, then CK_Reversal, then unique second reversal.
        await ExecuteAsync(database, Reversal(40, 10));
        await AssertRejectedAsync(database, Reversal(41, 11, reason: null), "CK_FinancialTransactions_Reversal");
        await AssertRejectedAsync(database, Tx(42, "Reversal", "Out", Till, ", \"Reason\"", ", 'x'"), "CK_FinancialTransactions_Reversal");
        await AssertRejectedAsync(database, Tx(43, "Transfer", "Out", Till, ", \"ReversalOfTransactionId\", \"Reason\"", $", '{G(11)}', 'x'"), "CK_FinancialTransactions_Reversal");
        await AssertRaisesAsync(() => ExecuteAsync(database, Reversal(44, 10)), "IX_FinancialTransactions_ReversalOfTransactionId");
        await AssertRaisesAsync(() => ExecuteAsync(database, Reversal(45, 9998)), "FK_FinancialTransactions_FinancialTransactions");
        await ExecuteAsync(database, Reversal(46, 11)); // a different entry reverses fine

        await AssertValidationCleanAsync(database);
    }

    [Fact]
    public async Task AccountOverdraft_CashMustBeZero_BankMayBePositive_NeverNegative()
    {
        await using var database = await CreateAtPreviousAsync();
        await MigrateToAsync(database, This);
        string Account(int id, string kind, decimal limit) =>
            $"""INSERT INTO "FinancialAccounts" ("Id", "CompanyId", "Name", "Kind", "IsActive", "OverdraftLimit", "CreatedByUserId", "CreatedAt") VALUES ('{G(id)}', '{DefaultCompany}', 'A{id}', '{kind}', true, {limit}, '{User}', now())""";

        await ExecuteAsync(database, Account(800, "Cash", 0) + ";" + Account(801, "Bank", 5000));
        await AssertRejectedAsync(database, Account(802, "Cash", 1), "CK_FinancialAccounts_Overdraft");
        await AssertRejectedAsync(database, Account(803, "Bank", -1), "CK_FinancialAccounts_Overdraft");
        await AssertRejectedAsync(database, $"""UPDATE "FinancialAccounts" SET "OverdraftLimit" = 1 WHERE "Id" = '{G(800)}'""", "CK_FinancialAccounts_Overdraft");
    }

    [Fact]
    public async Task NegativeBalanceReport_ListsAccountsBelowZero()
    {
        await using var database = await CreateWithLedgerAtPreviousAsync(); // Till: +10 opening, -10 expense = 0
        await ExecuteAsync(database, Tx(3, "ExpensePayment", "Out", Till, expense: G(404)));
        await MigrateToAsync(database, This);

        var rows = await QueryAsync(database, NegativeBalancesSql(), r => r.GetGuid(1).ToString());

        Assert.Equal([Till], rows);
    }

    [Fact]
    public async Task Permissions_FundAndReverseGoToEveryCashBankManageHolder_AndDownRemovesThem()
    {
        await using var database = await CreateAtPreviousAsync();
        var manager = G(3201); var viewer = G(3202);
        await ExecuteAsync(database, $"""
            INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "IsActive", "IsSystem", "CreatedAt") VALUES
              ('{manager}', '{DefaultCompany}', 'Cash manager', NULL, true, false, now()), ('{viewer}', '{DefaultCompany}', 'Cash viewer', NULL, true, false, now());
            INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES
              ('{G(3211)}', 'CashBank.Manage', 'Accounts', 'Manage', 'D'), ('{G(3212)}', 'CashBank.View', 'Accounts', 'View', 'D');
            INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt") VALUES
              ('{manager}', '{G(3211)}', now()), ('{viewer}', '{G(3212)}', now());
            """);

        await MigrateToAsync(database, This);

        foreach (var code in new[] { "CashBank.Fund", "CashBank.Reverse" })
        {
            Assert.Equal([manager], await QueryAsync(database, $"""SELECT rp."RoleId"::text FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" = '{code}' ORDER BY 1""", r => r.GetString(0)));
        }

        await MigrateToAsync(database, Previous);

        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" WHERE "Code" IN ('CashBank.Fund', 'CashBank.Reverse')"""));
        Assert.Equal(2L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "RolePermissions" WHERE "RoleId" IN ('{manager}', '{viewer}')"""));
    }

    [Fact]
    public async Task Down_RestoresPreviousSchema_AndRefusesWhileFundingRowsExist()
    {
        await using var reference = await CreateAtPreviousAsync();
        var expected = await SchemaSignatureAsync(reference);

        await using var database = await CreateWithLedgerAtPreviousAsync();
        await MigrateToAsync(database, This);
        await ExecuteAsync(database, Funding(10, "OwnerCapital"));
        await AssertRaisesAsync(() => MigrateToAsync(database, Previous), "Down refused");

        await ExecuteAsync(database, """DELETE FROM "FinancialTransactions" WHERE "Type" = 'Funding'""");
        await MigrateToAsync(database, Previous);

        Assert.Equal(expected, await SchemaSignatureAsync(database));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "FinancialTransactions" """));
        await MigrateToAsync(database, This); // Down then Up
        await AssertValidationCleanAsync(database);
    }

    private static string ScriptDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "db", "validation", "V__cash-bank-funding-validation.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "backend", "db", "validation");
    }

    private static string NegativeBalancesSql() =>
        File.ReadAllText(Path.Combine(ScriptDir(), "V__cash-bank-negative-balances.sql")).Trim().TrimEnd(';');

    private static async Task AssertValidationCleanAsync(TestDatabase database)
    {
        var sql = string.Join('\n', File.ReadAllLines(Path.Combine(ScriptDir(), "V__cash-bank-funding-validation.sql")).Where(l => !l.TrimStart().StartsWith("--")));
        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToList();
        Assert.Equal(5, statements.Count); // C1..C5
        foreach (var statement in statements)
            Assert.Empty(await QueryAsync(database, statement, r => r.GetString(0)));
    }
}
