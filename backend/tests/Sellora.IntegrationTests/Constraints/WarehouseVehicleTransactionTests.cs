using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Phase 4 T2/T3 (PostgreSQL half): the services write location + subtype in one transaction, a failed subtype insert leaves no location,
/// rejected requests leave nothing behind, mirroring and the migration V-block hold on real rows, and the usage hook gates against a real database.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WarehouseVehicleTransactionTests(PostgresFixture postgres)
{
    private static readonly Guid UserId = new("00000000-0000-0000-0000-00000000000c");

    private static Task SeedUserAsync(TestDatabase database) => ExecuteAsync(database, $"""
        INSERT INTO "BusinessSettings" ("Id", "CompanyId", "BusinessName", "InvoicePrefix", "BaseCurrencyId") VALUES (gen_random_uuid(), '{DefaultCompany}', 'Acme', 'INV', '7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01');
        INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES (gen_random_uuid(), 'Administrator', NULL, true, true);
        INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
          SELECT '{UserId}', now(), 'a@b.c', 'R', 'K', 'x', "Id", 'Active', 'admin' FROM "Roles" LIMIT 1;
        """);

    private sealed class InUse : IStockLocationUsage
    {
        public Task<StockLocationUsage> GetAsync(Guid stockLocationId, CancellationToken ct = default) => Task.FromResult(new StockLocationUsage(true, false));
    }

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        Assert.Empty(await QueryAsync(database, """
            SELECT l."Id"::text FROM "StockLocations" l WHERE l."LocationType" IN ('CompanyWarehouse', 'BranchWarehouse')
              AND (SELECT COUNT(*) FROM "Warehouses" w WHERE w."StockLocationId" = l."Id") <> 1
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT l."Id"::text FROM "StockLocations" l WHERE l."LocationType" = 'Vehicle' AND (SELECT COUNT(*) FROM "Vehicles" v WHERE v."StockLocationId" = l."Id") <> 1
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT v."Id"::text FROM "Vehicles" v JOIN "StockLocations" l ON l."Id" = v."StockLocationId"
            WHERE l."Code" <> v."VehicleNumber" OR l."Name" <> v."VehicleName" OR l."IsActive" <> v."IsActive" OR l."BranchId" IS NOT NULL
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, """
            SELECT v."Id"::text FROM "Vehicles" v JOIN "Branches" b ON b."Id" = v."AssignedBranchId" WHERE b."CompanyId" <> v."CompanyId"
            """, r => r.GetString(0)));
    }

    [Fact]
    public async Task CreateUpdateDeactivate_AgainstPostgres_KeepsLocationAndSubtypeInStep()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedUserAsync(database);
        await using var db = database.CreateDbContext();
        var branch = await new BranchService(db).CreateAsync(new CreateBranchInput("BLR", "Bengaluru", null, null, null, null, null, null, null), UserId);
        var warehouses = new WarehouseService(db, new StockLocationUsageService());
        var vehicles = new VehicleService(db, new StockLocationUsageService());

        var company = await warehouses.CreateAsync(new("CW1", "Central", null), UserId);
        var branchWh = await warehouses.CreateAsync(new("BW1", "Bengaluru back", branch.Id), UserId);
        var van = await vehicles.CreateAsync(new("tn 01 ab 1", "Van one", VehicleType.Van, branch.Id), UserId);
        await warehouses.UpdateAsync(branchWh.Id, new("Bengaluru rear"), UserId);
        await vehicles.UpdateAsync(van.Id, new("Van uno", VehicleType.Truck, null), UserId);
        await warehouses.SetStatusAsync(company.Id, false, UserId);
        await vehicles.SetStatusAsync(van.Id, false, UserId);

        await AssertVBlockAsync(database);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" WHERE "Code" = 'TN-01-AB-1' AND "Name" = 'Van uno' AND NOT "IsActive" AND "BranchId" IS NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockLocations" WHERE "Code" = 'BW1' AND "Name" = 'Bengaluru rear' AND "BranchId" = '{branch.Id}' AND "LocationType" = 'BranchWarehouse' AND "IsActive" """));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Warehouses" w JOIN "StockLocations" l ON l."Id" = w."StockLocationId" WHERE l."Code" = 'CW1' AND NOT l."IsActive" """));
        Assert.Equal(3L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" WHERE "LocationType" IN ('CompanyWarehouse', 'BranchWarehouse', 'Vehicle')"""));
    }

    [Fact]
    public async Task SaveChanges_LocationWithAWrongTypedSubtypeRow_FailsAndLeavesNoLocation()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var location = new StockLocation { Id = Guid.NewGuid(), CompanyId = Guid.Parse(DefaultCompany), LocationType = StockLocationType.Vehicle, Code = "BAD1", Name = "Bad", IsActive = true };
        // A warehouse row claiming to be a company warehouse over a Vehicle location: the type-matching FK must reject the unit of work as a whole.
        var warehouse = new Warehouse { Id = Guid.NewGuid(), CompanyId = location.CompanyId, StockLocationId = location.Id, LocationType = StockLocationType.CompanyWarehouse };
        db.StockLocations.Add(location);
        db.Warehouses.Add(warehouse);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" WHERE "Code" = 'BAD1'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Warehouses" """));
    }

    [Fact]
    public async Task CreateAsync_RejectedRequests_LeaveNoLocationOrSubtypeBehind()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedUserAsync(database);
        await using var db = database.CreateDbContext();
        var warehouses = new WarehouseService(db, new StockLocationUsageService());
        var vehicles = new VehicleService(db, new StockLocationUsageService());
        await warehouses.CreateAsync(new("CW1", "Central", null), UserId);
        var locations = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """);

        await Assert.ThrowsAsync<DomainValidationException>(() => warehouses.CreateAsync(new("MAIN", "Other", null), UserId));      // branch code
        await Assert.ThrowsAsync<DomainValidationException>(() => warehouses.CreateAsync(new("TRANSIT", "Other", null), UserId));   // transit
        await Assert.ThrowsAsync<DomainValidationException>(() => warehouses.CreateAsync(new("CW2", "central", null), UserId));     // name
        await Assert.ThrowsAsync<DomainValidationException>(() => vehicles.CreateAsync(new("CW1", "Van", VehicleType.Van, null), UserId)); // warehouse code
        await Assert.ThrowsAsync<DomainValidationException>(() => vehicles.CreateAsync(new("V1", "Van", VehicleType.Van, Guid.NewGuid()), UserId)); // branch

        Assert.Equal(locations, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Warehouses" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Vehicles" """));
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task UsageGate_InUseLocation_RefusesDeactivationAndReassignment_AgainstPostgresRows()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedUserAsync(database);
        await using var db = database.CreateDbContext();
        var branch = await new BranchService(db).CreateAsync(new CreateBranchInput("BLR", "Bengaluru", null, null, null, null, null, null, null), UserId);
        var warehouse = await new WarehouseService(db, new StockLocationUsageService()).CreateAsync(new("CW1", "Central", null), UserId);
        var van = await new VehicleService(db, new StockLocationUsageService()).CreateAsync(new("V1", "Van", VehicleType.Van, null), UserId);

        var whEx = await Assert.ThrowsAsync<DomainValidationException>(() => new WarehouseService(db, new InUse()).SetStatusAsync(warehouse.Id, false, UserId));
        var vEx = await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, new InUse()).SetStatusAsync(van.Id, false, UserId));
        var reEx = await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, new InUse()).UpdateAsync(van.Id, new("Van", VehicleType.Van, branch.Id), UserId));

        Assert.Equal(("IsActive", "IsActive", "AssignedBranchId"), (whEx.Field, vEx.Field, reEx.Field));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" WHERE "LocationType" IN ('CompanyWarehouse','Vehicle') AND NOT "IsActive" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Vehicles" WHERE "AssignedBranchId" IS NOT NULL"""));
        // The transaction was rolled back: a later write on the same context still works.
        await new VehicleService(db, new StockLocationUsageService()).UpdateAsync(van.Id, new("Van", VehicleType.Van, branch.Id), UserId);
        await AssertVBlockAsync(database);
    }

    [Fact]
    public async Task SetStatusAsync_BranchDeactivationRule_AgainstPostgres()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedUserAsync(database);
        await using var db = database.CreateDbContext();
        var branches = new BranchService(db);
        var branch = await branches.CreateAsync(new CreateBranchInput("BLR", "Bengaluru", null, null, null, null, null, null, null), UserId);
        var warehouse = await new WarehouseService(db, new StockLocationUsageService()).CreateAsync(new("BW1", "Back", branch.Id), UserId);

        await Assert.ThrowsAsync<DomainValidationException>(() => branches.SetStatusAsync(branch.Id, false, UserId));
        await new WarehouseService(db, new StockLocationUsageService()).SetStatusAsync(warehouse.Id, false, UserId);
        await branches.SetStatusAsync(branch.Id, false, UserId);

        Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "Branches" WHERE "Id" = '{branch.Id}' AND "IsActive" """));
    }
}
