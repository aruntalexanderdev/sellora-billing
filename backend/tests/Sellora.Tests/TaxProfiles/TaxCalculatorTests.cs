using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;
using static Sellora.Tests.TaxProfiles.TaxCalcBuilders;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-2 pure calculator tests with the numbers of docs/features/tax-profiles.md (AC1-AC9, AC19, AC23, E2, E3, E10).</summary>
public class TaxCalculatorTests
{
    [Fact]
    public void Calculate_ExclusiveGstIntraWithDiscount_SplitsCgstSgst()
    {
        var r = Calc(Line(Gst(18), 100, qty: 10, discount: 50));
        var l = r.Lines[0];
        Assert.Equal(950.00m, l.Taxable);
        Assert.Equal(2, l.Rows.Count);
        Assert.Equal(85.50m, Row(l, "CGST").TaxAmount);
        Assert.Equal(85.50m, Row(l, "SGST").TaxAmount);
        Assert.Equal(18m, l.TaxRatePercent);
        Assert.Equal(171.00m, l.TaxTotal);
        Assert.Equal(1121.00m, l.LineTotal);
        Assert.Equal(1121.00m, r.GrandTotal);
    }

    [Fact]
    public void Calculate_ExclusiveGstInterState_SingleIgstRow()
    {
        var l = Calc(Line(Gst(18), 100, qty: 10, discount: 50), pos: PlaceOfSupply.InterState).Lines[0];
        var row = Assert.Single(l.Rows);
        Assert.Equal("IGST", row.Code);
        Assert.Equal(171.00m, row.TaxAmount);
        Assert.Equal(950.00m, row.BaseAmount);
        Assert.Equal(1121.00m, l.LineTotal);
    }

    [Fact]
    public void Calculate_InclusiveGst118_Taxable100()
    {
        var l = Calc(Line(Gst(18), 118, inclusive: true)).Lines[0];
        Assert.Equal(100.00m, l.Taxable);
        Assert.Equal(9.00m, Row(l, "CGST").TaxAmount);
        Assert.Equal(9.00m, Row(l, "SGST").TaxAmount);
        Assert.Equal(118.00m, l.LineTotal);
    }

    [Fact]
    public void Calculate_SameStoredPriceExclusive_TaxableIs118()
    {
        var l = Calc(Line(Gst(18), 118)).Lines[0];
        Assert.Equal(118.00m, l.Taxable);
        Assert.Equal(21.24m, l.TaxTotal);
        Assert.Equal(139.24m, l.LineTotal);
    }

    [Fact]
    public void Calculate_InclusiveOddAmount_CgstEqualsSgstAndCustomerPaysExactly()
    {
        var l = Calc(Line(Gst(5), 99, inclusive: true)).Lines[0];
        Assert.Equal(2.36m, Row(l, "CGST").TaxAmount);
        Assert.Equal(2.36m, Row(l, "SGST").TaxAmount);
        Assert.Equal(94.28m, l.Taxable);
        Assert.Equal(99.00m, l.LineTotal);
    }

    private static TaxRateSet CessProfile() => Custom(TaxRegime.Gst,
        Percent(TaxComponentKind.Cgst, "CGST", 14, TaxApplicability.IntraState, 1),
        Percent(TaxComponentKind.Sgst, "SGST", 14, TaxApplicability.IntraState, 2),
        Percent(TaxComponentKind.Cess, "CESS", 5, TaxApplicability.Always, 4),
        PerUnit(TaxComponentKind.Cess, "CESSU", 2.00m, 5));

    [Fact]
    public void Calculate_ExclusiveWithPercentAndPerUnitCess_AllRows()
    {
        var l = Calc(Line(CessProfile(), 100, qty: 10, baseQty: 10)).Lines[0];
        Assert.Equal(1000.00m, l.Taxable);
        Assert.Equal(140.00m, Row(l, "CGST").TaxAmount);
        Assert.Equal(140.00m, Row(l, "SGST").TaxAmount);
        Assert.Equal(50.00m, Row(l, "CESS").TaxAmount);
        Assert.Equal(20.00m, Row(l, "CESSU").TaxAmount);
        Assert.Equal(1350.00m, l.LineTotal);
    }

    [Fact]
    public void Calculate_InclusiveWithPerUnitCess_SameRowsAsExclusive()
    {
        var l = Calc(Line(CessProfile(), 135, qty: 10, baseQty: 10, inclusive: true)).Lines[0];
        Assert.Equal(1000.00m, l.Taxable);
        Assert.Equal(140.00m, Row(l, "CGST").TaxAmount);
        Assert.Equal(140.00m, Row(l, "SGST").TaxAmount);
        Assert.Equal(50.00m, Row(l, "CESS").TaxAmount);
        Assert.Equal(20.00m, Row(l, "CESSU").TaxAmount);
        Assert.Equal(1350.00m, l.LineTotal);
    }

    [Fact]
    public void Calculate_BillDiscountAcrossTwoRates_SharesByWeight()
    {
        var r = Calc(Doc([Line(Gst(5), 600), Line(Gst(40), 400)], amount: 100));
        Assert.Equal(60.00m, r.Lines[0].BillDiscountShare);
        Assert.Equal(40.00m, r.Lines[1].BillDiscountShare);
        Assert.Equal(540.00m, r.Lines[0].Taxable);
        Assert.Equal(360.00m, r.Lines[1].Taxable);
        Assert.Equal(13.50m, Row(r.Lines[0], "CGST").TaxAmount);
        Assert.Equal(13.50m, Row(r.Lines[0], "SGST").TaxAmount);
        Assert.Equal(72.00m, Row(r.Lines[1], "CGST").TaxAmount);
        Assert.Equal(72.00m, Row(r.Lines[1], "SGST").TaxAmount);
        Assert.Equal(1071.00m, r.GrandTotal);
    }

    [Fact]
    public void Calculate_BillDiscountOverThreeEqualLines_RemainderToLast()
    {
        var r = Calc(Doc([Line(Gst(18), 100), Line(Gst(18), 100), Line(Gst(18), 100)], amount: 100));
        Assert.Equal([33.33m, 33.33m, 33.34m], r.Lines.Select(l => l.BillDiscountShare).ToArray());
        Assert.Equal(100.00m, r.BillDiscountAmount);
    }

    [Fact]
    public void Calculate_BillDiscountUnequalWeights_RemainderToLargestNotLast()
    {
        // 0.10 over 200/100/100: 0.05, 0.025 -> 0.03, 0.03 sum 0.11, so -0.01 goes to the largest (first) line.
        var r = Calc(Doc([Line(Vat(10), 200), Line(Vat(10), 100), Line(Vat(10), 100)], amount: 0.10m));
        Assert.Equal([0.04m, 0.03m, 0.03m], r.Lines.Select(l => l.BillDiscountShare).ToArray());
    }

    [Fact]
    public void Calculate_BillDiscountRoundingOvershoots_NegativeRemainderTakenFromLastTiedLine()
    {
        // 0.02 over three equal weights: each R(0.00667) = 0.01, sum 0.03, remainder -0.01 comes off the last of the tied lines.
        var r = Calc(Doc([Line(Vat(10), 100), Line(Vat(10), 100), Line(Vat(10), 100)], amount: 0.02m));
        var shares = r.Lines.Select(l => l.BillDiscountShare).ToArray();
        Assert.Equal([0.01m, 0.01m, 0.00m], shares);
        Assert.Equal(0.02m, shares.Sum());
        Assert.All(shares, s => Assert.True(s >= 0m));
    }

    [Fact]
    public void Calculate_BillDiscountOvershootBySeveralCents_RemainderSpreadOverTiedLinesNeverNegative()
    {
        // 0.05 over seven equal weights: seven shares of 0.01 (0.07), remainder -0.02 comes off the last two lines.
        var lines = Enumerable.Range(0, 7).Select(_ => Line(Vat(10), 100)).ToList();
        var r = Calc(Doc(lines, amount: 0.05m));
        var shares = r.Lines.Select(l => l.BillDiscountShare).ToArray();
        Assert.Equal([0.01m, 0.01m, 0.01m, 0.01m, 0.01m, 0.00m, 0.00m], shares);
        Assert.Equal(0.05m, r.BillDiscountAmount);
        Assert.All(shares, s => Assert.True(s >= 0m));
    }

    [Fact]
    public void Calculate_BillDiscountLargestLineAlmostAtCap_OverflowMovesToNextLargestLine()
    {
        // 1.99 over eight lines: the shares round to 1.97, the 0.02 remainder wants the largest line (0.34) but it has room for 0.01 only,
        // so 0.01 goes to it and 0.01 to the next largest (0.33). No share exceeds its weight.
        decimal[] prices = [0.25m, 0.24m, 0.33m, 0.09m, 0.28m, 0.29m, 0.34m, 0.22m];
        var r = Calc(Doc(prices.Select(p => Line(Vat(10), p)).ToList(), amount: 1.99m));
        var shares = r.Lines.Select(l => l.BillDiscountShare).ToArray();
        Assert.Equal([0.24m, 0.23m, 0.33m, 0.09m, 0.27m, 0.28m, 0.34m, 0.21m], shares);
        Assert.Equal(1.99m, shares.Sum());
        for (var i = 0; i < prices.Length; i++) Assert.True(shares[i] <= prices[i]);
    }

    [Fact]
    public void Calculate_BillChargesOnZeroWeightBill_SpreadEquallyTaxedAndExact()
    {
        // DQ-TP2-8: every line weighs 0, so the charge cannot follow weights; it is spread equally (remainder to the last), still taxed.
        var r = Calc(Doc([Line(Vat(10), 0), Line(Vat(10), 0), Line(Vat(10), 0)], charges: 10m));
        Assert.Equal([3.33m, 3.33m, 3.34m], r.Lines.Select(l => l.BillChargeShare).ToArray());
        Assert.Equal(10.00m, r.ChargesTotal);
        Assert.Equal([3.33m, 3.33m, 3.34m], r.Lines.Select(l => l.Taxable).ToArray());
        Assert.Equal([0.33m, 0.33m, 0.33m], r.Lines.Select(l => Row(l, "VAT").TaxAmount).ToArray());
        Assert.Equal((10.00m, 0.99m, 10.99m), (r.TaxableTotal, r.TaxTotal, r.GrandTotal));
    }

    [Fact]
    public void Calculate_BillDiscountWithZeroWeightLine_ZeroShare()
    {
        var r = Calc(Doc([Line(Vat(10), 100), Line(Vat(10), 100), Line(Vat(10), 0)], amount: 10));
        Assert.Equal([5.00m, 5.00m, 0.00m], r.Lines.Select(l => l.BillDiscountShare).ToArray());
    }

    [Fact]
    public void Calculate_TinyBillDiscountWithZeroWeightLine_ZeroLineNeverGetsRemainder()
    {
        var r = Calc(Doc([Line(Vat(10), 100), Line(Vat(10), 100), Line(Vat(10), 0)], amount: 0.01m));
        Assert.Equal([0.01m, 0.00m, 0.00m], r.Lines.Select(l => l.BillDiscountShare).ToArray());
    }

    [Fact]
    public void Calculate_BillChargesAcrossTwoLines_SharedAndTaxed()
    {
        var r = Calc(Doc([Line(Gst(18), 2000), Line(Gst(18), 1000)], charges: 300));
        Assert.Equal(200.00m, r.Lines[0].BillChargeShare);
        Assert.Equal(100.00m, r.Lines[1].BillChargeShare);
        Assert.Equal(2200.00m, r.Lines[0].Taxable);
        Assert.Equal(1100.00m, r.Lines[1].Taxable);
        Assert.Equal(198.00m, Row(r.Lines[0], "CGST").TaxAmount);
        Assert.Equal(198.00m, Row(r.Lines[0], "SGST").TaxAmount);
        Assert.Equal(99.00m, Row(r.Lines[1], "CGST").TaxAmount);
        Assert.Equal(99.00m, Row(r.Lines[1], "SGST").TaxAmount);
        Assert.Equal(2596.00m, r.Lines[0].LineTotal);
        Assert.Equal(1298.00m, r.Lines[1].LineTotal);
        Assert.Equal(300.00m, r.ChargesTotal);
        Assert.Equal(3894.00m, r.GrandTotal);
    }

    [Fact]
    public void Calculate_MixedInclusiveAndExclusiveWithBillDiscount_SharesInOwnBasis()
    {
        var r = Calc(Doc([Line(Gst(18), 118, inclusive: true), Line(Gst(18), 100)], amount: 21.80m));
        Assert.Equal(11.80m, r.Lines[0].BillDiscountShare);
        Assert.Equal(10.00m, r.Lines[1].BillDiscountShare);
        Assert.Equal(90.00m, r.Lines[0].Taxable);
        Assert.Equal(90.00m, r.Lines[1].Taxable);
        foreach (var l in r.Lines)
        {
            Assert.Equal(8.10m, Row(l, "CGST").TaxAmount);
            Assert.Equal(8.10m, Row(l, "SGST").TaxAmount);
            Assert.Equal(106.20m, l.LineTotal);
        }
        Assert.Equal(212.40m, r.GrandTotal);
    }

    [Fact]
    public void Calculate_InclusiveLineWithBillCharge_ChargeAddedExclusiveAndTaxed()
    {
        var l = Calc(Doc([Line(Gst(18), 118, inclusive: true)], charges: 10)).Lines[0];
        Assert.Equal(10.00m, l.BillChargeShare);
        Assert.Equal(110.00m, Row(l, "CGST").BaseAmount);
        Assert.Equal(9.90m, Row(l, "CGST").TaxAmount);
        Assert.Equal(9.90m, Row(l, "SGST").TaxAmount);
        Assert.Equal(129.80m, l.LineTotal);
    }

    [Fact]
    public void Calculate_InclusivePerUnitTaxAbovePrice_UnitPriceError()
    {
        var rates = Custom(TaxRegime.Gst, PerUnit(TaxComponentKind.Cess, "CESSU", 2.00m));
        Assert.Equal(["lines[0].unitPrice"], ErrorFields(Doc([Line(rates, 1.00m, inclusive: true)])));
    }

    [Fact]
    public void Calculate_ZeroPriceExclusive_TwoZeroRows()
    {
        var l = Calc(Line(Gst(18), 0)).Lines[0];
        Assert.Equal(2, l.Rows.Count);
        Assert.All(l.Rows, r => Assert.Equal(0.00m, r.TaxAmount));
        Assert.Equal(0.00m, l.LineTotal);
    }

    [Theory]
    [InlineData(TaxTreatment.Exempt, "EXEMPT")]
    [InlineData(TaxTreatment.NilRated, "NILRATED")]
    [InlineData(TaxTreatment.ZeroRated, "ZERORATED")]
    [InlineData(TaxTreatment.NonTaxable, "NONTAXABLE")]
    public void Calculate_NonTaxableTreatment_OneZeroRowWithTreatmentCode(TaxTreatment treatment, string code)
    {
        var l = Calc(Line(NonTaxable(treatment), 100)).Lines[0];
        var row = Assert.Single(l.Rows);
        Assert.Null(row.ComponentId);
        Assert.Equal(code, row.Code);
        Assert.Equal(TaxComponentKind.Other, row.Kind);
        Assert.Equal(TaxCalculationMethod.Percent, row.Method);
        Assert.Equal(0m, row.RatePercent);
        Assert.Equal(100.00m, row.BaseAmount);
        Assert.Equal(0.00m, row.TaxAmount);
        Assert.Equal(0m, l.TaxRatePercent);
        Assert.Equal(100.00m, l.LineTotal);
    }

    [Fact]
    public void Calculate_BillDiscountPercent_AppliedToWeight()
    {
        var r = Calc(Doc([Line(Vat(10), 600), Line(Vat(10), 400)], pct: 10));
        Assert.Equal(100.00m, r.BillDiscountAmount);
        Assert.Equal(10m, r.EffectiveBillDiscountPercent);
        Assert.Equal(60.00m, r.Lines[0].BillDiscountShare);
        Assert.Equal(40.00m, r.Lines[1].BillDiscountShare);
    }

    [Fact]
    public void Calculate_HeaderTotals_AreSumsOfLines()
    {
        var r = Calc(Doc([Line(Gst(18), 100, qty: 3, discount: 20), Line(Vat(10), 55)], amount: 15, charges: 8));
        Assert.Equal(r.Lines.Sum(l => l.Taxable), r.TaxableTotal);
        Assert.Equal(r.Lines.Sum(l => l.TaxTotal), r.TaxTotal);
        Assert.Equal(r.Lines.Sum(l => l.LineTotal), r.GrandTotal);
        Assert.Equal(r.TaxableTotal + r.TaxTotal, r.GrandTotal);
    }

    // ---- validation ----

    [Fact]
    public void Calculate_BothBillDiscountPercentAndAmount_BillDiscountError() =>
        Assert.Contains("billDiscount", ErrorFields(Doc([Line(Vat(10), 100)], pct: 5, amount: 5)));

    [Theory]
    [InlineData(100.01)]
    [InlineData(-1)]
    public void Calculate_BillDiscountPercentOutOfRange_BillDiscountError(double pct) =>
        Assert.Contains("billDiscount", ErrorFields(Doc([Line(Vat(10), 100)], pct: (decimal)pct)));

    [Fact]
    public void Calculate_NegativeBillDiscountAmount_BillDiscountError() =>
        Assert.Contains("billDiscount", ErrorFields(Doc([Line(Vat(10), 100)], amount: -1)));

    [Fact]
    public void Calculate_NegativeBillCharges_BillChargesError() =>
        Assert.Contains("billCharges", ErrorFields(Doc([Line(Vat(10), 100)], charges: -1)));

    [Fact]
    public void Calculate_EmptyLines_LinesError() =>
        Assert.Contains("lines", ErrorFields(Doc([])));

    [Fact]
    public void Calculate_LineDiscountAboveGross_DiscountError() =>
        Assert.Contains("lines[1].discount", ErrorFields(Doc([Line(Vat(10), 100), Line(Vat(10), 100, discount: 100.01m)])));

    [Fact]
    public void Calculate_NegativeLineValues_FieldErrorsKeyedPerLine()
    {
        Assert.Contains("lines[0].unitPrice", ErrorFields(Doc([Line(Vat(10), -1)])));
        Assert.Contains("lines[0].qty", ErrorFields(Doc([Line(Vat(10), 1, qty: -1)])));
        Assert.Contains("lines[0].discount", ErrorFields(Doc([Line(Vat(10), 100, discount: -1)])));
        Assert.Contains("lines[0].otherCharges", ErrorFields(Doc([Line(Vat(10), 100, other: -1)])));
    }

    [Fact]
    public void Calculate_SeveralInvalidLines_AllErrorsCollected()
    {
        var fields = ErrorFields(Doc([Line(Vat(10), -1), Line(Vat(10), 100, discount: 500)], charges: -2));
        Assert.Contains("lines[0].unitPrice", fields);
        Assert.Contains("lines[1].discount", fields);
        Assert.Contains("billCharges", fields);
    }

    // ---- precision ----

    [Fact]
    public void Calculate_Precision0Inclusive_RoundingMovesIntoTaxable()
    {
        var l = Calc(Line(Gst(5), 99, inclusive: true), precision: 0).Lines[0];
        Assert.Equal(2m, Row(l, "CGST").TaxAmount);
        Assert.Equal(2m, Row(l, "SGST").TaxAmount);
        Assert.Equal(95m, l.Taxable);
        Assert.Equal(99m, l.LineTotal);
    }

    [Fact]
    public void Calculate_Precision3InclusiveVat_Taxable0909Vat0091()
    {
        var l = Calc(Line(Vat(10), 1.000m, inclusive: true), precision: 3).Lines[0];
        Assert.Equal(0.909m, l.Taxable);
        Assert.Equal(0.091m, Row(l, "VAT").TaxAmount);
        Assert.Equal(1.000m, l.LineTotal);
    }

    [Fact]
    public void Calculate_Precision3ExclusiveVat_RoundsAtDocumentPrecision()
    {
        var l = Calc(Line(Vat(10), 0.333m, qty: 3), precision: 3).Lines[0];
        Assert.Equal(0.100m, Row(l, "VAT").TaxAmount);
        Assert.Equal(1.099m, l.LineTotal);
    }

    [Fact]
    public void Calculate_Precision3BillDiscountOverThreeEqualLines_RemainderToLast()
    {
        var r = Calc(Doc([Line(Vat(10), 1.000m), Line(Vat(10), 1.000m), Line(Vat(10), 1.000m)], precision: 3, amount: 1.000m));
        Assert.Equal([0.333m, 0.333m, 0.334m], r.Lines.Select(l => l.BillDiscountShare).ToArray());
    }

    // ---- single-rate lines: fixed expected values (TP-4 retired SaleMoney; these are the figures its parity tests compared against) ----

    [Theory]
    [InlineData(2, "3", "19.99", "5.00", "18", "59.97", "54.97", "9.89", "64.86")]
    [InlineData(2, "7", "0.33", "0.01", "5", "2.31", "2.30", "0.12", "2.42")]
    [InlineData(3, "3", "0.333", "0.000", "10", "0.999", "0.999", "0.100", "1.099")]
    [InlineData(0, "9", "11", "3", "5", "99", "96", "5", "101")]
    [InlineData(2, "1", "100", "0", "0", "100.00", "100.00", "0.00", "100.00")]
    [InlineData(2, "12", "1234.56", "99.99", "12.5", "14814.72", "14714.73", "1839.34", "16554.07")]
    [InlineData(2, "1", "0.10", "0", "5", "0.10", "0.10", "0.01", "0.11")]   // tax 0.005 rounds up (banker's rounding would give 0.00)
    [InlineData(2, "3", "0.335", "0", "5", "1.01", "1.01", "0.05", "1.06")]  // gross 1.005 rounds up; tax 0.0505 -> 0.05
    public void Calculate_SingleComponentExclusive_FixedValues(int precision, string qty, string price, string discount, string rate, string gross, string taxable, string tax, string total)
    {
        var vat = Calc(Line(Vat(decimal.Parse(rate)), decimal.Parse(price), decimal.Parse(qty), decimal.Parse(discount)), precision).Lines[0];
        Assert.Equal(decimal.Parse(gross), vat.Gross);
        Assert.Equal(decimal.Parse(discount), vat.LineDiscount);
        Assert.Equal(decimal.Parse(taxable), vat.Taxable);
        Assert.Equal(decimal.Parse(tax), vat.TaxTotal);
        Assert.Equal(decimal.Parse(total), vat.LineTotal);

        var igst = Calc(Line(Gst(decimal.Parse(rate)), decimal.Parse(price), decimal.Parse(qty), decimal.Parse(discount)), precision, PlaceOfSupply.InterState).Lines[0];
        Assert.Equal(decimal.Parse(taxable), igst.Taxable);
        Assert.Equal(decimal.Parse(tax), igst.TaxTotal);
        Assert.Equal(decimal.Parse(total), igst.LineTotal);
    }
}
