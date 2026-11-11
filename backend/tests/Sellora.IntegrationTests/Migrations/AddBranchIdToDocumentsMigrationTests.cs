using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 2 / B2 <c>AddBranchIdToDocuments</c> on the legacy dataset carried through B1: V2 (every header on its company's
/// default branch), V3 (same company), V6 (Expenses/FinancialAccounts not tagged), nullability, composite FKs, guard G3 raise + rollback,
/// Down guard (second branch), Down restores the B1 schema, Down then Up, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddBranchIdToDocumentsMigrationTests(PostgresFixture postgres)
{
    private static readonly string[] AllSeven = [.. BranchHeaders, .. OptionalBranchTables];

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        foreach (var table in BranchHeaders)
        {
            // V2 (generalised to every company): header's branch is its company's default branch, never NULL.
            var bad = await ScalarAsync<long>(database, $"""
                SELECT COUNT(*) FROM "{table}" t LEFT JOIN "Branches" b ON b."Id" = t."BranchId"
                WHERE t."BranchId" IS NULL OR b."CompanyId" <> t."CompanyId" OR NOT b."IsDefault"
                """);
            Assert.True(bad == 0, $"{table}: {bad} row(s) not on their company's default branch (V2).");
        }
        // V2 literal for the single-office default company.
        foreach (var table in BranchHeaders)
        {
            Assert.Equal(0L, await ScalarAsync<long>(database,
                $"""SELECT COUNT(*) FROM "{table}" WHERE "CompanyId" = '{DefaultCompany}' AND "BranchId" <> '{DefaultBranch}'"""));
        }
        // V3: the branch belongs to the document's company (the composite FK makes this empty).
        foreach (var table in AllSeven)
        {
            Assert.Equal(0L, await ScalarAsync<long>(database,
                $"""SELECT COUNT(*) FROM "{table}" t JOIN "Branches" b ON b."Id" = t."BranchId" WHERE b."CompanyId" <> t."CompanyId" """));
        }
        // V6: nothing invented on the optional-branch tables.
        foreach (var table in OptionalBranchTables)
        {
            Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "{table}" WHERE "BranchId" IS NOT NULL"""));
        }
    }

    [Fact]
    public async Task Up_LegacyHeaders_AllLandOnTheDefaultBranchAndRowCountsAreUnchanged()
    {
        await using var database = await CreateAtAsync(postgres, B1);
        var before = await RowCountsAsync(database, CountedTables);
        var documentNumbers = await QueryAsync(database, """SELECT "PurchaseNo" FROM "PurchaseOrders" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B2);

        await AssertVBlockAsync(database);
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(documentNumbers, await QueryAsync(database, """SELECT "PurchaseNo" FROM "PurchaseOrders" ORDER BY 1""", r => r.GetString(0)));
        // Legacy fixture volumes: 3 purchases, 1 purchase return, 5 invoices, 1 sales return, 4 adjustments.
        Assert.Equal(new long[] { 4, 1, 5, 1, 4 },
            await Task.WhenAll(BranchHeaders.Select(t => ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "{t}" WHERE "BranchId" = '{DefaultBranch}'"""))));
    }

    [Fact]
    public async Task Up_Nullability_FiveHeadersRequiredTwoMastersOptional()
    {
        await using var database = await CreateAtAsync(postgres, B1);

        await MigrateToAsync(database, B2);

        foreach (var table in AllSeven)
        {
            var nullable = await ScalarAsync<string>(database,
                $"""SELECT is_nullable FROM information_schema.columns WHERE table_name = '{table}' AND column_name = 'BranchId'""");
            Assert.Equal(BranchHeaders.Contains(table) ? "NO" : "YES", nullable);
            Assert.Equal(1L, await ScalarAsync<long>(database, $"""
                SELECT COUNT(*) FROM pg_constraint WHERE conname = 'FK_{table}_Branches_CompanyId_BranchId' AND contype = 'f'
                """));
        }
    }

    [Fact]
    public async Task Up_SecondCompany_HeadersLandOnThatCompanysOwnMain()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B1);

        await MigrateToAsync(database, B2);

        await AssertVBlockAsync(database);
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "PurchaseOrders" WHERE "CompanyId" = '{OtherCompany}' AND "BranchId" = {OtherMainSql}"""));
        // V6 with a company-B expense and account present: still untagged.
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "Expenses" WHERE "CompanyId" = '{OtherCompany}' AND "BranchId" IS NULL"""));
    }

    [Fact]
    public async Task Up_CompanyWithoutADefaultBranch_GuardG3RaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, B1);
        await ExecuteAsync(database, $"""DELETE FROM "Branches" WHERE "CompanyId" = '{DefaultCompany}'""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B2), "has no default branch");

        Assert.DoesNotContain(B2, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database,
            """SELECT COUNT(*) FROM information_schema.columns WHERE column_name = 'BranchId' AND table_name = 'PurchaseOrders'"""));
    }

    [Fact]
    public async Task Up_EmptyDatabase_Succeeds()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B2);

        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task Down_SecondBranchExists_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, B1);
        await MigrateToAsync(database, B2);
        await ExecuteAsync(database, $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now())""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B1), "non-default branch");

        Assert.Contains(B2, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task Down_DocumentOnANonDefaultBranch_GuardRaisesAndKeepsTheBranchId()
    {
        await using var database = await CreateAtAsync(postgres, B1);
        await MigrateToAsync(database, B2);
        const string blr = "00000000-0000-0000-0000-0000000b1001";
        await ExecuteAsync(database, $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES ('{blr}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
            UPDATE "SalesInvoices" SET "BranchId" = '{blr}' WHERE "InvoiceNo" = 'SAL-0001';
            """);

        await AssertRaisesAsync(() => MigrateToAsync(database, B1), "non-default branch");

        Assert.Equal(Guid.Parse(blr), await ScalarAsync<Guid>(database, """SELECT "BranchId" FROM "SalesInvoices" WHERE "InvoiceNo" = 'SAL-0001'"""));
    }

    [Fact]
    public async Task Down_SingleBranchPerCompany_RestoresTheB1SchemaEvenWithTaggedExpenses()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B1);
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, B2);
        // Main tags on the optional tables are legal and are simply dropped on Down.
        await ExecuteAsync(database, $"""UPDATE "Expenses" SET "BranchId" = '{DefaultBranch}' WHERE "CompanyId" = '{DefaultCompany}'; UPDATE "FinancialAccounts" SET "BranchId" = '{DefaultBranch}' WHERE "CompanyId" = '{DefaultCompany}';""");

        await MigrateToAsync(database, B1);

        Assert.DoesNotContain(B2, await AppliedAsync(database));
        var expected = await ReferenceSchemaAsync(postgres, B1);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_AfterDown_BackfillsIdentically()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B1);
        await MigrateToAsync(database, B2);

        await MigrateToAsync(database, B1);
        await MigrateToAsync(database, B2);

        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunKeepsRowsAndBranches()
    {
        await using var database = await CreateAtAsync(postgres, B1);
        var script = await ScriptAsync(database, B1, B2);
        Assert.Contains(B2, script);

        await ExecuteAsync(database, script);

        await AssertVBlockAsync(database);
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);
        var counts = await RowCountsAsync(database, CountedTables);

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(counts, await RowCountsAsync(database, CountedTables));
        await AssertVBlockAsync(database);
    }
}
