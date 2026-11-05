using Sellora.Domain.Inventory;
using static Sellora.Domain.Inventory.ProductUnitGraph;

namespace Sellora.Tests.Inventory;

/// <summary>
/// Pure factor arithmetic over a product unit hierarchy (<see cref="ProductUnitGraph"/>):
/// <c>factor(base) = 1</c>, <c>factor(u) = u.InnerQuantity × factor(u.Inner)</c>, plus depth, sort order and the
/// equivalence chain shown in the editor.
/// </summary>
public class ProductUnitGraphTests
{
    private static readonly Guid Pc = Guid.NewGuid(), Box = Guid.NewGuid(), HCtn = Guid.NewGuid(), FCtn = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> PenNames = new()
    {
        [Pc] = "Piece", [Box] = "Box", [HCtn] = "Half Carton", [FCtn] = "Full Carton",
    };

    /// <summary>Canonical Pen: 1 Box contains 10 Piece, 1 Half Carton 5 Box, 1 Full Carton 2 Half Carton.</summary>
    private static Node[] Pen() =>
    [
        new(Pc, null, 1m),
        new(Box, Pc, 10m),
        new(HCtn, Box, 5m),
        new(FCtn, HCtn, 2m),
    ];

    [Fact]
    public void Compute_Pen_FactorsAre1_10_50_100WithDepths0To3()
    {
        var result = Compute(Pen());

        Assert.Equal((1m, 0), (result[Pc].Factor!.Value, result[Pc].Depth!.Value));
        Assert.Equal((10m, 1), (result[Box].Factor!.Value, result[Box].Depth!.Value));
        Assert.Equal((50m, 2), (result[HCtn].Factor!.Value, result[HCtn].Depth!.Value));
        Assert.Equal((100m, 3), (result[FCtn].Factor!.Value, result[FCtn].Depth!.Value));
        Assert.All(result.Values, r => Assert.Equal(Problem.None, r.Problem));
    }

    [Fact]
    public void Compute_NodesInAnyOrder_SameFactors()
    {
        var result = Compute(Pen().Reverse());

        Assert.Equal(100m, result[FCtn].Factor);
        Assert.Equal(50m, result[HCtn].Factor);
    }

    [Fact]
    public void Chain_PenFullCarton_ListsEveryLevelDownToBase()
    {
        var nodes = Pen();
        var result = Compute(nodes);
        var inner = nodes.ToDictionary(n => n.Key, n => n.InnerKey);

        var chain = Chain(FCtn, k => inner[k], k => result[k].Factor, k => PenNames[k]);

        Assert.Equal("1 Full Carton = 2 Half Carton = 10 Box = 100 Piece", chain);
        Assert.Equal("1 Box = 10 Piece", Chain(Box, k => inner[k], k => result[k].Factor, k => PenNames[k]));
        Assert.Equal("1 Piece", Chain(Pc, k => inner[k], k => result[k].Factor, k => PenNames[k]));
    }

    [Fact]
    public void Chain_UnknownFactorOnTheWay_ReturnsNull()
    {
        var nodes = Pen();
        var inner = nodes.ToDictionary(n => n.Key, n => n.InnerKey);

        Assert.Null(Chain(FCtn, k => inner[k], k => k == Box ? null : 1m, k => PenNames[k]));
    }

    [Fact]
    public void Compute_BoxOf10AndBoxOf20OnTwoProducts_FactorsAreProductSpecific()
    {
        // Both products use the same global "Box" unit; the factor lives on each product's own row.
        Guid pc1 = Guid.NewGuid(), box1 = Guid.NewGuid(), pc2 = Guid.NewGuid(), box2 = Guid.NewGuid();

        var p1 = Compute([new Node(pc1, null, 1m), new Node(box1, pc1, 10m)]);
        var p2 = Compute([new Node(pc2, null, 1m), new Node(box2, pc2, 20m)]);

        Assert.Equal(10m, p1[box1].Factor);
        Assert.Equal(20m, p2[box2].Factor);
    }

    [Fact]
    public void Compute_CartonOf12BoxesOf12_Is144()
    {
        Guid pc = Guid.NewGuid(), box = Guid.NewGuid(), carton = Guid.NewGuid();

        var result = Compute([new Node(pc, null, 1m), new Node(box, pc, 12m), new Node(carton, box, 12m)]);

        Assert.Equal(144m, result[carton].Factor);
    }

    [Fact]
    public void Compute_RiceKgWithGramAndBag_Is0_001And25()
    {
        Guid kg = Guid.NewGuid(), g = Guid.NewGuid(), bag = Guid.NewGuid();

        var result = Compute([new Node(kg, null, 1m), new Node(g, kg, 0.001m), new Node(bag, kg, 25m)]);

        Assert.Equal(0.001m, result[g].Factor);
        Assert.Equal(25m, result[bag].Factor);
        Assert.Equal(1000m, ProductUnitGraph.Convert(1m, result[kg].Factor!.Value, result[g].Factor!.Value)); // 1 kg = 1000 g
        Assert.Equal(25000m, ProductUnitGraph.Convert(1m, result[bag].Factor!.Value, result[g].Factor!.Value));
    }

    [Fact]
    public void Compute_OilLitreWithMillilitreBottleAndCase_Is0_001_0_5And6()
    {
        Guid l = Guid.NewGuid(), ml = Guid.NewGuid(), bottle = Guid.NewGuid(), caseKey = Guid.NewGuid();

        var result = Compute([
            new Node(l, null, 1m), new Node(ml, l, 0.001m), new Node(bottle, ml, 500m), new Node(caseKey, bottle, 12m)]);

        Assert.Equal(0.001m, result[ml].Factor);
        Assert.Equal(0.5m, result[bottle].Factor);
        Assert.Equal(6m, result[caseKey].Factor);
        Assert.Equal(3, result[caseKey].Depth);
    }

    [Fact]
    public void Compute_BaseOnly_FactorOneDepthZero()
    {
        var pc = Guid.NewGuid();

        var result = Assert.Single(Compute([new Node(pc, null, 1m)]));

        Assert.Equal(new NodeResult(pc, 1m, 0, Problem.None), result.Value);
    }

    [Fact]
    public void Compute_SelfCycle_ReportsCycle()
    {
        var box = Guid.NewGuid();

        var result = Compute([new Node(Pc, null, 1m), new Node(box, box, 10m)]);

        Assert.Equal(Problem.Cycle, result[box].Problem);
        Assert.Null(result[box].Factor);
        Assert.Equal(Problem.None, result[Pc].Problem);
    }

    [Fact]
    public void Compute_TwoCycle_BothNodesReportCycle()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        var result = Compute([new Node(Pc, null, 1m), new Node(a, b, 2m), new Node(b, a, 5m)]);

        Assert.Equal(Problem.Cycle, result[a].Problem);
        Assert.Equal(Problem.Cycle, result[b].Problem);
    }

    [Fact]
    public void Compute_ThreeCycleWithATail_LoopIsCycleAndTailIsInnerUnresolved()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), tail = Guid.NewGuid();

        var result = Compute([new Node(tail, a, 2m), new Node(Pc, null, 1m), new Node(a, b, 2m), new Node(b, c, 2m), new Node(c, a, 2m)]);

        Assert.All(new[] { a, b, c }, k => Assert.Equal(Problem.Cycle, result[k].Problem));
        Assert.Equal(Problem.InnerUnresolved, result[tail].Problem);
        Assert.Null(result[tail].Factor);
    }

    [Fact]
    public void Compute_InnerNotInGraph_ReportsMissingInnerAndParentsInnerUnresolved()
    {
        Guid orphan = Guid.NewGuid(), parent = Guid.NewGuid();

        var result = Compute([new Node(Pc, null, 1m), new Node(orphan, Guid.NewGuid(), 10m), new Node(parent, orphan, 2m)]);

        Assert.Equal(Problem.MissingInner, result[orphan].Problem);
        Assert.Equal(Problem.InnerUnresolved, result[parent].Problem);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000000.5")]
    [InlineData("0.0000001")] // 7 decimal places
    public void Compute_InvalidInnerQuantity_ReportsInvalidQuantity(string qty)
    {
        var box = Guid.NewGuid();

        var result = Compute([new Node(Pc, null, 1m), new Node(box, Pc, decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture))]);

        Assert.Equal(Problem.InvalidQuantity, result[box].Problem);
    }

    [Fact]
    public void Compute_ChainAboveOneBillion_OutOfRangeWithoutOverflow()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

        var result = Compute([
            new Node(Pc, null, 1m), new Node(a, Pc, 1_000_000m), new Node(b, a, 10_000m), new Node(c, b, 1_000_000_000m)]);

        Assert.Equal(Problem.OutOfRange, result[b].Problem);
        Assert.Null(result[b].Factor);
        Assert.Equal(Problem.InnerUnresolved, result[c].Problem);
    }

    [Fact]
    public void Compute_FactorExactlyOneBillion_Accepted()
    {
        var a = Guid.NewGuid();

        var result = Compute([new Node(Pc, null, 1m), new Node(a, Pc, 1_000_000_000m)]);

        Assert.Equal((Problem.None, (decimal?)MaxFactor), (result[a].Problem, result[a].Factor));
    }

    [Fact]
    public void Compute_FactorBelowOneMillionth_OutOfRangeWithFactorSet()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        var result = Compute([new Node(Pc, null, 1m), new Node(a, Pc, 0.000001m), new Node(b, a, 0.5m)]);

        Assert.Equal(Problem.None, result[a].Problem);
        Assert.Equal(Problem.OutOfRange, result[b].Problem);
        Assert.Equal(0.0000005m, result[b].Factor);
    }

    [Fact]
    public void Compute_LongChain_IsIterativeAndDoesNotOverflowTheStack()
    {
        var keys = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid()).ToArray();
        var nodes = keys.Select((k, i) => new Node(k, i == 0 ? null : keys[i - 1], 1m));

        var result = Compute(nodes);

        Assert.Equal((1m, 19_999), (result[keys[^1]].Factor!.Value, result[keys[^1]].Depth!.Value));
    }

    [Theory]
    [InlineData("10", 0, true)]
    [InlineData("2.5", 0, false)]
    [InlineData("0.001", 3, true)]
    [InlineData("0.0005", 3, false)]
    [InlineData("0.2", 3, true)]
    [InlineData("0.25", 1, false)]
    public void IsWholeForBase_FactorTimesTenToBaseDecimals_MustBeInteger(string factor, short dp, bool expected)
    {
        Assert.Equal(expected, IsWholeForBase(decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture), dp));
    }

    [Fact]
    public void Convert_QtyTimesFromOverTo()
    {
        Assert.Equal(12.2m, ProductUnitGraph.Convert(122m, 1m, 10m));   // 122 Piece = 12.2 Box
        Assert.Equal(200m, ProductUnitGraph.Convert(2m, 100m, 1m));     // 2 Full Carton = 200 Piece
        Assert.Equal(10m, Factor(5m, 2m));
    }

    [Fact]
    public void SortOrders_BaseFirstThenFactorThenDepthThenName_UnresolvedLast()
    {
        Guid baseKey = Guid.NewGuid(), big = Guid.NewGuid(), small = Guid.NewGuid(), tieDeep = Guid.NewGuid(), tieShallowB = Guid.NewGuid(),
            tieShallowA = Guid.NewGuid(), broken = Guid.NewGuid();

        var orders = SortOrders([
            (broken, false, null, null, "Aaa"),
            (big, false, 100m, 3, "Full Carton"),
            (tieDeep, false, 10m, 2, "Aaa deep"),
            (tieShallowB, false, 10m, 1, "Box"),
            (tieShallowA, false, 10m, 1, "Arm"),
            (small, false, 0.5m, 1, "Half"),
            (baseKey, true, 1m, 0, "Zzz"),
        ]);

        Assert.Equal(
            new[] { baseKey, small, tieShallowA, tieShallowB, tieDeep, big, broken },
            orders.OrderBy(o => o.Value).Select(o => o.Key));
        Assert.Equal(0, orders[baseKey]);
    }

    [Theory]
    [InlineData("0.2000", "0.2")]
    [InlineData("1000", "1000")]
    [InlineData("0.001", "0.001")]
    [InlineData("12.50", "12.5")]
    public void FormatQuantity_InvariantWithoutTrailingZeros(string value, string expected)
    {
        Assert.Equal(expected, FormatQuantity(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
