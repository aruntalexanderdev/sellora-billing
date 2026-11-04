using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

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

    private sealed record Fixture(SelloraDbContext Db, PricingService Service, ProductSellingMode Mode, Guid UserId);

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
        var mode = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Product = product, Label = "10 kg bag", ConversionFactorToBase = 10m };

        db.AddRange(role, user, unit, tax, product, mode);
        await db.SaveChangesAsync();

        return new Fixture(db, new PricingService(db), mode, user.Id);
    }

    [Fact]
    public async Task SetPriceAsync_FirstPrice_CreatesOneOpenRow()
    {
        var f = await SeedAsync();

        await f.Service.SetPriceAsync(f.Mode.Id, 620m, f.UserId);

        var prices = await f.Db.SellingPrices.Where(p => p.ProductSellingModeId == f.Mode.Id).ToListAsync();
        Assert.Single(prices);
        Assert.Equal(620m, prices[0].Price);
        Assert.Null(prices[0].EffectiveTo);
    }

    [Fact]
    public async Task SetPriceAsync_SecondPrice_ClosesPreviousAndOpensNew()
    {
        var f = await SeedAsync();
        await f.Service.SetPriceAsync(f.Mode.Id, 620m, f.UserId);

        await Task.Delay(5); // ensure a distinct timestamp
        await f.Service.SetPriceAsync(f.Mode.Id, 650m, f.UserId);

        var prices = await f.Db.SellingPrices.Where(p => p.ProductSellingModeId == f.Mode.Id).OrderBy(p => p.EffectiveFrom).ToListAsync();
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

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.SetPriceAsync(f.Mode.Id, -1m, f.UserId));
    }

    [Fact]
    public async Task SetPriceAsync_UnknownSellingMode_Throws()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.SetPriceAsync(Guid.NewGuid(), 100m, f.UserId));
    }
}
