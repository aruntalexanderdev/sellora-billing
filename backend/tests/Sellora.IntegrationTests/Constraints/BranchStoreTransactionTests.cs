using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>Phase 3 T3 (PostgreSQL half): the branch and its store are written together; a rejected branch leaves no branch and no store, and mirroring hits real rows.</summary>
[Collection(PostgresCollection.Name)]
public class BranchStoreTransactionTests(PostgresFixture postgres)
{
    private static readonly Guid UserId = new("00000000-0000-0000-0000-00000000000c");

    private static Task SeedUserAsync(TestDatabase database) => ExecuteAsync(database, $"""
        INSERT INTO "BusinessSettings" ("Id", "CompanyId", "BusinessName", "InvoicePrefix", "BaseCurrencyId") VALUES (gen_random_uuid(), '{DefaultCompany}', 'Acme', 'INV', '7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01');
        INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "IsActive", "IsSystem", "CreatedAt") VALUES (gen_random_uuid(), '0c0a1f00-0000-4000-8000-000000000001', 'Administrator', NULL, true, true, now());
        INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
          SELECT '{UserId}', now(), 'a@b.c', 'R', 'K', 'x', "Id", 'Active', 'admin' FROM "Roles" LIMIT 1;
        """);

    private static CreateBranchInput New(string code, string name) => new(code, name, null, null, null, null, null, null, null);

    [Fact]
    public async Task CreateAsync_CodeUsedByAnExistingLocation_RejectedWithNoBranchAndNoStore()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await ExecuteAsync(database, $"""INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{DefaultCompany}', NULL, 'Vehicle', 'VAN1', 'Van 1', true, now())""");
        await SeedUserAsync(database);
        await using var db = database.CreateDbContext();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new BranchService(db).CreateAsync(New("VAN1", "Van branch"), UserId));

        Assert.Equal("Code", ex.Field);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Branches" """));
        Assert.Equal(3L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockLocations" """)); // Main store, Transit, the van
    }

    [Fact]
    public async Task CreateRenameDeactivate_AgainstPostgres_KeepsTheStoreInStepAndSatisfiesTheMigrationVBlock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await SeedUserAsync(database);
        await using var db = database.CreateDbContext();
        var service = new BranchService(db);

        var branch = await service.CreateAsync(New("KAR", "Karur"), UserId);
        await service.UpdateAsync(branch.Id, new UpdateBranchInput("Karur East", null, null, null, null, null, null, null), UserId);
        await service.SetStatusAsync(branch.Id, false, UserId);

        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT COUNT(*) FROM "StockLocations" WHERE "BranchId" = '{branch.Id}' AND "LocationType" = 'BranchStore' AND "Code" = 'KAR' AND "Name" = 'Karur East' AND NOT "IsActive" """));
        Assert.Empty(await QueryAsync(database, """
            SELECT b."Id"::text FROM "Branches" b WHERE (SELECT COUNT(*) FROM "StockLocations" l WHERE l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore'
              AND l."CompanyId" = b."CompanyId" AND l."IsActive" = b."IsActive" AND l."Code" = b."Code" AND l."Name" = b."Name") <> 1
            """, r => r.GetString(0)));
    }
}
