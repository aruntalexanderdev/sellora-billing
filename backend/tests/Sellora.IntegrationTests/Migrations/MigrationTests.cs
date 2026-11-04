using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Migrations;

[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MigrateAsync_EmptyDatabase_AppliesAllMigrationsAndModelMatchesSnapshot()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using var db = database.CreateDbContext();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(),
            "The entity model differs from the migrations' model snapshot — a migration is missing.");
    }

    [Fact]
    public async Task SeedAsync_RunTwiceOnMigratedDatabase_DoesNotFailOrDuplicateRows()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var hasher = new PasswordHasher();

        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, hasher);
        }
        var afterFirst = await CountRowsAsync(database);

        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, hasher);
        }
        var afterSecond = await CountRowsAsync(database);

        Assert.True(afterFirst.Units > 0 && afterFirst.Products > 0, "The first seed run should insert units and products.");
        Assert.Equal(afterFirst, afterSecond);
    }

    private sealed record RowCounts(
        int Units, int Products, int SellingModes, int Taxes, int Suppliers, int Customers, int Users, int Permissions,
        int Roles, int RolePermissions, int Currencies, int ExchangeRates, int BusinessSettings, int FinancialAccounts,
        int ExpenseCategories);

    private static async Task<RowCounts> CountRowsAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return new RowCounts(
            await db.Units.CountAsync(), await db.Products.CountAsync(), await db.ProductSellingModes.CountAsync(),
            await db.Taxes.CountAsync(), await db.Suppliers.CountAsync(), await db.Customers.CountAsync(),
            await db.Users.CountAsync(), await db.Permissions.CountAsync(),
            await db.Roles.CountAsync(), await db.RolePermissions.CountAsync(), await db.Currencies.CountAsync(),
            await db.ExchangeRates.CountAsync(), await db.BusinessSettings.CountAsync(), await db.FinancialAccounts.CountAsync(),
            await db.ExpenseCategories.CountAsync());
    }
}
