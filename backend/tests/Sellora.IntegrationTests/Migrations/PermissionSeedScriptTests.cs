using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>Phase 4 O-4 on PostgreSQL — scripts/sql/seed-permissions.sql runs cleanly, twice, leaves operator edits alone, adds only missing codes, and its result equals PermissionCatalog (V5 stays empty).</summary>
[Collection(PostgresCollection.Name)]
public class PermissionSeedScriptTests(PostgresFixture postgres)
{
    private static string Script => File.ReadAllText(PermissionScriptPath());

    private static string PermissionScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "sql", "seed-permissions.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("scripts/sql/seed-permissions.sql not found above " + AppContext.BaseDirectory);
    }

    private static string V5 => $"""
        SELECT c FROM unnest(ARRAY[{string.Join(",", PermissionCatalog.All.Select(p => $"'{p.Code.Replace("'", "''")}'"))}]) c WHERE c NOT IN (SELECT "Code" FROM "Permissions")
        """;

    private static async Task<List<string>> RowsAsync(TestDatabase database) =>
        await QueryAsync(database, """SELECT "Code" || '|' || "Module" || '|' || "Action" || '|' || "Description" FROM "Permissions" ORDER BY "Code" """, r => r.GetString(0));

    private static List<string> CatalogueRows() =>
        PermissionCatalog.All.Select(p => $"{p.Code}|{p.Module}|{p.Action}|{p.Description}").OrderBy(r => r, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Script_OnAnEmptyPermissionsTable_InsertsExactlyTheCatalogue()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" """));

        await ExecuteAsync(database, Script);

        Assert.Equal(CatalogueRows(), (await RowsAsync(database)).OrderBy(r => r, StringComparer.Ordinal).ToList());
        Assert.Empty(await QueryAsync(database, V5, r => r.GetString(0)));
        Assert.Equal(PermissionCatalog.All.Count, await ScalarAsync<long>(database, """SELECT COUNT(DISTINCT "Id") FROM "Permissions" """));
    }

    [Fact]
    public async Task Script_RunTwice_SecondRunChangesNothing_IdsStable()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await ExecuteAsync(database, Script);
        var ids = await QueryAsync(database, """SELECT "Id"::text || ':' || "Code" FROM "Permissions" ORDER BY "Code" """, r => r.GetString(0));

        await ExecuteAsync(database, Script);

        Assert.Equal(ids, await QueryAsync(database, """SELECT "Id"::text || ':' || "Code" FROM "Permissions" ORDER BY "Code" """, r => r.GetString(0)));
        Assert.Equal(PermissionCatalog.All.Count, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" """));
    }

    [Fact]
    public async Task Script_WhenOnlyPhase4CodesAreMissing_AddsJustThoseAndKeepsEditsAndRoleGrants()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await ExecuteAsync(database, Script);
        await ExecuteAsync(database, """DELETE FROM "Permissions" WHERE "Code" LIKE 'Company.Warehouses.%' OR "Code" LIKE 'Company.Vehicles.%'""");
        await ExecuteAsync(database, """UPDATE "Permissions" SET "Description" = 'operator wording' WHERE "Code" = 'Users.View'""");
        Assert.Equal(8, (await QueryAsync(database, V5, r => r.GetString(0))).Count);
        var untouched = await ScalarAsync<string>(database, """SELECT "Id"::text FROM "Permissions" WHERE "Code" = 'Users.View'""");

        await ExecuteAsync(database, Script);

        Assert.Empty(await QueryAsync(database, V5, r => r.GetString(0)));
        Assert.Equal("operator wording", await ScalarAsync<string>(database, """SELECT "Description" FROM "Permissions" WHERE "Code" = 'Users.View'"""));
        Assert.Equal(untouched, await ScalarAsync<string>(database, """SELECT "Id"::text FROM "Permissions" WHERE "Code" = 'Users.View'"""));
        Assert.Equal(PermissionCatalog.All.Count, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Permissions" """));
    }

    [Fact]
    public async Task Script_GrantsNothingToRoles()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var before = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" """);

        await ExecuteAsync(database, Script);

        Assert.Equal(before, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "RolePermissions" """));
    }
}
