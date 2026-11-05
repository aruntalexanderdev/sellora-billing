using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Pricing;

public class PricingServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SelloraDbContext(options);
    }

    private sealed record Fixture(SelloraDbContext Db, PricingService Service, ProductUnit Bag, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = NewDb();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var looseKg = TestProductUnits.Base(product, "Loose kg");
        var bag = TestProductUnits.Pack(product, TestUnits.Bag(), looseKg, 10m, "10 kg bag");

        db.AddRange(role, user, unit, tax, product);
        await db.SaveChangesAsync();

        return new Fixture(db, new PricingService(db), bag, user.Id);
    }

    [Fact]
    public async Task SetPriceAsync_FirstPrice_CreatesOneOpenRow()
    {
        var f = await SeedAsync();

        await f.Service.SetPriceAsync(f.Bag.Id, 620m, f.UserId);

        var prices = await f.Db.SellingPrices.Where(p => p.ProductUnitId == f.Bag.Id).ToListAsync();
        Assert.Single(prices);
        Assert.Equal(620m, prices[0].Price);
        Assert.Null(prices[0].EffectiveTo);
    }

    [Fact]
    public async Task SetPriceAsync_SecondPrice_ClosesPreviousAndOpensNew()
    {
        var f = await SeedAsync();
        await f.Service.SetPriceAsync(f.Bag.Id, 620m, f.UserId);

        await Task.Delay(5); // ensure a distinct timestamp
        await f.Service.SetPriceAsync(f.Bag.Id, 650m, f.UserId);

        var prices = await f.Db.SellingPrices.Where(p => p.ProductUnitId == f.Bag.Id).OrderBy(p => p.EffectiveFrom).ToListAsync();
        Assert.Equal(2, prices.Count);

        Assert.Equal(620m, prices[0].Price);
        Assert.NotNull(prices[0].EffectiveTo); // old price closed out, never overwritten

        Assert.Equal(650m, prices[1].Price);
        Assert.Null(prices[1].EffectiveTo); // new price is the open one
    }

    [Fact]
    public async Task SetPriceAsync_NegativePrice_Throws()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.SetPriceAsync(f.Bag.Id, -1m, f.UserId));
    }

    [Fact]
    public async Task SetPriceAsync_UnknownProductUnit_Throws()
    {
        var f = await SeedAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.SetPriceAsync(Guid.NewGuid(), 100m, f.UserId));

        Assert.Equal(("ProductUnitId", "Select a valid product unit."), (ex.Field, ex.Message));
        Assert.False(await f.Db.SellingPrices.AnyAsync());
    }

    [Fact]
    public async Task SetPriceAsync_InactiveProductUnit_RejectedOnProductUnitId()
    {
        var f = await SeedAsync();
        f.Bag.IsActive = false;
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.SetPriceAsync(f.Bag.Id, 620m, f.UserId));

        Assert.Equal(("ProductUnitId", "10 kg bag is no longer active and cannot be priced."), (ex.Field, ex.Message));
        Assert.False(await f.Db.SellingPrices.AnyAsync());
    }

    [Fact]
    public async Task SetPriceAsync_InactiveUnlabelledProductUnit_MessageUsesUnitName()
    {
        var f = await SeedAsync();
        f.Bag.IsActive = false;
        f.Bag.Label = null;
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.SetPriceAsync(f.Bag.Id, 620m, f.UserId));

        Assert.Equal("Bag is no longer active and cannot be priced.", ex.Message);
    }

    [Fact]
    public async Task SetPriceAsync_InactiveRowKeepsItsPriceHistory()
    {
        var f = await SeedAsync();
        await f.Service.SetPriceAsync(f.Bag.Id, 620m, f.UserId);
        f.Bag.IsActive = false;
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.SetPriceAsync(f.Bag.Id, 650m, f.UserId));

        var price = await f.Db.SellingPrices.SingleAsync();
        Assert.Equal((620m, (DateTimeOffset?)null), (price.Price, price.EffectiveTo));
    }
}
