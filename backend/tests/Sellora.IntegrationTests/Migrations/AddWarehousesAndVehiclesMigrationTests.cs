using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 4 / B5 <c>AddWarehousesAndVehicles</c> on the Phase 3 head (B4) with two companies: Up on an empty database and on
/// the legacy dataset, the new tables/constraints exist, V1-V4 clean (also after rows are written), guard G6 raise + rollback (pre-existing
/// warehouse/vehicle-type locations), Down guard D-G4 (a vehicle / a warehouse row), Down restores the B4 schema exactly, Down then Up,
/// idempotent script twice, stock tables untouched.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddWarehousesAndVehiclesMigrationTests(PostgresFixture postgres)
{
    // Subtype FK names are kept under PostgreSQL's 63-byte identifier limit so they are stored exactly as declared.
    private static readonly string WhFk = "FK_Warehouses_StockLocations_Typed";
    private static readonly string VehFk = "FK_Vehicles_StockLocations_Typed";

    private const string BranchBlr = "00000000-0000-0000-0000-0000000a1003";

    private async Task<TestDatabase> CreateAtB4Async()
    {
        var database = await CreateWithSecondCompanyAtAsync(postgres, B4);
        await ExecuteAsync(database, $"""
            INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES
              ('{BranchBlr}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
            """);
        return database;
    }

    private static string Location(string id, string company, string? branch, string type, string code) =>
        $"""INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{id}', '{company}', {(branch is null ? "NULL" : $"'{branch}'")}, '{type}', '{code}', 'Name {code}', true, now());""";

    private const string WhLocation = "00000000-0000-0000-0000-0000000c0001";
    private const string VanLocation = "00000000-0000-0000-0000-0000000c0002";

    /// <summary>One company warehouse and one assigned vehicle, written the way the services write them (location + subtype row).</summary>
    private static string WriteRowsSql() =>
        Location(WhLocation, DefaultCompany, null, "CompanyWarehouse", "WH1")
        + Location(VanLocation, DefaultCompany, null, "Vehicle", "VAN-1")
        + $"""
          INSERT INTO "Warehouses" ("Id", "CompanyId", "StockLocationId", "LocationType", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', '{WhLocation}', 'CompanyWarehouse', now());
          INSERT INTO "Vehicles" ("Id", "CompanyId", "StockLocationId", "LocationType", "VehicleNumber", "VehicleName", "VehicleType", "AssignedBranchId", "IsActive", "CreatedAt")
          VALUES (gen_random_uuid(), '{DefaultCompany}', '{VanLocation}', 'Vehicle', 'VAN-1', 'Name VAN-1', 'Van', '{BranchBlr}', true, now());
          """;

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        Assert.Empty(await QueryAsync(database, """
            SELECT l."Id"::text FROM "StockLocations" l WHERE l."LocationType" IN ('CompanyWarehouse', 'BranchWarehouse')
              AND (SELECT COUNT(*) FROM "Warehouses" w WHERE w."StockLocationId" = l."Id") <> 1
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT l."Id"::text FROM "StockLocations" l WHERE l."LocationType" = 'Vehicle'
              AND (SELECT COUNT(*) FROM "Vehicles" v WHERE v."StockLocationId" = l."Id") <> 1
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT v."Id"::text FROM "Vehicles" v JOIN "StockLocations" l ON l."Id" = v."StockLocationId"
            WHERE l."Code" <> v."VehicleNumber" OR l."Name" <> v."VehicleName" OR l."IsActive" <> v."IsActive" OR l."BranchId" IS NOT NULL
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT v."Id"::text FROM "Vehicles" v JOIN "Branches" b ON b."Id" = v."AssignedBranchId" WHERE b."CompanyId" <> v."CompanyId"
            """, r => r.GetString(0)));
        // Phase 3 invariants (V6 regression): still only the migrated stores and transits when nothing was written.
        Assert.Empty(await QueryAsync(database, """
            SELECT l."Id"::text FROM "StockLocations" l JOIN "Branches" b ON b."Id" = l."BranchId" WHERE b."CompanyId" <> l."CompanyId"
            """, r => r.GetString(0)));
    }

    private static async Task AssertNewSchemaAsync(TestDatabase database)
    {
        foreach (var table in new[] { "Warehouses", "Vehicles" })
            Assert.Equal(1L, await ScalarAsync<long>(database, $"SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = '{table}'"));
        var constraints = await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname IN ('Warehouses', 'Vehicles', 'StockLocations')
            """, r => r.GetString(0));
        foreach (var name in new[]
        {
            "AK_StockLocations_CompanyId_Id_LocationType", WhFk,
            VehFk, "FK_Vehicles_Branches_CompanyId_AssignedBranchId",
            "CK_Warehouses_LocationType", "CK_Vehicles_LocationType", "CK_Vehicles_VehicleType", "CK_Warehouses_Id_NotEmpty", "CK_Vehicles_Id_NotEmpty",
        })
            Assert.Contains(name, constraints);
        var indexes = await QueryAsync(database, """SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('Warehouses', 'Vehicles')""", r => r.GetString(0));
        foreach (var name in new[] { "IX_Warehouses_StockLocation", "IX_Vehicles_StockLocation", "IX_Vehicles_CompanyId_VehicleNumber", "IX_Vehicles_CompanyId_AssignedBranchId" })
            Assert.Contains(name, indexes);
        // The subtype FK indexes are unique (1:1 with the location).
        Assert.Equal(2L, await ScalarAsync<long>(database, """
            SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname IN ('IX_Warehouses_StockLocation', 'IX_Vehicles_StockLocation') AND indexdef LIKE 'CREATE UNIQUE%'
            """));
    }

    [Fact]
    public async Task Up_TwoCompaniesOnThePhase3Head_CreatesEmptyTablesAndLeavesLocationsAndStockUntouched()
    {
        await using var database = await CreateAtB4Async();
        var before = await RowCountsAsync(database, CountedTables);
        var locations = await QueryAsync(database, """SELECT "Id"::text || ':' || "LocationType" || ':' || "Code" FROM "StockLocations" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B5);

        Assert.Equal(B5, (await AppliedAsync(database)).Last());
        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Warehouses" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Vehicles" """));
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(locations, await QueryAsync(database, """SELECT "Id"::text || ':' || "LocationType" || ':' || "Code" FROM "StockLocations" ORDER BY 1""", r => r.GetString(0)));
    }

    [Fact]
    public async Task Up_EmptyDatabase_CreatesTheNewTables()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        await MigrateToAsync(database, B5);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task Up_ThenServiceShapedWrites_KeepTheVBlockClean()
    {
        await using var database = await CreateAtB4Async();
        await MigrateToAsync(database, B5);

        await ExecuteAsync(database, WriteRowsSql());

        await AssertVBlockAsync(database);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Warehouses" """));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Vehicles" """));
    }

    [Theory]
    [InlineData("CompanyWarehouse", null)]
    [InlineData("BranchWarehouse", BranchBlr)]
    [InlineData("Vehicle", null)]
    public async Task Up_AnOrphanWarehouseOrVehicleLocationExists_GuardG6RaisesAndRollsBack(string type, string? branch)
    {
        await using var database = await CreateAtB4Async();
        await ExecuteAsync(database, Location("00000000-0000-0000-0000-0000000c0009", DefaultCompany, branch, type, "ORPHAN"));
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B5), "(G6)");

        Assert.DoesNotContain(B5, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename IN ('Warehouses', 'Vehicles')"));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task Down_NoRows_RestoresTheB4SchemaExactly()
    {
        await using var database = await CreateAtB4Async();
        var before = await RowCountsAsync(database, CountedTables);
        var locations = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """);
        await MigrateToAsync(database, B5);

        await MigrateToAsync(database, B4);

        Assert.DoesNotContain(B5, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename IN ('Warehouses', 'Vehicles')"));
        var expected = await ReferenceSchemaAsync(postgres, B4);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(before, await RowCountsAsync(database, CountedTables));
        Assert.Equal(locations, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """));
    }

    [Theory]
    [InlineData("Vehicles")]
    [InlineData("Warehouses")]
    public async Task Down_ASubtypeRowExists_GuardDG4RaisesAndRollsBack(string table)
    {
        await using var database = await CreateAtB4Async();
        await MigrateToAsync(database, B5);
        await ExecuteAsync(database, WriteRowsSql());
        // Keep exactly one kind of subtype row so each guard branch is exercised on its own.
        await ExecuteAsync(database, table == "Vehicles" ? """DELETE FROM "Warehouses" """ : """DELETE FROM "Vehicles" """);
        var schema = await SchemaSignatureAsync(database);

        await AssertRaisesAsync(() => MigrateToAsync(database, B4), "(D-G4)");

        Assert.Contains(B5, await AppliedAsync(database));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "{table}" """));
    }

    [Fact]
    public async Task Up_AfterDown_RecreatesTheSameSchema()
    {
        await using var database = await CreateAtB4Async();
        await MigrateToAsync(database, B5);
        var first = await SchemaSignatureAsync(database);

        await MigrateToAsync(database, B4);
        await MigrateToAsync(database, B5);

        Assert.Equal(first, await SchemaSignatureAsync(database));
        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothing()
    {
        await using var database = await CreateAtB4Async();
        var script = await ScriptAsync(database, B4, B5);
        Assert.Contains(B5, script);

        await ExecuteAsync(database, script);

        await AssertNewSchemaAsync(database);
        await AssertVBlockAsync(database);
        // Rows written between runs must survive a re-run.
        await ExecuteAsync(database, WriteRowsSql());
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Warehouses" """));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Vehicles" """));
        await AssertVBlockAsync(database);
    }
}
