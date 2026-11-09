using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 2 / B1 <c>AddBranches</c> on the legacy dataset carried through Phase 1 (M4): one Main per company with the
/// reserved id for the default company and a deterministic md5 id for the others, V4, legacy rows untouched, Down guard with a second
/// branch, Down restores the M4 schema, Down then Up, empty database, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddBranchesMigrationTests(PostgresFixture postgres)
{
    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        // V4: every company has exactly one default active branch.
        var bad = await QueryAsync(database, """
            SELECT c."Id"::text FROM "Companies" c
            WHERE (SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault" AND b."IsActive") <> 1
            """, r => r.GetString(0));
        Assert.True(bad.Count == 0, $"Companies without exactly one default active branch: {string.Join(", ", bad)} (V4).");
        // V4b: the default company's Main is the reserved id.
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "Branches" WHERE "Id" = '{DefaultBranch}' AND "CompanyId" = '{DefaultCompany}' AND "IsDefault" AND "Code" = 'MAIN' AND "Name" = 'Main'"""));
    }

    [Fact]
    public async Task Up_LegacyData_AddsMainWithTheReservedIdAndLeavesEveryLegacyRowUntouched()
    {
        await using var database = await CreateAtAsync(postgres, M4);
        var before = await RowCountsAsync(database, CountedTables);

        await MigrateToAsync(database, B1);

        await AssertVBlockAsync(database);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" """));
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(B1, (await AppliedAsync(database)).Last());
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" WHERE "InvoicePrefix" IS NOT NULL OR "Gstin" IS NOT NULL"""));
    }

    [Fact]
    public async Task Up_SecondCompany_GetsItsOwnMainWithADeterministicId()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, M4);

        await MigrateToAsync(database, B1);

        await AssertVBlockAsync(database);
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" """));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "Branches" WHERE "CompanyId" = '{OtherCompany}' AND "Id" = {OtherMainSql} AND "Code" = 'MAIN' AND "IsDefault" AND "IsActive" """));
    }

    [Fact]
    public async Task Up_EmptyDatabase_CreatesOnlyTheDefaultCompanyMain()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B1);

        await AssertVBlockAsync(database);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" """));
    }

    [Fact]
    public async Task Down_CompanyWithASecondBranch_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, M4);
        await MigrateToAsync(database, B1);
        await ExecuteAsync(database, $"""INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now())""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, M4), "more than one branch");

        Assert.Contains(B1, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" """));
    }

    [Fact]
    public async Task Down_OnlyMainBranches_RestoresTheM4Schema()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, M4);
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, B1);

        await MigrateToAsync(database, M4);

        Assert.DoesNotContain(B1, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'Branches'"));
        var expected = await ReferenceSchemaAsync(postgres, M4);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_AfterDown_RecreatesTheSameBranches()
    {
        await using var database = await CreateWithSecondCompanyAtAsync(postgres, M4);
        await MigrateToAsync(database, B1);
        var first = await QueryAsync(database, """SELECT "Id"::text || ':' || "CompanyId"::text || ':' || "Code" FROM "Branches" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, M4);
        await MigrateToAsync(database, B1);

        Assert.Equal(first, await QueryAsync(database, """SELECT "Id"::text || ':' || "CompanyId"::text || ':' || "Code" FROM "Branches" ORDER BY 1""", r => r.GetString(0)));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothing()
    {
        await using var database = await CreateAtAsync(postgres, M4);
        var script = await ScriptAsync(database, M4, B1);
        Assert.Contains(B1, script);

        await ExecuteAsync(database, script);

        await AssertVBlockAsync(database);
        // An operator renames Main between runs: a re-run must not reset or duplicate it.
        await ExecuteAsync(database, $"""UPDATE "Branches" SET "Name" = 'Head office' WHERE "Id" = '{DefaultBranch}'""");
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await ExecuteAsync(database, script);

        Assert.Equal("Head office", await ScalarAsync<string>(database, $"""SELECT "Name" FROM "Branches" WHERE "Id" = '{DefaultBranch}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" """));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
    }
}
