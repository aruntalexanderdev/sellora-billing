using Microsoft.EntityFrameworkCore;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 1 / M2 <c>AddCompanyIdToCompanyTables</c> on the legacy dataset carried through M1: every company table
/// gets <c>CompanyId</c> backfilled to the default company with no row lost (V1/V2), NOT NULL + FK to Companies, Down guard
/// with rows of a second company (raises, rolls back), Down restores the M1 schema, Down then Up, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddCompanyIdToCompanyTablesMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Up_LegacyData_BackfillsEveryCompanyTableToDefaultCompanyWithoutLosingRows()
    {
        await using var database = await CreateAtAsync(postgres, M1);
        var before = await RowCountsAsync(database, CountedTables);
        Assert.True((await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" """)) > 0);

        await MigrateToAsync(database, M2);

        Assert.Equal(before, await RowCountsAsync(database, CountedTables));                                 // V1
        await AssertAllRowsOnDefaultCompanyAsync(database);                                                   // V2
        foreach (var table in CompanyTables)
        {
            Assert.Equal("NO", await ScalarAsync<string>(database,
                $"SELECT is_nullable FROM information_schema.columns WHERE table_name = '{table}' AND column_name = 'CompanyId'"));
            Assert.Equal(1L, await ScalarAsync<long>(database,
                $"SELECT COUNT(*) FROM pg_constraint WHERE conname = 'FK_{table}_Companies_CompanyId'"));
        }
        await using var check = database.CreateDbContext();
        await Sellora.IntegrationTests.TestData.StockInvariants.AssertHoldAsync(check);                      // V6 reconciliation unchanged
    }

    [Fact]
    public async Task Up_EmptyDatabaseAtM1_AppliesCleanly()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, M1);

        await MigrateToAsync(database, M2);

        await AssertAllRowsOnDefaultCompanyAsync(database);
    }

    [Fact]
    public async Task Up_CompanyIdColumnRejectsUnknownCompany()
    {
        await using var database = await CreateAtAsync(postgres, M2);

        await AssertRaisesAsync(() => ExecuteAsync(database,
            $"""INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive", "CompanyId") VALUES (gen_random_uuid(), 'X', 1, true, '{OtherCompany}')"""),
            "FK_Taxes_Companies_CompanyId");
    }

    [Fact]
    public async Task Down_WithRowsOfASecondCompany_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, M2);
        await ExecuteAsync(database, $"""
            INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now());
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive", "CompanyId") VALUES (gen_random_uuid(), 'B tax', 1, true, '{OtherCompany}');
            """);
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, M1), "more than one company exists");

        Assert.Contains(M2, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task Down_SingleCompany_RestoresTheM1SchemaAndRows()
    {
        await using var database = await CreateAtAsync(postgres, M1);
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, M2);

        await MigrateToAsync(database, M1);

        Assert.DoesNotContain(M2, await AppliedAsync(database));
        var expected = await ReferenceSchemaAsync(postgres, M1);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_AfterDown_BackfillsIdentically()
    {
        await using var database = await CreateAtAsync(postgres, M1);
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, M2);
        var schema = await SchemaSignatureAsync(database);

        await MigrateToAsync(database, M1);
        await MigrateToAsync(database, M2);

        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        await AssertAllRowsOnDefaultCompanyAsync(database);
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunIsNoOp()
    {
        await using var database = await CreateAtAsync(postgres, M1);
        var before = await RowCountsAsync(database, CountedTables);
        var script = await ScriptAsync(database, M1, M2);
        Assert.Contains(M2, script);

        await ExecuteAsync(database, script);

        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        await AssertAllRowsOnDefaultCompanyAsync(database);
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }
}
