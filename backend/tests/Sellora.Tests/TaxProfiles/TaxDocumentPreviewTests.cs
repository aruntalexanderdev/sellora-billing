using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-2: place of supply, rate sets, price basis, precision, field keys and the bill discount cap of POST /tax/preview (service level).</summary>
public class TaxDocumentPreviewTests
{
    private static readonly DateOnly D = TaxWorld.Today;

    private sealed record Fixture(TaxWorld W, TaxDocumentPreviewService Service, Product Product, ProductUnit Unit);

    private static async Task<Fixture> SetupAsync(string country = "IN", string profile = "GST-18", bool exclusive = true)
    {
        var w = await TaxWorld.CreateAsync(country);
        var p = await w.AddProductAsync("P1", taxId: (await w.MirrorAsync(await w.ProfileAsync(profile, country == "IN" ? w.India : w.Uae))).Id);
        p.PriceIncludesTax = !exclusive;
        var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = p.Id, UnitId = w.Piece.Id, IsBaseUnit = true, ConversionFactorToBase = 1m };
        w.Db.ProductUnits.Add(unit);
        await w.Db.SaveChangesAsync();
        return new Fixture(w, new TaxDocumentPreviewService(w.Db, w.Resolver), p, unit);
    }

    private static TaxDocumentPreviewInput Sale(Fixture f, Guid? party = null, decimal qty = 10, decimal price = 100, decimal discount = 0, decimal? pct = null, decimal? amount = null) =>
        new(TaxDirection.Sale, party, D, null, pct, amount, null, [new TaxPreviewLineInput(f.Product.Id, f.Unit.Id, qty, price, discount)]);

    private static async Task<Customer> CustomerAsync(Fixture f, Guid? state, Guid? country = null)
    {
        var c = new Customer { Id = Guid.NewGuid(), Name = "Cust", StateId = state, CountryId = country };
        f.W.Db.Customers.Add(c);
        await f.W.Db.SaveChangesAsync();
        return c;
    }

    private static async Task<Supplier> SupplierAsync(Fixture f, Guid? state, int precision = 2)
    {
        var cur = await f.W.Db.Currencies.FirstAsync();
        if (precision != cur.DecimalPrecision)
        {
            cur = new Currency { Id = Guid.NewGuid(), Code = "JPY", Name = "Yen", Symbol = "Y", DecimalPrecision = precision };
            f.W.Db.Currencies.Add(cur);
        }
        var s = new Supplier { Id = Guid.NewGuid(), Name = "Supp", CurrencyId = cur.Id, StateId = state };
        f.W.Db.Suppliers.Add(s);
        await f.W.Db.SaveChangesAsync();
        return s;
    }

    private static async Task SetCompanyStateAsync(Fixture f, Guid state)
    {
        f.W.Settings.StateId = state;
        await f.W.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Preview_WalkInSale_IsIntraStateWithCgstAndSgst()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());

        var r = await f.Service.PreviewAsync(Sale(f));

        Assert.Equal(PlaceOfSupply.IntraState, r.PlaceOfSupply);
        Assert.Equal(TaxRegime.Gst, r.Regime);
        Assert.Equal(["CGST", "SGST"], r.Lines[0].Calc.Rows.Select(x => x.Code));
        Assert.Equal((1000m, 180m, 1180m), (r.Totals.TaxableTotal, r.Totals.TaxTotal, r.Totals.GrandTotal));
    }

    [Fact]
    public async Task Preview_CustomerInOtherState_IsInterStateWithIgst()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());
        var customer = await CustomerAsync(f, Guid.NewGuid());

        var r = await f.Service.PreviewAsync(Sale(f, customer.Id));

        Assert.Equal(PlaceOfSupply.InterState, r.PlaceOfSupply);
        var row = Assert.Single(r.Lines[0].Calc.Rows);
        Assert.Equal(("IGST", 180m), (row.Code, row.TaxAmount));
    }

    [Fact]
    public async Task Preview_CustomerInSameState_IsIntraState()
    {
        var f = await SetupAsync();
        var state = Guid.NewGuid();
        await SetCompanyStateAsync(f, state);

        var r = await f.Service.PreviewAsync(Sale(f, (await CustomerAsync(f, state)).Id));

        Assert.Equal(PlaceOfSupply.IntraState, r.PlaceOfSupply);
    }

    [Fact]
    public async Task Preview_GstWithoutBranchOrCompanyState_Fails_OnBranchId()
    {
        var f = await SetupAsync();

        Assert.Equal("branchId", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(Sale(f))));
    }

    [Fact]
    public async Task Preview_SupplierWithoutState_IsIntraStateWithWarning()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());
        var supplier = await SupplierAsync(f, null);

        var r = await f.Service.PreviewAsync(new(TaxDirection.Purchase, supplier.Id, D, false, null, null, null, [new TaxPreviewLineInput(f.Product.Id, f.Unit.Id, 1, 100, 0)]));

        Assert.Equal(PlaceOfSupply.IntraState, r.PlaceOfSupply);
        Assert.Contains(r.Warnings, w => w.Field == "partyId");
    }

    [Fact]
    public async Task Preview_Purchase_UsesPurchaseProfile()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());
        await f.W.Assignments.UpsertAsync(new(TaxScope.Product, f.Product.Id, f.W.India.Id, (await f.W.ProfileAsync("GST-18")).Id, (await f.W.ProfileAsync("GST-5")).Id));
        var supplier = await SupplierAsync(f, null);
        var lines = new[] { new TaxPreviewLineInput(f.Product.Id, f.Unit.Id, 1, 100, 0) };

        var purchase = await f.Service.PreviewAsync(new(TaxDirection.Purchase, supplier.Id, D, false, null, null, null, lines));
        var sale = await f.Service.PreviewAsync(Sale(f, qty: 1));

        Assert.Equal(("GST-5", 5m), (purchase.Lines[0].ProfileCode, purchase.Totals.TaxTotal));
        Assert.Equal(("GST-18", 18m), (sale.Lines[0].ProfileCode, sale.Totals.TaxTotal));
    }

    [Fact]
    public async Task Preview_Purchase_RequiresSupplier_AndUsesItsCurrencyPrecision()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());
        Assert.Equal("partyId", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(new(TaxDirection.Purchase, null, D, null, null, null, null, [new TaxPreviewLineInput(f.Product.Id, f.Unit.Id, 1, 99, 0)]))));

        var supplier = await SupplierAsync(f, null, precision: 0);
        await f.W.Assignments.UpsertAsync(new(TaxScope.Product, f.Product.Id, f.W.India.Id, (await f.W.ProfileAsync("GST-5")).Id, null));
        var r = await f.Service.PreviewAsync(new(TaxDirection.Purchase, supplier.Id, D, false, null, null, null, [new TaxPreviewLineInput(f.Product.Id, f.Unit.Id, 1, 99, 0)]));

        Assert.Equal((0, "JPY"), (r.Precision, r.CurrencyCode));
        Assert.Equal([2m, 2m], r.Lines[0].Calc.Rows.Select(x => x.TaxAmount)); // 2.475 per half at precision 0
    }

    [Fact]
    public async Task Preview_Sale_UsesProductBasisThenCompanyBasis()
    {
        var f = await SetupAsync(exclusive: false);
        await SetCompanyStateAsync(f, Guid.NewGuid());

        var inclusive = await f.Service.PreviewAsync(Sale(f, qty: 1, price: 118));
        Assert.True(inclusive.Lines[0].Calc.PriceIncludesTax);
        Assert.Equal((100m, 118m), (inclusive.Lines[0].Calc.Taxable, inclusive.Totals.GrandTotal));

        f.Product.PriceIncludesTax = null;
        f.W.Settings.SellingPricesIncludeTax = false;
        await f.W.Db.SaveChangesAsync();
        var follows = await f.Service.PreviewAsync(Sale(f, qty: 1, price: 118));
        Assert.False(follows.Lines[0].Calc.PriceIncludesTax);
    }

    [Fact]
    public async Task Preview_Errors_AreKeyedByLineField()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());

        Assert.Equal("lines[0].qty", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(Sale(f, qty: 1.5m)))); // pieces are whole
        Assert.Equal("lines[0].discount", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(Sale(f, qty: 1, price: 10, discount: 11))));
        Assert.Equal("lines[0].productUnitId", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(new(TaxDirection.Sale, null, D, null, null, null, null, [new TaxPreviewLineInput(f.Product.Id, Guid.NewGuid(), 1, 1, 0)]))));
        Assert.Equal("lines", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(new(TaxDirection.Sale, null, D, null, null, null, null, []))));
    }

    [Fact]
    public async Task Preview_DateWithoutVersion_FailsOnLineProductId()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());

        var input = Sale(f) with { Date = new DateOnly(2000, 1, 1) };

        Assert.Equal("lines[0].productId", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(input)));
    }

    [Fact]
    public async Task Preview_BillDiscountAboveCap_FailsOnBillDiscount_AndAtCapPasses()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());
        f.W.Settings.MaxBillDiscountPercent = 10m;
        await f.W.Db.SaveChangesAsync();

        Assert.Equal("billDiscount", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(Sale(f, pct: 12m))));
        Assert.Equal("billDiscount", await TaxWorld.FieldOfAsync(() => f.Service.PreviewAsync(Sale(f, amount: 120m))));
        var ok = await f.Service.PreviewAsync(Sale(f, pct: 10m));
        Assert.Equal(100m, ok.Totals.BillDiscountAmount);
    }

    [Fact]
    public async Task Preview_Vat_IsNotApplicablePlaceWithOneVatRow()
    {
        var f = await SetupAsync("AE", "VAT-5");

        var r = await f.Service.PreviewAsync(Sale(f));

        Assert.Equal((PlaceOfSupply.NotApplicable, TaxRegime.Vat), (r.PlaceOfSupply, r.Regime));
        var row = Assert.Single(r.Lines[0].Calc.Rows);
        Assert.Equal(("VAT", 50m), (row.Code, row.TaxAmount));
    }

    [Fact]
    public async Task Preview_GstTaxableProductWithoutHsn_WarnsOnTheLine()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());

        var r = await f.Service.PreviewAsync(Sale(f));

        Assert.Contains(r.Warnings, w => w.Field == "lines[0].hsnCode");
    }

    [Fact]
    public async Task Preview_ZeroQuantity_ReturnsZeroRows()
    {
        var f = await SetupAsync();
        await SetCompanyStateAsync(f, Guid.NewGuid());

        var r = await f.Service.PreviewAsync(Sale(f, qty: 0));

        Assert.Equal(0m, r.Totals.GrandTotal);
        Assert.Equal(2, r.Lines[0].Calc.Rows.Count);
    }
}
