using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 2 / B3 <c>ScopeDocumentSequencesToBranches</c> on the legacy dataset carried through B2: PUR/PRT/INV/SRT/ADJ counters
/// move to (company, default branch) with NextValue preserved exactly, EXP stays company-level, a second company moves to its own Main,
/// guard G4 raise + rollback, Down guard (second branch / counter beyond the default branch), Down restores the B2 schema, Down then Up,
/// idempotent script twice, composite FK present.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ScopeDocumentSequencesToBranchesMigrationTests(PostgresFixture postgres)
{
    // Same legacy numbering as AddDocumentSequencesMigrationTests.
    private static readonly string[] ExpectedDefault = ["ADJ=5", "EXP=4", "INV=11", "PRT=2", "PUR=8", "SRT=2"];
    private static readonly string[] ExpectedOther = ["ADJ=1", "EXP=3", "INV=7", "PRT=1", "PUR=43", "SRT=1"];

    private static Task<List<string>> CountersAsync(TestDatabase database, string company) =>
        QueryAsync(database, $"""SELECT "Series" || '=' || "NextValue" FROM "DocumentSequences" WHERE "CompanyId" = '{company}' ORDER BY "Series" """, r => r.GetString(0));

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        // V5: no company-level row for the five branch series; EXP is company-level only.
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" IS NULL AND "Series" <> 'EXP'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" IS NOT NULL AND "Series" = 'EXP'"""));
        // Every moved counter sits on its own company's default branch.
        Assert.Equal(0L, await ScalarAsync<long>(database, """
            SELECT COUNT(*) FROM "DocumentSequences" s JOIN "Branches" b ON b."Id" = s."BranchId" WHERE b."CompanyId" <> s."CompanyId" OR NOT b."IsDefault"
            """));
        // Counters stay ahead of history (the numbers themselves did not move).
        foreach (var (series, table, column) in new[]
        {
            ("PUR", "PurchaseOrders", "PurchaseNo"), ("PRT", "PurchaseReturns", "ReturnNo"), ("INV", "SalesInvoices", "InvoiceNo"),
            ("SRT", "SalesReturns", "ReturnNo"), ("ADJ", "StockAdjustments", "AdjustmentNo"),
        })
        {
            var behind = await QueryAsync(database, $"""
                SELECT d."{column}" FROM "{table}" d JOIN "DocumentSequences" s ON s."CompanyId" = d."CompanyId" AND s."BranchId" = d."BranchId" AND s."Series" = '{series}'
                WHERE substring(d."{column}" FROM '([0-9]+)$')::bigint >= s."NextValue"
                """, r => r.GetString(0));
            Assert.True(behind.Count == 0, $"{series}: counter not ahead of {string.Join(", ", behind)}.");
        }
    }

    [Fact]
    public async Task Up_LegacyCounters_MoveToMainWithNextValuePreservedAndExpStaysCompanyLevel()
    {
        await using var database = await CreateAtAsync(postgres, B2);
        var before = await CountersAsync(database, DefaultCompany);
        Assert.Equal(ExpectedDefault, before);
        var ids = await QueryAsync(database, """SELECT "Id"::text FROM "DocumentSequences" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B3);

        Assert.Equal(ExpectedDefault, await CountersAsync(database, DefaultCompany));
        Assert.Equal(ids, await QueryAsync(database, """SELECT "Id"::text FROM "DocumentSequences" ORDER BY 1""", r => r.GetString(0)));
        await AssertVBlockAsync(database);
        Assert.Equal(5L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" = '{DefaultBranch}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" IS NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_constraint WHERE conname = 'FK_DocumentSequences_Branches_CompanyId_BranchId' AND contype = 'f'"));
    }

    [Fact]
    public async Task Up_SecondCompany_CountersMoveToItsOwnMainIndependently()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B2);

        await MigrateToAsync(database, B3);

        Assert.Equal(ExpectedDefault, await CountersAsync(database, DefaultCompany));
        Assert.Equal(ExpectedOther, await CountersAsync(database, OtherCompany));
        Assert.Equal(5L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "CompanyId" = '{OtherCompany}' AND "BranchId" = {OtherMainSql}"""));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task Up_EmptyDatabase_SequencesSitOnMain()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B3);

        Assert.Equal(new[] { "ADJ=1", "EXP=1", "INV=1", "PRT=1", "PUR=1", "SRT=1" }, await CountersAsync(database, DefaultCompany));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task Up_BranchLevelCounterAlreadyOnTheDefaultBranch_GuardG4RaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, B2);
        // A hand-made branch row for (Main, PUR) beside the company-level one: the move would collide.
        await ExecuteAsync(database, $"""INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', '{DefaultBranch}', 'PUR', 99, now())""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B3), "G4");

        Assert.DoesNotContain(B3, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "Series" = 'PUR' AND "BranchId" IS NULL"""));
        Assert.Equal(8L, await ScalarAsync<long>(database, """SELECT "NextValue" FROM "DocumentSequences" WHERE "Series" = 'PUR' AND "BranchId" IS NULL"""));
    }

    [Fact]
    public async Task Down_SecondBranchExists_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, B2);
        await MigrateToAsync(database, B3);
        await ExecuteAsync(database, $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now())""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B2), "beyond the default branch");

        Assert.Contains(B3, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(5L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" = '{DefaultBranch}'"""));
    }

    [Fact]
    public async Task Down_CounterOnANonDefaultBranch_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, B2);
        await MigrateToAsync(database, B3);
        const string blr = "00000000-0000-0000-0000-0000000b1001";
        await ExecuteAsync(database, $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES ('{blr}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
            INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', '{blr}', 'INV', 3, now());
            """);

        await AssertRaisesAsync(() => MigrateToAsync(database, B2), "beyond the default branch");

        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" = '{blr}'"""));
    }

    [Fact]
    public async Task Down_SingleBranchPerCompany_RestoresTheB2SchemaAndKeepsAdvancedCounters()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B2);
        await MigrateToAsync(database, B3);
        // The application advanced a counter after the release.
        await ExecuteAsync(database, $"""UPDATE "DocumentSequences" SET "NextValue" = 100 WHERE "CompanyId" = '{DefaultCompany}' AND "Series" = 'INV'""");

        await MigrateToAsync(database, B2);

        Assert.DoesNotContain(B3, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" IS NOT NULL"""));
        Assert.Equal(new[] { "ADJ=5", "EXP=4", "INV=100", "PRT=2", "PUR=8", "SRT=2" }, await CountersAsync(database, DefaultCompany));
        Assert.Equal(ExpectedOther, await CountersAsync(database, OtherCompany));
        var expected = await ReferenceSchemaAsync(postgres, B2);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
    }

    [Fact]
    public async Task Up_AfterDown_MovesIdentically()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, B2);
        await MigrateToAsync(database, B3);

        await MigrateToAsync(database, B2);
        await MigrateToAsync(database, B3);

        Assert.Equal(ExpectedDefault, await CountersAsync(database, DefaultCompany));
        Assert.Equal(ExpectedOther, await CountersAsync(database, OtherCompany));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunDoesNotResetCounters()
    {
        await using var database = await CreateAtAsync(postgres, B2);
        var script = await ScriptAsync(database, B2, B3);
        Assert.Contains(B3, script);

        await ExecuteAsync(database, script);

        Assert.Equal(ExpectedDefault, await CountersAsync(database, DefaultCompany));
        await ExecuteAsync(database, $"""UPDATE "DocumentSequences" SET "NextValue" = 100 WHERE "CompanyId" = '{DefaultCompany}' AND "Series" = 'INV'""");
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await ExecuteAsync(database, script);

        Assert.Equal(100L, await ScalarAsync<long>(database, $"""SELECT "NextValue" FROM "DocumentSequences" WHERE "CompanyId" = '{DefaultCompany}' AND "Series" = 'INV'"""));
        Assert.Equal(6L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "DocumentSequences" """));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
    }

    [Fact]
    public async Task Constraints_AfterUp_OneCounterPerBranchSeriesAndCompanyLevelExpAreEnforced()
    {
        await using var database = await CreateAtAsync(postgres, B2);
        await MigrateToAsync(database, B3);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            $"""INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', '{DefaultBranch}', 'PUR', 1, now())"""));
        Assert.Equal("IX_DocumentSequences_CompanyId_BranchId_Series", duplicate.ConstraintName);
        var companyLevel = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            $"""INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', NULL, 'EXP', 1, now())"""));
        Assert.Equal("IX_DocumentSequences_CompanyId_Series", companyLevel.ConstraintName);
    }
}
