using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 3 / B4 <c>AddStockLocations</c> on the legacy dataset carried through Phase 2 (B3): one BranchStore per branch
/// (active, extra and INACTIVE branches; the store mirrors code/name/active), one Transit per company, reserved ids for the default
/// company and md5-derived ids for the others, V1-V4, guard G5 raise + rollback, Down guard D-G3 (a Vehicle row), Down restores the B3
/// schema, Down then Up, empty database, idempotent script twice, stock tables untouched.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddStockLocationsMigrationTests(PostgresFixture postgres)
{
    private const string BranchBlr = "00000000-0000-0000-0000-0000000a1003";
    private const string BranchKar = "00000000-0000-0000-0000-0000000a1004";
    private const string BranchBOff = "00000000-0000-0000-0000-0000000b1005";

    /// <summary>Phase 2 head with two companies, and extra branches: BLR (active) and KAR (inactive) on the default company, an inactive branch on B.</summary>
    private async Task<TestDatabase> CreateAtB3Async()
    {
        var database = await CreateWithSecondCompanyAtAsync(postgres, B3);
        await ExecuteAsync(database, $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES
              ('{BranchBlr}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now()),
              ('{BranchKar}', '{DefaultCompany}', 'KAR', 'Karur', false, false, now()),
              ('{BranchBOff}', '{OtherCompany}', 'OLD', 'Old B', false, false, now());
            """);
        return database;
    }

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        Assert.Empty(await QueryAsync(database, """
            SELECT b."Id"::text FROM "Branches" b WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore'
              AND l."CompanyId" = b."CompanyId" AND l."IsActive" = b."IsActive" AND l."Code" = b."Code" AND l."Name" = b."Name") <> 1
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT c."Id"::text FROM "Companies" c WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."CompanyId" = c."Id" AND l."LocationType" = 'Transit' AND l."IsActive") <> 1
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT l."Id"::text FROM "StockLocations" l JOIN "Branches" b ON b."Id" = l."BranchId" WHERE b."CompanyId" <> l."CompanyId"
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """SELECT "Id"::text FROM "StockLocations" WHERE "LocationType" NOT IN ('BranchStore', 'Transit')""", r => r.GetString(0)));
        // V6 reserved ids of the default company.
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "StockLocations" WHERE "Id" = '{DefaultStore}' AND "CompanyId" = '{DefaultCompany}' AND "BranchId" = '{DefaultBranch}' AND "LocationType" = 'BranchStore' AND "Code" = 'MAIN' AND "IsActive" """));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "StockLocations" WHERE "Id" = '{DefaultTransit}' AND "CompanyId" = '{DefaultCompany}' AND "BranchId" IS NULL AND "LocationType" = 'Transit' AND "Code" = 'TRANSIT' AND "IsActive" """));
    }

    [Fact]
    public async Task Up_TwoCompaniesWithExtraAndInactiveBranches_BackfillsOneStorePerBranchAndOneTransitPerCompany()
    {
        await using var database = await CreateAtB3Async();
        var before = await RowCountsAsync(database, CountedTables);

        await MigrateToAsync(database, B4);

        await AssertVBlockAsync(database);
        Assert.Equal(B4, (await AppliedAsync(database)).Last());
        Assert.Equal(7L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """)); // 5 branch stores + 2 transits
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
    }

    [Fact]
    public async Task Up_InactiveBranch_GetsAnInactiveStoreAndTheCompanyTransitStaysActive()
    {
        await using var database = await CreateAtB3Async();

        await MigrateToAsync(database, B4);

        Assert.False(await ScalarAsync<bool>(database, $"""SELECT "IsActive" FROM "StockLocations" WHERE "BranchId" = '{BranchKar}' AND "LocationType" = 'BranchStore'"""));
        Assert.False(await ScalarAsync<bool>(database, $"""SELECT "IsActive" FROM "StockLocations" WHERE "BranchId" = '{BranchBOff}' AND "LocationType" = 'BranchStore'"""));
        Assert.True(await ScalarAsync<bool>(database, $"""SELECT "IsActive" FROM "StockLocations" WHERE "BranchId" = '{BranchBlr}' AND "LocationType" = 'BranchStore'"""));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" WHERE "LocationType" = 'Transit' AND "IsActive" """));
    }

    [Fact]
    public async Task Up_SecondCompany_GetsMd5DerivedIdsAndTheDefaultCompanyTheReservedOnes()
    {
        await using var database = await CreateAtB3Async();

        await MigrateToAsync(database, B4);

        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "StockLocations" WHERE "CompanyId" = '{OtherCompany}' AND "LocationType" = 'Transit' AND "Id" = {OtherTransitSql}"""));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "StockLocations" WHERE "CompanyId" = '{OtherCompany}' AND "BranchId" = {OtherMainSql} AND "LocationType" = 'BranchStore' AND "Id" = md5('location:store:' || {OtherMainSql}::text)::uuid"""));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "StockLocations" WHERE "BranchId" = '{BranchBlr}' AND "Id" = md5('location:store:' || '{BranchBlr}')::uuid"""));
        // Locations never cross companies.
        Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockLocations" WHERE "CompanyId" = '{OtherCompany}' AND "Id" IN ('{DefaultStore}', '{DefaultTransit}')"""));
    }

    [Fact]
    public async Task Up_LegacyData_LeavesEveryStockTableAndLedgerTotalUntouched()
    {
        await using var database = await CreateAtB3Async();
        var before = await RowCountsAsync(database, CountedTables);
        var qoh = await QueryAsync(database, """SELECT "ProductId"::text || ':' || "QuantityOnHand" FROM "StockItems" ORDER BY 1""", r => r.GetString(0));
        var ledger = await QueryAsync(database, """SELECT "ProductId"::text || ':' || SUM("QuantityBaseUnits") FROM "StockMovements" GROUP BY "ProductId" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B4);

        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(qoh, await QueryAsync(database, """SELECT "ProductId"::text || ':' || "QuantityOnHand" FROM "StockItems" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(ledger, await QueryAsync(database, """SELECT "ProductId"::text || ':' || SUM("QuantityBaseUnits") FROM "StockMovements" GROUP BY "ProductId" ORDER BY 1""", r => r.GetString(0)));
        // The reconciliation SQL of the skill (QOH vs ledger) returns no rows.
        Assert.Empty(await QueryAsync(database, """
            SELECT s."ProductId"::text FROM "StockItems" s LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
            WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
            """, r => r.GetString(0)));
    }

    [Fact]
    public async Task Up_EmptyDatabase_CreatesTheDefaultCompanyStoreAndTransitOnly()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B4);

        await AssertVBlockAsync(database);
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """));
    }

    [Fact]
    public async Task Up_CompanyWithoutADefaultBranch_GuardG5RaisesAndRollsBack()
    {
        await using var database = await CreateAtB3Async();
        await ExecuteAsync(database, """INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), 'C', 'Company C', true, now())""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B4), "exactly one default branch");

        Assert.DoesNotContain(B4, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'StockLocations'"));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task Down_OnlyStoresAndTransits_RestoresTheB3Schema()
    {
        await using var database = await CreateAtB3Async();
        var before = await RowCountsAsync(database, CountedTables);
        await MigrateToAsync(database, B4);

        await MigrateToAsync(database, B3);

        Assert.DoesNotContain(B4, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'StockLocations'"));
        var expected = await ReferenceSchemaAsync(postgres, B3);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(5L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" """));
    }

    [Fact]
    public async Task Down_AVehicleLocationExists_GuardDG3RaisesAndRollsBack()
    {
        await using var database = await CreateAtB3Async();
        await MigrateToAsync(database, B4);
        await ExecuteAsync(database, $"""INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', NULL, 'Vehicle', 'VAN1', 'Van 1', true, now())""");
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B3), "locations of another type");

        Assert.Contains(B4, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" WHERE "LocationType" = 'Vehicle'"""));
    }

    [Fact]
    public async Task Up_AfterDown_RecreatesTheSameLocationsWithTheSameIds()
    {
        await using var database = await CreateAtB3Async();
        await MigrateToAsync(database, B4);
        const string Q = """SELECT "Id"::text || ':' || "CompanyId"::text || ':' || "LocationType" || ':' || "Code" || ':' || "IsActive" FROM "StockLocations" ORDER BY 1""";
        var first = await QueryAsync(database, Q, r => r.GetString(0));

        await MigrateToAsync(database, B3);
        await MigrateToAsync(database, B4);

        Assert.Equal(first, await QueryAsync(database, Q, r => r.GetString(0)));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothingAndKeepsOperatorEdits()
    {
        await using var database = await CreateAtB3Async();
        var script = await ScriptAsync(database, B3, B4);
        Assert.Contains(B4, script);

        await ExecuteAsync(database, script);

        await AssertVBlockAsync(database);
        // An operator renames the Main store between runs: a re-run must not reset or duplicate it.
        await ExecuteAsync(database, $"""UPDATE "StockLocations" SET "Name" = 'Head store' WHERE "Id" = '{DefaultStore}'""");
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);
        var count = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """);

        await ExecuteAsync(database, script);

        Assert.Equal("Head store", await ScalarAsync<string>(database, $"""SELECT "Name" FROM "StockLocations" WHERE "Id" = '{DefaultStore}'"""));
        Assert.Equal(count, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
    }
}
