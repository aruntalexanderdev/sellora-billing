using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Minimal master data for stock flows on a migrated database: an admin user, kg unit, 5% tax, supplier, walk-in
/// customer, business settings (INR, inserted by the AddMultiCurrency migration) and one product ("Ponni Rice",
/// base kg) sold as a 10 kg bag or loose kg. Stock is only ever put in or taken out through the real services
/// (<see cref="PurchaseService"/>, <see cref="SalesService"/>, <see cref="StockAdjustmentService"/>), each call
/// using the context it is given so callers control isolation.
/// </summary>
public sealed record StockScenario(Guid UserId, Guid SupplierId, Guid CustomerId, Guid ProductId, Guid BagModeId, Guid LooseKgModeId)
{
    public const decimal BagFactor = 10m;
    public const decimal LooseKgPrice = 65m;

    public static readonly DateOnly PurchaseDate = new(2026, 9, 1);
    public static readonly DateOnly SaleDate = new(2026, 9, 18);
    public static readonly DateOnly ReturnDate = SaleDate.AddDays(1);

    public static async Task<StockScenario> SeedAsync(SelloraDbContext db)
    {
        var inr = await db.Currencies.SingleAsync(c => c.Code == "INR");

        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var looseKg = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = product.BaseUnitId, IsBaseUnit = true, Label = "Loose kg", ConversionFactorToBase = 1m };
        var bagUnit = new Unit { Id = Guid.NewGuid(), Name = "Bag", ShortCode = "bag", MeasurementCategory = MeasurementCategory.Packaging, DecimalPlaces = 0 };
        var bag = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = bagUnit.Id, InnerProductUnitId = looseKg.Id, InnerQuantity = BagFactor, Label = "10 kg bag", ConversionFactorToBase = BagFactor };
        product.Units.Add(bag);
        product.Units.Add(looseKg);

        // Only the base row is priced up front: a priced unit is immutable, and many tests edit the bag. Price the bag
        // with EnsureListPriceAsync in tests that sell it.
        var prices = new[]
        {
            new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = looseKg.Id, Price = LooseKgPrice, CreatedByUserId = user.Id },
        };

        db.AddRange(role, user, unit, bagUnit, tax, supplier, customer, settings, product);
        db.AddRange(prices);
        await db.SaveChangesAsync();
        await TaxFixtures.UseAsCompanyDefaultAsync(db, tax);   // TP-1: the product's 5% tax is the mirror of the company default profile

        return new StockScenario(user.Id, supplier.Id, customer.Id, product.Id, bag.Id, looseKg.Id);
    }

    /// <summary>Makes <paramref name="price"/> the open list price of a unit (the POS refuses unpriced units and
    /// price mismatches). Closes any other open price.</summary>
    public static async Task EnsureListPriceAsync(SelloraDbContext db, Guid userId, Guid productUnitId, decimal price)
    {
        var open = await db.SellingPrices.Where(p => p.ProductUnitId == productUnitId && p.EffectiveTo == null).ToListAsync();
        if (open.Count == 1 && open[0].Price == price) return;
        foreach (var p in open) p.EffectiveTo = DateTimeOffset.UtcNow;
        db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = productUnitId, Price = price, CreatedByUserId = userId });
        await db.SaveChangesAsync();
    }

    /// <summary>Buys <paramref name="bags"/> 10 kg bags at <paramref name="ratePerBag"/> through <see cref="PurchaseService"/>.</summary>
    public Task<PurchaseOrder> PurchaseBagsAsync(SelloraDbContext db, decimal bags, decimal ratePerBag = 580m) =>
        new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
            new CreatePurchaseInput(SupplierId, PurchaseDate, null, null, PaymentMethod.Cash, 0,
                [new PurchaseLineInput(ProductId, BagModeId, bags, ratePerBag, 0, 0)]),
            UserId);

    /// <summary>Sells <paramref name="kg"/> loose kg through <see cref="SalesService"/>.</summary>
    public Task<SalesInvoice> SellLooseKgAsync(SelloraDbContext db, decimal kg, decimal pricePerKg = LooseKgPrice) =>
        new SalesService(db).CreateSaleAsync(
            new CreateSaleInput(CustomerId, SaleDate, PaymentMethod.Cash, 0,
                [new SaleLineInput(ProductId, LooseKgModeId, kg, pricePerKg, 0)]),
            UserId);

    /// <summary>Returns <paramref name="qty"/> (in the line's selling unit) of one sale line through <see cref="SalesService"/>.</summary>
    public Task<SalesReturn> ReturnAsync(SelloraDbContext db, Guid salesInvoiceId, Guid salesInvoiceLineId, decimal qty) =>
        new SalesService(db).CreateReturnAsync(
            new CreateSaleReturnInput(salesInvoiceId, ReturnDate, "Customer return",
                [new SaleReturnLineInput(salesInvoiceLineId, null, qty)]),
            UserId);

    /// <summary>Posts a stock adjustment of <paramref name="baseQty"/> kg, entered in the base row (loose kg, factor 1, so
    /// <paramref name="unitCost"/> is per kg either way), through <see cref="StockAdjustmentService"/>.</summary>
    public Task<StockAdjustment> AdjustAsync(SelloraDbContext db, AdjustmentType type, AdjustmentDirection direction, decimal baseQty, decimal? unitCost = null) =>
        new StockAdjustmentService(db).CreateAdjustmentAsync(
            new CreateAdjustmentInput(ProductId, LooseKgModeId, type, direction, baseQty, unitCost, "Integration test"),
            UserId);
}
