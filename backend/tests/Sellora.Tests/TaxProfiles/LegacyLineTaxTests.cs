using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-1 D2 / T10: the four write paths add exactly one legacy tax row per line (component NULL, code = the tax name, kind Other, method Percent),
/// with the id and column values of <c>AddTaxProfiles.legacy-rows.sql</c>; the amounts of the lines are unchanged.</summary>
public class LegacyLineTaxTests
{
    private sealed record Fixture(SelloraDbContext Db, PurchaseService Purchases, ISalesService Sales, Supplier Supplier, Customer Customer, Product Product, ProductUnit Kg, Guid UserId, Tax Tax);

    private static readonly DateOnly D = new(2026, 9, 10);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestCompany.NewDb(new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User { Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin", Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var product = new Product { Id = Guid.NewGuid(), Sku = "RICE", Name = "Rice", BaseUnitId = unit.Id, BaseUnit = unit, TaxId = tax.Id, Tax = tax, ReorderLevel = 1 };
        var kg = TestProductUnits.Base(product, "kg");
        db.AddRange(role, user, unit, tax, inr, settings, supplier, customer, product);
        await db.SaveChangesAsync();
        await TaxFixtures.EnablePurchasesAsync(db); // TP-3: purchases need a country, an own state and a purchase profile per product
        return new Fixture(db, new PurchaseService(db, new ExchangeRateService(db)), ListPricedSales.For(db), supplier, customer, product, kg, user.Id, tax);
    }

    private static Task<PurchaseOrder> Buy(Fixture f, decimal qty, decimal rate) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, D, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(f.Product.Id, f.Kg.Id, qty, rate, 0, 0)]), f.UserId);

    private static Task<SalesInvoice> Sell(Fixture f, decimal qty, decimal price) =>
        f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, 0, [new SaleLineInput(f.Product.Id, f.Kg.Id, qty, price, 0)]), f.UserId);

    private static void AssertLegacyRow(LineTaxBase row, Guid lineId, Tax tax, decimal rate, decimal taxable, decimal taxAmount)
    {
        Assert.Equal(TaxSeedData.LegacyLineTaxId(lineId), row.Id); // the SQL catch-up (md5 of the line id) never duplicates it
        Assert.Null(row.TaxComponentId);
        Assert.Equal(tax.Name, row.ComponentCode);
        Assert.Equal(TaxComponentKind.Other, row.Kind);
        Assert.Equal(TaxCalculationMethod.Percent, row.Method);
        Assert.Equal(rate, row.RatePercent);
        Assert.Null(row.AmountPerBaseUnit);
        Assert.Equal(taxable, row.BaseAmount);
        Assert.Equal(taxAmount, row.TaxAmount);
    }

    [Fact]
    public async Task Sale_AddsOneLegacyRowPerLine_WithTheLinesOwnFigures()
    {
        var f = await SeedAsync();
        await Buy(f, 100, 50m);

        var sale = await Sell(f, 10, 100m); // 1000 + 5% = 1050

        var line = sale.Lines.Single();
        var row = await f.Db.SalesInvoiceLineTaxes.AsNoTracking().SingleAsync();
        Assert.Equal(line.Id, row.SalesInvoiceLineId);
        AssertLegacyRow(row, line.Id, f.Tax, 5m, 1000m, 50m);
        Assert.Equal((1000m, 50m, 1050m), (line.TaxableValue, line.TaxAmount, line.LineTotal)); // billing itself is unchanged
    }

    [Fact]
    public async Task Purchase_NoLongerWritesALegacyRow_ButOneCalculatorRowPerComponent() // TP-3 behaviour change: purchases run on the calculator
    {
        var f = await SeedAsync();

        var purchase = await Buy(f, 100, 50m); // 5000 + 5% = 5250, intra-state: CGST 2.5% + SGST 2.5%

        var line = purchase.Lines.Single();
        var rows = await f.Db.PurchaseOrderLineTaxes.AsNoTracking().Where(r => r.PurchaseOrderLineId == line.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.NotEqual(TaxSeedData.LegacyLineTaxId(line.Id), r.Id);
            Assert.NotNull(r.TaxComponentId);
            Assert.Equal(TaxTreatment.Taxable, r.Treatment);
            Assert.Equal(5000m, r.OriginalBaseAmount);
            Assert.Equal(125m, r.OriginalTaxAmount);
        });
        Assert.Equal((5000m, 250m, 5250m), (line.TaxableValue, line.TaxAmount, line.LineTotal));
    }

    [Fact]
    public async Task SaleWithSeveralLines_GetsOneRowPerLine()
    {
        var f = await SeedAsync();
        var wheat = new Product { Id = Guid.NewGuid(), Sku = "WHEAT", Name = "Wheat", BaseUnitId = f.Product.BaseUnitId, BaseUnit = f.Product.BaseUnit, TaxId = f.Tax.Id, Tax = f.Tax, ReorderLevel = 1 };
        var wheatKg = TestProductUnits.Base(wheat, "kg");
        f.Db.Add(wheat);
        await f.Db.SaveChangesAsync();
        await TaxFixtures.EnablePurchasesAsync(f.Db);
        await Buy(f, 100, 50m);
        await f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, D, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(wheat.Id, wheatKg.Id, 100, 40m, 0, 0)]), f.UserId);

        var sale = await f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, 0,
            [new SaleLineInput(f.Product.Id, f.Kg.Id, 2, 100m, 0), new SaleLineInput(wheat.Id, wheatKg.Id, 3, 100m, 0)]), f.UserId);

        var rows = await f.Db.SalesInvoiceLineTaxes.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(sale.Lines.Select(l => l.Id).Order(), rows.Select(r => r.SalesInvoiceLineId).Order());
        Assert.Equal(sale.Lines.Sum(l => l.TaxAmount), rows.Sum(r => r.TaxAmount)); // the sum of the rows = the lines' tax (validation SQL rule)
    }

    [Fact]
    public async Task SalesReturns_EachGetOneRow_TakingTheOriginalLinesRate_AndTheirRowsSumToTheLine()
    {
        var f = await SeedAsync();
        await Buy(f, 100, 50m);
        var sale = await Sell(f, 3, 33.33m); // an awkward amount: 99.99 + 5%
        var lineId = sale.Lines.Single().Id;

        var first = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, "damaged", [new SaleReturnLineInput(lineId, null, 1m)]), f.UserId);
        var second = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, "rest", [new SaleReturnLineInput(lineId, null, 2m)]), f.UserId);

        var rows = await f.Db.SalesReturnLineTaxes.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        foreach (var ret in new[] { first, second })
        {
            var returnLine = ret.Lines.Single();
            AssertLegacyRow(rows.Single(r => r.SalesReturnLineId == returnLine.Id), returnLine.Id, f.Tax, 5m, returnLine.TaxableValue, returnLine.TaxAmount);
        }
        var original = await f.Db.SalesInvoiceLineTaxes.AsNoTracking().SingleAsync();
        Assert.Equal(original.TaxAmount, rows.Sum(r => r.TaxAmount)); // AC15 shape: the returns add up exactly to the original row
        Assert.Equal(original.BaseAmount, rows.Sum(r => r.BaseAmount));
    }

    [Fact]
    public async Task PurchaseReturnsOfALegacyPurchase_EachGetOneLegacyRow_TakingTheOriginalLinesRate()
    {
        var f = await SeedAsync();
        var purchase = await Buy(f, 100, 33.33m);
        await TaxFixtures.MakeLegacyPurchaseAsync(f.Db, purchase.Id); // a purchase saved before TP-3: one legacy row, no document-currency twins
        var lineId = purchase.Lines.Single().Id;

        var first = await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, "wrong grade", [new PurchaseReturnLineInput(lineId, null, 30m)]), f.UserId);
        var second = await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, "rest", [new PurchaseReturnLineInput(lineId, null, 70m)]), f.UserId);

        var rows = await f.Db.PurchaseReturnLineTaxes.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        foreach (var ret in new[] { first, second })
        {
            var returnLine = ret.Lines.Single();
            var row = rows.Single(r => r.PurchaseReturnLineId == returnLine.Id);
            AssertLegacyRow(row, returnLine.Id, f.Tax, 5m, returnLine.TaxableValue, returnLine.TaxAmount);
            Assert.Null(row.OriginalTaxAmount);
        }
        var original = await f.Db.PurchaseOrderLineTaxes.AsNoTracking().SingleAsync();
        Assert.Equal(original.TaxAmount, rows.Sum(r => r.TaxAmount));
    }

    [Fact]
    public async Task ADocumentThatFails_LeavesNoTaxRows()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => Sell(f, 10, 100m)); // no stock

        Assert.Empty(await f.Db.SalesInvoiceLineTaxes.ToListAsync());
    }
}
