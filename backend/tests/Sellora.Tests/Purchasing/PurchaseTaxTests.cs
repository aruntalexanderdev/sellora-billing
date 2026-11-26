using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TaxProfiles;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Purchasing;

/// <summary>
/// TP-3: purchases on the tax calculator (docs/features/tax-profiles.md, TP-3 design). Place of supply, purchase profile (BR7), landed cost (AC9),
/// tax-inclusive rates, per-unit cess, foreign currency (rows converted, originals kept), returns (AC15), legacy purchases, the T25 guard,
/// the purchase GET projection and the purchase register. EF InMemory (no locks).
/// </summary>
public class PurchaseTaxTests
{
    private static readonly DateOnly D = TaxWorld.Today;

    private sealed record Fx(TaxWorld W, PurchaseService Service, Guid UserId, State Home, State Other, Supplier Supplier);

    private static async Task<Fx> SetupAsync(string country = "IN", bool ownState = true, bool supplierState = true)
    {
        var w = await TaxWorld.CreateAsync(country);
        var home = new State { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "KA", Name = "Karnataka", Kind = StateKind.State, GstStateCode = "29" };
        var other = new State { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "MH", Name = "Maharashtra", Kind = StateKind.State, GstStateCode = "27" };
        w.Db.States.AddRange(home, other);
        if (ownState) w.Settings.StateId = home.Id;
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = (await w.Db.Currencies.FirstAsync()).Id, StateId = supplierState ? home.Id : null };
        var user = TestUsers.Admin();
        w.Db.AddRange(supplier, user.Role, user);
        await w.Db.SaveChangesAsync();
        return new Fx(w, new PurchaseService(w.Db, new ExchangeRateService(w.Db), null, w.Resolver), user.Id, home, other, supplier);
    }

    /// <summary>A product with a base-unit row. <paramref name="sales"/> and <paramref name="purchase"/> are profile codes: both null = the company default (GST-18).</summary>
    private static async Task<(Product Product, ProductUnit Unit)> ProductAsync(Fx f, string? sales = null, string? purchase = null, string? hsn = "1006", Country? country = null)
    {
        var salesProfile = sales is null ? null : await f.W.ProfileAsync(sales, country);
        var purchaseProfile = purchase is null ? null : await f.W.ProfileAsync(purchase, country);
        var sku = "P" + Guid.NewGuid().ToString("N")[..6];
        var mirror = salesProfile is null ? null : await f.W.MirrorAsync(salesProfile);
        var product = await f.W.AddProductAsync(sku, hsn: hsn, taxId: mirror?.Id);
        if (salesProfile is not null)
        {
            f.W.Db.TaxAssignments.Add(new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = salesProfile.CountryId,
                SalesTaxProfileId = salesProfile.Id, PurchaseTaxProfileId = purchaseProfile?.Id, CreatedAt = DateTime.UtcNow,
            });
        }
        var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = f.W.Piece.Id, Unit = f.W.Piece, IsBaseUnit = true, ConversionFactorToBase = 1m };
        f.W.Db.ProductUnits.Add(unit);
        await f.W.Db.SaveChangesAsync();
        return (product, unit);
    }

    private static PurchaseLineInput Line((Product Product, ProductUnit Unit) p, decimal qty, decimal rate, decimal other = 0, decimal discount = 0) =>
        new(p.Product.Id, p.Unit.Id, qty, rate, other, discount);

    private static Task<PurchaseOrder> BuyAsync(Fx f, IReadOnlyList<PurchaseLineInput> lines, Supplier? supplier = null, DateOnly? date = null, bool inclusive = false,
        decimal? pct = null, decimal? amount = null, decimal charges = 0) =>
        f.Service.CreatePurchaseAsync(new CreatePurchaseInput((supplier ?? f.Supplier).Id, date ?? D, null, null, PaymentMethod.Cash, 0, lines,
            RatesIncludeTax: inclusive, BillDiscountPercent: pct, BillDiscountAmount: amount, BillCharges: charges), f.UserId);

    private static Task<PurchaseOrder> BuyAsync(Fx f, (Product Product, ProductUnit Unit) p, decimal qty, decimal rate, Supplier? supplier = null, bool inclusive = false, DateOnly? date = null) =>
        BuyAsync(f, [Line(p, qty, rate)], supplier, date, inclusive);

    private static async Task<List<PurchaseOrderLineTax>> RowsAsync(Fx f, PurchaseOrderLine line) =>
        (await f.W.Db.PurchaseOrderLineTaxes.AsNoTracking().Where(r => r.PurchaseOrderLineId == line.Id).ToListAsync()).OrderBy(r => r.Kind).ToList();

    private static async Task<DomainValidationException> RejectedAsync(Func<Task> act) => await Assert.ThrowsAsync<DomainValidationException>(act);

    // ------------------------------------------------------------------ AC9 landed cost, header

    [Fact]
    public async Task Create_AC9_BillChargesSpreadOverTheLines_AreInTheTaxableValueAndTheLandedUnitCost()
    {
        var f = await SetupAsync();
        var a = await ProductAsync(f);
        var b = await ProductAsync(f);

        var purchase = await BuyAsync(f, [Line(a, 20, 100m), Line(b, 10, 100m)], charges: 300m); // lines of 2,000 and 1,000, freight 300

        var (l1, l2) = (purchase.Lines.First(l => l.ProductId == a.Product.Id), purchase.Lines.First(l => l.ProductId == b.Product.Id));
        Assert.Equal((200m, 100m), (l1.BillChargeShare, l2.BillChargeShare));
        Assert.Equal((2200m, 1100m), (l1.TaxableValue, l2.TaxableValue));
        Assert.Equal((110m, 110m), (l1.UnitCost, l2.UnitCost)); // landed cost per base unit, tax excluded
        Assert.Equal([110m, 110m], (await f.W.Db.PurchaseBatches.ToListAsync()).Select(x => x.UnitCost));
        Assert.Equal([198m, 198m], (await RowsAsync(f, l1)).Select(r => r.TaxAmount)); // CGST = SGST = 9% of 2,200
        Assert.Equal([99m, 99m], (await RowsAsync(f, l2)).Select(r => r.TaxAmount));
        Assert.Equal(594m, purchase.TaxTotal);
        Assert.Equal((3000m, 300m, 0m, 3300m, 3894m), (purchase.Subtotal, purchase.OtherChargesTotal, purchase.DiscountTotal, purchase.TaxableTotal, purchase.GrandTotal));
        Assert.Equal((300m, 300m, 3894m), (purchase.BillCharges, purchase.OriginalBillCharges, purchase.OriginalGrandTotal));
        Assert.Equal(purchase.Lines.Sum(l => l.LineTotal), purchase.GrandTotal);
        Assert.Equal(20m, StockItemQty(f, a.Product.Id));
    }

    private static decimal StockItemQty(Fx f, Guid productId) => f.W.Db.StockItems.Single(s => s.ProductId == productId).QuantityOnHand;

    [Fact]
    public async Task Create_BillDiscountPercent_ReducesTheTaxableValueAndLandedCost()
    {
        var f = await SetupAsync();
        var a = await ProductAsync(f);
        var b = await ProductAsync(f);

        var purchase = await BuyAsync(f, [Line(a, 6, 100m), Line(b, 4, 100m)], pct: 10m); // 1,000 less 10% = 100

        Assert.Equal(10m, purchase.BillDiscountPercent);
        Assert.Equal((100m, 100m, 100m), (purchase.BillDiscountAmount, purchase.OriginalBillDiscountAmount, purchase.DiscountTotal));
        var l1 = purchase.Lines.First(l => l.ProductId == a.Product.Id);
        Assert.Equal((60m, 540m, 90m), (l1.BillDiscountShare, l1.TaxableValue, l1.UnitCost));
        Assert.Equal((900m, 162m, 1062m), (purchase.TaxableTotal, purchase.TaxTotal, purchase.GrandTotal));
    }

    // ------------------------------------------------------------------ place of supply, profiles

    [Fact]
    public async Task Create_SameStateSupplier_IsIntraStateWithCgstAndSgstRowsAndFullSnapshot()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, hsn: "1006");

        var purchase = await BuyAsync(f, p, 10, 100m);

        var line = purchase.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal([TaxComponentKind.Cgst, TaxComponentKind.Sgst], rows.Select(r => r.Kind));
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.TaxComponentId);
            Assert.Equal(TaxTreatment.Taxable, r.Treatment);
            Assert.Equal(9m, r.RatePercent);
            Assert.Equal((1000m, 90m), (r.BaseAmount, r.TaxAmount));
            Assert.Equal((1000m, 90m), (r.OriginalBaseAmount, r.OriginalTaxAmount));
        });
        Assert.Equal((1000m, 180m, 1180m, 18m), (line.TaxableValue, line.TaxAmount, line.LineTotal, line.TaxRatePercent));
        Assert.Equal(PlaceOfSupply.IntraState, line.PlaceOfSupply);
        Assert.Equal("1006", line.HsnCode);
        Assert.False(line.PriceIncludesTax);
        var profile = await f.W.ProfileAsync("GST-18");
        Assert.Equal((profile.Id, profile.Versions.Single().Id, profile.LegacyTaxId), (line.TaxProfileId, line.TaxProfileVersionId, line.TaxId));
        Assert.Equal((100m, 0m, 0m, 0m), (line.OriginalRate, line.OriginalDiscount, line.OriginalOtherCharges, line.OriginalBillChargeShare));
        Assert.Equal((1000m, 180m, 1180m), (line.OriginalTaxableValue, line.OriginalTaxAmount, line.OriginalLineTotal));
        Assert.Equal((PlaceOfSupply.IntraState, f.Home.Id, f.Home.Id, PartyTaxStatus.Unspecified), (purchase.PlaceOfSupply, purchase.PlaceOfSupplyStateId, purchase.SupplierStateId, purchase.PartyTaxStatus));
        Assert.Equal((1180m, 1180m), (purchase.GrandTotal, purchase.OriginalGrandTotal));
        Assert.Empty(purchase.Warnings);
    }

    [Fact]
    public async Task Create_SupplierInAnotherState_IsInterStateWithOneIgstRow()
    {
        var f = await SetupAsync();
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Pune Traders", CurrencyId = f.Supplier.CurrencyId, StateId = f.Other.Id };
        f.W.Db.Suppliers.Add(supplier);
        await f.W.Db.SaveChangesAsync();
        var p = await ProductAsync(f);

        var purchase = await BuyAsync(f, p, 10, 100m, supplier);

        var row = Assert.Single(await RowsAsync(f, purchase.Lines.Single()));
        Assert.Equal((TaxComponentKind.Igst, 18m, 180m), (row.Kind, row.RatePercent, row.TaxAmount));
        Assert.Equal(PlaceOfSupply.InterState, purchase.Lines.Single().PlaceOfSupply);
        Assert.Equal((PlaceOfSupply.InterState, f.Home.Id, f.Other.Id), (purchase.PlaceOfSupply, purchase.PlaceOfSupplyStateId, purchase.SupplierStateId));
        Assert.Equal(1180m, purchase.GrandTotal);
    }

    [Fact]
    public async Task Create_AC13_PurchaseProfileDiffersFromSales_UsesItsRatesAndMirrorTaxId_AndEmptyPurchaseProfileUsesSales()
    {
        var f = await SetupAsync();
        var sellsAt18BuysAt5 = await ProductAsync(f, sales: "GST-18", purchase: "GST-5");
        var sameBoth = await ProductAsync(f, sales: "GST-18");

        var purchase = await BuyAsync(f, [Line(sellsAt18BuysAt5, 10, 100m), Line(sameBoth, 10, 100m)]);

        var five = await f.W.ProfileAsync("GST-5");
        var eighteen = await f.W.ProfileAsync("GST-18");
        var l1 = purchase.Lines.First(l => l.ProductId == sellsAt18BuysAt5.Product.Id);
        var l2 = purchase.Lines.First(l => l.ProductId == sameBoth.Product.Id);
        Assert.Equal((five.Id, five.LegacyTaxId, 5m, 50m), (l1.TaxProfileId, l1.TaxId, l1.TaxRatePercent, l1.TaxAmount)); // the purchase profile's mirror, not the sales one
        Assert.Equal([25m, 25m], (await RowsAsync(f, l1)).Select(r => r.TaxAmount));
        Assert.Equal((eighteen.Id, eighteen.LegacyTaxId, 18m, 180m), (l2.TaxProfileId, l2.TaxId, l2.TaxRatePercent, l2.TaxAmount));
    }

    [Fact]
    public async Task Create_Vat_HasOneVatRowAndNoPlaceOfSupply()
    {
        var f = await SetupAsync("AE");
        var vat5 = (await f.W.Db.TaxProfiles.Include(p => p.Versions).Where(p => p.CountryId == f.W.Uae.Id && p.Treatment == TaxTreatment.Taxable).ToListAsync()).Single(p => p.Versions.Single().TotalPercent == 5m);
        var p = await ProductAsync(f, sales: vat5.Code, country: f.W.Uae);

        var purchase = await BuyAsync(f, p, 10, 100m);

        var row = Assert.Single(await RowsAsync(f, purchase.Lines.Single()));
        Assert.Equal((TaxComponentKind.Vat, 50m, TaxTreatment.Taxable), (row.Kind, row.TaxAmount, row.Treatment));
        Assert.Equal((PlaceOfSupply.NotApplicable, null, null), (purchase.PlaceOfSupply, purchase.PlaceOfSupplyStateId, purchase.SupplierStateId));
        Assert.Equal((1000m, 50m, 1050m), (purchase.TaxableTotal, purchase.TaxTotal, purchase.GrandTotal));
    }

    [Fact]
    public async Task Create_ExemptProfile_StoresOneZeroRowWithTheTreatment_AndTheCostHasNoTax()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, sales: "GST-EXEMPT");

        var purchase = await BuyAsync(f, p, 4, 25m);

        var line = purchase.Lines.Single();
        var row = Assert.Single(await RowsAsync(f, line));
        Assert.Equal((TaxTreatment.Exempt, 0m, null), (row.Treatment, row.TaxAmount, row.TaxComponentId));
        Assert.Equal((100m, 0m, 100m, 25m), (line.TaxableValue, line.TaxAmount, line.LineTotal, line.UnitCost));
    }

    // ------------------------------------------------------------------ price basis

    [Fact]
    public async Task Create_AC3_InclusiveRates_BackCalculateTheTaxableValue()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var purchase = await BuyAsync(f, p, 1, 118m, inclusive: true);

        var line = purchase.Lines.Single();
        Assert.True(purchase.RatesIncludeTax);
        Assert.True(line.PriceIncludesTax);
        Assert.Equal([9m, 9m], (await RowsAsync(f, line)).Select(r => r.TaxAmount));
        Assert.Equal((100m, 18m, 118m, 100m), (line.TaxableValue, line.TaxAmount, line.LineTotal, line.UnitCost));
        Assert.Equal((118m, 100m, 118m), (purchase.Subtotal, purchase.TaxableTotal, purchase.GrandTotal)); // Subtotal is the tax-inclusive gross (BR21)
        Assert.Equal(118m, purchase.OriginalGrandTotal);
    }

    [Fact]
    public async Task Create_AC4_InclusiveFivePercentOf99_PaysExactly99_AndCgstEqualsSgst()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, sales: "GST-5");

        var purchase = await BuyAsync(f, p, 1, 99m, inclusive: true);

        var line = purchase.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal(rows[0].TaxAmount, rows[1].TaxAmount);
        Assert.Equal(99m, line.TaxableValue + rows.Sum(r => r.TaxAmount));
        Assert.Equal((99m, 99m), (line.LineTotal, purchase.GrandTotal));
    }

    // ------------------------------------------------------------------ per-unit cess

    [Fact]
    public async Task Create_AC5_PercentAndPerUnitCess_AllRowsStored_PerUnitBaseAmountIsTheQuantity()
    {
        var f = await SetupAsync();
        var profile = await CustomProfileAsync(f.W);
        var p = await ProductAsync(f, sales: profile.Code);

        var purchase = await BuyAsync(f, p, 10, 100m);

        var line = purchase.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal(4, rows.Count);
        decimal Tax(string code) => rows.Single(r => r.ComponentCode == code).TaxAmount;
        Assert.Equal((140m, 140m, 50m, 20m), (Tax("CGST"), Tax("SGST"), Tax("CESS"), Tax("CESS-UNIT")));
        var perUnit = rows.Single(r => r.ComponentCode == "CESS-UNIT");
        Assert.Equal((TaxCalculationMethod.AmountPerBaseUnit, 2m, 10m), (perUnit.Method, perUnit.AmountPerBaseUnit, perUnit.BaseAmount)); // a quantity, not money
        Assert.Equal((1000m, 350m, 1350m), (line.TaxableValue, line.TaxAmount, line.LineTotal));
        Assert.Equal(33m, line.TaxRatePercent); // Σ applied percent rates (14 + 14 + 5)
        Assert.Equal(100m, line.UnitCost);
    }

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

    // ------------------------------------------------------------------ rounding, validation, warnings

    [Fact]
    public async Task Create_DQTP22_DomesticGrossAndTaxAreNowRoundedToTheCurrencyPrecision()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var purchase = await BuyAsync(f, p, 1, 10.005m); // before TP-3 the unrounded 10.005 was stored

        var line = purchase.Lines.Single();
        Assert.Equal((10.01m, 1.80m, 11.81m), (line.TaxableValue, line.TaxAmount, line.LineTotal)); // CGST 0.90 + SGST 0.90
        Assert.Equal(11.81m, purchase.GrandTotal);
    }

    [Fact]
    public async Task Create_RateWithMoreThanFourDecimals_Is400OnTheLineRate_AndNothingIsStocked()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var ex = await RejectedAsync(() => BuyAsync(f, p, 1, 1.23456m));

        Assert.Contains(ex.Errors, e => e.Field == "lines[0].rate");
        Assert.Empty(await f.W.Db.PurchaseOrders.ToListAsync());
        Assert.Empty(await f.W.Db.PurchaseBatches.ToListAsync());
    }

    [Fact]
    public async Task Create_OverPreciseDiscount_AndBothBillDiscounts_AreFieldErrors()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var discount = await RejectedAsync(() => BuyAsync(f, [Line(p, 1, 100m, discount: 1.005m)]));
        var both = await RejectedAsync(() => BuyAsync(f, [Line(p, 1, 100m)], pct: 5m, amount: 5m));

        Assert.Contains(discount.Errors, e => e.Field == "lines[0].discount");
        Assert.Contains(both.Errors, e => e.Field == "billDiscountPercent"); // the request field, not the calculator's billDiscount
    }

    [Fact]
    public async Task Create_LineErrors_AreKeyedByLineIndex()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var ex = await RejectedAsync(() => BuyAsync(f, [Line(p, 1, 100m), Line(p, 0, 100m), new PurchaseLineInput(p.Product.Id, Guid.NewGuid(), 1, 1, 0, 0)]));

        Assert.Equal(["lines[1].qty", "lines[2].productUnitId"], ex.Errors.Select(e => e.Field));
    }

    [Fact]
    public async Task Create_BR5_PurchaseDatedBeforeTheFirstVersion_Is400OnTheLine()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var ex = await RejectedAsync(() => BuyAsync(f, p, 1, 100m, date: new DateOnly(2017, 6, 30))); // GST starts 2017-07-01

        var error = Assert.Single(ex.Errors);
        Assert.Equal("lines[0].productId", error.Field);
        Assert.Contains("no version valid on 2017-06-30", error.Message);
    }

    [Fact]
    public async Task Create_SupplierWithoutState_SavesAsIntraStateWithAWarning()
    {
        var f = await SetupAsync(supplierState: false);
        var p = await ProductAsync(f);

        var purchase = await BuyAsync(f, p, 10, 100m);

        Assert.Equal(PlaceOfSupply.IntraState, purchase.PlaceOfSupply);
        Assert.Null(purchase.SupplierStateId);
        var warning = Assert.Single(purchase.Warnings);
        Assert.Equal("supplierId", warning.Field);
    }

    [Fact]
    public async Task Create_GstProductWithoutHsn_SavesWithAWarningOnTheLine()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, hsn: null);

        var purchase = await BuyAsync(f, p, 1, 100m);

        Assert.Null(purchase.Lines.Single().HsnCode);
        Assert.Contains(purchase.Warnings, w => w.Field == "lines[0].hsnCode");
    }

    [Fact]
    public async Task Create_GstWithoutOwnState_Is400OnBranchId_AndNothingIsSaved()
    {
        var f = await SetupAsync(ownState: false);
        var p = await ProductAsync(f);

        var ex = await RejectedAsync(() => BuyAsync(f, p, 1, 100m));

        Assert.Equal("branchId", ex.Field);
        Assert.Empty(await f.W.Db.PurchaseOrders.ToListAsync());
    }

    // ------------------------------------------------------------------ foreign currency

    private static async Task<Supplier> UsdSupplierAsync(Fx f, decimal rate = 83.20m)
    {
        var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$" };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Global Ingredients Inc.", CurrencyId = usd.Id, StateId = f.Home.Id };
        f.W.Db.AddRange(usd, supplier, new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = rate, EffectiveDate = new DateOnly(2026, 1, 1), CreatedByUserId = f.UserId });
        await f.W.Db.SaveChangesAsync();
        return supplier;
    }

    [Fact]
    public async Task Create_ForeignCurrency_ConvertsEachPartAndRow_KeepsOriginals_AndTheRowsSumToTheLineTax()
    {
        var f = await SetupAsync();
        var usd = await UsdSupplierAsync(f);
        var p = await ProductAsync(f);

        var purchase = await BuyAsync(f, p, 3, 10.10m, usd); // USD 30.30 + CGST 2.73 + SGST 2.73 (0.09 x 30.30 = 2.727)

        var line = purchase.Lines.Single();
        var rows = await RowsAsync(f, line);
        Assert.Equal([2.73m, 2.73m], rows.Select(r => r.OriginalTaxAmount));
        Assert.Equal([227.14m, 227.14m], rows.Select(r => r.TaxAmount)); // 2.73 x 83.20 = 227.136, each row converted on its own
        Assert.Equal(rows.Sum(r => r.TaxAmount), line.TaxAmount);
        Assert.Equal((2520.96m, 454.28m, 2975.24m), (line.TaxableValue, line.TaxAmount, line.LineTotal));
        Assert.Equal((10.10m, 840.32m), (line.OriginalRate, line.Rate));
        Assert.Equal((30.30m, 5.46m, 35.76m), (line.OriginalTaxableValue, line.OriginalTaxAmount, line.OriginalLineTotal));
        Assert.Equal((2975.24m, 35.76m, 83.20m), (purchase.GrandTotal, purchase.OriginalGrandTotal, purchase.ExchangeRateToBase));
        Assert.Equal(purchase.Lines.Sum(l => l.OriginalLineTotal), purchase.OriginalGrandTotal);
        Assert.Equal(StockCostOf(line), line.UnitCost);
    }

    private static decimal StockCostOf(PurchaseOrderLine line) => Math.Round(line.TaxableValue / line.BaseQty, 6, MidpointRounding.AwayFromZero);

    [Fact]
    public async Task Return_ForeignPurchase_ReversesOriginalsAtTheDocumentPrecision_AndTheReturnsSumToTheOriginal()
    {
        var f = await SetupAsync();
        var usd = await UsdSupplierAsync(f);
        var p = await ProductAsync(f);
        var purchase = await BuyAsync(f, p, 3, 10.10m, usd);
        var line = purchase.Lines.Single();

        var first = await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(line.Id, null, 1m)]), f.UserId);
        var second = await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(line.Id, null, 2m)]), f.UserId);

        var (r1, r2) = (first.Lines.Single(), second.Lines.Single());
        Assert.Equal((10.10m, 1.82m), (r1.OriginalTaxableValue, r1.OriginalTaxAmount)); // 1/3 of 30.30 and of 2 x 2.73 (0.91 + 0.91)
        Assert.Equal((line.OriginalTaxableValue, line.OriginalTaxAmount), (r1.OriginalTaxableValue + r2.OriginalTaxableValue, r1.OriginalTaxAmount + r2.OriginalTaxAmount));
        Assert.Equal((line.TaxableValue, line.TaxAmount), (r1.TaxableValue + r2.TaxableValue, r1.TaxAmount + r2.TaxAmount));
        Assert.Equal(purchase.GrandTotal, first.Total + second.Total);
        Assert.Equal(purchase.OriginalGrandTotal, first.OriginalTotal + second.OriginalTotal); // the document-currency credit is the sum of the reversed originals
        Assert.Equal(first.Total, r1.LineTotal);
        Assert.Equal(r1.TaxAmount, first.Lines.Single().Taxes.Sum(t => t.TaxAmount));
        Assert.All(first.Lines.Single().Taxes, t => Assert.NotNull(t.OriginalTaxAmount));
        Assert.Equal((purchase.GrandTotal, purchase.OriginalGrandTotal), (purchase.ReturnedAmount, purchase.OriginalReturnedAmount));
    }

    [Fact]
    public async Task Create_BillDiscountOverTheBill_IsKeyedByTheFieldTheCallerSent()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var amount = await RejectedAsync(() => BuyAsync(f, [Line(p, 1, 100m)], amount: 500m));
        var charges = await RejectedAsync(() => BuyAsync(f, [Line(p, 1, 100m)], charges: 1.005m));

        Assert.Equal(["billDiscountAmount"], amount.Errors.Select(e => e.Field));
        Assert.Contains(charges.Errors, e => e.Field == "billCharges");
    }

    [Fact]
    public async Task Post_OldStyleBodyWithOverPreciseDiscount_Is400OnTheLineDiscount()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var result = await Controller(f).Create(new CreatePurchaseRequest(
            f.Supplier.Id, D, null, null, PaymentMethod.Cash, 0, [new CreatePurchaseLineRequest(p.Product.Id, p.Unit.Id, 1, 100m, 0, 2.505m)]), default);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Contains("lines[0].discount", problem.Errors.Keys);
        Assert.Empty(await f.W.Db.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task Create_ForeignInclusiveLine_BaseAndOriginalReconcileWithinOneBaseUnit_AndHeaderSumsAreExact()
    {
        var f = await SetupAsync();
        var usd = await UsdSupplierAsync(f);
        var a = await ProductAsync(f);
        var b = await ProductAsync(f, sales: "GST-5");

        var purchase = await BuyAsync(f, [Line(a, 3, 33.33m), Line(b, 7, 12.34m)], usd, inclusive: true);

        foreach (var line in purchase.Lines)
        {
            Assert.True(Math.Abs(line.OriginalTaxableValue!.Value * purchase.ExchangeRateToBase - line.TaxableValue) <= 1m);
            Assert.Equal(line.TaxableValue + line.TaxAmount, line.LineTotal);
            Assert.Equal(line.TaxAmount, (await RowsAsync(f, line)).Sum(r => r.TaxAmount));
        }
        Assert.Equal(purchase.Lines.Sum(l => l.LineTotal), purchase.GrandTotal);
        Assert.Equal(purchase.Lines.Sum(l => l.TaxableValue), purchase.TaxableTotal);
        Assert.Equal(purchase.Lines.Sum(l => l.TaxAmount), purchase.TaxTotal);
        Assert.Equal(purchase.Lines.Sum(l => l.OriginalLineTotal), purchase.OriginalGrandTotal);
    }

    [Fact]
    public async Task Return_ForeignPurchaseFullyReturnedInTwo_OriginalAndBaseReturnedTotalsEqualTheDocumentTotals()
    {
        var f = await SetupAsync();
        var usd = await UsdSupplierAsync(f);
        var p = await ProductAsync(f);
        var purchase = await BuyAsync(f, p, 7, 13.37m, usd);
        var lineId = purchase.Lines.Single().Id;

        await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(lineId, null, 3m)]), f.UserId);
        await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(lineId, null, 4m)]), f.UserId);

        Assert.Equal(purchase.OriginalGrandTotal, purchase.OriginalReturnedAmount);
        Assert.Equal(purchase.GrandTotal, purchase.ReturnedAmount);
        Assert.Equal((0m, 0m), (purchase.NetTotal, purchase.OriginalNetTotal));
    }

    // ------------------------------------------------------------------ returns (AC15)

    [Fact]
    public async Task Return_AC15_EachRowIsReversedProportionally_AndTheReturnsSumExactlyToTheOriginalRows()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var purchase = await BuyAsync(f, p, 7, 135.71m); // taxable 949.97: CGST = SGST = 85.50
        var line = purchase.Lines.Single();

        var returns = new List<PurchaseReturn>();
        foreach (var qty in new[] { 2m, 2m, 3m })
        {
            returns.Add(await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(line.Id, null, qty)]), f.UserId));
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
        Assert.Equal(line.OriginalTaxAmount, returnLines.Sum(l => l.OriginalTaxAmount));
        Assert.Equal(line.OriginalTaxableValue, returnLines.Sum(l => l.OriginalTaxableValue));
        Assert.Equal((line.TaxProfileId, line.TaxProfileVersionId, line.PlaceOfSupply), (returnLines[0].TaxProfileId, returnLines[0].TaxProfileVersionId, returnLines[0].PlaceOfSupply));
        Assert.All(returnLines.SelectMany(l => l.Taxes), t => Assert.Equal(TaxTreatment.Taxable, t.Treatment));
        Assert.Equal(purchase.OriginalGrandTotal, returns.Sum(r => r.OriginalTotal));
        Assert.Equal(purchase.GrandTotal, returns.Sum(r => r.Total));
    }

    [Fact]
    public async Task Return_PerUnitCessRow_ReversesTheQuantityAndTheAmount()
    {
        var f = await SetupAsync();
        var profile = await CustomProfileAsync(f.W);
        var p = await ProductAsync(f, sales: profile.Code);
        var purchase = await BuyAsync(f, p, 10, 100m);

        var ret = await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 4m)]), f.UserId);

        var perUnit = ret.Lines.Single().Taxes.Single(t => t.ComponentCode == "CESS-UNIT");
        Assert.Equal((4m, 8m), (perUnit.BaseAmount, perUnit.TaxAmount));
        Assert.Equal(0.4m * 350m, ret.Lines.Single().TaxAmount);
    }

    [Fact]
    public async Task Return_OfALegacyPurchase_EqualsTheOldProportionAndKeepsTheLegacyRowShape()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, sales: "GST-5");
        var purchase = await BuyAsync(f, p, 3, 33.33m);
        await TaxFixtures.MakeLegacyPurchaseAsync(f.W.Db, purchase.Id); // history: one legacy row, no profile, no document-currency twins
        var line = purchase.Lines.Single();

        var first = await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(line.Id, null, 1m)]), f.UserId);
        var second = await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(line.Id, null, 2m)]), f.UserId);

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
            Assert.Null(row.OriginalTaxAmount);
            Assert.Equal((r.TaxableValue, r.TaxAmount), (row.BaseAmount, row.TaxAmount));
            Assert.Null(r.TaxProfileId);
            Assert.Null(r.OriginalTaxableValue);
        }
        Assert.Equal(first.Total, first.OriginalTotal); // legacy rule: domestic credit = base credit
        Assert.Equal(purchase.OriginalGrandTotal, first.OriginalTotal + second.OriginalTotal);
    }

    [Fact]
    public async Task Cancel_AfterTp3Purchase_StillWorks_AndLeavesTheTaxRowsAlone()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var purchase = await BuyAsync(f, p, 10, 100m);

        await f.Service.CancelPurchaseAsync(purchase.Id, f.UserId);

        Assert.Equal(PurchaseStatus.Cancelled, (await f.W.Db.PurchaseOrders.SingleAsync()).Status);
        Assert.Equal(2, await f.W.Db.PurchaseOrderLineTaxes.CountAsync());
    }

    // ------------------------------------------------------------------ T25

    [Fact]
    public async Task T25_AVersionOnOrBeforeAPurchaseDateCannotReplaceTheProfilesVersion_ButALaterOneCan()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var date = D.AddDays(-3);
        await BuyAsync(f, p, 1, 100m, date: date);
        var profile = await f.W.ProfileAsync("GST-18");

        var ex = await RejectedAsync(() => f.W.Profiles.AddVersionAsync(profile.Id, TaxWorld.Version(date, f.W.GstSetAsync(20m).Result), null));

        Assert.Equal("effectiveFrom", ex.Field);
        Assert.Contains("Documents dated up to", ex.Message);
        Assert.NotNull(await f.W.Profiles.AddVersionAsync(profile.Id, TaxWorld.Version(date.AddDays(1), await f.W.GstSetAsync(20m)), null));
    }

    // ------------------------------------------------------------------ API projection and register

    private static PurchasesController Controller(Fx f)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, f.UserId.ToString())], "test")) };
        return new PurchasesController(f.W.Db, f.Service, new FakeEffectivePermissions("Purchase.View", "Purchase.Create")) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    [Fact]
    public async Task Post_NewFieldsAreOptional_AndTheResponseCarriesTaxesAndWarnings()
    {
        var f = await SetupAsync(supplierState: false);
        var p = await ProductAsync(f, hsn: null);
        var controller = Controller(f);

        var dto = Ok(await controller.Create(new CreatePurchaseRequest(
            f.Supplier.Id, D, null, null, PaymentMethod.Cash, 0, [new CreatePurchaseLineRequest(p.Product.Id, p.Unit.Id, 2, 100m, 0, 0)]), default));

        Assert.Equal((200m, 36m, 236m), (dto.TaxableTotal, dto.TaxTotal, dto.GrandTotal));
        Assert.False(dto.RatesIncludeTax);
        Assert.Equal((PlaceOfSupply.IntraState, "Karnataka", null), (dto.PlaceOfSupply, dto.PlaceOfSupplyStateName, dto.SupplierStateName));
        Assert.Equal(PartyTaxStatus.Unspecified, dto.PartyTaxStatus);
        Assert.Equal(["supplierId", "lines[0].hsnCode"], dto.Warnings!.Select(w => w.Field));
        var line = dto.Lines.Single();
        Assert.Equal(["CGST", "SGST"], line.Taxes!.Select(t => t.Code));
        Assert.All(line.Taxes!, t => Assert.False(t.IsLegacy));
        Assert.Equal(("GST 18%", PlaceOfSupply.IntraState, false), (line.TaxProfileName, line.PlaceOfSupply, line.PriceIncludesTax));
        Assert.Equal((236m, 200m), (line.OriginalLineTotal, line.OriginalTaxableValue));
    }

    [Fact]
    public async Task Post_InclusiveBillDiscountAndCharges_ReachTheService()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);

        var dto = Ok(await Controller(f).Create(new CreatePurchaseRequest(
            f.Supplier.Id, D, null, null, PaymentMethod.Cash, 0, [new CreatePurchaseLineRequest(p.Product.Id, p.Unit.Id, 1, 118m, 0, 0)],
            RatesIncludeTax: true, BillDiscountAmount: 18m, BillCharges: 0m), default));

        Assert.True(dto.RatesIncludeTax);
        Assert.Equal((18m, 18m, 100m), (dto.BillDiscountAmount, dto.OriginalBillDiscountAmount, dto.Lines.Single().LineTotal)); // 118 - 18 = 100 inclusive
        Assert.True(dto.Lines.Single().PriceIncludesTax);
        Assert.Equal(18m, dto.Lines.Single().BillDiscountShare);
    }

    [Fact]
    public async Task Get_LegacyPurchase_HasTaxableTotalFallbackAndALegacyRow()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, sales: "GST-5");
        var purchase = await BuyAsync(f, [Line(p, 10, 100m, other: 50m, discount: 20m)]);
        await TaxFixtures.MakeLegacyPurchaseAsync(f.W.Db, purchase.Id);
        f.W.Db.ChangeTracker.Clear();

        var dto = Ok(await Controller(f).Get(purchase.Id, null, default));

        Assert.Equal(1030m, dto.TaxableTotal); // Subtotal 1,000 + other charges 50 - discount 20 (the fallback of TP3-Q1)
        Assert.Null(dto.PlaceOfSupply);
        var tax = Assert.Single(dto.Lines.Single().Taxes!);
        Assert.True(tax.IsLegacy);
        Assert.Null(dto.Lines.Single().TaxProfileId);
        Assert.Null(dto.Lines.Single().OriginalTaxableValue);
        Assert.Empty(dto.Warnings!);
    }

    [Fact]
    public async Task PurchaseReturnDetail_CarriesTheReversedRows_AndTheOriginals()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var purchase = await BuyAsync(f, p, 10, 100m);
        var ret = await f.Service.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 5m)]), f.UserId);
        f.W.Db.ChangeTracker.Clear();

        var dto = Ok(await new PurchaseReturnsController(f.W.Db, f.Service).Get(ret.Id, default));

        var line = dto.Lines.Single();
        Assert.Equal([TaxComponentKind.Cgst, TaxComponentKind.Sgst], line.Taxes!.Select(t => t.Kind));
        Assert.All(line.Taxes!, t => Assert.Equal((45m, 45m, false), (t.TaxAmount, t.OriginalTaxAmount, t.IsLegacy)));
        Assert.Equal((PlaceOfSupply.IntraState, 500m, 90m), (line.PlaceOfSupply, line.OriginalTaxableValue, line.OriginalTaxAmount));
        Assert.NotNull(line.TaxProfileId);
    }

    [Fact]
    public async Task PurchaseRegister_HasTaxableTotalAndTaxByKind_LegacyRowsCountAsOther()
    {
        var f = await SetupAsync();
        var plain = await ProductAsync(f);
        var cess = await ProductAsync(f, sales: (await CustomProfileAsync(f.W)).Code);
        var tp3 = await BuyAsync(f, [Line(plain, 10, 100m), Line(cess, 10, 100m)]);
        var legacy = await BuyAsync(f, plain, 2, 100m);
        await TaxFixtures.MakeLegacyPurchaseAsync(f.W.Db, legacy.Id);
        f.W.Db.ChangeTracker.Clear();

        var page = Ok(await new ReportsController(f.W.Db, new FakeEffectivePermissions("Reports.Purchase")).Purchases(null, null, null, null, 1, 20, null, default));

        var rowTp3 = page.Items.Single(r => r.Id == tp3.Id);
        var rowLegacy = page.Items.Single(r => r.Id == legacy.Id);
        Assert.Equal(2000m, rowTp3.TaxableTotal);
        Assert.Equal(
            new Dictionary<TaxComponentKind, decimal> { [TaxComponentKind.Cgst] = 230m, [TaxComponentKind.Sgst] = 230m, [TaxComponentKind.Cess] = 70m },
            rowTp3.TaxByKind!.ToDictionary(t => t.Kind, t => t.TaxAmount)); // CGST 90 + 140, SGST 90 + 140, cess 50 + 20
        Assert.Equal(rowTp3.TaxTotal, rowTp3.TaxByKind!.Sum(t => t.TaxAmount));
        Assert.Equal(200m, rowLegacy.TaxableTotal);
        var other = Assert.Single(rowLegacy.TaxByKind!);
        Assert.Equal((TaxComponentKind.Other, 36m), (other.Kind, other.TaxAmount));
    }
}
