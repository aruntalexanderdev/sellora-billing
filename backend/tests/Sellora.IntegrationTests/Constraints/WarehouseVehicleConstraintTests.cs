using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Phase 4 database backstops on a fully migrated database: 1:1 subtype FKs, the type-matching alternate key (a subtype row cannot point
/// at a location of the wrong type or of another company, and the location's type is frozen while referenced), the composite same-company
/// assignment FK, unique vehicle number per company, and the CHECKs.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WarehouseVehicleConstraintTests(PostgresFixture postgres)
{
    // Subtype FK names are kept under PostgreSQL's 63-byte identifier limit so they are stored exactly as declared.
    private static readonly string WhFk = "FK_Warehouses_StockLocations_Typed";
    private static readonly string VehFk = "FK_Vehicles_StockLocations_Typed";

    private const string BranchB = "00000000-0000-0000-0000-0000000b1002";
    private const string BranchA2 = "00000000-0000-0000-0000-0000000a1003";

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

    private static string Loc(string id, string company, string? branch, string type, string code) =>
        $"""INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{id}', '{company}', {(branch is null ? "NULL" : $"'{branch}'")}, '{type}', '{code}', 'Name {code}', true, now());""";

    private static string Wh(string company, string location, string type = "CompanyWarehouse", string? id = null) =>
        $"""INSERT INTO "Warehouses" ("Id", "CompanyId", "StockLocationId", "LocationType", "CreatedAt") VALUES ({(id is null ? "gen_random_uuid()" : $"'{id}'")}, '{company}', '{location}', '{type}', now())""";

    private static string Veh(string company, string location, string number, string type = "Van", string? assigned = null, string locationType = "Vehicle", string? id = null) =>
        $"""INSERT INTO "Vehicles" ("Id", "CompanyId", "StockLocationId", "LocationType", "VehicleNumber", "VehicleName", "VehicleType", "AssignedBranchId", "IsActive", "CreatedAt") VALUES ({(id is null ? "gen_random_uuid()" : $"'{id}'")}, '{company}', '{location}', '{locationType}', '{number}', 'Name {number}', '{type}', {(assigned is null ? "NULL" : $"'{assigned}'")}, true, now())""";

    private static async Task AssertViolatesAsync(TestDatabase database, string sql, string sqlState, string constraint)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, sql));
        Assert.Equal((sqlState, constraint), (ex.SqlState, ex.ConstraintName));
    }

    private const string L1 = "00000000-0000-0000-0000-0000000d0001";
    private const string L2 = "00000000-0000-0000-0000-0000000d0002";
    private const string L3 = "00000000-0000-0000-0000-0000000d0003";

    [Fact]
    public async Task Insert_WarehouseAndVehicleOnMatchingLocations_IsAccepted()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "CompanyWarehouse", "WH1"));
        await ExecuteAsync(database, Loc(L2, DefaultCompany, BranchA2, "BranchWarehouse", "WH2"));
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));

        await ExecuteAsync(database, Wh(DefaultCompany, L1));
        await ExecuteAsync(database, Wh(DefaultCompany, L2, "BranchWarehouse"));
        await ExecuteAsync(database, Veh(DefaultCompany, L3, "VAN1", assigned: BranchA2));
    }

    [Fact]
    public async Task Insert_WarehouseOnAVehicleLocation_IsRejectedByTheTypeMatchingKey()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "Vehicle", "VAN1"));

        // Claims CompanyWarehouse for a location that is a Vehicle.
        await AssertViolatesAsync(database, Wh(DefaultCompany, L1), "23503", WhFk);
        await AssertViolatesAsync(database, Wh(DefaultCompany, L1, "BranchWarehouse"), "23503", WhFk);
    }

    [Fact]
    public async Task Insert_WarehouseOnABranchStoreOrTransit_IsRejected()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, Wh(DefaultCompany, DefaultStore, "BranchWarehouse"), "23503", WhFk);
        await AssertViolatesAsync(database, Wh(DefaultCompany, DefaultTransit, "CompanyWarehouse"), "23503", WhFk);
        // The warehouse type CHECK rejects claiming the real store type.
        await AssertViolatesAsync(database, Wh(DefaultCompany, DefaultStore, "BranchStore"), "23514", "CK_Warehouses_LocationType");
        await AssertViolatesAsync(database, Veh(DefaultCompany, DefaultTransit, "T1", locationType: "Transit"), "23514", "CK_Vehicles_LocationType");
    }

    [Fact]
    public async Task Insert_BranchWarehouseLocationUnderAVehicleRow_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, BranchA2, "BranchWarehouse", "WH2"));

        await AssertViolatesAsync(database, Veh(DefaultCompany, L1, "VAN1"), "23503", VehFk);
        await AssertViolatesAsync(database, Veh(DefaultCompany, L1, "VAN1", locationType: "BranchWarehouse"), "23514", "CK_Vehicles_LocationType");
    }

    [Fact]
    public async Task Insert_SubtypeOverAnotherCompanysLocation_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, OtherCompany, null, "CompanyWarehouse", "WH1"));
        await ExecuteAsync(database, Loc(L3, OtherCompany, null, "Vehicle", "VAN1"));

        await AssertViolatesAsync(database, Wh(DefaultCompany, L1), "23503", WhFk);
        await AssertViolatesAsync(database, Veh(DefaultCompany, L3, "VAN1"), "23503", VehFk);
    }

    [Fact]
    public async Task Insert_SecondSubtypeRowForTheSameLocation_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "CompanyWarehouse", "WH1"));
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));
        await ExecuteAsync(database, Wh(DefaultCompany, L1));
        await ExecuteAsync(database, Veh(DefaultCompany, L3, "VAN1"));

        await AssertViolatesAsync(database, Wh(DefaultCompany, L1), "23505", "IX_Warehouses_StockLocation");
        await AssertViolatesAsync(database, Veh(DefaultCompany, L3, "VAN2"), "23505", "IX_Vehicles_StockLocation");
    }

    [Fact]
    public async Task Update_LocationTypeWhileReferenced_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "CompanyWarehouse", "WH1"));
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));
        await ExecuteAsync(database, Wh(DefaultCompany, L1));
        await ExecuteAsync(database, Veh(DefaultCompany, L3, "VAN1"));

        await AssertViolatesAsync(database, $"""UPDATE "StockLocations" SET "LocationType" = 'Vehicle' WHERE "Id" = '{L1}'""", "23503", WhFk);
        await AssertViolatesAsync(database, $"""UPDATE "StockLocations" SET "LocationType" = 'CompanyWarehouse' WHERE "Id" = '{L3}'""", "23503", VehFk);
        // Deleting a referenced location is refused as well (Restrict).
        await AssertViolatesAsync(database, $"""DELETE FROM "StockLocations" WHERE "Id" = '{L1}'""", "23503", WhFk);
    }

    [Fact]
    public async Task Insert_VehicleAssignedToABranchOfAnotherCompany_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));

        await AssertViolatesAsync(database, Veh(DefaultCompany, L3, "VAN1", assigned: BranchB), "23503", "FK_Vehicles_Branches_CompanyId_AssignedBranchId");
        await ExecuteAsync(database, Veh(DefaultCompany, L3, "VAN1", assigned: null));
        await AssertViolatesAsync(database, $"""UPDATE "Vehicles" SET "AssignedBranchId" = '{BranchB}' WHERE "VehicleNumber" = 'VAN1'""", "23503", "FK_Vehicles_Branches_CompanyId_AssignedBranchId");
    }

    [Fact]
    public async Task Delete_BranchWithAnAssignedVehicle_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));
        await ExecuteAsync(database, Veh(DefaultCompany, L3, "VAN1", assigned: BranchA2));

        await AssertViolatesAsync(database, $"""DELETE FROM "Branches" WHERE "Id" = '{BranchA2}'""", "23503", "FK_Vehicles_Branches_CompanyId_AssignedBranchId");
    }

    [Fact]
    public async Task Insert_DuplicateVehicleNumberInACompany_IsRejectedButAnotherCompanyMayReuseIt()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "Vehicle", "VAN1"));
        await ExecuteAsync(database, Loc(L2, DefaultCompany, null, "Vehicle", "VAN2"));
        await ExecuteAsync(database, Loc("00000000-0000-0000-0000-0000000d0004", OtherCompany, null, "Vehicle", "VAN1"));
        await ExecuteAsync(database, Veh(DefaultCompany, L1, "VAN1"));

        await AssertViolatesAsync(database, Veh(DefaultCompany, L2, "VAN1"), "23505", "IX_Vehicles_CompanyId_VehicleNumber");
        await ExecuteAsync(database, Veh(OtherCompany, "00000000-0000-0000-0000-0000000d0004", "VAN1"));
    }

    [Fact]
    public async Task Insert_UnknownVehicleType_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));

        await AssertViolatesAsync(database, Veh(DefaultCompany, L3, "VAN1", type: "Bicycle"), "23514", "CK_Vehicles_VehicleType");
        await AssertViolatesAsync(database, Veh(DefaultCompany, L3, "VAN1", type: "van"), "23514", "CK_Vehicles_VehicleType");
        foreach (var type in new[] { "Van", "Truck", "Car", "Motorcycle", "ThreeWheeler", "Other" })
        {
            await ExecuteAsync(database, $"""DELETE FROM "Vehicles" """);
            await ExecuteAsync(database, Veh(DefaultCompany, L3, "VAN1", type: type));
        }
    }

    [Fact]
    public async Task Insert_BadLocationTypeStrings_AreRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "CompanyWarehouse", "WH1"));
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));

        await AssertViolatesAsync(database, Wh(DefaultCompany, L1, "Warehouse"), "23514", "CK_Warehouses_LocationType");
        await AssertViolatesAsync(database, Wh(DefaultCompany, L1, "companywarehouse"), "23514", "CK_Warehouses_LocationType");
        await AssertViolatesAsync(database, Veh(DefaultCompany, L3, "VAN1", locationType: "vehicle"), "23514", "CK_Vehicles_LocationType");
    }

    [Fact]
    public async Task Insert_EmptyGuidIds_AreRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "CompanyWarehouse", "WH1"));
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));
        const string zero = "00000000-0000-0000-0000-000000000000";

        await AssertViolatesAsync(database, Wh(DefaultCompany, L1, id: zero), "23514", "CK_Warehouses_Id_NotEmpty");
        await AssertViolatesAsync(database, Veh(DefaultCompany, L3, "VAN1", id: zero), "23514", "CK_Vehicles_Id_NotEmpty");
    }

    [Fact]
    public async Task Insert_OverlongVehicleNumber_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L3, DefaultCompany, null, "Vehicle", "VAN1"));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, Veh(DefaultCompany, L3, new string('X', 21))));
        Assert.Equal("22001", ex.SqlState);
    }

    [Fact]
    public async Task Insert_SubtypeForUnknownCompany_IsRejected()
    {
        await using var database = await SeedAsync();
        await ExecuteAsync(database, Loc(L1, DefaultCompany, null, "CompanyWarehouse", "WH1"));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, Wh(Guid.NewGuid().ToString(), L1)));
        Assert.Equal("23503", ex.SqlState);
    }
}
