using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Tests.TaxProfiles;

/// <summary>TP-2 BR25 / DQ-TP2-4: returns reverse every tax row proportionally by base quantity; the last return takes the exact remainder.</summary>
public class TaxReversalTests
{
    private static TaxRowResult Row(string code, decimal baseAmount, decimal tax, Guid? id = null) =>
        new(id, code, TaxComponentKind.Cgst, TaxCalculationMethod.Percent, 9m, null, baseAmount, tax);

    [Fact]
    public void Share_TwoHalfReturns_SumToOriginal()
    {
        var first = TaxReversal.Share(85.50m, 0m, 5m, 10m, 10m, 2);
        var second = TaxReversal.Share(85.50m, first, 5m, 5m, 10m, 2);
        Assert.Equal(42.75m, first);
        Assert.Equal(42.75m, second);
    }

    [Fact]
    public void Share_QtySevenReturnedTwoTwoThree_LastTakesRemainder()
    {
        var a = TaxReversal.Share(85.50m, 0m, 2m, 7m, 7m, 2);
        var b = TaxReversal.Share(85.50m, a, 2m, 5m, 7m, 2);
        var c = TaxReversal.Share(85.50m, a + b, 3m, 3m, 7m, 2);
        Assert.Equal([24.43m, 24.43m, 36.64m], [a, b, c]);
        Assert.Equal(85.50m, a + b + c);
    }

    [Fact]
    public void Share_TaxableNinetyFifty_SplitsAndSumsExactly()
    {
        var a = TaxReversal.Share(950m, 0m, 2m, 7m, 7m, 2);
        var b = TaxReversal.Share(950m, a, 2m, 5m, 7m, 2);
        var c = TaxReversal.Share(950m, a + b, 3m, 3m, 7m, 2);
        Assert.Equal([271.43m, 271.43m, 407.14m], [a, b, c]);
    }

    [Fact]
    public void Share_FullReturnAtOnce_IsOriginal() =>
        Assert.Equal(85.50m, TaxReversal.Share(85.50m, 0m, 7m, 7m, 7m, 2));

    [Fact]
    public void ReverseRows_ByComponentId_ReversesEachRowAndKeepsIdentity()
    {
        var cgst = Guid.NewGuid();
        var sgst = Guid.NewGuid();
        TaxRowResult[] original = [Row("CGST", 950m, 85.50m, cgst), Row("SGST", 950m, 85.50m, sgst)];

        var r1 = TaxReversal.ReverseRows(original, [], 2m, 7m, 7m, 2);
        var r2 = TaxReversal.ReverseRows(original, r1, 2m, 5m, 7m, 2);
        var r3 = TaxReversal.ReverseRows(original, [.. r1, .. r2], 3m, 3m, 7m, 2);

        Assert.Equal(2, r1.Count);
        Assert.Equal(24.43m, r1.Single(r => r.ComponentId == cgst).TaxAmount);
        Assert.Equal(24.43m, r2.Single(r => r.ComponentId == sgst).TaxAmount);
        Assert.Equal(36.64m, r3.Single(r => r.ComponentId == cgst).TaxAmount);
        Assert.Equal(271.43m, r1[0].BaseAmount);
        Assert.Equal(407.14m, r3[0].BaseAmount);
        Assert.Equal("CGST", r1.Single(r => r.ComponentId == cgst).Code);
    }

    [Fact]
    public void ReverseRows_LegacyRowWithoutComponentId_MatchedByCode()
    {
        TaxRowResult[] original = [Row("GST 18%", 950m, 171.00m)];
        var r1 = TaxReversal.ReverseRows(original, [], 2m, 7m, 7m, 2);
        var r2 = TaxReversal.ReverseRows(original, r1, 2m, 5m, 7m, 2);
        var r3 = TaxReversal.ReverseRows(original, [.. r1, .. r2], 3m, 3m, 7m, 2);
        Assert.Equal(48.86m, r1[0].TaxAmount);
        Assert.Equal(48.86m, r2[0].TaxAmount);
        Assert.Equal(171.00m, r1[0].TaxAmount + r2[0].TaxAmount + r3[0].TaxAmount);
        Assert.Equal(950m, r1[0].BaseAmount + r2[0].BaseAmount + r3[0].BaseAmount);
    }

    [Fact]
    public void ReverseRows_PriorRowsOfOtherComponentsIgnored()
    {
        var cgst = Guid.NewGuid();
        var sgst = Guid.NewGuid();
        TaxRowResult[] original = [Row("CGST", 100m, 9m, cgst), Row("SGST", 100m, 9m, sgst)];
        TaxRowResult[] prior = [Row("CGST", 50m, 4.50m, cgst)];
        var r = TaxReversal.ReverseRows(original, prior, 5m, 5m, 10m, 2);
        Assert.Equal(4.50m, r.Single(x => x.ComponentId == cgst).TaxAmount);
        Assert.Equal(9m, r.Single(x => x.ComponentId == sgst).TaxAmount);
    }

    [Fact]
    public void ReverseRows_PerUnitRowPartialReturn_BaseQtyReversesExactlyAndLastTakesRemainder()
    {
        var cess = Guid.NewGuid();
        // 10.125 kg at 0.50 per base unit: the row's BaseAmount is the base quantity, its tax 5.06 (R(5.0625)).
        TaxRowResult[] original = [new(cess, "CESS", TaxComponentKind.Cess, TaxCalculationMethod.AmountPerBaseUnit, null, 0.50m, 10.125m, 5.06m)];

        var r1 = TaxReversal.ReverseRows(original, [], 4.003m, 10.125m, 10.125m, 2);
        var r2 = TaxReversal.ReverseRows(original, r1, 3m, 6.122m, 10.125m, 2);
        var r3 = TaxReversal.ReverseRows(original, [.. r1, .. r2], 3.122m, 3.122m, 10.125m, 2);

        Assert.Equal([4.003m, 3m, 3.122m], [r1[0].BaseAmount, r2[0].BaseAmount, r3[0].BaseAmount]);
        Assert.Equal(10.125m, r1[0].BaseAmount + r2[0].BaseAmount + r3[0].BaseAmount);
        Assert.Equal(5.06m, r1[0].TaxAmount + r2[0].TaxAmount + r3[0].TaxAmount);
        Assert.Equal(2.00m, r1[0].TaxAmount); // R(5.06 x 4.003 / 10.125)
    }

    [Fact]
    public void ReverseRows_PerUnitRowFullReturnAtOnce_IsTheOriginalRow()
    {
        var cess = Guid.NewGuid();
        TaxRowResult[] original = [new(cess, "CESS", TaxComponentKind.Cess, TaxCalculationMethod.AmountPerBaseUnit, null, 12m, 7m, 84m)];
        var r = TaxReversal.ReverseRows(original, [], 7m, 7m, 7m, 2);
        Assert.Equal((7m, 84m), (r[0].BaseAmount, r[0].TaxAmount));
    }
}
