using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TaxProfiles;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Sales;

/// <summary>
/// TP-4: sales, POS and sales returns on the tax calculator (docs/features/tax-profiles.md, TP-4 design). Place of supply (walk-in), price basis (T16, T18), profiles and
/// versions by invoice date (BR5), bill discount and charges, the cap and the permission (T15, BR19a), <c>expectedGrandTotal</c>, idempotent replay, preview = save, returns
/// (AC15, legacy invoices), payments on inclusive totals, the sales GET/catalog/report projections. EF InMemory (no locks).
/// </summary>
public class SalesTaxTests
{
    private static readonly DateOnly D = TaxWorld.Today;

    private sealed record Fx(
        TaxWorld W, SalesService Sales, SalesService NoBillDiscount, PurchaseService Purchases, Guid UserId, State Home, State Other, Supplier Supplier,
        Customer Walk, Customer Local, Customer Far, Customer NoState);

    private static async Task<Fx> SetupAsync(string country = "IN", bool ownState = true, bool inclusive = false)
    {
        var w = await TaxWorld.CreateAsync(country);
        var home = new State { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "KA", Name = "Karnataka", Kind = StateKind.State, GstStateCode = "29" };
        var other = new State { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "MH", Name = "Maharashtra", Kind = StateKind.State, GstStateCode = "27" };
        w.Db.States.AddRange(home, other);
        if (ownState) w.Settings.StateId = home.Id;
        w.Settings.SellingPricesIncludeTax = inclusive;
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = (await w.Db.Currencies.FirstAsync()).Id, StateId = home.Id };
        // A walk-in customer that happens to carry another state: it must still be intra-state (TP4-Q3).
        var walk = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true, CountryId = w.India.Id, StateId = other.Id };
        var local = new Customer { Id = Guid.NewGuid(), Name = "Local Stores", CountryId = w.India.Id, StateId = home.Id };
        var far = new Customer { Id = Guid.NewGuid(), Name = "Mumbai Traders", CountryId = w.India.Id, StateId = other.Id };
        var noState = new Customer { Id = Guid.NewGuid(), Name = "Stateless Buyer" };
        var user = TestUsers.Admin();
        w.Db.AddRange(supplier, walk, local, far, noState, user.Role, user);
        await w.Db.SaveChangesAsync();
        var full = new FakeEffectivePermissions("Sales.Create", "Sales.BillDiscount", "Sales.PriceOverride");
        return new Fx(w, new SalesService(w.Db, full, w.Resolver), new SalesService(w.Db, new FakeEffectivePermissions("Sales.Create"), w.Resolver),
            new PurchaseService(w.Db, new ExchangeRateService(w.Db), null, w.Resolver), user.Id, home, other, supplier, walk, local, far, noState);
    }

    /// <summary>A product with a base-unit row and 1,000 pieces in stock. <paramref name="sales"/> is a profile code (null = the company default, GST-18);
    /// <paramref name="includes"/> is the product's own price basis (null = follow the company).</summary>
    private static async Task<(Product Product, ProductUnit Unit)> ProductAsync(
        Fx f, string? sales = null, string? hsn = "1006", bool? includes = null, Country? country = null, decimal stock = 1000m, Guid? subcategoryId = null)
    {
        var salesProfile = sales is null ? null : await f.W.ProfileAsync(sales, country);
        var sku = "P" + Guid.NewGuid().ToString("N")[..6];
        var mirror = salesProfile is null ? null : await f.W.MirrorAsync(salesProfile);
        var product = await f.W.AddProductAsync(sku, subcategoryId: subcategoryId, hsn: hsn, taxId: mirror?.Id);
        product.PriceIncludesTax = includes;
        if (salesProfile is not null)
        {
            f.W.Db.TaxAssignments.Add(new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = salesProfile.CountryId, SalesTaxProfileId = salesProfile.Id, CreatedAt = DateTime.UtcNow,
            });
        }
        var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = f.W.Piece.Id, Unit = f.W.Piece, IsBaseUnit = true, ConversionFactorToBase = 1m };
        f.W.Db.ProductUnits.Add(unit);
        await f.W.Db.SaveChangesAsync();
        if (stock > 0)
        {
            await f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, D.AddDays(-30), null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(product.Id, unit.Id, stock, 10m, 0, 0)]), f.UserId);
        }
        return (product, unit);
    }

    private static async Task SetListPriceAsync(Fx f, ProductUnit unit, decimal price)
    {
        var open = await f.W.Db.SellingPrices.Where(p => p.ProductUnitId == unit.Id && p.EffectiveTo == null).ToListAsync();
        if (open.Count == 1 && open[0].Price == price) return;
        foreach (var p in open) p.EffectiveTo = DateTimeOffset.UtcNow;
        f.W.Db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = unit.Id, Price = price, CreatedByUserId = f.UserId });
        await f.W.Db.SaveChangesAsync();
    }

    private static SaleLineInput Line((Product Product, ProductUnit Unit) p, decimal qty, decimal price, decimal discount = 0m) =>
        new(p.Product.Id, p.Unit.Id, qty, price, discount);

    private static async Task<SalesInvoice> SellAsync(
        Fx f, IReadOnlyList<SaleLineInput> lines, Customer? customer = null, decimal paid = 0m, decimal? pct = null, decimal? amount = null, decimal charges = 0m,
        decimal? expected = null, DateOnly? date = null, string? key = null, bool canOverride = true, SalesService? service = null)
    {
        foreach (var line in lines)
        {
            await SetListPriceAsync(f, await f.W.Db.ProductUnits.SingleAsync(u => u.Id == line.ProductUnitId), line.UnitPrice); // the cart is at the shelf price
        }
        return await (service ?? f.Sales).CreateSaleAsync(new CreateSaleInput(
            (customer ?? f.Local).Id, date ?? D, PaymentMethod.Cash, paid, lines, null, key, canOverride, null, pct, amount, charges, expected), f.UserId);
    }

    private static Task<SalesInvoice> SellAsync(Fx f, (Product Product, ProductUnit Unit) p, decimal qty, decimal price, Customer? customer = null, decimal discount = 0m, decimal paid = 0m) =>
        SellAsync(f, [Line(p, qty, price, discount)], customer, paid);

    private static async Task<List<SalesInvoiceLineTax>> RowsAsync(Fx f, SalesInvoiceLine line) =>
        (await f.W.Db.SalesInvoiceLineTaxes.AsNoTracking().Where(r => r.SalesInvoiceLineId == line.Id).ToListAsync()).OrderBy(r => r.Kind).ThenBy(r => r.ComponentCode, StringComparer.Ordinal).ToList();

    private static async Task<DomainValidationException> RejectedAsync(Func<Task> act) => await Assert.ThrowsAsync<DomainValidationException>(act);

    private static decimal StockQty(Fx f, Guid productId) => f.W.Db.StockItems.Single(s => s.ProductId == productId).QuantityOnHand;

    private static async Task<TaxProfile> CustomProfileAsync(TaxWorld w)
    {
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 28% + cess", RatePercent = 28m };
        w.Db.Taxes.Add(tax);
        var profile = new TaxProfile
        {
            Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "GST-28-CESS", Name = "GST 28% + cess", Regime = TaxRegime.Gst, Treatment = TaxTreatment.Taxable,
            LegacyTaxId = tax.Id, CreatedAt = DateTime.UtcNow,
        };
        var version = new TaxProfileVersion { Id = Guid.NewGuid(), TaxProfileId = profile.Id, EffectiveFrom = new DateOnly(2017, 7, 1), TotalPercent = 28m, CreatedAt = DateTime.UtcNow };
        TaxProfileVersionComponent Comp(TaxComponent c, TaxApplicability a, decimal? rate, decimal? amount, int sort) => new()
        {
            Id = Guid.NewGuid(), TaxProfileVersionId = version.Id, TaxComponentId = c.Id, Method = c.Method, Applicability = a, RatePercent = rate, AmountPerBaseUnit = amount, SortOrder = sort,
        };
        version.Components.Add(Comp(await w.ComponentAsync("CGST"), TaxApplicability.IntraState, 14m, null, 1));
        version.Components.Add(Comp(await w.ComponentAsync("SGST"), TaxApplicability.IntraState, 14m, null, 2));
        version.Components.Add(Comp(await w.ComponentAsync("IGST"), TaxApplicability.InterState, 28m, null, 3));
        version.Components.Add(Comp(await w.ComponentAsync("CESS"), TaxApplicability.Always, 5m, null, 4));
        version.Components.Add(Comp(await w.ComponentAsync("CESS-UNIT"), TaxApplicability.Always, null, 2m, 5));
        profile.Versions.Add(version);
        w.Db.TaxProfiles.Add(profile);
        await w.Db.SaveChangesAsync();
        return profile;
    }

    // ------------------------------------------------------------------ AC1, AC2: place of supply

    [Fact]
    public async Task Create_AC1_SameStateCustomer_IsIntraStateWithCgstAndSgstRowsAndTheFullSnapshot()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var gst18 = await f.W.ProfileAsync("GST-18");

        var sale = await SellAsync(f, p, 10, 100m, discount: 50m); // 1,000 less 5%

        var line = sale.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal([TaxComponentKind.Cgst, TaxComponentKind.Sgst], rows.Select(r => r.Kind));
        Assert.Equal([85.50m, 85.50m], rows.Select(r => r.TaxAmount));
        Assert.All(rows, r => Assert.Equal((TaxTreatment.Taxable, 9m, 950m), (r.Treatment, r.RatePercent, r.BaseAmount)));
        Assert.All(rows, r => Assert.NotNull(r.TaxComponentId));
        Assert.Equal((50m, 950m, 171m, 1121m), (line.Discount, line.TaxableValue, line.TaxAmount, line.LineTotal));
        Assert.Equal(gst18.Id, line.TaxProfileId);
        Assert.Equal(gst18.Versions.Single(v => v.EffectiveTo == null).Id, line.TaxProfileVersionId);
        Assert.Equal(gst18.LegacyTaxId, line.TaxId); // the profile's mirror tax
        Assert.Equal((18m, PlaceOfSupply.IntraState, "1006", false), (line.TaxRatePercent, line.PlaceOfSupply, line.HsnCode, line.PriceIncludesTax));
        Assert.Equal((0m, 0m), (line.BillDiscountShare, line.BillChargeShare));
        Assert.Equal((1000m, 171m, 50m, 950m, 1121m), (sale.Subtotal, sale.TaxTotal, sale.DiscountTotal, sale.TaxableTotal, sale.GrandTotal));
        Assert.Equal((PlaceOfSupply.IntraState, f.Home.Id, f.Home.Id), (sale.PlaceOfSupply, sale.PlaceOfSupplyStateId, sale.SellerStateId));
        Assert.Equal((0m, 0m, null), (sale.BillDiscountAmount, sale.BillCharges, sale.BillDiscountPercent));
        Assert.Equal(PartyTaxStatus.Unspecified, sale.PartyTaxStatus);
        Assert.Equal(990m, StockQty(f, p.Product.Id));
    }

    [Fact]
    public async Task Create_AC2_CustomerInAnotherState_IsInterStateWithOneIgstRow_AndTheBuyersStateIsThePlaceOfSupply()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var sale = await SellAsync(f, p, 10, 100m, f.Far, discount: 50m);

        var line = sale.Lines.Single();
        var row = Assert.Single(await RowsAsync(f, line));
        Assert.Equal((TaxComponentKind.Igst, 171m, 18m, 950m), (row.Kind, row.TaxAmount, row.RatePercent, row.BaseAmount));
        Assert.Equal((950m, 171m, 1121m, PlaceOfSupply.InterState), (line.TaxableValue, line.TaxAmount, line.LineTotal, line.PlaceOfSupply));
        Assert.Equal((PlaceOfSupply.InterState, f.Other.Id, f.Home.Id), (sale.PlaceOfSupply, sale.PlaceOfSupplyStateId, sale.SellerStateId));
    }

    [Fact]
    public async Task Create_WalkInCustomerWithAState_IsAlwaysIntraState_AndSoIsACustomerWithoutOne()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var walk = await SellAsync(f, p, 1, 100m, f.Walk); // the walk-in customer is stored in Maharashtra, the shop is in Karnataka
        var stateless = await SellAsync(f, p, 1, 100m, f.NoState);

        foreach (var sale in new[] { walk, stateless })
        {
            Assert.Equal(PlaceOfSupply.IntraState, sale.PlaceOfSupply);
            Assert.Equal([TaxComponentKind.Cgst, TaxComponentKind.Sgst], (await RowsAsync(f, sale.Lines.Single())).Select(r => r.Kind));
            Assert.Equal(f.Home.Id, sale.PlaceOfSupplyStateId); // no buyer state: the seller's own
            Assert.Equal(f.Home.Id, sale.SellerStateId);
            Assert.Empty(sale.Warnings);
        }
    }

    [Fact]
    public async Task Create_CustomerInAnotherCountry_IsTreatedAsInterState()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var abroad = new Customer { Id = Guid.NewGuid(), Name = "Dubai Importer", CountryId = f.W.Uae.Id, StateId = f.Other.Id }; // a stale Indian state on a foreign customer
        f.W.Db.Customers.Add(abroad);
        await f.W.Db.SaveChangesAsync();

        var sale = await SellAsync(f, p, 1, 100m, abroad);

        Assert.Equal(PlaceOfSupply.InterState, sale.PlaceOfSupply); // DQ-TP2-7
        Assert.Null(sale.PlaceOfSupplyStateId); // T31c: no GST state for a buyer abroad
        Assert.Equal(f.Home.Id, sale.SellerStateId);
        Assert.Equal(TaxComponentKind.Igst, Assert.Single(await RowsAsync(f, sale.Lines.Single())).Kind);
    }

    [Fact]
    public async Task Create_GstWithoutAnyOwnState_Is400OnBranchId_AndNothingIsSaved()
    {
        var f = await SetupAsync(ownState: false);
        var p = await ProductAsync(f, stock: 0m);

        var ex = await RejectedAsync(() => SellAsync(f, p, 1, 100m));

        Assert.Equal("branchId", ex.Field);
        Assert.Empty(await f.W.Db.SalesInvoices.ToListAsync());
    }

    [Fact]
    public async Task Create_Vat_HasOneVatRowAndNoPlaceOfSupply()
    {
        var f = await SetupAsync("AE");
        var vat5 = (await f.W.Db.TaxProfiles.Include(p => p.Versions).Where(p => p.CountryId == f.W.Uae.Id && p.Treatment == TaxTreatment.Taxable).ToListAsync()).Single(p => p.Versions.Single().TotalPercent == 5m);
        var p = await ProductAsync(f, sales: vat5.Code, country: f.W.Uae);

        var sale = await SellAsync(f, p, 10, 100m);

        var row = Assert.Single(await RowsAsync(f, sale.Lines.Single()));
        Assert.Equal((TaxComponentKind.Vat, 50m, TaxTreatment.Taxable), (row.Kind, row.TaxAmount, row.Treatment));
        Assert.Equal((PlaceOfSupply.NotApplicable, null, null), (sale.PlaceOfSupply, sale.PlaceOfSupplyStateId, sale.SellerStateId));
        Assert.Equal(PlaceOfSupply.NotApplicable, sale.Lines.Single().PlaceOfSupply);
        Assert.Equal((1000m, 50m, 1050m), (sale.TaxableTotal, sale.TaxTotal, sale.GrandTotal));
    }

    // ------------------------------------------------------------------ price basis: AC3, AC4, AC6, AC18, AC23, E2, E3

    [Fact]
    public async Task Create_AC3_InclusivePrice_BackCalculatesTheTaxableValue_AndSubtotalIsTheInclusiveGross()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f);

        var sale = await SellAsync(f, p, 1, 118m);

        var line = sale.Lines.Single();
        Assert.Equal([9m, 9m], (await RowsAsync(f, line)).Select(r => r.TaxAmount));
        Assert.Equal((100m, 18m, 118m, true, 118m), (line.TaxableValue, line.TaxAmount, line.LineTotal, line.PriceIncludesTax, line.UnitPrice)); // the stored price is the shelf price
        Assert.Equal((118m, 0m, 100m, 18m, 118m), (sale.Subtotal, sale.DiscountTotal, sale.TaxableTotal, sale.TaxTotal, sale.GrandTotal)); // BR21
    }

    [Fact]
    public async Task Create_AC4_InclusiveFivePercentOf99_PaysExactly99_AndCgstEqualsSgst()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f, sales: "GST-5");

        var sale = await SellAsync(f, p, 1, 99m);

        var line = sale.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal(rows[0].TaxAmount, rows[1].TaxAmount);
        Assert.Equal(99m, line.TaxableValue + rows.Sum(r => r.TaxAmount));
        Assert.Equal((99m, 99m), (line.LineTotal, sale.GrandTotal));
    }

    [Fact]
    public async Task Create_AC5_PercentAndPerUnitCess_AllRowsStored_PerUnitBaseAmountIsTheQuantity()
    {
        var f = await SetupAsync();
        var profile = await CustomProfileAsync(f.W);
        var p = await ProductAsync(f, sales: profile.Code);

        var sale = await SellAsync(f, p, 10, 100m);

        var line = sale.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal(4, rows.Count);
        decimal Tax(string code) => rows.Single(r => r.ComponentCode == code).TaxAmount;
        Assert.Equal((140m, 140m, 50m, 20m), (Tax("CGST"), Tax("SGST"), Tax("CESS"), Tax("CESS-UNIT")));
        var perUnit = rows.Single(r => r.ComponentCode == "CESS-UNIT");
        Assert.Equal((TaxCalculationMethod.AmountPerBaseUnit, 2m, 10m), (perUnit.Method, perUnit.AmountPerBaseUnit, perUnit.BaseAmount)); // a quantity, not money
        Assert.Equal((1000m, 350m, 1350m), (line.TaxableValue, line.TaxAmount, line.LineTotal));
        Assert.Equal(33m, line.TaxRatePercent); // Σ applied percent rates (14 + 14 + 5)
        Assert.Equal(profile.LegacyTaxId, line.TaxId);
    }

    [Fact]
    public async Task Create_AC6_InclusiveLineWithPerUnitCess_RemovesTheCessBeforeDividing()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f, sales: (await CustomProfileAsync(f.W)).Code);

        var sale = await SellAsync(f, p, 10, 135m); // 1,350 inclusive: 20 cess per unit first, then 1,330 / 1.33

        var line = sale.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal((140m, 140m, 50m, 20m), (rows.Single(r => r.ComponentCode == "CGST").TaxAmount, rows.Single(r => r.ComponentCode == "SGST").TaxAmount,
            rows.Single(r => r.ComponentCode == "CESS").TaxAmount, rows.Single(r => r.ComponentCode == "CESS-UNIT").TaxAmount));
        Assert.Equal((1000m, 350m, 1350m), (line.TaxableValue, line.TaxAmount, line.LineTotal));
    }

    [Fact]
    public async Task Create_E3_InclusiveLineWhosePerUnitCessExceedsTheAmount_IsAFieldErrorOnTheLinePrice_AndNothingIsSaved()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f, sales: (await CustomProfileAsync(f.W)).Code);

        var ex = await RejectedAsync(() => SellAsync(f, p, 1, 1.50m)); // 2.00 cess per unit on a 1.50 price

        Assert.Equal("lines[0].unitPrice", ex.Field);
        Assert.Empty(await f.W.Db.SalesInvoices.ToListAsync());
        Assert.Equal(1000m, StockQty(f, p.Product.Id));
    }

    [Fact]
    public async Task Create_AC18_ProductOverrideBeatsTheCompanyBasis_AndEachLineSnapshotsItsOwn_E2()
    {
        var f = await SetupAsync(inclusive: true);
        var follows = await ProductAsync(f); // null: follows the company (inclusive)
        var exclusive = await ProductAsync(f, includes: false);

        var sale = await SellAsync(f, [Line(follows, 1, 118m), Line(exclusive, 1, 100m)], pct: null, amount: 21.80m); // E2: bill discount over mixed bases

        var (a, b) = (sale.Lines.First(l => l.ProductId == follows.Product.Id), sale.Lines.First(l => l.ProductId == exclusive.Product.Id));
        Assert.Equal((true, false), (a.PriceIncludesTax, b.PriceIncludesTax));
        Assert.Equal((11.80m, 10m), (a.BillDiscountShare, b.BillDiscountShare)); // shares follow each line's own entered amount
        Assert.Equal((90m, 90m), (a.TaxableValue, b.TaxableValue));
        Assert.Equal((106.20m, 106.20m, 212.40m), (a.LineTotal, b.LineTotal, sale.GrandTotal));
        Assert.Equal((21.80m, 218m, 21.80m, 180m, 32.40m), (sale.BillDiscountAmount, sale.Subtotal, sale.DiscountTotal, sale.TaxableTotal, sale.TaxTotal));
    }

    [Fact]
    public async Task Create_AC18_ExclusiveOverrideOnAnInclusiveCompany_TreatsThePriceAsBeforeTax()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f, includes: false);

        var sale = await SellAsync(f, p, 1, 100m);

        var line = sale.Lines.Single();
        Assert.Equal((false, 100m, 18m, 118m), (line.PriceIncludesTax, line.TaxableValue, line.TaxAmount, line.LineTotal));
    }

    [Fact]
    public async Task Create_AC23_InclusiveShelfPrice99_PaysExactly99_AndOnceSwitchedToExclusiveTheSame99IsBeforeTax()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f, includes: true);

        var inclusive = await SellAsync(f, p, 1, 99m);
        p.Product.PriceIncludesTax = false; // T16: the stored price keeps its number, the basis follows the product
        await f.W.Db.SaveChangesAsync();
        var exclusive = await SellAsync(f, p, 1, 99m);

        Assert.Equal((99m, true), (inclusive.GrandTotal, inclusive.Lines.Single().PriceIncludesTax));
        var line = exclusive.Lines.Single();
        Assert.Equal((99m, 17.82m, 116.82m, false), (line.TaxableValue, line.TaxAmount, exclusive.GrandTotal, line.PriceIncludesTax));
    }

    // ------------------------------------------------------------------ AC7, AC8, charges

    [Fact]
    public async Task Create_AC7_BillDiscountIsSpreadByValue_AndEachLineIsTaxedOnItsReducedValue()
    {
        var f = await SetupAsync();
        var rice = await ProductAsync(f, sales: "GST-5");
        var cola = await ProductAsync(f, sales: "GST-40");

        var sale = await SellAsync(f, [Line(rice, 6, 100m), Line(cola, 4, 100m)], amount: 100m);

        var (r, c) = (sale.Lines.First(l => l.ProductId == rice.Product.Id), sale.Lines.First(l => l.ProductId == cola.Product.Id));
        Assert.Equal((60m, 40m), (r.BillDiscountShare, c.BillDiscountShare));
        Assert.Equal((540m, 360m), (r.TaxableValue, c.TaxableValue));
        Assert.Equal([13.50m, 13.50m], (await RowsAsync(f, r)).Select(x => x.TaxAmount));
        Assert.Equal([72m, 72m], (await RowsAsync(f, c)).Select(x => x.TaxAmount));
        Assert.Equal((1000m, 100m, 100m, 900m, 171m, 1071m), (sale.Subtotal, sale.DiscountTotal, sale.BillDiscountAmount, sale.TaxableTotal, sale.TaxTotal, sale.GrandTotal));
        Assert.Null(sale.BillDiscountPercent);
    }

    [Fact]
    public async Task Create_AC8_BillDiscountOverThreeEqualLines_GivesTheRemainderToTheLast()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var sale = await SellAsync(f, [Line(p, 1, 100m), Line(p, 1, 100m), Line(p, 1, 100m)], amount: 100m);

        Assert.Equal([33.33m, 33.33m, 33.34m], sale.Lines.Select(l => l.BillDiscountShare));
        Assert.Equal(100m, sale.BillDiscountAmount);
        Assert.Equal(sale.Lines.Sum(l => l.TaxableValue), sale.TaxableTotal);
        Assert.Equal(997m, StockQty(f, p.Product.Id));
    }

    [Fact]
    public async Task Create_BillDiscountPercent_IsStoredAsEntered_AndReducesEveryLine()
    {
        var f = await SetupAsync();
        var a = await ProductAsync(f);
        var b = await ProductAsync(f);

        var sale = await SellAsync(f, [Line(a, 6, 100m), Line(b, 4, 100m)], pct: 10m);

        Assert.Equal((10m, 100m), (sale.BillDiscountPercent, sale.BillDiscountAmount));
        Assert.Equal((900m, 162m, 1062m), (sale.TaxableTotal, sale.TaxTotal, sale.GrandTotal));
    }

    [Fact]
    public async Task Create_BillCharges_AreSpreadByValue_AreTaxed_AndSumExactly()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var sale = await SellAsync(f, [Line(p, 2, 100m), Line(p, 1, 100m)], charges: 30m); // packing 30 over lines of 200 and 100

        Assert.Equal([20m, 10m], sale.Lines.Select(l => l.BillChargeShare));
        Assert.Equal([220m, 110m], sale.Lines.Select(l => l.TaxableValue));
        Assert.Equal([19.80m, 19.80m], (await RowsAsync(f, sale.Lines.ElementAt(0))).Select(r => r.TaxAmount));
        Assert.Equal((30m, 330m, 59.40m, 389.40m), (sale.BillCharges, sale.TaxableTotal, sale.TaxTotal, sale.GrandTotal));
        Assert.Equal((300m, 0m), (sale.Subtotal, sale.DiscountTotal));
        Assert.Equal(sale.Lines.Sum(l => l.LineTotal), sale.GrandTotal);
    }

    [Fact]
    public async Task Create_BillDiscountOverTheBill_BothAndOverPrecise_AreKeyedByTheFieldTheCallerSent()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        Assert.Equal("billDiscountPercent", (await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], pct: 120m))).Field);
        Assert.Equal("billDiscountAmount", (await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], amount: 101m))).Field);
        Assert.Equal("billDiscountAmount", (await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], amount: 1.005m))).Field);
        Assert.Equal("billDiscountPercent", (await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], pct: 5m, amount: 5m))).Field); // both: reported on the percent
        Assert.Equal("billCharges", (await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], charges: -1m))).Field);
        Assert.Empty(await f.W.Db.SalesInvoices.ToListAsync());
    }

    // ------------------------------------------------------------------ AC19: exempt and nil rated

    [Theory]
    [InlineData("GST-EXEMPT", TaxTreatment.Exempt)]
    [InlineData("GST-NIL", TaxTreatment.NilRated)]
    public async Task Create_AC19_ExemptAndNilRated_StoreOneZeroRowWithTheTreatment_AndNoCgstSgst(string code, TaxTreatment treatment)
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, sales: code);

        var sale = await SellAsync(f, p, 4, 25m);

        var line = sale.Lines.Single();
        var row = Assert.Single(await RowsAsync(f, line));
        Assert.Equal((treatment, 0m, null), (row.Treatment, row.TaxAmount, row.TaxComponentId));
        Assert.Equal((100m, 0m, 100m, 0m), (line.TaxableValue, line.TaxAmount, line.LineTotal, line.TaxRatePercent));
        Assert.Equal((100m, 100m), (sale.TaxableTotal, sale.GrandTotal));
    }

    // ------------------------------------------------------------------ AC22: permission and cap

    [Fact]
    public async Task Create_AC22_BillDiscountWithoutThePermission_IsRefusedByTheService_ButAZeroDiscountIsNot()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var amount = await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], amount: 10m, service: f.NoBillDiscount));
        var percent = await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], pct: 5m, service: f.NoBillDiscount));
        var zero = await SellAsync(f, [Line(p, 1, 100m)], pct: 0m, service: f.NoBillDiscount);

        Assert.Equal("billDiscountAmount", amount.Field);
        Assert.Equal("billDiscountPercent", percent.Field);
        Assert.Equal(1, await f.W.Db.SalesInvoices.CountAsync());
        Assert.Equal(0m, zero.BillDiscountAmount);
    }

    [Fact]
    public async Task Create_AC22_Controller_Is403WithoutSalesBillDiscount_AndNothingIsSaved()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SetListPriceAsync(f, p.Unit, 100m);
        var controller = Controller(f, "Sales.View", "Sales.Create");

        var result = await controller.Create(Request(f, p, billDiscountPercent: 5m), default);

        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(await f.W.Db.SalesInvoices.ToListAsync());
    }

    [Fact]
    public async Task Create_AC22_TheCompanyCapOnTheBillDiscount_Is400OnTheFieldSent_AndTheCapItselfIsAllowed()
    {
        var f = await SetupAsync();
        f.W.Settings.MaxBillDiscountPercent = 10m;
        await f.W.Db.SaveChangesAsync();
        var p = await ProductAsync(f);

        var overPercent = await RejectedAsync(() => SellAsync(f, [Line(p, 10, 100m)], pct: 12m));
        var overAmount = await RejectedAsync(() => SellAsync(f, [Line(p, 10, 100m)], amount: 120m));
        var atCap = await SellAsync(f, [Line(p, 10, 100m)], pct: 10m);

        Assert.Equal(("billDiscountPercent", "billDiscountAmount"), (overPercent.Field, overAmount.Field));
        Assert.Contains("10%", overPercent.Message);
        Assert.Equal(100m, atCap.BillDiscountAmount);
        Assert.Equal(1, await f.W.Db.SalesInvoices.CountAsync());
    }

    // ------------------------------------------------------------------ AC10, BR5, T25

    [Fact]
    public async Task Create_AC10_TheVersionValidOnTheInvoiceDateIsSnapshotted_AndAFutureVersionDoesNotApplyYet()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var profile = await f.W.ProfileAsync("GST-18");
        var change = D.AddDays(-4);
        var v2 = await f.W.Profiles.AddVersionAsync(profile.Id, TaxWorld.Version(change, await f.W.GstSetAsync(20m)), null);
        var v3 = await f.W.Profiles.AddVersionAsync(profile.Id, TaxWorld.Version(D.AddDays(10), await f.W.GstSetAsync(28m)), null); // T19 lifted: a future version is allowed
        var v1Id = (await f.W.Db.TaxProfileVersions.AsNoTracking().Where(v => v.TaxProfileId == profile.Id && v.EffectiveFrom < change).SingleAsync()).Id;

        var before = await SellAsync(f, [Line(p, 1, 100m)], date: change.AddDays(-1));
        var onTheDay = await SellAsync(f, [Line(p, 1, 100m)], date: change);
        var today = await SellAsync(f, [Line(p, 1, 100m)]);
        var later = await SellAsync(f, [Line(p, 1, 100m)], date: D.AddDays(10));

        Assert.Equal((v1Id, 18m), (before.Lines.Single().TaxProfileVersionId, before.Lines.Single().TaxRatePercent));
        Assert.Equal((v2!.Id, 20m), (onTheDay.Lines.Single().TaxProfileVersionId, onTheDay.Lines.Single().TaxRatePercent));
        Assert.Equal(v2.Id, today.Lines.Single().TaxProfileVersionId);
        Assert.Equal((v3!.Id, 28m), (later.Lines.Single().TaxProfileVersionId, later.Lines.Single().TaxRatePercent));
        Assert.Equal([10m, 10m], (await RowsAsync(f, onTheDay.Lines.Single())).Select(r => r.RatePercent));
    }

    [Fact]
    public async Task Create_BR5_InvoiceDatedBeforeTheFirstVersion_Is400OnTheLine_AndNothingIsSaved()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var ex = await RejectedAsync(() => SellAsync(f, [Line(p, 1, 100m)], date: new DateOnly(2017, 1, 1)));

        Assert.Equal("lines[0].productId", ex.Field);
        Assert.Contains("no version valid on 2017-01-01", ex.Message);
        Assert.Empty(await f.W.Db.SalesInvoices.ToListAsync());
    }

    [Fact]
    public async Task T25_AVersionOnOrBeforeASaleDateCannotReplaceTheProfilesVersion_ButALaterOneCan()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var date = D.AddDays(-3);
        await SellAsync(f, [Line(p, 1, 100m)], date: date);
        var profile = await f.W.ProfileAsync("GST-18");

        var ex = await RejectedAsync(() => f.W.Profiles.AddVersionAsync(profile.Id, TaxWorld.Version(date, f.W.GstSetAsync(20m).Result), null));

        Assert.Equal("effectiveFrom", ex.Field); // the sale line carries the profile's mirror tax id, so the version counts as used
        Assert.Contains("Documents dated up to", ex.Message);
        Assert.NotNull(await f.W.Profiles.AddVersionAsync(profile.Id, TaxWorld.Version(date.AddDays(1), await f.W.GstSetAsync(20m)), null));
    }

    // ------------------------------------------------------------------ line errors, warnings

    [Fact]
    public async Task Create_LineErrors_AreKeyedByLineIndex_AndAllLinesAreReported()
    {
        var f = await SetupAsync();
        var a = await ProductAsync(f);
        var b = await ProductAsync(f);
        await SetListPriceAsync(f, a.Unit, 100m);
        await SetListPriceAsync(f, b.Unit, 50m);

        var ex = await RejectedAsync(() => f.Sales.CreateSaleAsync(new CreateSaleInput(f.Local.Id, D, PaymentMethod.Cash, 0,
            [new SaleLineInput(a.Product.Id, a.Unit.Id, 1, 90m, 0), new SaleLineInput(b.Product.Id, b.Unit.Id, 0, 50m, 0), new SaleLineInput(a.Product.Id, a.Unit.Id, 1, 100m, 500m)],
            CanOverridePrice: true), f.UserId));

        Assert.Equal(["lines[0].unitPrice", "lines[1].qty", "lines[2].discount"], ex.Errors.Select(e => e.Field));
        Assert.Empty(await f.W.Db.SalesInvoices.ToListAsync());
    }

    [Fact]
    public async Task Create_GstProductWithoutHsn_SavesWithOneWarningPerProductOnTheLine_AndTheWarningIsNotStored()
    {
        var f = await SetupAsync();
        var noHsn = await ProductAsync(f, hsn: null);
        var withHsn = await ProductAsync(f);

        var sale = await SellAsync(f, [Line(withHsn, 1, 100m), Line(noHsn, 1, 100m), Line(noHsn, 2, 100m)]);

        Assert.Equal(["lines[1].hsnCode"], sale.Warnings.Select(w => w.Field));
        Assert.Contains("HSN", sale.Warnings.Single().Message);
        Assert.Null(sale.Lines.ElementAt(1).HsnCode);
        f.W.Db.ChangeTracker.Clear();
        Assert.Empty((await f.W.Db.SalesInvoices.SingleAsync()).Warnings);
    }

    // ------------------------------------------------------------------ expectedGrandTotal, idempotency, preview

    [Fact]
    public async Task Create_ExpectedGrandTotal_AMismatchIs400OnTheField_NothingIsTaken_AndAMatchSaves()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var ex = await RejectedAsync(() => SellAsync(f, [Line(p, 10, 100m, 50m)], paid: 1000m, expected: 1000m));

        Assert.Equal("expectedGrandTotal", ex.Field);
        Assert.Equal("The total is now 1121.00.", ex.Message);
        Assert.Empty(await f.W.Db.SalesInvoices.ToListAsync());
        Assert.Empty(await f.W.Db.SalesPayments.ToListAsync());
        Assert.Equal(1000m, StockQty(f, p.Product.Id));

        var sale = await SellAsync(f, [Line(p, 10, 100m, 50m)], paid: 1121m, expected: 1121.0m);
        Assert.Equal((1121m, 0m), (sale.GrandTotal, sale.DueAmount));
    }

    [Fact]
    public async Task Create_IdempotentReplay_ReturnsTheStoredRows_AndWritesNothingTwice()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var first = await SellAsync(f, [Line(p, 10, 100m, 50m)], key: "pos-1", paid: 500m);
        f.W.Db.ChangeTracker.Clear();
        var second = await SellAsync(f, [Line(p, 10, 100m, 50m)], key: "pos-1", paid: 500m);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await f.W.Db.SalesInvoices.CountAsync());
        Assert.Equal(2, await f.W.Db.SalesInvoiceLineTaxes.CountAsync());
        var line = Assert.Single(second.Lines);
        Assert.Equal([85.50m, 85.50m], line.Taxes.OrderBy(t => t.Kind).Select(t => t.TaxAmount)); // the stored rows, not recalculated
        Assert.Equal(1, await f.W.Db.SalesPayments.CountAsync());
        Assert.Equal(990m, StockQty(f, p.Product.Id));
    }

    [Fact]
    public async Task Preview_Total_EqualsTheSavedTotal_ForTheSameCart()
    {
        var f = await SetupAsync(inclusive: true);
        var preview = new TaxDocumentPreviewService(f.W.Db, f.W.Resolver);
        var follows = await ProductAsync(f);
        var exclusive = await ProductAsync(f, includes: false);
        var five = await ProductAsync(f, sales: "GST-5");

        async Task Check(Customer customer, IReadOnlyList<SaleLineInput> lines, decimal? pct, decimal? amount, decimal charges)
        {
            var p = await preview.PreviewAsync(new TaxDocumentPreviewInput(TaxDirection.Sale, customer.Id, D, null, pct, amount, charges,
                lines.Select(l => new TaxPreviewLineInput(l.ProductId, l.ProductUnitId, l.Qty, l.UnitPrice, l.Discount)).ToList()));
            var sale = await SellAsync(f, lines, customer, pct: pct, amount: amount, charges: charges, expected: p.Totals.GrandTotal);

            Assert.Equal(p.Totals.GrandTotal, sale.GrandTotal);
            Assert.Equal((p.Totals.Subtotal, p.Totals.DiscountTotal, p.Totals.TaxableTotal, p.Totals.TaxTotal), (sale.Subtotal, sale.DiscountTotal, sale.TaxableTotal, sale.TaxTotal));
            Assert.Equal((p.PlaceOfSupply, p.Totals.BillDiscountAmount, p.Totals.ChargesTotal), (sale.PlaceOfSupply, sale.BillDiscountAmount, sale.BillCharges));
            Assert.Equal(p.Lines.Select(l => (l.Calc.LineTotal, l.Calc.TaxTotal, l.Calc.Taxable)), sale.Lines.Select(l => (l.LineTotal, l.TaxAmount, l.TaxableValue)));
            for (var i = 0; i < lines.Count; i++)
            {
                Assert.Equal(p.Lines[i].Calc.Rows.OrderBy(r => r.Code, StringComparer.Ordinal).Select(r => (r.Code, r.TaxAmount)),
                    (await RowsAsync(f, sale.Lines.ElementAt(i))).OrderBy(r => r.ComponentCode, StringComparer.Ordinal).Select(r => (r.ComponentCode, r.TaxAmount)));
            }
        }

        await Check(f.Local, [Line(follows, 1, 118m), Line(exclusive, 1, 100m)], null, 21.80m, 0m);
        await Check(f.Walk, [Line(five, 3, 33.33m), Line(follows, 2, 59m)], 5m, null, 7.50m); // a walk-in with another state: intra-state in both
        await Check(f.Far, [Line(five, 7, 135.71m)], null, null, 0m);
    }

    // ------------------------------------------------------------------ returns: AC15, legacy, inclusive

    [Fact]
    public async Task Return_AC15_EachRowIsReversedProportionally_AndTheReturnsSumExactlyToTheOriginalRows()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var sale = await SellAsync(f, p, 7, 135.71m); // taxable 949.97: CGST = SGST = 85.50
        var line = sale.Lines.Single();

        var returns = new List<SalesReturn>();
        foreach (var qty in new[] { 2m, 2m, 3m })
        {
            returns.Add(await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(line.Id, null, qty)]), f.UserId));
        }

        var cgst = returns.Select(r => r.Lines.Single().Taxes.Single(t => t.Kind == TaxComponentKind.Cgst).TaxAmount).ToList();
        var sgst = returns.Select(r => r.Lines.Single().Taxes.Single(t => t.Kind == TaxComponentKind.Sgst).TaxAmount).ToList();
        Assert.Equal([24.43m, 24.43m, 36.64m], cgst); // the last return takes the remainder
        Assert.Equal(cgst, sgst);
        Assert.Equal(85.50m, cgst.Sum());
        var returnLines = returns.Select(r => r.Lines.Single()).ToList();
        Assert.Equal(returnLines.Select(l => l.TaxAmount), returnLines.Select(l => l.Taxes.Sum(t => t.TaxAmount))); // DQ-TP2-4: line tax = Σ rows
        Assert.Equal(line.TaxableValue, returnLines.Sum(l => l.TaxableValue));
        Assert.Equal(line.TaxAmount, returnLines.Sum(l => l.TaxAmount));
        Assert.Equal((line.TaxProfileId, line.TaxProfileVersionId, line.PlaceOfSupply), (returnLines[0].TaxProfileId, returnLines[0].TaxProfileVersionId, returnLines[0].PlaceOfSupply));
        Assert.All(returnLines.SelectMany(l => l.Taxes), t => Assert.Equal((TaxTreatment.Taxable, false), (t.Treatment, t.TaxComponentId == null)));
        Assert.Equal(sale.GrandTotal, returns.Sum(r => r.Total));
        Assert.Equal(sale.GrandTotal, (await f.W.Db.SalesInvoices.SingleAsync()).ReturnedAmount);
        Assert.Equal(1000m, StockQty(f, p.Product.Id));
    }

    [Fact]
    public async Task Return_InclusiveSale_TheFinalReturnIsExact_PerRowAndInTotal()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f);
        var sale = await SellAsync(f, p, 3, 33.33m); // 99.99 inclusive
        var line = sale.Lines.Single();
        var originalRows = await RowsAsync(f, line);

        var first = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(line.Id, null, 1m)]), f.UserId);
        var last = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(line.Id, null, 2m)]), f.UserId);

        var lines = new[] { first.Lines.Single(), last.Lines.Single() };
        Assert.Equal(99.99m, lines.Sum(l => l.LineTotal));
        Assert.Equal((line.TaxableValue, line.TaxAmount), (lines.Sum(l => l.TaxableValue), lines.Sum(l => l.TaxAmount)));
        foreach (var row in originalRows) Assert.Equal(row.TaxAmount, lines.SelectMany(l => l.Taxes).Where(t => t.TaxComponentId == row.TaxComponentId).Sum(t => t.TaxAmount));
        Assert.Equal((99.99m, 0m), ((await f.W.Db.SalesInvoices.SingleAsync()).ReturnedAmount, (await f.W.Db.SalesInvoices.SingleAsync()).NetTotal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Return_OfALegacyInvoice_EqualsTheOldProportion_AndKeepsTheLegacyRowShape(bool legacyRowsExist)
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, sales: "GST-5");
        var sale = await SellAsync(f, p, 3, 33.33m);
        await TaxFixtures.MakeLegacySaleAsync(f.W.Db, sale.Id, legacyRowsExist); // history: one legacy row (or none), no profile, no place
        var line = sale.Lines.Single();

        var first = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(line.Id, null, 1m)]), f.UserId);
        var second = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(line.Id, null, 2m)]), f.UserId);

        var (r1, r2) = (first.Lines.Single(), second.Lines.Single());
        Assert.Equal(Math.Round(line.TaxableValue / 3m, 2, MidpointRounding.AwayFromZero), r1.TaxableValue); // the proportion of before
        Assert.Equal(Math.Round(line.TaxAmount / 3m, 2, MidpointRounding.AwayFromZero), r1.TaxAmount);
        Assert.Equal((line.TaxableValue, line.TaxAmount), (r1.TaxableValue + r2.TaxableValue, r1.TaxAmount + r2.TaxAmount));
        foreach (var r in new[] { r1, r2 })
        {
            var row = Assert.Single(r.Taxes);
            Assert.Equal(TaxSeedData.LegacyLineTaxId(r.Id), row.Id);
            Assert.Null(row.TaxComponentId);
            Assert.Null(row.Treatment);
            Assert.Equal((TaxComponentKind.Other, TaxCalculationMethod.Percent, "GST 5%", 5m), (row.Kind, row.Method, row.ComponentCode, row.RatePercent));
            Assert.Equal((r.TaxableValue, r.TaxAmount), (row.BaseAmount, row.TaxAmount));
            Assert.Null(r.TaxProfileId);
            Assert.Null(r.PlaceOfSupply);
        }
        Assert.Equal(sale.GrandTotal, first.Total + second.Total);
    }

    // ------------------------------------------------------------------ money: payments, due, change; cancel

    [Fact]
    public async Task Payments_OnAnInclusiveTotal_PaidDueAndChangeUseTheSavedGrandTotal()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f, sales: "GST-5");

        var exact = await SellAsync(f, p, 1, 99m, paid: 99m);
        var change = await SellAsync(f, p, 1, 99m, paid: 100m);
        var part = await SellAsync(f, p, 1, 99m, paid: 50m);

        Assert.Equal((99m, 99m, 0m, PaymentStatus.Paid), (exact.GrandTotal, exact.PaidAmount, exact.DueAmount, exact.PaymentStatus));
        Assert.Equal((99m, 0m, PaymentStatus.Paid), (change.PaidAmount, change.DueAmount, change.PaymentStatus)); // the 1.00 over is change, not money received
        Assert.Equal((50m, 49m, PaymentStatus.PartiallyPaid), (part.PaidAmount, part.DueAmount, part.PaymentStatus));

        var settled = await f.Sales.AddPaymentAsync(part.Id, new AddSalePaymentInput(49m, PaymentMethod.Cash, 49m), f.UserId);
        Assert.Equal((99m, 0m, PaymentStatus.Paid), (settled!.PaidAmount, settled.DueAmount, settled.PaymentStatus));
        Assert.Equal(99m, (await f.W.Db.SalesPayments.Where(x => x.SalesInvoiceId == change.Id).SingleAsync()).Amount);
    }

    [Fact]
    public async Task Cancel_AfterATp4Sale_StillWorks_RestoresStock_AndLeavesTheTaxRowsAlone()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var sale = await SellAsync(f, p, 10, 100m);

        await f.Sales.CancelSaleAsync(sale.Id, f.UserId);

        Assert.Equal(SalesStatus.Cancelled, (await f.W.Db.SalesInvoices.SingleAsync()).Status);
        Assert.Equal(2, await f.W.Db.SalesInvoiceLineTaxes.CountAsync());
        Assert.Equal(1000m, StockQty(f, p.Product.Id));
        Assert.Single(await f.W.Db.StockMovements.Where(m => m.Type == StockMovementType.SaleCancellation).ToListAsync());
    }

    // ------------------------------------------------------------------ API: request, detail, catalog, return detail, register

    private static SalesController Controller(Fx f, params string[] permissions)
    {
        var services = new ServiceCollection().AddLogging().AddMvcCore().Services.AddSingleton(new BranchContext()).BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, f.UserId.ToString())], "test")),
            RequestServices = services,
        };
        var perms = new FakeEffectivePermissions(permissions);
        return new SalesController(f.W.Db, new SalesService(f.W.Db, perms, f.W.Resolver), perms) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static CreateSaleRequest Request(Fx f, (Product Product, ProductUnit Unit) p, decimal qty = 1, decimal price = 100m, Guid? customerId = null,
        decimal? billDiscountPercent = null, decimal? billDiscountAmount = null, decimal billCharges = 0m, decimal? expected = null) =>
        new(customerId ?? f.Local.Id, D, PaymentMethod.Cash, 0, [new CreateSaleLineRequest(p.Product.Id, p.Unit.Id, qty, price, 0)], null, null,
            billDiscountPercent, billDiscountAmount, billCharges, expected);

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    [Fact]
    public async Task Post_NewFieldsAreOptional_AndTheResponseCarriesTaxesStatesAndWarnings()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, hsn: null);
        await SetListPriceAsync(f, p.Unit, 100m);

        var dto = Ok(await Controller(f, "Sales.View", "Sales.Create").Create(Request(f, p, 2, customerId: f.Far.Id), default));

        Assert.Equal((200m, 36m, 236m), (dto.TaxableTotal, dto.TaxTotal, dto.GrandTotal));
        Assert.Equal((PlaceOfSupply.InterState, TaxRegime.Gst, "Maharashtra", "Karnataka"), (dto.PlaceOfSupply, dto.Regime, dto.PlaceOfSupplyStateName, dto.SellerStateName));
        Assert.Equal(PartyTaxStatus.Unspecified, dto.PartyTaxStatus);
        Assert.Equal(["lines[0].hsnCode"], dto.Warnings!.Select(w => w.Field));
        var line = dto.Lines.Single();
        Assert.Equal(["IGST"], line.Taxes!.Select(t => t.Code));
        Assert.All(line.Taxes!, t => Assert.False(t.IsLegacy));
        Assert.Equal(("GST 18%", PlaceOfSupply.InterState, false, null), (line.TaxProfileName, line.PlaceOfSupply, line.PriceIncludesTax, line.HsnCode));
    }

    [Fact]
    public async Task Post_BillDiscountAndChargesAndExpectedTotal_ReachTheService()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SetListPriceAsync(f, p.Unit, 100m);
        var controller = Controller(f, "Sales.View", "Sales.Create", "Sales.BillDiscount");

        var dto = Ok(await controller.Create(Request(f, p, 10, billDiscountPercent: 10m, billCharges: 20m, expected: 1085.60m), default)); // 1,000 - 100 + 20 charges = 920, taxed 18%

        Assert.Equal((10m, 100m, 20m), (dto.BillDiscountPercent, dto.BillDiscountAmount, dto.BillCharges));
        Assert.Equal((920m, 165.60m, 1085.60m), (dto.TaxableTotal, dto.TaxTotal, dto.GrandTotal));
        Assert.Equal((100m, 20m), (dto.Lines.Single().BillDiscountShare, dto.Lines.Single().BillChargeShare));
    }

    [Fact]
    public async Task Post_AStaleExpectedTotal_Is400OnExpectedGrandTotal()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SetListPriceAsync(f, p.Unit, 100m);

        var result = await Controller(f, "Sales.View", "Sales.Create").Create(Request(f, p, 1, expected: 100m), default);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal("The total is now 118.00.", problem.Errors["expectedGrandTotal"].Single());
    }

    [Fact]
    public async Task Post_EveryFieldErrorIsReturned_NotOnlyTheFirst()
    {
        var f = await SetupAsync();
        var a = await ProductAsync(f);
        var b = await ProductAsync(f);
        await SetListPriceAsync(f, a.Unit, 100m);
        await SetListPriceAsync(f, b.Unit, 50m);
        var request = new CreateSaleRequest(f.Local.Id, D, PaymentMethod.Cash, 0,
            [new CreateSaleLineRequest(a.Product.Id, a.Unit.Id, 1, 90m, 0), new CreateSaleLineRequest(b.Product.Id, b.Unit.Id, 1, 40m, 0)]);

        var result = await Controller(f, "Sales.View", "Sales.Create").Create(request, default);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(["lines[0].unitPrice", "lines[1].unitPrice"], problem.Errors.Keys.Order());
    }

    [Fact]
    public async Task Get_LegacyInvoice_HasTheTaxableTotalFallback_NoRegime_AndOneLegacyRow()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, sales: "GST-5");
        var sale = await SellAsync(f, p, 10, 100m, discount: 20m);
        await TaxFixtures.MakeLegacySaleAsync(f.W.Db, sale.Id);
        f.W.Db.ChangeTracker.Clear();

        var dto = Ok(await Controller(f, "Sales.View").Get(sale.Id, default));

        Assert.Equal((980m, null, null, null), (dto.TaxableTotal, dto.PlaceOfSupply, dto.Regime, dto.PlaceOfSupplyStateName));
        Assert.Equal(PartyTaxStatus.Unspecified, dto.PartyTaxStatus);
        var line = dto.Lines.Single();
        var row = Assert.Single(line.Taxes!);
        Assert.Equal((true, "GST 5%", 49m), (row.IsLegacy, row.Code, row.TaxAmount));
        Assert.Equal((null, null, false, null), (line.TaxProfileName, line.PlaceOfSupply, line.PriceIncludesTax, line.HsnCode));
        Assert.Equal((0m, 0m, 0m), (dto.BillDiscountAmount, dto.BillCharges, line.BillDiscountShare));
    }

    [Fact]
    public async Task Catalog_CarriesThePriceBasisAndTheEffectiveHsn()
    {
        var f = await SetupAsync(inclusive: true);
        var beverages = await f.W.AddCategoryAsync("BEV", "Beverages");
        var soft = await f.W.AddSubcategoryAsync(beverages, "SOFT", "Soft Drinks", "2202");
        var own = await ProductAsync(f, hsn: "1006", includes: false);
        var inherits = await ProductAsync(f, hsn: null, subcategoryId: soft.Id);
        var neither = await ProductAsync(f, hsn: null);
        foreach (var x in new[] { own, inherits, neither }) await SetListPriceAsync(f, x.Unit, 10m);

        var items = Ok(await Controller(f, "Sales.Create").Catalog(null, null, default));

        SaleCatalogProductDto Item((Product Product, ProductUnit Unit) x) => items.Single(i => i.ProductId == x.Product.Id);
        Assert.Equal((false, "1006"), (Item(own).PriceIncludesTax, Item(own).HsnCode)); // the product override beats the company's inclusive basis
        Assert.Equal((true, "2202"), (Item(inherits).PriceIncludesTax, Item(inherits).HsnCode)); // the subcategory's HSN
        Assert.Equal((true, null), (Item(neither).PriceIncludesTax, Item(neither).HsnCode));
    }

    [Fact]
    public async Task ReturnDetail_CarriesTheReversedRowsAndThePlaceOfSupply()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var sale = await SellAsync(f, p, 4, 100m);
        var created = await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 1m)]), f.UserId);
        f.W.Db.ChangeTracker.Clear();

        var dto = Ok(await new SalesReturnsController(f.W.Db, f.Sales).Get(created.Id, default));

        var line = dto.Lines.Single();
        Assert.Equal(PlaceOfSupply.IntraState, line.PlaceOfSupply);
        Assert.Equal(["CGST", "SGST"], line.Taxes!.Select(t => t.Code));
        Assert.Equal([9m, 9m], line.Taxes!.Select(t => t.TaxAmount));
        Assert.Equal(line.TaxAmount, line.Taxes!.Sum(t => t.TaxAmount));
    }

    [Fact]
    public async Task SalesRegister_HasTaxableTotalAndTaxByKind_LegacyRowsCountAsOther()
    {
        var f = await SetupAsync();
        var plain = await ProductAsync(f);
        var cess = await ProductAsync(f, sales: (await CustomProfileAsync(f.W)).Code);
        var tp4 = await SellAsync(f, [Line(plain, 10, 100m), Line(cess, 10, 100m)]);
        var legacy = await SellAsync(f, plain, 2, 100m);
        await TaxFixtures.MakeLegacySaleAsync(f.W.Db, legacy.Id);
        f.W.Db.ChangeTracker.Clear();

        var page = Ok(await new ReportsController(f.W.Db, new FakeEffectivePermissions("Reports.Sales")).Sales(null, null, null, null, 1, 20, null, default));

        var rowTp4 = page.Items.Single(r => r.Id == tp4.Id);
        var rowLegacy = page.Items.Single(r => r.Id == legacy.Id);
        Assert.Equal(2000m, rowTp4.TaxableTotal);
        Assert.Equal(
            new Dictionary<TaxComponentKind, decimal> { [TaxComponentKind.Cgst] = 230m, [TaxComponentKind.Sgst] = 230m, [TaxComponentKind.Cess] = 70m },
            rowTp4.TaxByKind!.ToDictionary(t => t.Kind, t => t.TaxAmount)); // CGST 90 + 140, SGST 90 + 140, cess 50 + 20
        Assert.Equal(rowTp4.TaxTotal, rowTp4.TaxByKind!.Sum(t => t.TaxAmount));
        Assert.Equal(200m, rowLegacy.TaxableTotal);
        var other = Assert.Single(rowLegacy.TaxByKind!);
        Assert.Equal((TaxComponentKind.Other, 36m), (other.Kind, other.TaxAmount));
    }

    // ------------------------------------------------------------------ T31a: the rate shown comes from the version valid today

    [Fact]
    public async Task Catalog_AndProductDetail_ShowTheVersionValidToday_NotTheMirrorTax()
    {
        var f = await SetupAsync();
        var five = await ProductAsync(f, sales: "GST-5");
        var eighteen = await ProductAsync(f);
        var cess = await ProductAsync(f, sales: (await CustomProfileAsync(f.W)).Code);
        var gst18 = await f.W.ProfileAsync("GST-18");
        await f.W.Profiles.AddVersionAsync(gst18.Id, TaxWorld.Version(D.AddDays(10), await f.W.GstSetAsync(28m)), null); // future: not valid today
        foreach (var mirror in await f.W.Db.Taxes.ToListAsync()) mirror.RatePercent = 99m; // a stale mirror (rollback copy)
        await f.W.Db.SaveChangesAsync();
        foreach (var x in new[] { five, eighteen, cess }) await SetListPriceAsync(f, x.Unit, 10m);

        var items = Ok(await Controller(f, "Sales.Create").Catalog(null, null, default));
        var detail = Ok(await new ProductsController(f.W.Db, new ProductService(f.W.Db), f.W.Resolver).Get(five.Product.Id, default));

        Assert.Equal(5m, items.Single(i => i.ProductId == five.Product.Id).TaxRatePercent);
        Assert.Equal(18m, items.Single(i => i.ProductId == eighteen.Product.Id).TaxRatePercent);
        Assert.Equal(28m, items.Single(i => i.ProductId == cess.Product.Id).TaxRatePercent);
        Assert.Equal(5m, detail.TaxRatePercent);
    }

    // ------------------------------------------------------------------ T31b: the seller block

    [Fact]
    public async Task Get_CarriesTheSellerBlockFromTheBranch_ForAReaderWithoutMastersView()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var branch = await f.W.Db.Branches.FirstAsync();
        (branch.AddressLine, branch.City, branch.Gstin) = ("12 MG Road", "Bengaluru", "29ABCDE1234F1Z5");
        await f.W.Db.SaveChangesAsync();
        var sale = await SellAsync(f, p, 1, 100m, f.Far);
        f.W.Db.ChangeTracker.Clear();

        var dto = Ok(await Controller(f, "Sales.View").Get(sale.Id, default)); // Sales.View only: no Masters.View, no Settings.View

        Assert.Equal(("Sellora Mart", "12 MG Road, Bengaluru", "29ABCDE1234F1Z5", "GSTIN"), (dto.SellerName, dto.SellerAddress, dto.SellerGstin, dto.SellerTaxIdLabel));
        Assert.Equal(("Karnataka", "29", "Maharashtra", "27"), (dto.SellerStateName, dto.SellerStateGstCode, dto.PlaceOfSupplyStateName, dto.PlaceOfSupplyStateGstCode));
    }

    [Fact]
    public async Task Get_SellerBlock_FallsBackToTheCompany_AndAVatInvoiceLabelsTheTaxIdTrn()
    {
        var f = await SetupAsync("AE");
        var vat5 = (await f.W.Db.TaxProfiles.Include(p => p.Versions).Where(p => p.CountryId == f.W.Uae.Id && p.Treatment == TaxTreatment.Taxable).ToListAsync()).Single(p => p.Versions.Single().TotalPercent == 5m);
        var p = await ProductAsync(f, sales: vat5.Code, country: f.W.Uae);
        f.W.Settings.AddressLine = "Sheikh Zayed Road";
        f.W.Settings.City = "Dubai";
        await f.W.Db.SaveChangesAsync();
        var sale = await SellAsync(f, p, 1, 100m);
        f.W.Db.ChangeTracker.Clear();

        var dto = Ok(await Controller(f, "Sales.View").Get(sale.Id, default));

        Assert.Equal(("TRN", "Sheikh Zayed Road, Dubai"), (dto.SellerTaxIdLabel, dto.SellerAddress));
        Assert.Null(dto.PlaceOfSupplyStateGstCode);
    }
}
