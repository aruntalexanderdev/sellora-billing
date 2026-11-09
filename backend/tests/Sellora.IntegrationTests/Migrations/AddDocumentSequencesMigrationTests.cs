using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 1 / M4 <c>AddDocumentSequences</c> on the legacy dataset carried through M3: per company x series seeding
/// from existing numbers (gap, non-numeric number, two invoice prefixes, empty series, a second company with its own numbers),
/// deterministic ids, unique/check constraints, V5 (sequence ahead of history), Down guard with a second company, Down
/// restores the M3 schema, Down then Up, empty database, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddDocumentSequencesMigrationTests(PostgresFixture postgres)
{
    // Legacy fixture numbers: PUR-0001, PUR-0002, PUR-0007 (gap), LEGACY-ABC (non-numeric) -> max(count 4, suffix 7) + 1 = 8;
    // PRT-0001 -> 2; SAL-0001..0004 + INV-0010 (two prefixes, count 5, suffix 10) -> 11; SRT-0001 -> 2;
    // ADJ-0001..0004 -> 5; EXP-0001, EXP-0003 (gap; count 2, suffix 3) -> 4.
    private static readonly string[] ExpectedDefault = ["ADJ=5", "EXP=4", "INV=11", "PRT=2", "PUR=8", "SRT=2"];

    private const string SecondCompanyWithNumbersSql = $"""
        INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now());
        INSERT INTO "Suppliers" ("Id", "Name", "IsActive", "CompanyId") VALUES ('00000000-0000-0000-0000-0000000b0014', 'Sup B', true, '{OtherCompany}');
        INSERT INTO "PurchaseOrders" ("Id", "CreatedAt", "CreatedByUserId", "DiscountTotal", "GrandTotal", "OtherChargesTotal", "PaidAmount", "PaymentMethod", "PurchaseDate", "PurchaseNo", "Status", "Subtotal", "SupplierId", "TaxTotal", "CompanyId")
        VALUES ('00000000-0000-0000-0000-0000000b0040', now(), '00000000-0000-0000-0000-00000000000c', 0, 0, 0, 0, 'Cash', DATE '2026-10-01', 'PUR-0042', 'Cancelled', 0, '00000000-0000-0000-0000-0000000b0014', 0, '{OtherCompany}');
        """;

    private static Task<List<string>> SequencesAsync(TestDatabase database, string company) =>
        QueryAsync(database, $"""SELECT "Series" || '=' || "NextValue" FROM "DocumentSequences" WHERE "CompanyId" = '{company}' AND "BranchId" IS NULL ORDER BY "Series" """, r => r.GetString(0));

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        // V5: every sequence is ahead of the highest numeric suffix of its series, for every company.
        foreach (var (series, table, column) in new[]
        {
            ("PUR", "PurchaseOrders", "PurchaseNo"), ("PRT", "PurchaseReturns", "ReturnNo"), ("INV", "SalesInvoices", "InvoiceNo"),
            ("SRT", "SalesReturns", "ReturnNo"), ("ADJ", "StockAdjustments", "AdjustmentNo"), ("EXP", "Expenses", "ExpenseNo"),
        })
        {
            var behind = await QueryAsync(database, $"""
                SELECT d."{column}" FROM "{table}" d JOIN "DocumentSequences" s ON s."CompanyId" = d."CompanyId" AND s."BranchId" IS NULL AND s."Series" = '{series}'
                WHERE substring(d."{column}" FROM '([0-9]+)$')::bigint >= s."NextValue"
                """, r => r.GetString(0));
            Assert.True(behind.Count == 0, $"{series}: sequence not ahead of {string.Join(", ", behind)} (V5).");
        }
        Assert.Equal((await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Companies" """)) * 6,
            await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" IS NULL"""));
    }

    [Fact]
    public async Task Up_LegacyNumbersWithGapsNonNumericAndTwoInvoicePrefixes_SeedsEverySeriesAheadOfHistory()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        var before = await RowCountsAsync(database, CountedTables);

        await MigrateToAsync(database, M4);

        Assert.Equal(ExpectedDefault, await SequencesAsync(database, DefaultCompany));
        await AssertVBlockAsync(database);
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(6L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" """));
        // Deterministic ids: md5('docseq:' || company || ':' || series).
        Assert.Equal(0L, await ScalarAsync<long>(database,
            """SELECT COUNT(*) FROM "DocumentSequences" WHERE "Id" <> md5('docseq:' || "CompanyId"::text || ':' || "Series")::uuid"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" IS NOT NULL"""));
    }

    [Fact]
    public async Task Up_SecondCompany_GetsItsOwnIndependentSequences()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        await ExecuteAsync(database, SecondCompanyWithNumbersSql);

        await MigrateToAsync(database, M4);

        Assert.Equal(ExpectedDefault, await SequencesAsync(database, DefaultCompany));
        Assert.Equal(new[] { "ADJ=1", "EXP=1", "INV=1", "PRT=1", "PUR=43", "SRT=1" }, await SequencesAsync(database, OtherCompany));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task Up_EmptyDatabase_SeedsDefaultCompanySequencesAtOne()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, M4);

        Assert.Equal(new[] { "ADJ=1", "EXP=1", "INV=1", "PRT=1", "PUR=1", "SRT=1" }, await SequencesAsync(database, DefaultCompany));
    }

    [Fact]
    public async Task Constraints_DuplicateSeriesNonPositiveNextValueAndEmptySeries_AreRejected()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        await MigrateToAsync(database, M4);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            $"""INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', NULL, 'PUR', 1, now())"""));
        Assert.Equal("IX_DocumentSequences_CompanyId_Series", duplicate.ConstraintName);
        await AssertRaisesAsync(() => ExecuteAsync(database,
            $"""INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', NULL, 'ZZZ', 0, now())"""),
            "CK_DocumentSequences_NextValue_Positive");
        await AssertRaisesAsync(() => ExecuteAsync(database,
            $"""INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', NULL, '', 1, now())"""),
            "CK_DocumentSequences_Series_NotEmpty");
        // The sequence advances atomically with UPDATE ... RETURNING (the production numbering primitive).
        Assert.Equal(8L, await ScalarAsync<long>(database,
            $"""UPDATE "DocumentSequences" SET "NextValue" = "NextValue" + 1, "UpdatedAt" = now() WHERE "CompanyId" = '{DefaultCompany}' AND "BranchId" IS NULL AND "Series" = 'PUR' RETURNING "NextValue" - 1"""));
    }

    [Fact]
    public async Task Down_WithSecondCompany_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        await ExecuteAsync(database, SecondCompanyWithNumbersSql);
        await MigrateToAsync(database, M4);
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, M3), "more than one company exists");

        Assert.Contains(M4, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(12L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" """));
    }

    [Fact]
    public async Task Down_SingleCompany_RestoresTheM3Schema()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, M4);

        await MigrateToAsync(database, M3);

        Assert.DoesNotContain(M4, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'DocumentSequences'"));
        var expected = await ReferenceSchemaAsync(postgres, M3);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_AfterDown_SeedsIdentically()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        await MigrateToAsync(database, M4);
        var first = await SequencesAsync(database, DefaultCompany);

        await MigrateToAsync(database, M3);
        await MigrateToAsync(database, M4);

        Assert.Equal(first, await SequencesAsync(database, DefaultCompany));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunKeepsSequencesAsAdvancedByTheApplication()
    {
        await using var database = await CreateAtAsync(postgres, M3);
        var script = await ScriptAsync(database, M3, M4);
        Assert.Contains(M4, script);

        await ExecuteAsync(database, script);

        Assert.Equal(ExpectedDefault, await SequencesAsync(database, DefaultCompany));
        await AssertVBlockAsync(database);
        await ExecuteAsync(database, $"""UPDATE "DocumentSequences" SET "NextValue" = 100 WHERE "CompanyId" = '{DefaultCompany}' AND "Series" = 'INV'""");
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await ExecuteAsync(database, script); // second run: the block is skipped, no re-seed, no reset

        Assert.Equal(100L, await ScalarAsync<long>(database, $"""SELECT "NextValue" FROM "DocumentSequences" WHERE "CompanyId" = '{DefaultCompany}' AND "Series" = 'INV'"""));
        Assert.Equal(6L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" """));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
    }
}
