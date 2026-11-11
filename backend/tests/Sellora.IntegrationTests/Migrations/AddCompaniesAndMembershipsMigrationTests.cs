using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 1 / M1 <c>AddCompaniesAndMemberships</c> on a legacy dataset in the pre-multi-company schema: default
/// company (name from the business profile), BusinessSettings and membership backfill, V-block, guard G1 (raises and rolls
/// back), Down guard with a second company, Down restoring the previous schema, Down then Up, empty database, and the
/// idempotent script run twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddCompaniesAndMembershipsMigrationTests(PostgresFixture postgres)
{
    private static readonly string[] Counted = ["Users", "BusinessSettings"];

    private static async Task AssertVBlockAsync(Sellora.IntegrationTests.Infrastructure.TestDatabase database, List<string> countsBefore)
    {
        Assert.Equal(countsBefore, await RowCountsAsync(database, Counted));                                                  // V1
        Assert.Empty(await QueryAsync(database, $"""SELECT "Id" FROM "Companies" WHERE "Id" <> '{DefaultCompany}'""", r => r.GetGuid(0))); // V2
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "Companies" WHERE "Id" = '{DefaultCompany}' AND "Code" = 'DEFAULT'"""));
        Assert.Empty(await QueryAsync(database, $"""
            SELECT u."Id" FROM "Users" u WHERE NOT EXISTS (SELECT 1 FROM "UserCompanyMemberships" m
              WHERE m."UserId" = u."Id" AND m."CompanyId" = '{DefaultCompany}' AND m."Status" = 'Active')
            """, r => r.GetGuid(0)));                                                                                          // V3
        Assert.Empty(await QueryAsync(database, $"""SELECT "Id" FROM "BusinessSettings" WHERE "CompanyId" <> '{DefaultCompany}'""", r => r.GetGuid(0))); // V4
    }

    [Fact]
    public async Task Up_LegacyData_CreatesDefaultCompanyBackfillsMembershipsAndSettingsAndVBlockIsEmpty()
    {
        await using var database = await CreateAtAsync(postgres);
        var before = await RowCountsAsync(database, Counted);
        Assert.Equal(new[] { "Users=1", "BusinessSettings=1" }, before);

        await MigrateToAsync(database, M1);

        await AssertVBlockAsync(database, before);
        Assert.Equal("Acme Retail", await ScalarAsync<string>(database, $"""SELECT "Name" FROM "Companies" WHERE "Id" = '{DefaultCompany}'"""));
        Assert.Equal("CompanyWide", await ScalarAsync<string>(database, """SELECT "AccessScope" FROM "UserCompanyMemberships" """));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "CreatedByUserId" IS NULL"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RefreshTokens" WHERE "CompanyId" IS NOT NULL"""));
        Assert.Equal("NO", await ScalarAsync<string>(database, """SELECT is_nullable FROM information_schema.columns WHERE table_name = 'BusinessSettings' AND column_name = 'CompanyId'"""));
        Assert.Equal("YES", await ScalarAsync<string>(database, """SELECT is_nullable FROM information_schema.columns WHERE table_name = 'RefreshTokens' AND column_name = 'CompanyId'"""));
        var constraints = (await QueryAsync(database, """SELECT conname FROM pg_constraint""", r => r.GetString(0))).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "CK_Companies_Id_NotEmpty", "CK_UserCompanyMemberships_Status", "CK_UserCompanyMemberships_AccessScope",
            "FK_BusinessSettings_Companies_CompanyId", "FK_RefreshTokens_Companies_CompanyId",
        }, constraints);
        var indexes = (await QueryAsync(database, """SELECT indexname FROM pg_indexes""", r => r.GetString(0))).ToHashSet();
        Assert.Superset(new HashSet<string> { "IX_Companies_Code", "IX_UserCompanyMemberships_UserId_CompanyId", "IX_BusinessSettings_CompanyId" }, indexes);
        // The default company's id is a CHECK-protected non-empty sentinel: Guid.Empty is the "no company" filter value.
        await AssertRaisesAsync(() => ExecuteAsync(database,
            """INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('00000000-0000-0000-0000-000000000000', 'EMPTY', 'x', true, now())"""),
            "CK_Companies_Id_NotEmpty");
    }

    [Fact]
    public async Task Up_EmptyDatabase_InsertsDefaultCompanyNamedDefaultCompanyAndNoMemberships()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, M1);

        Assert.Equal("Default Company", await ScalarAsync<string>(database, $"""SELECT "Name" FROM "Companies" WHERE "Id" = '{DefaultCompany}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" """));
    }

    [Fact]
    public async Task Up_MoreThanOneBusinessSettingsRow_GuardG1RaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres, extraLegacySql:
            """INSERT INTO "BusinessSettings" ("Id", "BusinessName", "InvoicePrefix") VALUES ('00000000-0000-0000-0000-000000000499', 'Second', 'X');""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, M1), "BusinessSettings has more than one row");

        Assert.DoesNotContain(M1, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'Companies'"));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "BusinessSettings" """));
    }

    [Fact]
    public async Task Down_WithSecondCompany_GuardRaisesAndRollsBack()
    {
        await using var database = await CreateAtAsync(postgres);
        await MigrateToAsync(database, M1);
        await ExecuteAsync(database, $"""INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now())""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, Legacy), "more than one company exists");

        Assert.Contains(M1, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Companies" """));
    }

    [Fact]
    public async Task Down_SingleCompany_RestoresThePreviousSchemaAndLegacyRows()
    {
        await using var database = await CreateAtAsync(postgres);
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, M1);

        await MigrateToAsync(database, Legacy);

        Assert.DoesNotContain(M1, await AppliedAsync(database));
        var expected = await ReferenceSchemaAsync(postgres, Legacy);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_AfterDown_BackfillsIdentically()
    {
        await using var database = await CreateAtAsync(postgres);
        var before = await RowCountsAsync(database, Counted);
        await MigrateToAsync(database, M1);
        var firstSchema = await SchemaSignatureAsync(database);

        await MigrateToAsync(database, Legacy);
        await MigrateToAsync(database, M1);

        await AssertVBlockAsync(database, before);
        Assert.Equal(firstSchema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunIsNoOp()
    {
        await using var database = await CreateAtAsync(postgres);
        var before = await RowCountsAsync(database, Counted);
        var script = await ScriptAsync(database, Legacy, M1);
        Assert.Contains("DO $EF$", script);
        Assert.Contains(M1, script);

        await ExecuteAsync(database, script);

        Assert.Contains(M1, await AppliedAsync(database));
        await AssertVBlockAsync(database, before);
        var history = await AppliedAsync(database);
        var schema = await SchemaSignatureAsync(database);
        var members = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" """);

        await ExecuteAsync(database, script);

        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(members, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "UserCompanyMemberships" """));
        await using var db = database.CreateDbContext();
        Assert.Contains(M1, await db.Database.GetAppliedMigrationsAsync());
    }
}
