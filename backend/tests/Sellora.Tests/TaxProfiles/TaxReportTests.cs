using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.TaxProfiles;

/// <summary>
/// TP-5a tax reports (docs/features/tax-profiles.md, "TP-5 design"): GST summary, HSN summary and VAT summary. Documents come from the real sale and purchase
/// services (so the rows are what the calculator wrote) or, for history, are built by hand; every expected figure is computed by hand. EF InMemory.
/// </summary>
public class TaxReportTests
{
    private static readonly DateOnly D = TaxWorld.Today; // 2026-11-24
    private static readonly DateOnly NovFrom = new(2026, 11, 1);
    private static readonly DateOnly NovTo = new(2026, 11, 30);
    private static readonly DateOnly OctFrom = new(2026, 10, 2); // the stock purchases of the fixtures are on 1 October
    private static readonly DateOnly OctTo = new(2026, 10, 31);
    private static readonly DateOnly DecFrom = new(2026, 12, 1);
    private static readonly DateOnly DecTo = new(2026, 12, 31);
    private const string CompanyGstin = "29COMPANY0000Z1";
    private const string BranchGstin = "29AABCS1234A1Z5";

    private sealed record Fx(
        TaxWorld W, SalesService Sales, PurchaseService Purchases, Guid UserId, State Home, State Other, Supplier Supplier, Customer Walk, Customer Local, Customer Far, Unit Kg);

    private static async Task<Fx> SetupAsync(string country = "IN", bool inclusive = false, bool branchGstin = false)
    {
        var w = await TaxWorld.CreateAsync(country);
        var home = new State { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "KA", Name = "Karnataka", Kind = StateKind.State, GstStateCode = "29" };
        var other = new State { Id = Guid.NewGuid(), CountryId = w.India.Id, Code = "MH", Name = "Maharashtra", Kind = StateKind.State, GstStateCode = "27" };
        var dubai = new State { Id = Guid.NewGuid(), CountryId = w.Uae.Id, Code = "DU", Name = "Dubai", Kind = StateKind.Emirate };
        w.Db.States.AddRange(home, other, dubai);
        w.Settings.StateId = country == "IN" ? home.Id : dubai.Id;
        w.Settings.Gstin = CompanyGstin;
        w.Settings.SellingPricesIncludeTax = inclusive;
        if (branchGstin) (await w.Db.Branches.SingleAsync(b => b.Id == DefaultTenant.BranchId)).Gstin = BranchGstin;
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = (await w.Db.Currencies.FirstAsync()).Id, StateId = country == "IN" ? home.Id : null };
        var walk = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var local = new Customer { Id = Guid.NewGuid(), Name = "Local Stores", CountryId = w.India.Id, StateId = home.Id };
        var far = new Customer { Id = Guid.NewGuid(), Name = "Mumbai Traders", CountryId = w.India.Id, StateId = other.Id };
        var kg = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
        w.Piece.Uqc = "PCS";
        var user = TestUsers.Admin();
        w.Db.AddRange(supplier, walk, local, far, kg, user.Role, user);
        await w.Db.SaveChangesAsync();
        var perms = new FakeEffectivePermissions("Sales.Create", "Sales.BillDiscount", "Sales.PriceOverride");
        return new Fx(w, new SalesService(w.Db, perms, w.Resolver), new PurchaseService(w.Db, new ExchangeRateService(w.Db), null, w.Resolver), user.Id,
            home, other, supplier, walk, local, far, kg);
    }

    /// <summary>A product with a base-unit row (<paramref name="unit"/> = piece unless given) and stock bought a month before the report period (October).</summary>
    private static async Task<(Product Product, ProductUnit Unit)> ProductAsync(
        Fx f, string? sales = null, string? hsn = "1006", bool? includes = null, Country? country = null, Unit? unit = null, decimal stock = 1000m, TaxProfile? profile = null)
    {
        var salesProfile = profile ?? (sales is null ? null : await f.W.ProfileAsync(sales, country));
        var product = await f.W.AddProductAsync("P" + Guid.NewGuid().ToString("N")[..6], hsn: hsn, taxId: salesProfile is null ? null : (await f.W.MirrorAsync(salesProfile)).Id);
        product.PriceIncludesTax = includes;
        var baseUnit = unit ?? f.W.Piece;
        product.BaseUnitId = baseUnit.Id;
        if (salesProfile is not null)
        {
            f.W.Db.TaxAssignments.Add(new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = salesProfile.CountryId, SalesTaxProfileId = salesProfile.Id, CreatedAt = DateTime.UtcNow,
            });
        }
        var row = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = baseUnit.Id, Unit = baseUnit, IsBaseUnit = true, ConversionFactorToBase = 1m };
        f.W.Db.ProductUnits.Add(row);
        await f.W.Db.SaveChangesAsync();
        if (stock > 0)
        {
            await f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 10, 1), null, null, PaymentMethod.Cash, 0,
                [new PurchaseLineInput(product.Id, row.Id, stock, 1m, 0, 0)]), f.UserId);
        }
        return (product, row);
    }

    private static async Task<SalesInvoice> SellAsync(Fx f, IReadOnlyList<((Product Product, ProductUnit Unit) P, decimal Qty, decimal Price)> lines, Customer? customer = null, DateOnly? date = null)
    {
        foreach (var l in lines)
        {
            var open = await f.W.Db.SellingPrices.Where(p => p.ProductUnitId == l.P.Unit.Id && p.EffectiveTo == null).ToListAsync();
            foreach (var p in open) p.EffectiveTo = DateTimeOffset.UtcNow;
            f.W.Db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = l.P.Unit.Id, Price = l.Price, CreatedByUserId = f.UserId });
        }
        await f.W.Db.SaveChangesAsync();
        return await f.Sales.CreateSaleAsync(new CreateSaleInput(
            (customer ?? f.Local).Id, date ?? D, PaymentMethod.Cash, 0m, lines.Select(l => new SaleLineInput(l.P.Product.Id, l.P.Unit.Id, l.Qty, l.Price, 0m)).ToList(), null, null, true), f.UserId);
    }

    private static Task<SalesInvoice> SellAsync(Fx f, (Product Product, ProductUnit Unit) p, decimal qty, decimal price, Customer? customer = null, DateOnly? date = null) =>
        SellAsync(f, [(p, qty, price)], customer, date);

    private static Task<PurchaseOrder> BuyAsync(Fx f, (Product Product, ProductUnit Unit) p, decimal qty, decimal rate, Supplier? supplier = null, DateOnly? date = null) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput((supplier ?? f.Supplier).Id, date ?? D, null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(p.Product.Id, p.Unit.Id, qty, rate, 0, 0)]), f.UserId);

    private static TaxReportsController Controller(SelloraDbContext db) => new(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static T Ok<T>(ActionResult<T> r) => (T)Assert.IsType<OkObjectResult>(r.Result).Value!;

    private static async Task<GstSummaryDto> GstAsync(Fx f, DateOnly from, DateOnly to, Guid? branchId = null) =>
        Ok(await Controller(f.W.Db).GstSummary(from, to, branchId, default));

    private static async Task<HsnSummaryDto> HsnAsync(Fx f, DateOnly from, DateOnly to, HsnDirection direction = HsnDirection.Sales) =>
        Ok(await Controller(f.W.Db).HsnSummary(from, to, direction, null, default));

    private static async Task<VatSummaryDto> VatAsync(Fx f, DateOnly from, DateOnly to) => Ok(await Controller(f.W.Db).VatSummary(from, to, null, default));

    private static GstSummaryRowDto Row(GstSummaryDto r, GstReportSection section) => Assert.Single(r.Rows, x => x.Section == section);

    private static GstGstinBlockDto Block(GstSummaryDto r, string? gstin) => Assert.Single(r.Blocks, b => b.Gstin == gstin);

    // ------------------------------------------------------------------ GST: intra, inter, cess, inclusive, zero treatments

    [Fact]
    public async Task GstSummary_IntraStateSale_SplitsCgstAndSgstAtTheSumOfTheRates()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SellAsync(f, p, 10, 100m); // 1,000 + CGST 90 + SGST 90

        var report = await GstAsync(f, NovFrom, NovTo);

        var row = Row(report, GstReportSection.Outward);
        Assert.Equal((CompanyGstin, f.Home.Id, "Karnataka", "29"), (row.Gstin, row.PlaceOfSupplyStateId, row.PlaceOfSupplyStateName, row.PlaceOfSupplyStateCode));
        Assert.Equal((PlaceOfSupply.IntraState, TaxTreatment.Taxable, 18m, false), (row.SupplyType, row.Treatment, row.RatePercent, row.IsLegacy));
        Assert.Equal((1000m, 90m, 90m, 0m, 0m, 0m, 1), (row.TaxableValue, row.Cgst, row.SgstUtgst, row.Igst, row.Cess, row.LegacyTax, row.DocumentCount));
        var block = Block(report, CompanyGstin);
        Assert.Equal(new TaxReportAmountsDto(1000m, 90m, 90m, 0m, 0m, 0m, 180m), block.OutwardNet);
        Assert.Equal(new TaxReportAmountsDto(0m, 0m, 0m, 0m, 0m, 0m, 0m), block.InwardNet);
        Assert.Equal(["NoPartyGstin", "InputTaxAsInvoiced"], report.Limitations.Select(l => l.Code));
        Assert.Equal("INR", report.CurrencyCode);
    }

    [Fact]
    public async Task GstSummary_InterStateSale_ShowsIgstAtTheRateOfTheIgstComponent()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SellAsync(f, p, 10, 100m, f.Far); // 1,000 + IGST 180

        var row = Row(await GstAsync(f, NovFrom, NovTo), GstReportSection.Outward);

        Assert.Equal((f.Other.Id, "27", PlaceOfSupply.InterState, 18m), (row.PlaceOfSupplyStateId, row.PlaceOfSupplyStateCode, row.SupplyType, row.RatePercent));
        Assert.Equal((1000m, 0m, 0m, 180m), (row.TaxableValue, row.Cgst, row.SgstUtgst, row.Igst));
    }

    [Fact]
    public async Task GstSummary_Cess_IsReportedAsAnAmountAndNotAddedToTheRate()
    {
        var f = await SetupAsync();
        var profile = await CustomProfileAsync(f.W); // CGST 14 + SGST 14 (IGST 28) + cess 5% + cess 2 per unit
        var p = await ProductAsync(f, profile: profile);
        await SellAsync(f, p, 10, 100m); // 1,000: CGST 140 + SGST 140 + cess 50 + 20

        var row = Row(await GstAsync(f, NovFrom, NovTo), GstReportSection.Outward);

        Assert.Equal(28m, row.RatePercent);
        Assert.Equal((1000m, 140m, 140m, 0m, 70m), (row.TaxableValue, row.Cgst, row.SgstUtgst, row.Igst, row.Cess));
    }

    [Fact]
    public async Task GstSummary_InclusivePrices_ReportTheTaxExclusiveValue()
    {
        var f = await SetupAsync(inclusive: true);
        var p = await ProductAsync(f);
        await SellAsync(f, p, 10, 118m); // 1,180 inclusive = 1,000 + 90 + 90

        var row = Row(await GstAsync(f, NovFrom, NovTo), GstReportSection.Outward);

        Assert.Equal((1000m, 90m, 90m), (row.TaxableValue, row.Cgst, row.SgstUtgst));
    }

    [Fact]
    public async Task GstSummary_NilRatedAndExemptLines_FallInTheirOwnTreatmentBuckets()
    {
        var f = await SetupAsync();
        var nil = await ProductAsync(f, "GST-NIL", hsn: null);
        var exempt = await ProductAsync(f, "GST-EXEMPT", hsn: null);
        var taxable = await ProductAsync(f);
        await SellAsync(f, [(nil, 5, 100m), (exempt, 3, 100m), (taxable, 1, 100m)]);

        var rows = (await GstAsync(f, NovFrom, NovTo)).Rows;

        Assert.Equal(3, rows.Count);
        var nilRow = Assert.Single(rows, r => r.Treatment == TaxTreatment.NilRated);
        Assert.Equal((500m, 0m, 0m, 0m, 0m, 0m, 0m), (nilRow.TaxableValue, nilRow.Cgst, nilRow.SgstUtgst, nilRow.Igst, nilRow.Cess, nilRow.LegacyTax, nilRow.RatePercent));
        var exemptRow = Assert.Single(rows, r => r.Treatment == TaxTreatment.Exempt);
        Assert.Equal((300m, 0m), (exemptRow.TaxableValue, exemptRow.RatePercent));
        var taxableRow = Assert.Single(rows, r => r.Treatment == TaxTreatment.Taxable);
        Assert.Equal((100m, 9m, 9m), (taxableRow.TaxableValue, taxableRow.Cgst, taxableRow.SgstUtgst));
        Assert.All(rows, r => Assert.Equal(1, r.DocumentCount)); // one invoice, once per bucket
    }

    [Fact]
    public async Task GstSummary_TaxableValueIsCountedOncePerLine_WhateverTheNumberOfTaxRows()
    {
        var f = await SetupAsync();
        var profile = await CustomProfileAsync(f.W); // five tax rows per line on an intra-state sale
        var p = await ProductAsync(f, profile: profile);
        await SellAsync(f, p, 1, 100m);

        var report = await GstAsync(f, NovFrom, NovTo);

        Assert.Equal(100m, Assert.Single(report.Rows).TaxableValue);
        Assert.Equal(100m, Block(report, CompanyGstin).OutwardNet.TaxableValue);
    }

    [Fact]
    public async Task GstSummary_SellerGstin_IsTheBranchsElseTheCompanys_AndBranchesWithTheSameGstinMerge()
    {
        var f = await SetupAsync(branchGstin: true);
        var p = await ProductAsync(f);
        await SellAsync(f, p, 10, 100m);

        var report = await GstAsync(f, NovFrom, NovTo);

        Assert.Equal(BranchGstin, Assert.Single(report.Rows).Gstin);
        Assert.Equal([BranchGstin], report.Blocks.Select(b => b.Gstin));

        // A second branch with no GSTIN of its own falls back to the company's; one with the same GSTIN merges into the first row.
        var blr = BranchBuilder.Extra("BLR");
        var twin = BranchBuilder.Extra("TWIN");
        twin.Gstin = BranchGstin;
        f.W.Db.Branches.AddRange(blr, twin);
        AddLegacySale(f, D, blr.Id, 100m, 12m, 12m);
        AddLegacySale(f, D, twin.Id, 200m, 12m, 24m);
        await f.W.Db.SaveChangesAsync();

        var after = await GstAsync(f, NovFrom, NovTo);

        Assert.Equal([BranchGstin, CompanyGstin], after.Blocks.Select(b => b.Gstin).Order(StringComparer.Ordinal));
        Assert.Equal(1000m + 200m + 0m, Block(after, BranchGstin).OutwardNet.TaxableValue); // Main + TWIN
        Assert.Equal(100m, Block(after, CompanyGstin).OutwardNet.TaxableValue);
    }

    // ------------------------------------------------------------------ legacy (history) rows

    private static (SalesInvoice Invoice, SalesInvoiceLine Line) AddLegacySale(Fx f, DateOnly date, Guid branchId, decimal taxable, decimal ratePercent, decimal tax, bool backfilledProfile = false)
    {
        var invoice = new SalesInvoice { Id = Guid.NewGuid(), InvoiceNo = "H-" + Guid.NewGuid().ToString("N")[..6], BranchId = branchId, InvoiceDate = date, TaxTotal = tax, GrandTotal = taxable + tax };
        var line = new SalesInvoiceLine
        {
            Id = Guid.NewGuid(), SalesInvoiceId = invoice.Id, ProductId = Guid.NewGuid(), TaxId = Guid.NewGuid(), TaxRatePercent = ratePercent, TaxableValue = taxable, TaxAmount = tax,
            LineTotal = taxable + tax, BaseQty = 1m, Qty = 1m, TaxProfileId = backfilledProfile ? Guid.NewGuid() : null, // TP-5a backfill: profile but no version
        };
        line.Taxes.Add(new SalesInvoiceLineTax
        {
            Id = Guid.NewGuid(), SalesInvoiceLineId = line.Id, TaxComponentId = null, ComponentCode = $"GST {ratePercent}%", Kind = TaxComponentKind.Other, Method = TaxCalculationMethod.Percent,
            RatePercent = ratePercent, BaseAmount = taxable, TaxAmount = tax, Treatment = null,
        });
        invoice.Lines.Add(line);
        f.W.Db.SalesInvoices.Add(invoice);
        return (invoice, line);
    }

    [Fact]
    public async Task GstSummary_LegacyRows_AreOneUnsplitBucketPerRate_BackfilledOrNot()
    {
        var f = await SetupAsync();
        AddLegacySale(f, D, DefaultTenant.BranchId, 100m, 12m, 12m);
        AddLegacySale(f, D, DefaultTenant.BranchId, 300m, 12m, 36m, backfilledProfile: true);
        AddLegacySale(f, D, DefaultTenant.BranchId, 200m, 5m, 10m);
        await f.W.Db.SaveChangesAsync();

        var rows = (await GstAsync(f, NovFrom, NovTo)).Rows.OrderBy(r => r.RatePercent).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((true, null, null, 0m, 0m, 0m, 0m), (r.IsLegacy, r.Treatment, r.SupplyType, r.Cgst, r.SgstUtgst, r.Igst, r.Cess)));
        Assert.Equal((5m, 200m, 10m, 1), (rows[0].RatePercent, rows[0].TaxableValue, rows[0].LegacyTax, rows[0].DocumentCount));
        Assert.Equal((12m, 400m, 48m, 2), (rows[1].RatePercent, rows[1].TaxableValue, rows[1].LegacyTax, rows[1].DocumentCount));
    }

    [Fact]
    public async Task GstSummary_LegacyAndNewDocuments_AreNeverMixedInOneRow()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SellAsync(f, p, 10, 100m);
        AddLegacySale(f, D, DefaultTenant.BranchId, 100m, 18m, 18m);
        await f.W.Db.SaveChangesAsync();

        var rows = (await GstAsync(f, NovFrom, NovTo)).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(1000m, Assert.Single(rows, r => !r.IsLegacy).TaxableValue);
        var legacy = Assert.Single(rows, r => r.IsLegacy);
        Assert.Equal((100m, 18m, 18m), (legacy.TaxableValue, legacy.LegacyTax, legacy.RatePercent));
    }

    // ------------------------------------------------------------------ returns

    [Fact]
    public async Task GstSummary_PartialAndFullReturns_AreReportedInThePeriodOfTheReturnDate_AtTheOriginalsPlaceOfSupply()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var sale = await SellAsync(f, p, 10, 100m, f.Far, new DateOnly(2026, 10, 20)); // October, inter-state: 1,000 + IGST 180
        var line = sale.Lines.Single();
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, new DateOnly(2026, 11, 3), null, [new SaleReturnLineInput(line.Id, null, 4m)]), f.UserId); // 400 + IGST 72
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, new DateOnly(2026, 11, 20), null, [new SaleReturnLineInput(line.Id, null, 6m)]), f.UserId); // 600 + IGST 108

        var oct = await GstAsync(f, OctFrom, OctTo);
        Assert.DoesNotContain(oct.Rows, r => r.Section == GstReportSection.OutwardReturn);
        Assert.Equal(1000m, Block(oct, CompanyGstin).OutwardNet.TaxableValue);

        var nov = await GstAsync(f, NovFrom, NovTo);
        Assert.DoesNotContain(nov.Rows, r => r.Section == GstReportSection.Outward); // the sale is not in November
        var ret = Row(nov, GstReportSection.OutwardReturn); // both returns in one row: same state, supply type, rate
        Assert.Equal((f.Other.Id, PlaceOfSupply.InterState, 18m), (ret.PlaceOfSupplyStateId, ret.SupplyType, ret.RatePercent));
        Assert.Equal((1000m, 0m, 0m, 180m, 2), (ret.TaxableValue, ret.Cgst, ret.SgstUtgst, ret.Igst, ret.DocumentCount));
        Assert.Equal(new TaxReportAmountsDto(-1000m, 0m, 0m, -180m, 0m, 0m, -180m), Block(nov, CompanyGstin).OutwardNet);
    }

    [Fact]
    public async Task GstSummary_ReturnInALaterPeriod_ReducesThatPeriodsNet_AndLeavesTheSalesPeriodAlone()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var sale = await SellAsync(f, p, 10, 100m);
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 4m)]), f.UserId); // same month: 400 + 36 + 36

        var both = await GstAsync(f, NovFrom, NovTo);

        Assert.Equal((1000m, 90m), (Row(both, GstReportSection.Outward).TaxableValue, Row(both, GstReportSection.Outward).Cgst));
        Assert.Equal((400m, 36m, 36m), (Row(both, GstReportSection.OutwardReturn).TaxableValue, Row(both, GstReportSection.OutwardReturn).Cgst, Row(both, GstReportSection.OutwardReturn).SgstUtgst));
        Assert.Equal(new TaxReportAmountsDto(600m, 54m, 54m, 0m, 0m, 0m, 108m), Block(both, CompanyGstin).OutwardNet);
        var totals = Block(both, CompanyGstin).Sections;
        Assert.Equal((1, 1), (totals.Single(s => s.Section == GstReportSection.Outward).DocumentCount, totals.Single(s => s.Section == GstReportSection.OutwardReturn).DocumentCount));
    }

    [Fact]
    public async Task GstSummary_CancelledSales_AreExcluded()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        var kept = await SellAsync(f, p, 1, 100m);
        var cancelled = await SellAsync(f, p, 5, 100m);
        await f.Sales.CancelSaleAsync(cancelled.Id, f.UserId);

        var report = await GstAsync(f, NovFrom, NovTo);

        var row = Row(report, GstReportSection.Outward);
        Assert.Equal((100m, 1), (row.TaxableValue, row.DocumentCount));
        Assert.Equal(kept.Id, (await f.W.Db.SalesInvoices.SingleAsync(i => i.Status == SalesStatus.Completed && i.InvoiceDate == D)).Id);
    }

    // ------------------------------------------------------------------ purchases

    [Fact]
    public async Task GstSummary_Purchases_AreInwardSupplies_AndPurchaseReturnsReduceTheInwardNet()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f, stock: 0m);
        var purchase = await BuyAsync(f, p, 20, 50m); // 1,000 + 90 + 90
        await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 5m)]), f.UserId); // 250 + 22.5 + 22.5

        var report = await GstAsync(f, NovFrom, NovTo);

        var inward = Row(report, GstReportSection.Inward);
        Assert.Equal((CompanyGstin, f.Home.Id, PlaceOfSupply.IntraState, 1000m, 90m, 90m), (inward.Gstin, inward.PlaceOfSupplyStateId, inward.SupplyType, inward.TaxableValue, inward.Cgst, inward.SgstUtgst));
        var ret = Row(report, GstReportSection.InwardReturn);
        Assert.Equal((250m, 22.5m, 22.5m), (ret.TaxableValue, ret.Cgst, ret.SgstUtgst));
        Assert.Equal(new TaxReportAmountsDto(750m, 67.5m, 67.5m, 0m, 0m, 0m, 135m), Block(report, CompanyGstin).InwardNet);
        Assert.Equal(new TaxReportAmountsDto(0m, 0m, 0m, 0m, 0m, 0m, 0m), Block(report, CompanyGstin).OutwardNet);
    }

    [Fact]
    public async Task GstSummary_ForeignCurrencyPurchase_IsReportedInTheBaseCurrency()
    {
        var f = await SetupAsync();
        var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$" };
        var usdSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Global Ingredients Inc.", CurrencyId = usd.Id, StateId = f.Home.Id };
        f.W.Db.AddRange(usd, usdSupplier, new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = 83.20m, EffectiveDate = new DateOnly(2026, 1, 1), CreatedByUserId = f.UserId });
        await f.W.Db.SaveChangesAsync();
        var p = await ProductAsync(f, stock: 0m);
        await BuyAsync(f, p, 10, 10m, usdSupplier); // USD 100 + 9 + 9 = INR 8,320.00 + 748.80 + 748.80

        var report = await GstAsync(f, NovFrom, NovTo);

        var row = Row(report, GstReportSection.Inward);
        Assert.Equal((8320.00m, 748.80m, 748.80m), (row.TaxableValue, row.Cgst, row.SgstUtgst));
        Assert.Equal("INR", report.CurrencyCode);
    }

    [Fact]
    public async Task GstSummary_DocumentsOutsideThePeriod_AreLeftOut_AndBoundsAreInclusive()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SellAsync(f, p, 1, 100m, date: new DateOnly(2026, 11, 1));
        await SellAsync(f, p, 2, 100m, date: new DateOnly(2026, 11, 30));
        await SellAsync(f, p, 4, 100m, date: new DateOnly(2026, 12, 1));

        Assert.Equal(300m, Row(await GstAsync(f, NovFrom, NovTo), GstReportSection.Outward).TaxableValue);
        Assert.Equal(400m, Row(await GstAsync(f, DecFrom, DecTo), GstReportSection.Outward).TaxableValue);
    }

    // ------------------------------------------------------------------ HSN summary

    [Fact]
    public async Task HsnSummary_AddsQuantitiesOnlyWithinOneHsnAndOneBaseUnit_NetOfReturns()
    {
        var f = await SetupAsync();
        var a = await ProductAsync(f, hsn: "1006");
        var b = await ProductAsync(f, hsn: "1006");
        var c = await ProductAsync(f, hsn: "1006", unit: f.Kg); // same HSN, another base unit: its own row
        var d = await ProductAsync(f, hsn: "1905");
        var e = await ProductAsync(f, hsn: null); // pre-HSN: not recorded
        var sale = await SellAsync(f, [(a, 10, 100m), (b, 5, 40m), (c, 2.5m, 80m), (d, 1, 50m), (e, 3, 10m)]);

        var report = await HsnAsync(f, NovFrom, NovTo);

        Assert.Equal(4, report.Rows.Count);
        var rice = Assert.Single(report.Rows, r => r.HsnCode == "1006" && r.UnitCode == "pc");
        Assert.Equal(("PCS", 18m, 15m, 1200m, 108m, 108m, 0m, 1416m), (rice.Uqc, rice.RatePercent, rice.BaseQty, rice.TaxableValue, rice.Cgst, rice.SgstUtgst, rice.Igst, rice.TotalValue));
        var riceKg = Assert.Single(report.Rows, r => r.HsnCode == "1006" && r.UnitCode == "kg");
        Assert.Equal(("kg", 2.5m, 200m, 18m, 18m, 236m), (riceKg.Uqc, riceKg.BaseQty, riceKg.TaxableValue, riceKg.Cgst, riceKg.SgstUtgst, riceKg.TotalValue)); // no UQC: the unit code
        var snack = Assert.Single(report.Rows, r => r.HsnCode == "1905");
        Assert.Equal((1m, 50m, 4.5m, 59m), (snack.BaseQty, snack.TaxableValue, snack.Cgst, snack.TotalValue));
        var none = Assert.Single(report.Rows, r => r.HsnCode is null);
        Assert.Equal((3m, 30m, 35.4m), (none.BaseQty, none.TaxableValue, none.TotalValue));
        Assert.Equal(["HsnNotRecorded"], report.Limitations.Select(l => l.Code));
        Assert.Equal(1416m + 236m + 59m + 35.4m, report.Totals.TotalValue); // value totals only, never a quantity total
        Assert.Equal(1200m + 200m + 50m + 30m, report.Totals.TaxableValue);

        // A return of 3 pieces of the first product lowers that HSN's quantity and values (3 x 100 = 300 + 27 + 27).
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(sale.Lines.First(l => l.ProductId == a.Product.Id).Id, null, 3m)]), f.UserId);
        var net = Assert.Single((await HsnAsync(f, NovFrom, NovTo)).Rows, r => r.HsnCode == "1006" && r.UnitCode == "pc");
        Assert.Equal((12m, 900m, 81m, 81m, 1062m), (net.BaseQty, net.TaxableValue, net.Cgst, net.SgstUtgst, net.TotalValue));
    }

    [Fact]
    public async Task HsnSummary_Purchases_UseTheirOwnLines()
    {
        var f = await SetupAsync();
        var a = await ProductAsync(f, hsn: "1006", stock: 0m);
        var b = await ProductAsync(f, hsn: "1006", stock: 0m);
        await BuyAsync(f, a, 10, 20m);
        await BuyAsync(f, b, 4, 25m);
        await SellAsync(f, await ProductAsync(f, hsn: "1006"), 1, 100m); // a sale must not leak into the purchase report

        var report = await HsnAsync(f, NovFrom, NovTo, HsnDirection.Purchases);

        var row = Assert.Single(report.Rows);
        Assert.Equal(("1006", 14m, 300m, 27m, 27m), (row.HsnCode, row.BaseQty, row.TaxableValue, row.Cgst, row.SgstUtgst));
        Assert.Equal(HsnDirection.Purchases, report.Direction);
    }

    [Fact]
    public async Task HsnSummary_LegacyLines_AreHsnNotRecorded()
    {
        var f = await SetupAsync();
        AddLegacySale(f, D, DefaultTenant.BranchId, 100m, 12m, 12m);
        await f.W.Db.SaveChangesAsync();

        // The legacy line's product no longer exists in the unit master here: give it a base unit so the report can name it.
        var product = await f.W.AddProductAsync("LEG1", hsn: null);
        var line = await f.W.Db.SalesInvoiceLines.SingleAsync();
        line.ProductId = product.Id;
        await f.W.Db.SaveChangesAsync();

        var row = Assert.Single((await HsnAsync(f, NovFrom, NovTo)).Rows);
        Assert.Equal((null, 12m, 100m, 12m, 112m), (row.HsnCode, row.RatePercent, row.TaxableValue, row.LegacyTax, row.TotalValue));
    }

    // ------------------------------------------------------------------ VAT summary

    private static async Task<(Fx F, Country Uae)> VatWorldAsync()
    {
        var f = await SetupAsync("AE");
        return (f, f.W.Uae);
    }

    [Fact]
    public async Task VatSummary_SplitsStandardZeroAndExempt_AndNetsOutputAndInputVat()
    {
        var (f, uae) = await VatWorldAsync();
        var std = await ProductAsync(f, "VAT-5", hsn: null, country: uae);
        var zero = await ProductAsync(f, "VAT-ZERO", hsn: null, country: uae);
        var exempt = await ProductAsync(f, "VAT-EXEMPT", hsn: null, country: uae);
        var sale = await SellAsync(f, [(std, 10, 100m), (zero, 2, 100m), (exempt, 3, 100m)], f.Walk); // 1,000 + 50 | 200 | 300
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, [new SaleReturnLineInput(sale.Lines.First(l => l.ProductId == std.Product.Id).Id, null, 2m)]), f.UserId); // 200 + 10
        await BuyAsync(f, std, 20, 25m); // 500 + 25

        var report = await VatAsync(f, NovFrom, NovTo);

        var output = report.Rows.Where(r => r.Section == VatReportSection.Output).OrderBy(r => r.Category).ToList();
        Assert.Equal([VatCategory.StandardRated, VatCategory.ZeroRated, VatCategory.Exempt], output.Select(r => r.Category));
        Assert.Equal((1000m, 50m, 5m), (output[0].TaxableValue, output[0].VatAmount, output[0].RatePercent));
        Assert.Equal((200m, 0m, 0m), (output[1].TaxableValue, output[1].VatAmount, output[1].RatePercent));
        Assert.Equal((300m, 0m), (output[2].TaxableValue, output[2].VatAmount));
        Assert.All(output, r => Assert.Equal((f.W.Settings.StateId, "Dubai"), (r.RegionStateId, r.RegionName)));
        var ret = Assert.Single(report.Rows, r => r.Section == VatReportSection.OutputReturn);
        Assert.Equal((VatCategory.StandardRated, 200m, 10m), (ret.Category, ret.TaxableValue, ret.VatAmount));
        Assert.Equal(new VatNetDto(1300m, 40m), report.OutputVat); // taxable 1,000 + 200 + 300 - 200; VAT 50 - 10
        Assert.Equal(new VatNetDto(500m, 25m), report.InputVat);
        Assert.Equal(["NoPartyTaxRegistration"], report.Limitations.Select(l => l.Code));
    }

    [Fact]
    public async Task VatSummary_LegacyRows_GoToTheLegacyBucketByRate_AndTheGstReportIsEmptyForAVatCompany()
    {
        var (f, _) = await VatWorldAsync();
        AddLegacySale(f, D, DefaultTenant.BranchId, 100m, 5m, 5m);
        AddLegacySale(f, D, DefaultTenant.BranchId, 200m, 5m, 10m);
        await f.W.Db.SaveChangesAsync();

        var vat = await VatAsync(f, NovFrom, NovTo);
        var row = Assert.Single(vat.Rows);
        Assert.Equal((VatCategory.Legacy, 5m, 300m, 15m, 2), (row.Category, row.RatePercent, row.TaxableValue, row.VatAmount, row.DocumentCount));

        Assert.Empty((await GstAsync(f, NovFrom, NovTo)).Rows); // the branch is in a VAT country
        Assert.Empty((await HsnAsync(f, NovFrom, NovTo)).Rows);
    }

    [Fact]
    public async Task VatSummary_ForAGstCompany_IsEmpty()
    {
        var f = await SetupAsync();
        var p = await ProductAsync(f);
        await SellAsync(f, p, 1, 100m);

        Assert.Empty((await VatAsync(f, NovFrom, NovTo)).Rows);
    }

    // ------------------------------------------------------------------ period, branch and company scope

    private static async Task<ValidationProblemDetails> BadRequestAsync(Task<ActionResult<GstSummaryDto>> call)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>((await call).Result);
        Assert.Equal(400, result.StatusCode ?? 400);
        return Assert.IsAssignableFrom<ValidationProblemDetails>(result.Value);
    }

    [Fact]
    public async Task Reports_PeriodRules_DatesAreRequired_ReversedAndOverlongSpansAre400OnDateTo()
    {
        var f = await SetupAsync();
        var c = Controller(f.W.Db);

        Assert.Equal(["dateFrom", "dateTo"], (await BadRequestAsync(c.GstSummary(null, null, null, default))).Errors.Keys.Order());
        Assert.Equal(["dateTo"], (await BadRequestAsync(Controller(f.W.Db).GstSummary(NovTo, NovFrom, null, default))).Errors.Keys);
        Assert.Equal(["dateTo"], (await BadRequestAsync(Controller(f.W.Db).GstSummary(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 2), null, default))).Errors.Keys); // 367 days

        // 366 days inclusive (a leap year, or 1 Jan to 1 Jan) is allowed, and so is a one-day period.
        Assert.IsType<OkObjectResult>((await Controller(f.W.Db).GstSummary(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), null, default)).Result);
        Assert.IsType<OkObjectResult>((await Controller(f.W.Db).GstSummary(D, D, null, default)).Result);

        Assert.Equal(["dateTo"], Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(
            (await Controller(f.W.Db).HsnSummary(NovTo, NovFrom, HsnDirection.Sales, null, default)).Result).Value).Errors.Keys);
        Assert.Equal(["dateTo"], Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(
            (await Controller(f.W.Db).VatSummary(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 2), null, default)).Result).Value).Errors.Keys);
    }

    [Fact]
    public async Task GstSummary_BranchFilter_ReportsOneBranch_AndAnInaccessibleBranchIs403()
    {
        var f = await SetupAsync();
        var blr = BranchBuilder.Extra("BLR");
        f.W.Db.Branches.Add(blr);
        AddLegacySale(f, D, DefaultTenant.BranchId, 100m, 12m, 12m);
        AddLegacySale(f, D, blr.Id, 700m, 12m, 84m);
        await f.W.Db.SaveChangesAsync();

        Assert.Equal(800m, (await GstAsync(f, NovFrom, NovTo)).Rows.Sum(r => r.TaxableValue));
        var blrOnly = await GstAsync(f, NovFrom, NovTo, blr.Id);
        Assert.Equal((700m, blr.Id), (blrOnly.Rows.Sum(r => r.TaxableValue), blrOnly.BranchId));

        // A caller allowed in the main branch only: sees its documents, and asking for BLR is the house 403.
        var scope = new BranchContext();
        scope.Set(null, new HashSet<Guid> { DefaultTenant.BranchId }, DefaultTenant.BranchId);
        await using var restricted = new SelloraDbContext(f.W.Options, TestCompany.Context, scope);
        Assert.Equal(100m, Ok(await Controller(restricted).GstSummary(NovFrom, NovTo, null, default)).Rows.Sum(r => r.TaxableValue));
        var denied = Assert.IsAssignableFrom<ObjectResult>((await Controller(restricted).GstSummary(NovFrom, NovTo, blr.Id, default)).Result);
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>((await Controller(restricted).VatSummary(NovFrom, NovTo, blr.Id, default)).Result).StatusCode);
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>((await Controller(restricted).HsnSummary(NovFrom, NovTo, HsnDirection.Sales, blr.Id, default)).Result).StatusCode);
    }

    [Fact]
    public async Task Reports_AnotherCompany_SeesNothingOfThisCompany_AndItsBranchIdIs403()
    {
        var f = await SetupAsync();
        AddLegacySale(f, D, DefaultTenant.BranchId, 100m, 12m, 12m);
        await f.W.Db.SaveChangesAsync();

        var otherId = TestCompany.OtherId;
        var options = f.W.Options;
        await using (var seed = new SelloraDbContext(options, TestCompany.For(otherId), BranchContext.Unrestricted()))
        {
            seed.BusinessSettings.Add(new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Other Co", InvoicePrefix = "OTH", BaseCurrencyId = f.W.Settings.BaseCurrencyId });
            seed.Branches.Add(BranchBuilder.MainBranch(otherId));
            await seed.SaveChangesAsync();
        }

        await using var other = new SelloraDbContext(options, TestCompany.For(otherId), BranchContext.Unrestricted());
        Assert.Empty(Ok(await Controller(other).GstSummary(NovFrom, NovTo, null, default)).Rows);
        Assert.Empty(Ok(await Controller(other).VatSummary(NovFrom, NovTo, null, default)).Rows);
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>((await Controller(other).GstSummary(NovFrom, NovTo, DefaultTenant.BranchId, default)).Result).StatusCode);
    }

    // ------------------------------------------------------------------ permission

    [Fact]
    public void TaxReportsController_RequiresReportsTax_AndTheCodeIsInTheCatalogue()
    {
        var policies = typeof(TaxReportsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(a => a.Policy).ToArray();

        Assert.Equal("Reports.Tax", Assert.Single(policies));
        Assert.Contains(Sellora.Domain.Permissions.PermissionCatalog.All, p => p.Code == "Reports.Tax");
        Assert.Equal("api/v1/reports/tax", typeof(TaxReportsController).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Single().Template);
    }

    [Fact]
    public async Task ReportsTax_IsAHardGate_ReportsSalesAloneDoesNotOpenTheTaxReports()
    {
        foreach (var (held, expected) in new[] { ("Reports.Sales", false), ("Reports.Tax", true) })
        {
            var w = new RbacWorld();
            var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", held));
            w.Db.ChangeTracker.Clear();
            var membership = new MembershipContext();
            membership.Set(w.Db.UserCompanyMemberships.AsEnumerable().Single(x => x.Id == m.Id).Id, w.Db.UserCompanyMemberships.AsEnumerable().Single(x => x.Id == m.Id).AccessVersion);
            var branches = new BranchContext();
            branches.Set(null, null);
            var handler = new Sellora.Api.Authorization.PermissionAuthorizationHandler(Rbac.Effective(w.Db, membership), membership, branches);
            var requirement = new Sellora.Api.Authorization.PermissionRequirement("Reports.Tax");
            var context = new Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext(
                [requirement], new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("test")), null);

            await handler.HandleAsync(context);

            Assert.Equal(expected, context.HasSucceeded);
        }
    }

    // ------------------------------------------------------------------ helpers

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
}
