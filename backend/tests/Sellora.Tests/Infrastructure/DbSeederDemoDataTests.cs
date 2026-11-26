using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Infrastructure;

/// <summary>Database:SeedDemoData (the <c>seedDemoData</c> argument): with false the seeder keeps a database that was emptied on
/// purpose (scripts/sql/dev-reset-fresh-setup.sql) empty of demo masters, while system data, the walk-in customer and the Main Cash
/// account are still seeded. The default (true) is unchanged.</summary>
public class DbSeederDemoDataTests
{
    // INR is inserted by the AddMultiCurrency migration on Postgres; InMemory has no migrations.
    private static SelloraDbContext NewDb()
    {
        var db = TestDb.NewInMemory();
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task SeedAsync_ByDefault_SeedsTheDemoMasters()
    {
        var db = NewDb();

        await DbSeeder.SeedAsync(db, new PasswordHasher());

        Assert.NotEmpty(await db.Units.ToListAsync());
        Assert.NotEmpty(await db.Taxes.ToListAsync());
        Assert.NotEmpty(await db.Suppliers.ToListAsync());
        Assert.NotEmpty(await db.ProductCategories.ToListAsync());
        Assert.NotEmpty(await db.Products.ToListAsync());
        Assert.NotEmpty(await db.ExpenseCategories.ToListAsync());
        Assert.Contains(await db.Customers.ToListAsync(), c => !c.IsWalkIn);
    }

    [Fact]
    public async Task SeedAsync_DemoDataOff_SeedsSystemDataTheWalkInCustomerAndMainCashOnly()
    {
        var db = NewDb();

        await DbSeeder.SeedAsync(db, new PasswordHasher(), seedDemoData: false);

        Assert.Empty(await db.Units.ToListAsync());
        Assert.Empty(await db.Taxes.ToListAsync());
        Assert.Empty(await db.Suppliers.ToListAsync());
        Assert.Empty(await db.ProductCategories.ToListAsync());
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Empty(await db.ProductUnits.ToListAsync());
        Assert.Empty(await db.ExpenseCategories.ToListAsync());

        var customer = Assert.Single(await db.Customers.ToListAsync());
        Assert.True(customer.IsWalkIn);
        var account = Assert.Single(await db.FinancialAccounts.ToListAsync());
        Assert.Equal("Main Cash", account.Name);
        Assert.Equal(FinancialAccountKind.Cash, account.Kind);

        // System data does not depend on the switch.
        Assert.Single(await db.Users.Where(u => u.Email == DbSeeder.SeedAdminEmail).ToListAsync());
        Assert.Equal(PermissionCatalog.All.Count, await db.Permissions.CountAsync());
        Assert.Single(await db.BusinessSettings.ToListAsync());
        Assert.Equal(7, await db.Countries.CountAsync());
        Assert.Contains(await db.Currencies.ToListAsync(), c => c.Code == "USD");
    }

    [Fact]
    public async Task SeedAsync_DemoDataOff_RunTwice_DoesNotDuplicateTheWalkInCustomerOrMainCash()
    {
        var db = NewDb();
        await DbSeeder.SeedAsync(db, new PasswordHasher(), seedDemoData: false);

        await DbSeeder.SeedAsync(db, new PasswordHasher(), seedDemoData: false);

        Assert.Single(await db.Customers.ToListAsync());
        Assert.Single(await db.FinancialAccounts.ToListAsync());
        Assert.Empty(await db.Units.ToListAsync());
    }

    [Fact]
    public async Task SeedAsync_DemoDataOff_OnASeededDatabase_LeavesExistingMastersUntouched()
    {
        var db = NewDb();
        await DbSeeder.SeedAsync(db, new PasswordHasher());
        var before = await CountsAsync(db);

        await DbSeeder.SeedAsync(db, new PasswordHasher(), seedDemoData: false);

        Assert.Equal(before, await CountsAsync(db));
    }

    /// <summary>The scenario the switch exists for: the fresh-setup reset removed units, taxes, products, categories, expense
    /// categories and accounts (customers and suppliers are kept by default); the next startup seed must not put them back.</summary>
    [Fact]
    public async Task SeedAsync_DemoDataOff_AfterAFreshSetupReset_DoesNotBringTheRemovedMastersBack()
    {
        var db = NewDb();
        await DbSeeder.SeedAsync(db, new PasswordHasher());
        var suppliers = await db.Suppliers.CountAsync();
        var customers = await db.Customers.CountAsync();
        db.ProductUnits.RemoveRange(await db.ProductUnits.ToListAsync());
        db.Products.RemoveRange(await db.Products.ToListAsync());
        db.ProductCategories.RemoveRange(await db.ProductCategories.ToListAsync());
        db.Units.RemoveRange(await db.Units.ToListAsync());
        // The reset removes the tax profiles with the taxes (the mirror tax of every profile hangs on them).
        db.TaxAssignments.RemoveRange(await db.TaxAssignments.ToListAsync());
        db.TaxProfileVersionComponents.RemoveRange(await db.TaxProfileVersionComponents.ToListAsync());
        db.TaxProfileVersions.RemoveRange(await db.TaxProfileVersions.ToListAsync());
        db.TaxProfiles.RemoveRange(await db.TaxProfiles.ToListAsync());
        db.TaxComponents.RemoveRange(await db.TaxComponents.ToListAsync());
        db.Taxes.RemoveRange(await db.Taxes.ToListAsync());
        db.ExpenseCategories.RemoveRange(await db.ExpenseCategories.ToListAsync());
        db.FinancialAccounts.RemoveRange(await db.FinancialAccounts.ToListAsync());
        await db.SaveChangesAsync();

        await DbSeeder.SeedAsync(db, new PasswordHasher(), seedDemoData: false);

        Assert.Empty(await db.Units.ToListAsync());
        Assert.Empty(await db.Taxes.ToListAsync());
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Empty(await db.ProductCategories.ToListAsync());
        Assert.Empty(await db.ExpenseCategories.ToListAsync());
        Assert.Equal("Main Cash", Assert.Single(await db.FinancialAccounts.ToListAsync()).Name);
        Assert.Equal(suppliers, await db.Suppliers.CountAsync());
        Assert.Equal(customers, await db.Customers.CountAsync());
    }

    private static async Task<(int, int, int, int, int, int, int, int, int)> CountsAsync(SelloraDbContext db) =>
        (await db.Units.CountAsync(), await db.Taxes.CountAsync(), await db.Suppliers.CountAsync(), await db.ProductCategories.CountAsync(),
         await db.Products.CountAsync(), await db.ProductUnits.CountAsync(), await db.Customers.CountAsync(),
         await db.ExpenseCategories.CountAsync(), await db.FinancialAccounts.CountAsync());
}
