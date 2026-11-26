using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Purchasing;

public class MultiCurrencyPurchaseTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return Sellora.Tests.TestData.TestCompany.NewDb(options);
    }

    private sealed record Fixture(
        SelloraDbContext Db, PurchaseService Purchases, ExchangeRateService Rates,
        Currency Inr, Currency Usd, Supplier UsdSupplier, Supplier InrSupplier,
        Product Product, ProductUnit Bag, ExchangeRate UsdRate, Guid UserId);

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
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
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
        var looseKg = TestProductUnits.Base(product, "Loose kg");
        var bag = TestProductUnits.Pack(product, TestUnits.Bag(), looseKg, 10m, "10 kg bag");
        var usdRate = new ExchangeRate
        {
            Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = 83.20m,
            EffectiveDate = new DateOnly(2026, 9, 1), CreatedByUserId = user.Id,
        };

        db.AddRange(role, user, unit, tax, inr, usd, settings, usdSupplier, inrSupplier, product, usdRate);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        await TaxFixtures.EnablePurchasesAsync(db); // TP-3: purchases need a country, an own state and a purchase profile per product
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

        // 1 × USD 4.38: tax 0.219 is invoiced as USD 0.22 (TP-3: CGST 0.11 + SGST 0.11), so the invoice total is USD 4.60.
        // Each tax row is converted on its own (0.11 × 95.97 = 10.56 twice), so the base tax is one paisa above 0.22 × 95.97.
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 18), 1, 4.38m), f.UserId);

        Assert.Equal(4.60m, purchase.OriginalGrandTotal);
        Assert.Equal(420.35m, purchase.Subtotal);   // 4.38 × 95.97
        Assert.Equal(21.12m, purchase.TaxTotal);    // 2 × (0.11 × 95.97): TP-3 converts each row
        Assert.Equal(441.47m, purchase.GrandTotal); // Σ line totals
        Assert.Equal(purchase.Subtotal + purchase.TaxTotal, purchase.Lines.Single().LineTotal);
    }

    private static CreatePurchaseInput InputPaid(Fixture f, Supplier supplier, DateOnly date, decimal bags, decimal ratePerBag, decimal paid) =>
        new(supplier.Id, date, null, null, PaymentMethod.Cash, paid,
            [new PurchaseLineInput(f.Product.Id, f.Bag.Id, bags, ratePerBag, 0, 0)]);

    [Fact]
    public async Task CreatePurchaseAsync_ForeignPartialPayment_RecordsBothCurrenciesAndBothDues()
    {
        var f = await SeedAsync();

        // USD 1,000 purchase, USD 400 paid at 83.20
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m, 400m), f.UserId);

        Assert.Equal(400m, purchase.OriginalPaidAmount);
        Assert.Equal(33280m, purchase.PaidAmount);
        Assert.Equal(600m, purchase.OriginalDueAmount);
        Assert.Equal(49920m, purchase.DueAmount);
        Assert.Equal(PaymentStatus.PartiallyPaid, purchase.PaymentStatus);
    }

    [Fact]
    public async Task CreatePurchaseAsync_ForeignFullPayment_ClearsBothDuesExactly()
    {
        var f = await SeedAsync(taxRate: 5m);
        f.Db.ExchangeRates.Add(new ExchangeRate
        {
            Id = Guid.NewGuid(), CurrencyId = f.Usd.Id, RateToBase = 95.97m,
            EffectiveDate = new DateOnly(2026, 9, 10), CreatedByUserId = f.UserId,
        });
        await f.Db.SaveChangesAsync();

        // Invoice total USD 4.60 = INR 441.46; 4.60 x 95.97 could round differently, so full payment must book the exact total.
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.UsdSupplier, new DateOnly(2026, 9, 18), 1, 4.38m, 4.60m), f.UserId);

        Assert.Equal(4.60m, purchase.OriginalPaidAmount);
        Assert.Equal(purchase.GrandTotal, purchase.PaidAmount);
        Assert.Equal(0m, purchase.OriginalDueAmount);
        Assert.Equal(0m, purchase.DueAmount);
        Assert.Equal(PaymentStatus.Paid, purchase.PaymentStatus);
    }

    [Fact]
    public async Task AddPaymentAsync_ForeignPurchase_SnapshotsThePurchaseRateOnEachPaymentRow()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);

        // The rate master changes after the purchase; the payment must still use the purchase's own 83.20.
        f.UsdRate.RateToBase = 99m;
        await f.Db.SaveChangesAsync();

        var updated = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(250m, PaymentMethod.Upi, 1000m), f.UserId);
        Assert.Equal(250m, updated!.OriginalPaidAmount);
        Assert.Equal(20800m, updated.PaidAmount);
        Assert.Equal(750m, updated.OriginalDueAmount);
        Assert.Equal(62400m, updated.DueAmount);

        var settled = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(750m, PaymentMethod.Upi, 750m), f.UserId);
        Assert.Equal(83200m, settled!.PaidAmount);
        Assert.Equal(0m, settled.DueAmount);

        var rows = await f.Db.PurchasePayments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(83.20m, r.ExchangeRateToBase));
        Assert.Equal([250m, 750m], rows.Select(r => r.Amount));
        Assert.Equal(20800m, rows[0].BaseAmount);
        Assert.Equal(settled.PaidAmount, rows.Sum(r => r.BaseAmount));
    }

    [Fact]
    public async Task AddPaymentAsync_EveryPaymentIsANewRow_AndExistingRowsAreNeverChanged()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m, 25m), f.UserId);

        await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(25m, PaymentMethod.Upi, 75m), f.UserId);
        await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(50m, PaymentMethod.Card, 50m), f.UserId);

        var rows = await f.Db.PurchasePayments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
        Assert.Equal([25m, 25m, 50m], rows.Select(r => r.Amount));
        Assert.Equal([PaymentMethod.Cash, PaymentMethod.Upi, PaymentMethod.Card], rows.Select(r => r.PaymentMethod));
        Assert.All(rows, r => Assert.Equal(f.UserId, r.CreatedByUserId));
        Assert.Equal(100m, (await f.Db.PurchaseOrders.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Fact]
    public async Task CreatePurchaseAsync_WithoutUpfrontPayment_WritesNoPaymentRow()
    {
        var f = await SeedAsync();
        await f.Purchases.CreatePurchaseAsync(Input(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m), f.UserId);

        Assert.Empty(await f.Db.PurchasePayments.ToListAsync());
    }

    [Fact]
    public async Task UpdateHeaderAsync_ChangesOnlyInvoiceAndNotes_NeverThePaidAmount()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m, 40m), f.UserId);

        var updated = await f.Purchases.UpdateHeaderAsync(purchase.Id, new UpdatePurchaseHeaderInput("SI-9", "n"));

        Assert.Equal("SI-9", updated!.SupplierInvoiceNumber);
        Assert.Equal(40m, updated.PaidAmount);
        Assert.Single(await f.Db.PurchasePayments.ToListAsync());
    }

    [Fact]
    public async Task UpdateHeaderAsync_CancelledPurchase_IsRejected_AndUnknownIdReturnsNull()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId);
        await f.Purchases.CancelPurchaseAsync(purchase.Id, f.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.UpdateHeaderAsync(purchase.Id, new UpdatePurchaseHeaderInput(null, null)));
        Assert.Equal("Status", ex.Field);
        Assert.Null(await f.Purchases.UpdateHeaderAsync(Guid.NewGuid(), new UpdatePurchaseHeaderInput(null, null)));
    }

    [Fact]
    public async Task BaseCurrencyPurchase_PaymentIsUnchanged_BothAmountsAndDuesEqual()
    {
        var f = await SeedAsync();

        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 580m, 2000m), f.UserId);
        Assert.Equal(2000m, purchase.PaidAmount);
        Assert.Equal(2000m, purchase.OriginalPaidAmount);
        Assert.Equal(3800m, purchase.DueAmount);
        Assert.Equal(3800m, purchase.OriginalDueAmount);

        var updated = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(3800m, PaymentMethod.Cash, 3800m), f.UserId);
        Assert.Equal(5800m, updated!.PaidAmount);
        Assert.Equal(0m, updated.DueAmount);
        Assert.Equal(PaymentStatus.Paid, updated.PaymentStatus);
    }

    [Fact]
    public async Task AddPaymentAsync_BaseCurrency_HundredWithSixtyPaid_AllowsExactlyTheRemainingForty()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m, 60m), f.UserId);
        Assert.Equal(40m, purchase.DueAmount);

        var over = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(40.01m, PaymentMethod.Upi, 40m), f.UserId));
        Assert.Equal("Amount", over.Field);

        var paid = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(40m, PaymentMethod.Upi, 40m), f.UserId);
        Assert.Equal(100m, paid!.PaidAmount);
        Assert.Equal(0m, paid.DueAmount);
        Assert.Equal(PaymentStatus.Paid, paid.PaymentStatus);
        Assert.Equal(PaymentMethod.Upi, paid.PaymentMethod);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(-0.01)]
    public async Task AddPaymentAsync_ZeroOrNegativeAmount_IsRejected(double amount)
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m, 60m), f.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput((decimal)amount, PaymentMethod.Cash, 40m), f.UserId));
        Assert.Equal("Amount", ex.Field);
        Assert.Equal(60m, (await f.Db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.Id == purchase.Id)).PaidAmount);
    }

    [Fact]
    public async Task AddPaymentAsync_FullyPaidPurchase_IsRejected()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m, 100m), f.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(1m, PaymentMethod.Cash, 0m), f.UserId));
        Assert.Equal("Amount", ex.Field);
    }

    [Fact]
    public async Task AddPaymentAsync_ForeignPurchase_AccumulatesInBothCurrenciesAndSettlesExactly()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m), f.UserId); // USD 1,000 = INR 83,200

        var first = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(400m, PaymentMethod.Cash, 1000m), f.UserId);
        Assert.Equal(400m, first!.OriginalPaidAmount);
        Assert.Equal(33280m, first.PaidAmount);
        Assert.Equal(600m, first.OriginalDueAmount);
        Assert.Equal(49920m, first.DueAmount);

        var second = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(300m, PaymentMethod.BankTransfer, 600m), f.UserId);
        Assert.Equal(700m, second!.OriginalPaidAmount);
        Assert.Equal(58240m, second.PaidAmount);
        Assert.Equal(300m, second.OriginalDueAmount);
        Assert.Equal(24960m, second.DueAmount);
        Assert.Equal(PaymentStatus.PartiallyPaid, second.PaymentStatus);

        var third = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(300m, PaymentMethod.Cash, 300m), f.UserId);
        Assert.Equal(83200m, third!.PaidAmount);
        Assert.Equal(0m, third.DueAmount);
        Assert.Equal(0m, third.OriginalDueAmount);
        Assert.Equal(PaymentStatus.Paid, third.PaymentStatus);
    }

    [Fact]
    public async Task AddPaymentAsync_ForeignPurchase_PaymentAboveDueIsRejected()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m, 400m), f.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(600.01m, PaymentMethod.Cash, 600m), f.UserId));
        Assert.Equal("Amount", ex.Field);
    }

    [Fact]
    public async Task AddPaymentAsync_DuplicateSubmission_IsAppliedOnlyOnce()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m, 0m), f.UserId);
        var request = new AddPurchasePaymentInput(30m, PaymentMethod.Cash, 100m);

        await f.Purchases.AddPaymentAsync(purchase.Id, request, f.UserId);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Purchases.AddPaymentAsync(purchase.Id, request, f.UserId));

        Assert.Equal("ExpectedDue", ex.Field);
        Assert.Equal(30m, (await f.Db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.Id == purchase.Id)).PaidAmount);
    }

    [Fact]
    public async Task AddPaymentAsync_CancelledPurchase_IsRejected_AndUnknownIdReturnsNull()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m, 0m), f.UserId);
        await f.Purchases.CancelPurchaseAsync(purchase.Id, f.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(10m, PaymentMethod.Cash, 100m), f.UserId));
        Assert.Equal("Status", ex.Field);
        Assert.Null(await f.Purchases.AddPaymentAsync(Guid.NewGuid(), new AddPurchasePaymentInput(10m, PaymentMethod.Cash, 0m), f.UserId));
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

    [Fact]
    public async Task Payments_EachWriteASupplierPaymentTransaction_WithTheSnapshottedRate()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(InputPaid(f, f.UsdSupplier, new DateOnly(2026, 9, 5), 10, 100m, 400m), f.UserId);
        await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(600m, PaymentMethod.Upi, 600m), f.UserId);

        var payments = await f.Db.PurchasePayments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
        var txns = await f.Db.FinancialTransactions.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();

        Assert.Equal(2, txns.Count);
        Assert.All(txns, t =>
        {
            Assert.Equal(FinancialTransactionType.SupplierPayment, t.Type);
            Assert.Equal(MoneyDirection.Out, t.Direction);
            Assert.Equal(FinancialTransactionStatus.Active, t.Status);
            Assert.Equal(f.Usd.Id, t.CurrencyId);
            Assert.Equal(83.20m, t.ExchangeRateToBase);
        });
        Assert.Equal(payments.Select(p => p.Id), txns.Select(t => t.PurchasePaymentId!.Value));
        Assert.Equal(payments.Select(p => p.Amount), txns.Select(t => t.Amount));
        Assert.Equal(83200m, txns.Sum(t => t.BaseAmount)); // fully paid: exactly the base grand total
    }

    [Fact]
    public async Task RejectedPayment_WritesNoTransaction()
    {
        var f = await SeedAsync();
        var purchase = await f.Purchases.CreatePurchaseAsync(Input(f, f.InrSupplier, new DateOnly(2026, 9, 5), 10, 10m), f.UserId);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(101m, PaymentMethod.Cash, 100m), f.UserId));

        Assert.Empty(await f.Db.FinancialTransactions.ToListAsync());
    }
}
