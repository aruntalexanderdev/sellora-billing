using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Phase 3 database backstops on a fully migrated database: location type CHECK, store/warehouse &lt;=&gt; BranchId CHECK, Transit always
/// active, empty id, unique code per company (not across companies), one BranchStore per branch, one Transit per company, and the
/// composite same-company FK to Branches.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockLocationConstraintTests(PostgresFixture postgres)
{
    private const string BranchB = "00000000-0000-0000-0000-0000000b1002";
    private const string BranchA2 = "00000000-0000-0000-0000-0000000a1003";

    // A = default company (Main + store + Transit exist after migration); A2 = a second branch of A without a store; B = a second company with a Main (no store/transit).
    private const string SeedSql = $"""
        INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now());
        INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES
          ('{BranchB}', '{OtherCompany}', 'MAIN', 'Main', true, true, now()),
          ('{BranchA2}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
        """;

    private async Task<TestDatabase> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await ExecuteAsync(database, SeedSql);
        return database;
    }

    private static string Insert(string company, string? branch, string type, string code, bool active = true, string? id = null) =>
        $"""INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES ({(id is null ? "gen_random_uuid()" : $"'{id}'")}, '{company}', {(branch is null ? "NULL" : $"'{branch}'")}, '{type}', '{code}', 'Name {code}', {(active ? "true" : "false")}, now())""";

    private static async Task AssertViolatesAsync(TestDatabase database, string sql, string sqlState, string constraint)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, sql));
        Assert.Equal((sqlState, constraint), (ex.SqlState, ex.ConstraintName));
    }

    [Theory]
    [InlineData("BranchWarehouse", true)]
    [InlineData("CompanyWarehouse", false)]
    [InlineData("Vehicle", false)]
    public async Task Insert_TypeWithItsCorrectBranchShape_IsAccepted(string type, bool withBranch)
    {
        await using var database = await SeedAsync();

        await ExecuteAsync(database, Insert(DefaultCompany, withBranch ? BranchA2 : null, type, "L1"));
    }

    [Fact]
    public async Task Insert_BranchStoreForAnUnstoredBranch_IsAccepted()
    {
        await using var database = await SeedAsync();

        await ExecuteAsync(database, Insert(DefaultCompany, BranchA2, "BranchStore", "BLR"));
    }

    [Theory]
    [InlineData("BranchStore")]
    [InlineData("BranchWarehouse")]
    public async Task Insert_StoreOrWarehouseWithoutABranch_IsRejected(string type)
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(DefaultCompany, null, type, "NOB"), "23514", "CK_StockLocations_Type_Branch");
    }

    [Theory]
    [InlineData("CompanyWarehouse")]
    [InlineData("Vehicle")]
    [InlineData("Transit")]
    public async Task Insert_CompanyLevelTypeWithABranch_IsRejected(string type)
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(DefaultCompany, BranchA2, type, "WITHB"), "23514", "CK_StockLocations_Type_Branch");
    }

    [Fact]
    public async Task Insert_UnknownLocationType_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(DefaultCompany, null, "Warehouse", "BAD"), "23514", "CK_StockLocations_LocationType");
        await AssertViolatesAsync(database, Insert(DefaultCompany, null, "vehicle", "BAD2"), "23514", "CK_StockLocations_LocationType");
    }

    [Fact]
    public async Task Insert_BranchOfAnotherCompany_IsRejectedByTheCompositeForeignKey()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(DefaultCompany, BranchB, "BranchWarehouse", "X1"), "23503", "FK_StockLocations_Branches_CompanyId_BranchId");
        await AssertViolatesAsync(database, $"""UPDATE "StockLocations" SET "BranchId" = '{BranchB}' WHERE "Id" = '{DefaultStore}'""", "23503", "FK_StockLocations_Branches_CompanyId_BranchId");
    }

    [Fact]
    public async Task Insert_UnknownCompany_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(Guid.NewGuid().ToString(), null, "Vehicle", "ZZ"), "23503", "FK_StockLocations_Companies_CompanyId");
    }

    [Fact]
    public async Task Insert_SecondBranchStoreForTheSameBranch_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(DefaultCompany, DefaultBranch, "BranchStore", "MAIN2"), "23505", "IX_StockLocations_BranchId_BranchStore");
        // A warehouse on the same branch is fine (only stores are limited to one).
        await ExecuteAsync(database, Insert(DefaultCompany, DefaultBranch, "BranchWarehouse", "MAINWH"));
    }

    [Fact]
    public async Task Insert_SecondTransitForTheSameCompany_IsRejectedButAnotherCompanyMayHaveItsOwn()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(DefaultCompany, null, "Transit", "TRANSIT2"), "23505", "IX_StockLocations_CompanyId_Transit");
        await ExecuteAsync(database, Insert(OtherCompany, null, "Transit", "TRANSIT"));
    }

    [Fact]
    public async Task Update_TransitMadeInactive_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "StockLocations" SET "IsActive" = false WHERE "Id" = '{DefaultTransit}'""", "23514", "CK_StockLocations_Transit_Active");
        await AssertViolatesAsync(database, Insert(OtherCompany, null, "Transit", "TRANSIT", active: false), "23514", "CK_StockLocations_Transit_Active");
        // A store may be deactivated.
        await ExecuteAsync(database, $"""UPDATE "StockLocations" SET "IsActive" = false WHERE "Id" = '{DefaultStore}'""");
    }

    [Fact]
    public async Task Insert_DuplicateCodeInTheSameCompany_IsRejectedButTheSameCodeInAnotherCompanyIsFine()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Insert(DefaultCompany, null, "Vehicle", "VAN1"));

        await AssertViolatesAsync(database, Insert(DefaultCompany, null, "CompanyWarehouse", "VAN1"), "23505", "IX_StockLocations_CompanyId_Code");
        await ExecuteAsync(database, Insert(OtherCompany, null, "Vehicle", "VAN1"));
        // The migrated store already uses the branch code MAIN: another location cannot reuse it in the same company.
        await AssertViolatesAsync(database, Insert(DefaultCompany, null, "Vehicle", "MAIN"), "23505", "IX_StockLocations_CompanyId_Code");
    }

    [Fact]
    public async Task Insert_EmptyGuidId_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Insert(DefaultCompany, null, "Vehicle", "ZERO", id: "00000000-0000-0000-0000-000000000000"), "23514", "CK_StockLocations_Id_NotEmpty");
    }

    [Fact]
    public async Task Insert_OverlongCode_IsRejected()
    {
        await using var database = await SeedAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, Insert(DefaultCompany, null, "Vehicle", new string('X', 21))));
        Assert.Equal("22001", ex.SqlState);
    }

    [Fact]
    public async Task Delete_BranchThatOwnsAStore_IsRejected()
    {
        await using var database = await SeedAsync();

        await ExecuteAsync(database, "DELETE FROM \"DocumentSequences\""); // counters also reference the branch; isolate the location FK
        await AssertViolatesAsync(database, $"""DELETE FROM "Branches" WHERE "Id" = '{DefaultBranch}'""", "23503", "FK_StockLocations_Branches_CompanyId_BranchId");
    }
}
