using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Geography;

/// <summary>DbSeeder seeds the Country/State masters for dev/InMemory (migrations do not run there), idempotently and without touching admin edits.</summary>
public class DbSeederGeographyTests
{
    // INR is inserted by the AddMultiCurrency migration on Postgres; InMemory has no migrations.
    private static SelloraDbContext NewDb()
    {
        var db = TestDb.NewInMemory();
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "\u20B9" });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task SeedAsync_EmptyDatabase_SeedsSevenCountriesAnd85ActiveSystemStates_WithDeterministicIds()
    {
        var db = NewDb();

        await DbSeeder.SeedAsync(db, new PasswordHasher());

        var countries = await db.Countries.ToListAsync();
        var states = await db.States.ToListAsync();
        Assert.Equal(7, countries.Count);
        Assert.Equal(85, states.Count);
        Assert.All(countries, c => { Assert.True(c.IsActive); Assert.True(c.IsSystem); });
        Assert.All(states, s => { Assert.True(s.IsActive); Assert.True(s.IsSystem); });
        Assert.All(GeoSeedData.Countries, c => Assert.Contains(countries, x => x.Id == GeoSeedData.CountryId(c.Code) && x.Name == c.Name && x.Iso3Code == c.Iso3Code));
        var kerala = states.Single(s => s.Id == GeoSeedData.StateId("IN", "KL"));
        Assert.Equal("32", kerala.GstStateCode);
        Assert.Equal(GeoSeedData.CountryId("IN"), kerala.CountryId);
    }

    [Fact]
    public async Task SeedAsync_RunTwice_DoesNotDuplicateOrChangeRows()
    {
        var db = NewDb();
        await DbSeeder.SeedAsync(db, new PasswordHasher());

        await DbSeeder.SeedAsync(db, new PasswordHasher());

        Assert.Equal(7, await db.Countries.CountAsync());
        Assert.Equal(85, await db.States.CountAsync());
    }

    [Fact]
    public async Task SeedAsync_AdminDeactivatedOrRenamedRows_ArePreserved()
    {
        var db = NewDb();
        await DbSeeder.SeedAsync(db, new PasswordHasher());
        var oman = await db.Countries.SingleAsync(c => c.Code == "OM");
        oman.IsActive = false;
        var kerala = await db.States.SingleAsync(s => s.Id == GeoSeedData.StateId("IN", "KL"));
        kerala.Name = "Kerala Edited";
        kerala.IsActive = false;
        await db.SaveChangesAsync();

        await DbSeeder.SeedAsync(db, new PasswordHasher());

        Assert.False((await db.Countries.SingleAsync(c => c.Code == "OM")).IsActive);
        var again = await db.States.SingleAsync(s => s.Id == GeoSeedData.StateId("IN", "KL"));
        Assert.Equal("Kerala Edited", again.Name);
        Assert.False(again.IsActive);
        Assert.Equal(85, await db.States.CountAsync());
    }

    [Fact]
    public async Task SeedAsync_RowsAlreadyPresentFromTheMigration_AreReusedByCode()
    {
        var db = NewDb();
        var india = new Country { Id = GeoSeedData.CountryId("IN"), Code = "IN", Iso3Code = "IND", Name = "India", IsActive = true, IsSystem = true };
        db.Countries.Add(india);
        await db.SaveChangesAsync();

        await DbSeeder.SeedAsync(db, new PasswordHasher());

        Assert.Equal(1, await db.Countries.CountAsync(c => c.Code == "IN"));
        Assert.Equal(36, await db.States.CountAsync(s => s.CountryId == india.Id));
    }

    [Fact]
    public async Task SeedAsync_DemoData_LinksOnlyIndianRowsWithEvidence_AndLeavesTheForeignSupplierAndWalkInUnlinked()
    {
        var db = NewDb();

        await DbSeeder.SeedAsync(db, new PasswordHasher());

        // design section 2: every stored link must be internally consistent; the USD supplier and the walk-in customer stay NULL
        foreach (var s in await db.Suppliers.Where(s => s.StateId != null).ToListAsync())
            Assert.Equal((await db.States.SingleAsync(x => x.Id == s.StateId)).CountryId, s.CountryId);
        Assert.All(await db.Suppliers.Where(s => s.Name == "Global Ingredients Inc.").ToListAsync(), s => { Assert.Null(s.StateId); Assert.Null(s.CountryId); });
        Assert.All(await db.Customers.Where(c => c.IsWalkIn).ToListAsync(), c => { Assert.Null(c.StateId); Assert.Null(c.CountryId); });
        Assert.All(await db.Customers.Where(c => c.StateId != null).ToListAsync(), c => Assert.Equal(GeoSeedData.CountryId("IN"), c.CountryId));
    }
}
