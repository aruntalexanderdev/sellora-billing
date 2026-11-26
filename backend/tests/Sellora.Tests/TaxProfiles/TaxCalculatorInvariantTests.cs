using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using static Sellora.Tests.TaxProfiles.TaxCalcBuilders;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-2 invariants over 500 deterministic random documents (fixed seed): header = sum of lines, exact shares, CGST = SGST,
/// inclusive lines total exactly what the customer pays, and full return sequences sum to the original rows.</summary>
public class TaxCalculatorInvariantTests
{
    private const int Count = 500;

    private static decimal Money(Random rnd, int precision, double max) => Math.Round((decimal)(rnd.NextDouble() * max), precision, MidpointRounding.AwayFromZero);

    private static TaxRateSet RandomRates(Random rnd, bool allowPerUnit)
    {
        switch (rnd.Next(5))
        {
            case 0: return Vat(new[] { 5m, 10m, 15m }[rnd.Next(3)]);
            case 1: return Gst(new[] { 5m, 12m, 18m, 28m }[rnd.Next(4)]);
            case 2: return Custom(TaxRegime.Gst,
                Percent(TaxComponentKind.Cgst, "CGST", 14, TaxApplicability.IntraState, 1),
                Percent(TaxComponentKind.Sgst, "SGST", 14, TaxApplicability.IntraState, 2),
                Percent(TaxComponentKind.Igst, "IGST", 28, TaxApplicability.InterState, 3),
                Percent(TaxComponentKind.Cess, "CESS", 5, TaxApplicability.Always, 4));
            case 3 when allowPerUnit: return Custom(TaxRegime.Gst,
                Percent(TaxComponentKind.Cgst, "CGST", 9, TaxApplicability.IntraState, 1),
                Percent(TaxComponentKind.Sgst, "SGST", 9, TaxApplicability.IntraState, 2),
                Percent(TaxComponentKind.Igst, "IGST", 18, TaxApplicability.InterState, 3),
                PerUnit(TaxComponentKind.Cess, "CESSU", 1.5m, 4));
            default: return Gst(new[] { 3m, 6m, 18m }[rnd.Next(3)]);
        }
    }

    private static TaxCalcDocument RandomDocument(Random rnd)
    {
        var precision = new[] { 0, 2, 3 }[rnd.Next(3)];
        var pos = rnd.Next(3) == 0 ? PlaceOfSupply.InterState : PlaceOfSupply.IntraState;
        var n = rnd.Next(1, 6);
        var lines = new List<TaxCalcLine>();
        for (var i = 0; i < n; i++)
        {
            var inclusive = rnd.Next(3) == 0;
            var qty = Math.Round((decimal)(rnd.NextDouble() * 20), 3);
            if (precision == 0) qty = Math.Max(1, Math.Round(qty));
            var price = Money(rnd, precision, 500);
            var gross = Math.Round(qty * price, precision, MidpointRounding.AwayFromZero);
            var disc = rnd.Next(2) == 0 ? 0m : Math.Round(gross * (decimal)(rnd.NextDouble() * 0.5), precision, MidpointRounding.AwayFromZero);
            var other = !inclusive && rnd.Next(4) == 0 ? Money(rnd, precision, 20) : 0m;
            lines.Add(Line(RandomRates(rnd, !inclusive), price, qty, disc, other, inclusive));
        }
        var weight = lines.Sum(l => Math.Round(l.Qty * l.UnitPrice, precision, MidpointRounding.AwayFromZero) - l.LineDiscount);
        decimal? pct = null, amount = null;
        switch (rnd.Next(3))
        {
            case 1: pct = Math.Round((decimal)(rnd.NextDouble() * 60), 2); break;
            case 2: amount = Math.Round(weight * (decimal)(rnd.NextDouble() * 0.8), precision, MidpointRounding.AwayFromZero); break;
        }
        var charges = rnd.Next(3) == 0 ? Money(rnd, precision, 50) : 0m;
        return Doc(lines, precision, pos, pct, amount, charges);
    }

    private static IEnumerable<(TaxCalcDocument Doc, TaxDocumentResult Result)> Documents()
    {
        var rnd = new Random(20260925);
        for (var i = 0; i < Count; i++)
        {
            var doc = RandomDocument(rnd);
            yield return (doc, Calc(doc));
        }
    }

    [Fact]
    public void Calculate_RandomDocuments_HeaderEqualsSumOfLines()
    {
        foreach (var (_, r) in Documents())
        {
            Assert.Equal(r.Lines.Sum(l => l.Taxable), r.TaxableTotal);
            Assert.Equal(r.Lines.Sum(l => l.TaxTotal), r.TaxTotal);
            Assert.Equal(r.Lines.Sum(l => l.LineTotal), r.GrandTotal);
            Assert.Equal(r.TaxableTotal + r.TaxTotal, r.GrandTotal);
            Assert.Equal(r.Lines.Sum(l => l.Gross), r.Subtotal);
            Assert.All(r.Lines, l =>
            {
                Assert.Equal(l.Rows.Sum(x => x.TaxAmount), l.TaxTotal);
                Assert.Equal(l.Taxable + l.TaxTotal, l.LineTotal);
            });
        }
    }

    [Fact]
    public void Calculate_RandomDocuments_CgstEqualsSgstOnEveryLine()
    {
        var checkedLines = 0;
        foreach (var (_, r) in Documents())
            foreach (var l in r.Lines)
            {
                var c = l.Rows.SingleOrDefault(x => x.Code == "CGST");
                var s = l.Rows.SingleOrDefault(x => x.Code == "SGST");
                Assert.Equal(c is null, s is null);
                if (c is null) continue;
                Assert.Equal(c.TaxAmount, s!.TaxAmount);
                Assert.Equal(c.BaseAmount, s.BaseAmount);
                checkedLines++;
            }
        Assert.True(checkedLines > 200);
    }

    [Fact]
    public void Calculate_RandomDocuments_InclusiveLineWithoutChargesTotalsWhatCustomerPays()
    {
        var checkedLines = 0;
        foreach (var (doc, r) in Documents())
            for (var i = 0; i < r.Lines.Count; i++)
            {
                var l = r.Lines[i];
                if (!l.PriceIncludesTax || l.OtherCharges != 0 || l.BillChargeShare != 0) continue;
                Assert.Equal(l.Gross - l.LineDiscount - l.BillDiscountShare, l.LineTotal);
                checkedLines++;
            }
        Assert.True(checkedLines > 50);
    }

    [Fact]
    public void Calculate_RandomDocuments_BillSharesSumExactlyAndStayWithinWeight()
    {
        foreach (var (doc, r) in Documents())
        {
            Assert.Equal(r.BillDiscountAmount, r.Lines.Sum(l => l.BillDiscountShare));
            Assert.Equal(doc.BillCharges, r.Lines.Sum(l => l.BillChargeShare));
            Assert.All(r.Lines, l =>
            {
                Assert.InRange(l.BillDiscountShare, 0m, l.Gross - l.LineDiscount);
                Assert.True(l.BillChargeShare >= 0m);
            });
            if (doc.BillDiscountAmount is { } amount) Assert.Equal(amount, r.BillDiscountAmount);
        }
    }

    [Fact]
    public void Calculate_RandomDocuments_FullReturnSequenceSumsToOriginalRows()
    {
        var rnd = new Random(77);
        var reversedLines = 0;
        foreach (var (doc, r) in Documents())
            for (var i = 0; i < r.Lines.Count; i++)
            {
                var precision = doc.Precision;
                var lineBase = doc.Lines[i].BaseQty;
                if (lineBase <= 0) continue;
                var rows = r.Lines[i].Rows;
                var parts = rnd.Next(1, 5);
                var remaining = lineBase;
                var prior = new List<TaxRowResult>();
                for (var p = 0; p < parts && remaining > 0; p++)
                {
                    var take = p == parts - 1 ? remaining : Math.Round(remaining * (decimal)(0.1 + rnd.NextDouble() * 0.8), 3);
                    if (take <= 0 || take > remaining) take = remaining;
                    prior.AddRange(TaxReversal.ReverseRows(rows, prior, take, remaining, lineBase, precision));
                    remaining -= take;
                }
                if (remaining > 0) prior.AddRange(TaxReversal.ReverseRows(rows, prior, remaining, remaining, lineBase, precision));
                foreach (var row in rows)
                {
                    var rev = prior.Where(x => x.Code == row.Code);
                    Assert.Equal(row.TaxAmount, rev.Sum(x => x.TaxAmount));
                    Assert.Equal(row.BaseAmount, rev.Sum(x => x.BaseAmount));
                }
                reversedLines++;
            }
        Assert.True(reversedLines > 500);
    }
}
