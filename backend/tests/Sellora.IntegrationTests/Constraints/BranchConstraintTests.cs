using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Phase 2 database backstops (design D1, D3, D8), on a fully migrated database: Branches uniques per company, one default per
/// company, default implies active, reserved empty id; the composite same-company FK from the five headers, Expenses, FinancialAccounts
/// and DocumentSequences rejects a branch of another company (and accepts the company's own and, where allowed, NULL); a branch that
/// documents reference cannot be deleted.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BranchConstraintTests(PostgresFixture postgres)
{
    private const string BranchB = "00000000-0000-0000-0000-0000000b1002";
    private const string BranchA2 = "00000000-0000-0000-0000-0000000a1003";

    // Parents every header insert below needs; company A is the default company (its Main exists after migration), B is a second company with its own Main.
    private const string SeedSql = $"""
        CREATE FUNCTION pg_temp.g(n int) RETURNS uuid LANGUAGE sql AS $$ SELECT ('00000000-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid $$;
        INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "IsActive", "IsSystem", "CreatedAt") VALUES (pg_temp.g(11), '0c0a1f00-0000-4000-8000-000000000001', 'Administrator', NULL, true, true, now());
        INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
          VALUES (pg_temp.g(12), now(), 'a@b.c', 'R', 'K', 'x', pg_temp.g(11), 'Active', 'admin');
        INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES (pg_temp.g(1), 'Piece', 'pc', 'Count', 0, NULL, true);
        INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive", "CompanyId") VALUES (pg_temp.g(10), 'GST', 5, true, '{DefaultCompany}');
        INSERT INTO "Customers" ("Id", "Name", "IsActive", "IsWalkIn", "CompanyId") VALUES (pg_temp.g(13), 'Walk-in', true, true, '{DefaultCompany}');
        INSERT INTO "Suppliers" ("Id", "Name", "IsActive", "CompanyId") VALUES (pg_temp.g(14), 'Sup', true, '{DefaultCompany}');
        INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive", "CompanyId")
          VALUES (pg_temp.g(20), 'PEN', 'Pen', NULL, pg_temp.g(1), pg_temp.g(10), 0, true, '{DefaultCompany}');
        INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit",
          "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive", "CompanyId")
          VALUES (pg_temp.g(30), pg_temp.g(20), pg_temp.g(1), NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true, '{DefaultCompany}');
        INSERT INTO "ExpenseCategories" ("Id", "Name", "IsActive", "CompanyId") VALUES (pg_temp.g(401), 'Rent', true, '{DefaultCompany}');
        INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now());
        INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES
          ('{BranchB}', '{OtherCompany}', 'MAIN', 'Main', true, true, now()),
          ('{BranchA2}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
        """;

    private static string Header(string table, string branch, string id) => table switch
    {
        "PurchaseOrders" => $"""
            INSERT INTO "PurchaseOrders" ("Id", "CompanyId", "BranchId", "CreatedAt", "CreatedByUserId", "DiscountTotal", "GrandTotal", "OtherChargesTotal", "PaidAmount", "PaymentMethod", "PurchaseDate", "PurchaseNo", "Status", "Subtotal", "SupplierId", "TaxTotal", "StockLocationId")
            VALUES ('{id}', '{DefaultCompany}', {branch}, now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-09-01', 'PUR-' || right('{id}', 8), 'Completed', 0, pg_temp.g(14), 0, '{DefaultStore}')
            """,
        "PurchaseReturns" => $"""
            INSERT INTO "PurchaseReturns" ("Id", "CompanyId", "BranchId", "CreatedAt", "CreatedByUserId", "OriginalTotal", "PurchaseOrderId", "ReturnDate", "ReturnNo", "TaxTotal", "TaxableTotal", "Total", "StockLocationId")
            VALUES ('{id}', '{DefaultCompany}', {branch}, now(), pg_temp.g(12), 0, '00000000-0000-0000-0000-0000000c0001', DATE '2026-09-03', 'PRT-' || right('{id}', 8), 0, 0, 0, '{DefaultStore}')
            """,
        "SalesInvoices" => $"""
            INSERT INTO "SalesInvoices" ("Id", "CompanyId", "BranchId", "InvoiceNo", "CustomerId", "InvoiceDate", "PaymentMethod", "PaidAmount", "Subtotal", "TaxTotal", "DiscountTotal", "GrandTotal", "Status", "CreatedByUserId", "CreatedAt", "StockLocationId")
            VALUES ('{id}', '{DefaultCompany}', {branch}, 'INV-' || right('{id}', 8), pg_temp.g(13), DATE '2026-09-04', 'Cash', 0, 0, 0, 0, 0, 'Completed', pg_temp.g(12), now(), '{DefaultStore}')
            """,
        "SalesReturns" => $"""
            INSERT INTO "SalesReturns" ("Id", "CompanyId", "BranchId", "ReturnNo", "SalesInvoiceId", "ReturnDate", "TaxableTotal", "TaxTotal", "Total", "CreatedByUserId", "CreatedAt", "StockLocationId")
            VALUES ('{id}', '{DefaultCompany}', {branch}, 'SRT-' || right('{id}', 8), '00000000-0000-0000-0000-0000000c0002', DATE '2026-09-07', 0, 0, 0, pg_temp.g(12), now(), '{DefaultStore}')
            """,
        "StockAdjustments" => $"""
            INSERT INTO "StockAdjustments" ("Id", "CompanyId", "BranchId", "AdjustmentNo", "AfterQty", "BeforeQty", "ConversionFactorToBase", "CreatedAt", "Direction", "EnteredQuantity", "ProductId", "ProductUnitId", "QuantityBaseUnits", "Type", "UnitCode", "UnitId", "UserId", "StockLocationId")
            VALUES ('{id}', '{DefaultCompany}', {branch}, 'ADJ-' || right('{id}', 8), 1, 0, 1, now(), 'Increase', 1, pg_temp.g(20), pg_temp.g(30), 1, 'OpeningStock', 'pc', pg_temp.g(1), pg_temp.g(12), '{DefaultStore}')
            """,
        "Expenses" => $"""
            INSERT INTO "Expenses" ("Id", "CompanyId", "BranchId", "ExpenseNo", "CategoryId", "ExpenseDate", "CurrencyId", "Amount", "ExchangeRateToBase", "BaseAmount", "PaymentMethod", "Status", "CreatedByUserId", "CreatedAt")
            VALUES ('{id}', '{DefaultCompany}', {branch}, 'EXP-' || right('{id}', 8), pg_temp.g(401), DATE '2026-09-10', '7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01', 100, 1, 100, 'Cash', 'Active', pg_temp.g(12), now())
            """,
        "FinancialAccounts" => $"""
            INSERT INTO "FinancialAccounts" ("Id", "CompanyId", "BranchId", "Name", "Kind", "Reference", "IsActive", "CreatedByUserId", "CreatedAt")
            VALUES ('{id}', '{DefaultCompany}', {branch}, 'Till-' || right('{id}', 8), 'Cash', NULL, true, pg_temp.g(12), now())
            """,
        "DocumentSequences" => $"""
            INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt")
            VALUES ('{id}', '{DefaultCompany}', {branch}, 'ZZZ', 1, now())
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private async Task<TestDatabase> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        // Parent documents the two return tables point at (on company A's Main).
        await ExecuteAsync(database, SeedSql
            + Header("PurchaseOrders", $"'{DefaultBranch}'", "00000000-0000-0000-0000-0000000c0001") + ";\n"
            + Header("SalesInvoices", $"'{DefaultBranch}'", "00000000-0000-0000-0000-0000000c0002") + ";\n");
        return database;
    }

    private static string Fn(string sql) => """CREATE FUNCTION pg_temp.g(n int) RETURNS uuid LANGUAGE sql AS $$ SELECT ('00000000-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid $$;""" + "\n" + sql;

    private static async Task<PostgresException> AssertViolatesAsync(TestDatabase database, string sql, string sqlState, string constraint)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, sql));
        Assert.Equal((sqlState, constraint), (ex.SqlState, ex.ConstraintName));
        return ex;
    }

    public static TheoryData<string> BranchTables => new()
    {
        "PurchaseOrders", "PurchaseReturns", "SalesInvoices", "SalesReturns", "StockAdjustments", "Expenses", "FinancialAccounts", "DocumentSequences",
    };

    [Theory]
    [MemberData(nameof(BranchTables))]
    public async Task Insert_BranchOfAnotherCompany_IsRejectedByTheCompositeForeignKey(string table)
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Fn(Header(table, $"'{BranchB}'", Guid.NewGuid().ToString())), "23503", $"FK_{table}_Branches_CompanyId_BranchId");
    }

    [Theory]
    [MemberData(nameof(BranchTables))]
    public async Task Insert_OwnCompanysBranch_IsAccepted(string table)
    {
        await using var database = await SeedAsync();

        // Main and a second branch of the same company are both fine (a branch of the company, not necessarily the default).
        await ExecuteAsync(database, Fn(Header(table, $"'{DefaultBranch}'", Guid.NewGuid().ToString())));
        await ExecuteAsync(database, Fn(Header(table, $"'{BranchA2}'", Guid.NewGuid().ToString()).Replace("'ZZZ'", "'ZZY'")));
    }

    [Theory]
    [InlineData("Expenses")]
    [InlineData("FinancialAccounts")]
    [InlineData("DocumentSequences")]
    public async Task Insert_NullBranch_IsAcceptedWhereBranchIsOptional(string table)
    {
        await using var database = await SeedAsync();

        await ExecuteAsync(database, Fn(Header(table, "NULL", Guid.NewGuid().ToString())));
    }

    [Theory]
    [InlineData("PurchaseOrders")]
    [InlineData("PurchaseReturns")]
    [InlineData("SalesInvoices")]
    [InlineData("SalesReturns")]
    [InlineData("StockAdjustments")]
    public async Task Insert_NullBranchOnADocumentHeader_IsRejected(string table)
    {
        await using var database = await SeedAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, Fn(Header(table, "NULL", Guid.NewGuid().ToString()))));
        Assert.Equal("23502", ex.SqlState);
        Assert.Equal("BranchId", ex.ColumnName);
    }

    [Fact]
    public async Task Update_MovingADocumentToAnotherCompanysBranch_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "SalesInvoices" SET "BranchId" = '{BranchB}' """, "23503", "FK_SalesInvoices_Branches_CompanyId_BranchId");
    }

    [Fact]
    public async Task Delete_BranchReferencedByADocument_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""DELETE FROM "Branches" WHERE "Id" = '{DefaultBranch}'""", "23503", "FK_PurchaseOrders_Branches_CompanyId_BranchId");
    }

    [Fact]
    public async Task Branches_DuplicateCodeInTheSameCompany_IsRejectedButTheSameCodeInAnotherCompanyIsFine()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database,
            $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'BLR', 'Another name', false, true, now())""",
            "23505", "IX_Branches_CompanyId_Code");
        // MAIN exists in both companies (seeded above): the unique is per company.
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" WHERE "Code" = 'MAIN'"""));
    }

    [Fact]
    public async Task Branches_DuplicateNameInTheSameCompany_IsRejectedButTheSameNameInAnotherCompanyIsFine()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database,
            $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'KAR', 'Bengaluru', false, true, now())""",
            "23505", "IX_Branches_CompanyId_Name");
        await ExecuteAsync(database,
            $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{OtherCompany}', 'KAR', 'Bengaluru', false, true, now())""");
    }

    [Fact]
    public async Task Branches_SecondDefaultInTheSameCompany_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database,
            $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'HYD', 'Hyderabad', true, true, now())""",
            "23505", "IX_Branches_CompanyId_IsDefault");
        await AssertViolatesAsync(database, $"""UPDATE "Branches" SET "IsDefault" = true WHERE "Id" = '{BranchA2}'""", "23505", "IX_Branches_CompanyId_IsDefault");
    }

    [Fact]
    public async Task Branches_DefaultBranchThatIsInactive_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "Branches" SET "IsActive" = false WHERE "Id" = '{DefaultBranch}'""", "23514", "CK_Branches_Default_Active");
        // A non-default branch may be deactivated.
        await ExecuteAsync(database, $"""UPDATE "Branches" SET "IsActive" = false WHERE "Id" = '{BranchA2}'""");
    }

    [Fact]
    public async Task Branches_EmptyGuidIdAndOverlongCode_AreRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database,
            $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES ('00000000-0000-0000-0000-000000000000', '{DefaultCompany}', 'ZERO', 'Zero', false, true, now())""",
            "23514", "CK_Branches_Id_NotEmpty");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'TOOLONG', 'Long', false, true, now())"""));
        Assert.Equal("22001", ex.SqlState);
    }

    [Fact]
    public async Task Branches_UnknownCompany_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database,
            """INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), gen_random_uuid(), 'X', 'X', false, true, now())""",
            "23503", "FK_Branches_Companies_CompanyId");
    }
}
