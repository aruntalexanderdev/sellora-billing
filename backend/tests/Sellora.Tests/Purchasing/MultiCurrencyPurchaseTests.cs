using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Purchasing;

public class MultiCurrencyPurchaseTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SelloraDbContext(options);
    }

    private sealed record Fixture(
        SelloraDbContext Db, PurchaseService Purchases, ExchangeRateService Rates,
        Currency Inr, Currency Usd, Supplier UsdSupplier, Supplier InrSupplier,
        Product Product, ProductSellingMode Bag, ExchangeRate UsdRate, Guid UserId);

    // Zero-rated tax so the worked example (USD 1,000 @ 83.20 = INR 83,200) is exact.
    private static async Task<Fixture> SeedAsync(decimal taxRate = 0m)
    {
        var db = NewDb();

        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", Type = UnitType.Weight };
        var tax = new Tax { Id = Guid.NewGuid(), Name = $"GST {taxRate}%", RatePercent = taxRate };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var usdSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Global Ingredients Inc.", CurrencyId = usd.Id };
        var inrSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var bag = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "10 kg bag", ConversionFactorToBase = 10m };
        product.SellingModes.Add(bag);
        var usdRate = new ExchangeRate
        {
            Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = 83.20m,
            EffectiveDate = new DateOnly(2026, 9, 1), CreatedByUserId = user.Id,
        };

        db.AddRange(role, user, unit, tax, inr, usd, settings, usdSupplier, inrSupplier, product, usdRate);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        return new Fixture(db, new PurchaseService(db, rates), rates, inr, usd, usdSupplier, inrSupplier, product, bag, usdRate, user.Id);
    }

    private static CreatePurchaseInput Input(Fixture f, Supplier supplier, DateOnly date, decimal bags, decimal ratePerBag) =>
        new(supplier.Id, date, null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(f.Product.Id, f.Bag.Id, bags, ratePerBag, 0, 0)]);

    [Fact]
    public async Task CreatePurchaseAsync_ForeignSupplier_StoresOriginalAndBaseCurrencyValues()
    {
        var f = await SeedAsync();

        // 10 bags @ USD 100 = USD 1,000 → INR 83,200 at 83.20
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);

        Assert.Equal(f.Usd.Id, purchase.CurrencyId);
        Assert.Equal(83.20m, purchase.ExchangeRateToBase);
        Assert.Equal(1000m, purchase.OriginalGrandTotal);
        Assert.Equal(83200m, purchase.Subtotal);
        Assert.Equal(83200m, purchase.GrandTotal);

        var line = purchase.Lines.Single();
        Assert.Equal(8320m, line.Rate);
        Assert.Equal(83200m, line.TaxableValue);
        Assert.Equal(83200m, line.LineTotal);
    }

    [Fact]
    public async Task CreatePurchaseAsync_ForeignSupplier_StockCostIsInBaseCurrency()
    {
        var f = await SeedAsync();

        await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);

        // 10 bags × 10 kg = 100 kg; INR 83,200 / 100 kg = INR 832 per kg
        var batch = await f.Db.PurchaseBatches.SingleAsync();
        Assert.Equal(832m, batch.UnitCost);
        var stock = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(100m, stock.QuantityOnHand);
        Assert.Equal(832m, stock.AverageCost);
    }

    [Fact]
    public async Task CreatePurchaseAsync_ForeignSupplierWithTax_ConvertsTaxAndKeepsOriginalTotalInTransactionCurrency()
    {
        var f = await SeedAsync(taxRate: 5m);

        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);

        Assert.Equal(1050m, purchase.OriginalGrandTotal); // USD 1,000 + 5% tax
        Assert.Equal(83200m, purchase.Subtotal);
        Assert.Equal(4160m, purchase.TaxTotal);
        Assert.Equal(87360m, purchase.GrandTotal); // 1,050 × 83.20
    }

    [Fact]
    public async Task CreatePurchaseAsync_ForeignSupplier_RoundsInvoiceFiguresBeforeConverting()
    {
        var f = await SeedAsync(taxRate: 5m);
        f.Db.ExchangeRates.Add(new ExchangeRate
        {
            Id = Guid.NewGuid(), CurrencyId = f.Usd.Id, RateToBase = 95.97m,
            EffectiveDate = new DateOnly(2026, 9, 10), CreatedByUserId = f.UserId,
        });
        await f.Db.SaveChangesAsync();

        // 1 × USD 4.38: tax 0.219 is invoiced as USD 0.22, so the invoice total is USD 4.60.
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 18), 1, 4.38m), f.UserId);

        Assert.Equal(4.60m, purchase.OriginalGrandTotal);
        Assert.Equal(420.35m, purchase.Subtotal);   // 4.38 × 95.97
        Assert.Equal(21.11m, purchase.TaxTotal);    // 0.22 × 95.97
        Assert.Equal(441.46m, purchase.GrandTotal); // 4.60 × 95.97, matching what the invoice shows
        Assert.Equal(purchase.Subtotal + purchase.TaxTotal, purchase.Lines.Single().LineTotal);
    }

    [Fact]
    public async Task CreatePurchaseAsync_BaseCurrencySupplier_UsesRateOfOne()
    {
        var f = await SeedAsync();

        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 580m), f.UserId);

        Assert.Equal(f.Inr.Id, purchase.CurrencyId);
        Assert.Equal(1m, purchase.ExchangeRateToBase);
        Assert.Equal(5800m, purchase.GrandTotal);
        Assert.Equal(5800m, purchase.OriginalGrandTotal);
    }

    [Fact]
    public async Task ChangingExchangeRateMaster_DoesNotAlterAlreadySavedPurchase()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);

        f.UsdRate.RateToBase = 99m;
        f.Db.ExchangeRates.Add(new ExchangeRate
        {
            Id = Guid.NewGuid(), CurrencyId = f.Usd.Id, RateToBase = 120m,
            EffectiveDate = new DateOnly(2026, 9, 4), CreatedByUserId = f.UserId,
        });
        await f.Db.SaveChangesAsync();

        var reloaded = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.Id == purchase.Id);
        Assert.Equal(83.20m, reloaded.ExchangeRateToBase);
        Assert.Equal(83200m, reloaded.GrandTotal);
        Assert.Equal(1000m, reloaded.OriginalGrandTotal);
    }

    [Fact]
    public async Task CreatePurchaseAsync_ForeignSupplierChangesCurrencyLater_SavedPurchaseKeepsItsOwnCurrency()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);

        f.UsdSupplier.CurrencyId = f.Inr.Id;
        await f.Db.SaveChangesAsync();

        var reloaded = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.Id == purchase.Id);
        Assert.Equal(f.Usd.Id, reloaded.CurrencyId);
    }

    [Fact]
    public async Task CreatePurchaseAsync_SupplierCurrencyInactive_IsRejected()
    {
        var f = await SeedAsync();
        f.Usd.IsActive = false;
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId));
    }

    [Fact]
    public async Task InactiveCurrency_DoesNotAffectHistoricalPurchase()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);

        f.Usd.IsActive = false;
        await f.Db.SaveChangesAsync();

        var reloaded = await f.Db.PurchaseOrders.Include(p => p.Currency).AsNoTracking().SingleAsync(p => p.Id == purchase.Id);
        Assert.Equal("USD", reloaded.Currency.Code);
        Assert.Equal(1000m, reloaded.OriginalGrandTotal);
    }

    [Fact]
    public async Task ResolveRateToBaseAsync_PicksLatestRateEffectiveOnOrBeforeDate()
    {
        var f = await SeedAsync();
        f.Db.ExchangeRates.Add(new ExchangeRate
        {
            Id = Guid.NewGuid(), CurrencyId = f.Usd.Id, RateToBase = 85m,
            EffectiveDate = new DateOnly(2026, 9, 10), CreatedByUserId = f.UserId,
        });
        await f.Db.SaveChangesAsync();

        Assert.Equal(83.20m, await f.Rates.ResolveRateToBaseAsync(f.Usd.Id, new DateOnly(2026, 9, 1)));
        Assert.Equal(83.20m, await f.Rates.ResolveRateToBaseAsync(f.Usd.Id, new DateOnly(2026, 9, 9)));
        Assert.Equal(85m, await f.Rates.ResolveRateToBaseAsync(f.Usd.Id, new DateOnly(2026, 9, 10)));
        Assert.Equal(85m, await f.Rates.ResolveRateToBaseAsync(f.Usd.Id, new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public async Task ResolveRateToBaseAsync_NoRateOnOrBeforeDate_Throws()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Rates.ResolveRateToBaseAsync(f.Usd.Id, new DateOnly(2026, 8, 31)));
    }

    [Fact]
    public async Task CreatePurchaseAsync_ForeignSupplierWithNoRate_IsRejectedAndSavesNothing()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 8, 1), 10, 100m), f.UserId));

        Assert.Empty(await f.Db.PurchaseOrders.ToListAsync());
        Assert.Empty(await f.Db.StockItems.ToListAsync());
    }
}
