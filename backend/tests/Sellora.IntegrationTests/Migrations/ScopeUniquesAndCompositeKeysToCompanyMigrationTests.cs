using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 1 / M3 <c>ScopeUniquesAndCompositeKeysToCompany</c> on the legacy dataset carried through M2: scoped
/// uniques replace the global ones (same SKU / name / number in two companies), alternate keys and the 13 composite company
/// FKs exist and refuse cross-company references, guard G2 (cross-company orphan) raises and rolls back, V4 is empty,
/// Down guard with a second company, Down restores the M2 schema, Down then Up, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ScopeUniquesAndCompositeKeysToCompanyMigrationTests(PostgresFixture postgres)
{
    private static readonly string[] CompositeForeignKeys =
    [
        "FK_ProductUnits_Products_CompanyId_ProductId", "FK_Products_Taxes_CompanyId_TaxId", "FK_StockItems_Products_CompanyId_ProductId",
        "FK_PurchaseBatches_Products_CompanyId_ProductId", "FK_StockMovements_Products_CompanyId_ProductId",
        "FK_StockMovements_PurchaseBatches_CompanyId_PurchaseBatchId", "FK_StockAdjustments_Products_CompanyId_ProductId",
        "FK_PurchaseOrders_Suppliers_CompanyId_SupplierId", "FK_PurchaseReturns_PurchaseOrders_CompanyId_PurchaseOrderId",
        "FK_SalesInvoices_Customers_CompanyId_CustomerId", "FK_SalesReturns_SalesInvoices_CompanyId_SalesInvoiceId",
        "FK_Expenses_ExpenseCategories_CompanyId_CategoryId", "FK_FinancialTransactions_FinancialAccounts_CompanyId_AccountId",
    ];

    private static readonly string[] AlternateKeyTables =
        ["Products", "Taxes", "Suppliers", "Customers", "PurchaseOrders", "SalesInvoices", "PurchaseBatches", "ExpenseCategories", "FinancialAccounts"];

    private static readonly string[] ScopedIndexes =
    [
        "IX_Products_CompanyId_Sku", "IX_ProductUnits_CompanyId_Barcode", "IX_Taxes_CompanyId_Name", "IX_ExpenseCategories_CompanyId_Name",
        "IX_FinancialAccounts_CompanyId_Name", "IX_ExchangeRates_CompanyId_CurrencyId_EffectiveDate", "IX_PurchaseOrders_CompanyId_PurchaseNo",
        "IX_PurchaseReturns_CompanyId_ReturnNo", "IX_SalesInvoices_CompanyId_InvoiceNo", "IX_SalesReturns_CompanyId_ReturnNo",
        "IX_StockAdjustments_CompanyId_AdjustmentNo", "IX_Expenses_CompanyId_ExpenseNo", "IX_IdempotencyKeys_CompanyId_UserId_Scope_Key",
    ];

    private static readonly string[] GlobalIndexesRemoved =
    [
        "IX_Products_Sku", "IX_ProductUnits_Barcode", "IX_Taxes_Name", "IX_ExpenseCategories_Name", "IX_FinancialAccounts_Name",
        "IX_ExchangeRates_CurrencyId_EffectiveDate", "IX_PurchaseOrders_PurchaseNo", "IX_PurchaseReturns_ReturnNo", "IX_SalesInvoices_InvoiceNo",
        "IX_SalesReturns_ReturnNo", "IX_StockAdjustments_AdjustmentNo", "IX_Expenses_ExpenseNo", "IX_IdempotencyKeys_UserId_Scope_Key",
    ];

    private const string SecondCompanySql = $"""
        INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now());
        INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive", "CompanyId") VALUES ('00000000-0000-0000-0000-0000000b0010', 'GST', 5, true, '{OtherCompany}');
        """;

    [Fact]
    public async Task Up_LegacyData_ScopesUniquesAddsKeysAndCompositeForeignKeysAndVBlockIsEmpty()
    {
        await using var database = await CreateAtAsync(postgres, M2);
        var before = await RowCountsAsync(database, CountedTables);

        await MigrateToAsync(database, M3);

        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        await AssertAllRowsOnDefaultCompanyAsync(database);
        var indexes = (await QueryAsync(database, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'", r => r.GetString(0))).ToHashSet();
        Assert.Superset(ScopedIndexes.ToHashSet(), indexes);
        Assert.DoesNotContain(GlobalIndexesRemoved, indexes.Contains);
        var constraints = (await QueryAsync(database, "SELECT conname FROM pg_constraint", r => r.GetString(0))).ToHashSet();
        Assert.Superset(CompositeForeignKeys.ToHashSet(), constraints);
        Assert.Superset(AlternateKeyTables.Select(t => $"AK_{t}_CompanyId_Id").ToHashSet(), constraints);
        // Platform uniques stay global.
        Assert.Contains("IX_Users_Username", indexes);
        // V4: no company-owned row points at a master of another company.
        Assert.Empty(await QueryAsync(database, """
            SELECT pu."Id" FROM "ProductUnits" pu JOIN "Products" p ON p."Id" = pu."ProductId" WHERE pu."CompanyId" <> p."CompanyId"
            UNION ALL SELECT l."Id" FROM "SalesInvoiceLines" l JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId" JOIN "Products" p ON p."Id" = l."ProductId" WHERE p."CompanyId" <> i."CompanyId"
            UNION ALL SELECT l."Id" FROM "PurchaseOrderLines" l JOIN "PurchaseOrders" o ON o."Id" = l."PurchaseOrderId" JOIN "Products" p ON p."Id" = l."ProductId" WHERE p."CompanyId" <> o."CompanyId"
            """, r => r.GetGuid(0)));
    }

    [Fact]
    public async Task Up_AfterMigration_SameSkuNameAndDocumentNumberAllowedInAnotherCompanyButNotWithin()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        await ExecuteAsync(database, SecondCompanySql);
        var b = "'" + OtherCompany + "'";
        var tax = "'00000000-0000-0000-0000-0000000b0010'";
        var unit = "'00000000-0000-0000-0000-000000000001'"; // legacy 'Piece' unit (global)

        // Company B reuses company A's SKU 'PEN' and tax name 'GST'.
        await ExecuteAsync(database, $"""
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive", "CompanyId")
            VALUES ('00000000-0000-0000-0000-0000000b0020', 'PEN', 'Pen B', NULL, {unit}, {tax}, 0, true, {b});
            """);
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Products" WHERE "Sku" = 'PEN'"""));
        // Within company A the SKU is still unique.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive", "CompanyId")
            VALUES (gen_random_uuid(), 'PEN', 'Pen dup', NULL, {unit}, '00000000-0000-0000-0000-00000000000a', 0, true, '{DefaultCompany}')
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("IX_Products_CompanyId_Sku", ex.ConstraintName);
    }

    [Fact]
    public async Task Up_CompositeForeignKey_RejectsCrossCompanyReference()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        await ExecuteAsync(database, SecondCompanySql);

        // A company-B product pointing at company A's tax (single-column FK would accept it, the composite one must not).
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive", "CompanyId")
            VALUES (gen_random_uuid(), 'CROSS', 'Cross', NULL, '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-00000000000a', 0, true, '{OtherCompany}')
            """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_Products_Taxes_CompanyId_TaxId", ex.ConstraintName);
    }

    [Fact]
    public async Task Up_CrossCompanyOrphan_GuardG2RaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, M2);
        // A ProductUnit stamped with another company while its product stays in the default company.
        await ExecuteAsync(database, $"""
            INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now());
            UPDATE "ProductUnits" SET "CompanyId" = '{OtherCompany}' WHERE "Id" = '00000000-0000-0000-0000-00000000001e';
            """);
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, M3), "ProductUnits.ProductId");

        Assert.DoesNotContain(M3, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_Products_Sku'"));
    }

    [Fact]
    public async Task Down_WithRowsOfASecondCompany_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        await ExecuteAsync(database, SecondCompanySql);
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, M2), "more than one company exists");

        Assert.Contains(M3, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task Down_SingleCompany_RestoresTheM2SchemaAndRows()
    {
        await using var database = await CreateAtAsync(postgres, M2);
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, M3);

        await MigrateToAsync(database, M2);

        Assert.DoesNotContain(M3, await AppliedAsync(database));
        var expected = await ReferenceSchemaAsync(postgres, M2);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_AfterDown_ReappliesIdentically()
    {
        await using var database = await CreateAtAsync(postgres, M2);
        await MigrateToAsync(database, M3);
        var schema = await SchemaSignatureAsync(database);

        await MigrateToAsync(database, M2);
        await MigrateToAsync(database, M3);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunIsNoOp()
    {
        await using var database = await CreateAtAsync(postgres, M2);
        var before = await RowCountsAsync(database, CountedTables);
        var script = await ScriptAsync(database, M2, M3);
        Assert.Contains(M3, script);

        await ExecuteAsync(database, script);

        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
    }
}
