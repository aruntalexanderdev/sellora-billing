using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Tests.TestData;
using V = Sellora.Domain.Inventory.ProductUnitConfigurationValidator;

namespace Sellora.Tests.Inventory;

/// <summary>
/// Rules R1–R9 of the pure hierarchy validator (Phase 2 design §5.1), keyed by request field (<c>Units[i].Field</c>,
/// <c>Units</c>, <c>BaseUnitId</c>). Drafts are built the way the product service builds them: inner rows resolved by
/// UnitId among the active rows, stored rows carrying their stored structure and lock reason.
/// </summary>
public class ProductUnitConfigurationValidatorTests
{
    private static UnitInfo Info(Unit unit) => UnitInfo.From(unit);

    private static readonly UnitInfo Piece = Info(TestUnits.Piece());
    private static readonly UnitInfo Box = Info(TestUnits.Box());
    private static readonly UnitInfo HalfCarton = Info(TestUnits.HalfCarton());
    private static readonly UnitInfo FullCarton = Info(TestUnits.FullCarton());
    private static readonly UnitInfo Kg = Info(TestUnits.Kilogram());
    private static readonly UnitInfo Gram = Info(TestUnits.Gram());
    private static readonly UnitInfo Bag = Info(TestUnits.Bag());
    private static readonly UnitInfo Litre = Info(TestUnits.Litre());
    private static readonly UnitInfo Millilitre = Info(TestUnits.Millilitre());
    private static readonly UnitInfo Bottle = Info(TestUnits.Bottle());
    private static readonly UnitInfo Case = Info(TestUnits.Case());
    private static readonly UnitInfo Dozen = Info(TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null));

    /// <summary>A hierarchy under construction. Rows get their submitted index in the order they are added.</summary>
    private sealed class Config(UnitInfo baseUnit)
    {
        private readonly List<ProductUnitDraft> _drafts = [];
        public UnitInfo? BaseUnit { get; set; } = baseUnit;
        public Guid BaseUnitId { get; set; } = baseUnit.Id;
        public Guid? StoredBaseUnitId { get; set; }
        public bool HasStockHistory { get; set; }
        public HashSet<string> BarcodesElsewhere { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<ProductUnitDraft> Drafts => _drafts;
        public ProductUnitDraft this[int index] => _drafts[index];

        public Config Base(Func<ProductUnitDraft, ProductUnitDraft>? with = null) =>
            Row(BaseUnit!, null, 1m, with);

        /// <summary>"1 <paramref name="unit"/> contains <paramref name="qty"/> <paramref name="inner"/>".</summary>
        public Config Pack(UnitInfo unit, UnitInfo inner, decimal? qty, Func<ProductUnitDraft, ProductUnitDraft>? with = null) =>
            Row(unit, inner, qty, with);

        private Config Row(UnitInfo unit, UnitInfo? inner, decimal? qty, Func<ProductUnitDraft, ProductUnitDraft>? with)
        {
            var draft = new ProductUnitDraft
            {
                Index = _drafts.Count, Id = Guid.NewGuid(), Unit = unit, UnitId = unit.Id, InnerUnitId = inner?.Id, InnerQuantity = qty,
                IsActive = true, IsPurchasable = true, IsSellable = true, IsNew = true,
            };
            _drafts.Add(with is null ? draft : with(draft));
            return this;
        }

        public Config Edit(int index, Func<ProductUnitDraft, ProductUnitDraft> change)
        {
            _drafts[index] = change(Resolve()[index]);
            return this;
        }

        /// <summary>Turns every row into an unchanged stored row (as if saved earlier, possibly by legacy code that did not
        /// enforce the current rules), optionally locking some of them.</summary>
        public Config Persist(params (int Index, string Reason)[] locks)
        {
            var resolved = Resolve();
            var graph = ProductUnitGraph.Compute(resolved.Select(d => new ProductUnitGraph.Node(d.Id, d.InnerUnitId is null ? null : d.InnerRef ?? Guid.Empty, d.InnerUnitId is null ? 1m : d.InnerQuantity ?? 0m)));
            var byId = resolved.ToDictionary(d => d.Id);
            for (var i = 0; i < resolved.Count; i++)
            {
                var d = resolved[i];
                _drafts[i] = d with
                {
                    IsNew = false,
                    StoredIsBase = d.InnerUnitId is null,
                    StoredUnitId = d.UnitId,
                    StoredInnerId = d.InnerRef,
                    StoredInnerUnitId = d.InnerRef is { } r ? byId[r].UnitId : null,
                    StoredInnerQuantity = d.InnerUnitId is null ? 1m : d.InnerQuantity,
                    StoredFactor = graph[d.Id].Factor,
                    StoredIsActive = d.IsActive,
                    LockReason = locks.Where(l => l.Index == i).Select(l => l.Reason).FirstOrDefault(),
                };
            }
            StoredBaseUnitId = BaseUnitId;
            return this;
        }

        /// <summary>Resolves InnerRef like the service: the active row with that UnitId (a row naming its own unit
        /// contains itself); stored rows keep an already resolved reference.</summary>
        private List<ProductUnitDraft> Resolve() => _drafts.Select(d =>
        {
            if (d.InnerUnitId is not { } innerUnit || d.InnerRef is not null) return d;
            if (innerUnit == d.UnitId) return d with { InnerRef = d.Id };
            var pick = _drafts.FirstOrDefault(o => o.Id != d.Id && o.UnitId == innerUnit && o.IsActive)
                       ?? _drafts.FirstOrDefault(o => o.Id != d.Id && o.UnitId == innerUnit);
            return pick is null ? d : d with { InnerRef = pick.Id };
        }).ToList();

        public ProductUnitConfigurationResult Validate()
        {
            var drafts = Resolve();
            _drafts.Clear();
            _drafts.AddRange(drafts);
            return V.Validate(new ProductUnitValidationContext(BaseUnitId, BaseUnit, StoredBaseUnitId, HasStockHistory, BarcodesElsewhere), drafts);
        }
    }

    private static Config Pen() => new Config(Piece)
        .Base()
        .Pack(Box, Piece, 10m)
        .Pack(HalfCarton, Box, 5m)
        .Pack(FullCarton, HalfCarton, 2m);

    private static void AssertValid(ProductUnitConfigurationResult result) =>
        Assert.True(result.IsValid, "Unexpected errors: " + string.Join(" | ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));

    private static FieldError AssertError(ProductUnitConfigurationResult result, string field, string? message = null)
    {
        var matches = result.Errors.Where(e => e.Field == field && (message is null || e.Message == message)).ToList();
        Assert.True(matches.Count > 0,
            $"Expected error on {field}{(message is null ? "" : $" '{message}'")}. Errors: " + string.Join(" | ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return matches[0];
    }

    private static void AssertWarning(ProductUnitConfigurationResult result, string field, string? contains = null)
    {
        Assert.True(result.Warnings.Any(w => w.Field == field && (contains is null || w.Message.Contains(contains))),
            $"Expected warning on {field}. Warnings: " + string.Join(" | ", result.Warnings.Select(e => $"{e.Field}: {e.Message}")));
    }

    private static decimal FactorAt(Config c, ProductUnitConfigurationResult r, int index) => r.Row(c[index].Id).Factor!.Value;

    // ------------------------------------------------------------------ canonical hierarchies

    [Fact]
    public void Validate_Pen_ValidWithFactors1_10_50_100SortOrderAndChain()
    {
        var c = Pen();

        var r = c.Validate();

        AssertValid(r);
        Assert.Empty(r.Warnings);
        Assert.Equal(new[] { 1m, 10m, 50m, 100m }, Enumerable.Range(0, 4).Select(i => FactorAt(c, r, i)));
        Assert.Equal(new short[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(i => r.Row(c[i].Id).SortOrder));
        Assert.Equal(new int?[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(i => r.Row(c[i].Id).Depth));
        Assert.Equal("1 Full Carton = 2 Half Carton = 10 Box = 100 Piece", r.Row(c[3].Id).Chain);
        Assert.All(r.Rows, row => Assert.True(row.IsStructuralChange)); // all new
    }

    [Fact]
    public void Validate_PenSubmittedTopDown_SameFactorsAndSortOrderByFactor()
    {
        var c = new Config(Piece)
            .Pack(FullCarton, HalfCarton, 2m)
            .Pack(HalfCarton, Box, 5m)
            .Pack(Box, Piece, 10m)
            .Base();

        var r = c.Validate();

        AssertValid(r);
        Assert.Equal(new[] { 100m, 50m, 10m, 1m }, Enumerable.Range(0, 4).Select(i => FactorAt(c, r, i)));
        Assert.Equal(new short[] { 3, 2, 1, 0 }, Enumerable.Range(0, 4).Select(i => r.Row(c[i].Id).SortOrder));
    }

    [Fact]
    public void Validate_BoxOf10AndBoxOf20OnTwoProductsSharingGlobalBox_BothValid()
    {
        var p1 = new Config(Piece).Base().Pack(Box, Piece, 10m);
        var p2 = new Config(Piece).Base().Pack(Box, Piece, 20m);

        var r1 = p1.Validate();
        var r2 = p2.Validate();

        AssertValid(r1);
        AssertValid(r2);
        Assert.Equal(Box.Id, p1[1].UnitId);
        Assert.Equal(Box.Id, p2[1].UnitId);
        Assert.Equal((10m, 20m), (FactorAt(p1, r1, 1), FactorAt(p2, r2, 1)));
    }

    [Fact]
    public void Validate_CartonOf144_Valid()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 12m).Pack(FullCarton, Box, 12m);

        var r = c.Validate();

        AssertValid(r);
        Assert.Equal(144m, FactorAt(c, r, 2));
        Assert.Equal("1 Full Carton = 12 Box = 144 Piece", r.Row(c[2].Id).Chain);
    }

    [Fact]
    public void Validate_RiceKgWithGramAndBag25_Valid()
    {
        var c = new Config(Kg).Base().Pack(Gram, Kg, 0.001m).Pack(Bag, Kg, 25m);

        var r = c.Validate();

        AssertValid(r);
        Assert.Equal((0.001m, 25m), (FactorAt(c, r, 1), FactorAt(c, r, 2)));
        Assert.Equal("1 Gram = 0.001 Kilogram", r.Row(c[1].Id).Chain);
        Assert.Equal(new short[] { 0, 1, 2 }, Enumerable.Range(0, 3).Select(i => r.Row(c[i].Id).SortOrder));
    }

    [Fact]
    public void Validate_OilLitreWithMillilitreBottleHalfLitreAndCaseOf12_Valid()
    {
        var c = new Config(Litre).Base().Pack(Millilitre, Litre, 0.001m).Pack(Bottle, Millilitre, 500m).Pack(Case, Bottle, 12m);

        var r = c.Validate();

        AssertValid(r);
        Assert.Equal(new[] { 1m, 0.001m, 0.5m, 6m }, Enumerable.Range(0, 4).Select(i => FactorAt(c, r, i)));
        Assert.Equal("1 Case = 12 Bottle = 6000 Millilitre = 6 Litre", r.Row(c[3].Id).Chain);
    }

    [Fact]
    public void Validate_BaseOnly_Valid()
    {
        var c = new Config(Piece).Base();

        var r = c.Validate();

        AssertValid(r);
        var row = Assert.Single(r.Rows);
        Assert.Equal((1m, 0, (short)0, "1 Piece"), (row.Factor!.Value, row.Depth!.Value, row.SortOrder, row.Chain));
    }

    [Fact]
    public void Validate_DozenOverPiece_CountOverCountBaseAllowed()
    {
        AssertValid(new Config(Piece).Base().Pack(Dozen, Piece, 12m).Validate());
    }

    // ------------------------------------------------------------------ R1: base row

    [Fact]
    public void Validate_NoBaseRow_ErrorOnUnits()
    {
        var c = new Config(Piece).Pack(Box, Piece, 10m);

        AssertError(c.Validate(), "Units", "Add the base unit row (the unit that contains no other unit).");
    }

    [Fact]
    public void Validate_NoRowsAtAll_ErrorOnUnits()
    {
        AssertError(new Config(Piece).Validate(), "Units", "Add at least the base unit.");
    }

    [Fact]
    public void Validate_TwoBaseRows_SecondGetsInnerUnitIdError()
    {
        var c = new Config(Piece).Base().Pack(Box, null!, null);

        AssertError(c.Validate(), "Units[1].InnerUnitId", "Only the base unit can contain no other unit; choose the unit this one contains.");
    }

    [Fact]
    public void Validate_BaseRowUnitDiffersFromProductBaseUnit_ErrorOnUnitId()
    {
        var c = new Config(Piece) { BaseUnitId = Kg.Id, BaseUnit = Kg }.Base(d => d with { Unit = Piece, UnitId = Piece.Id });

        AssertError(c.Validate(), "Units[0].UnitId", "The base unit row must use the product's base unit.");
    }

    [Fact]
    public void Validate_PackagingBaseUnit_ErrorOnBaseUnitId()
    {
        var c = new Config(Box).Base();

        AssertError(c.Validate(), "BaseUnitId", V.PackagingBaseUnitMessage);
    }

    [Fact]
    public void Validate_UnknownBaseUnit_ErrorOnBaseUnitId()
    {
        var c = new Config(Piece).Base();
        c.BaseUnit = null;

        AssertError(c.Validate(), "BaseUnitId", V.InvalidBaseUnitMessage);
    }

    [Fact]
    public void Validate_NewlyAssignedInactiveBaseUnit_ErrorOnBaseUnitId_KeptInactiveBaseAllowed()
    {
        var inactivePiece = Piece with { IsActive = false };

        AssertError(new Config(inactivePiece).Base().Validate(), "BaseUnitId", V.InactiveBaseUnitMessage);

        var kept = new Config(inactivePiece).Base().Persist();
        var r = kept.Validate();
        AssertValid(r);
        AssertWarning(r, "Units[0].UnitId", "inactive in the Unit Master");
    }

    [Fact]
    public void Validate_BaseQuantityNotOne_ErrorOnInnerQuantity()
    {
        var c = new Config(Piece).Base(d => d with { InnerQuantity = 2m });

        AssertError(c.Validate(), "Units[0].InnerQuantity", "The base unit always has quantity 1.");
    }

    [Fact]
    public void Validate_BaseQuantityNull_TreatedAsOne()
    {
        AssertValid(new Config(Piece).Base(d => d with { InnerQuantity = null }).Pack(Box, Piece, 10m).Validate());
    }

    [Fact]
    public void Validate_BaseDeactivated_ErrorOnIsActive()
    {
        var c = new Config(Piece).Base(d => d with { IsActive = false });

        AssertError(c.Validate(), "Units[0].IsActive", "The base unit cannot be deactivated.");
    }

    [Fact]
    public void Validate_StoredBaseGivenAnInner_ErrorOnInnerUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m).Persist()
            .Edit(0, d => d with { InnerUnitId = Box.Id });

        var r = c.Validate();

        AssertError(r, "Units[0].InnerUnitId", "The base unit cannot contain another unit.");
    }

    [Fact]
    public void Validate_StoredNonBaseTurnedIntoBase_ErrorOnInnerUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m).Persist()
            .Edit(1, d => d with { InnerUnitId = null, InnerRef = null, Unit = Piece, UnitId = Piece.Id })
            .Edit(0, d => d with { InnerUnitId = Box.Id });

        var r = c.Validate();

        Assert.False(r.IsValid);
        AssertError(r, "Units[1].InnerUnitId", "This unit is not the base unit; choose the unit it contains.");
    }

    // ------------------------------------------------------------------ R2: graph

    [Fact]
    public void Validate_SelfCycle_ErrorOnInnerUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box, Box, 10m);

        AssertError(c.Validate(), "Units[1].InnerUnitId", "A unit cannot contain itself.");
    }

    [Fact]
    public void Validate_TwoCycle_ErrorOnBothRows()
    {
        var c = new Config(Piece).Base().Pack(Box, HalfCarton, 2m).Pack(HalfCarton, Box, 5m);

        var r = c.Validate();

        const string loop = "These units contain each other in a loop; one of them must contain a smaller unit.";
        AssertError(r, "Units[1].InnerUnitId", loop);
        AssertError(r, "Units[2].InnerUnitId", loop);
        Assert.Null(r.Row(c[1].Id).Factor);
        Assert.Null(r.Row(c[1].Id).Chain);
    }

    [Fact]
    public void Validate_ThreeCycle_ErrorOnEveryRowInTheLoop()
    {
        var c = new Config(Piece).Base().Pack(Box, FullCarton, 2m).Pack(HalfCarton, Box, 5m).Pack(FullCarton, HalfCarton, 2m);

        var r = c.Validate();

        Assert.All(new[] { 1, 2, 3 }, i => AssertError(r, $"Units[{i}].InnerUnitId"));
        Assert.DoesNotContain(r.Errors, e => e.Field.StartsWith("Units[0]"));
    }

    [Fact]
    public void Validate_InnerNotAUnitOfThisProduct_OrphanErrorOnInnerUnitId()
    {
        var c = new Config(Piece).Base().Pack(FullCarton, Bag, 2m);

        AssertError(c.Validate(), "Units[1].InnerUnitId", "Choose the unit this one contains from this product's units.");
    }

    [Fact]
    public void Validate_InnerBeingRemoved_ReportsResolutionError()
    {
        const string message = "Box is being removed, but this unit still contains it; keep it or choose another contained unit.";
        var c = new Config(Piece).Base().Pack(HalfCarton, Box, 5m, d => d with { InnerResolutionError = message });

        AssertError(c.Validate(), "Units[1].InnerUnitId", message);
    }

    [Fact]
    public void Validate_MissingQuantity_ErrorOnInnerQuantity()
    {
        AssertError(new Config(Piece).Base().Pack(Box, Piece, null).Validate(), "Units[1].InnerQuantity",
            "Enter how many of the contained unit this unit holds.");
    }

    // ------------------------------------------------------------------ R3: category

    public static TheoryData<MeasurementCategory, MeasurementCategory, bool> CategoryMatrix()
    {
        var data = new TheoryData<MeasurementCategory, MeasurementCategory, bool>();
        foreach (var b in Enum.GetValues<MeasurementCategory>())
        {
            foreach (var u in Enum.GetValues<MeasurementCategory>())
            {
                var allowed = u == MeasurementCategory.Packaging
                              || (u == MeasurementCategory.Count && b == MeasurementCategory.Count)
                              || (u is MeasurementCategory.Weight or MeasurementCategory.Volume or MeasurementCategory.Length && u == b);
                data.Add(b, u, allowed);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CategoryMatrix))]
    public void IsCategoryAllowed_FiveByFiveMatrix(MeasurementCategory baseCategory, MeasurementCategory unitCategory, bool allowed)
    {
        Assert.Equal(allowed, V.IsCategoryAllowed(baseCategory, unitCategory));
    }

    [Theory]
    [MemberData(nameof(CategoryMatrix))]
    public void Validate_CategoryMatrix_NewRowRejectedOnUnitIdOnlyWhenIncompatible(MeasurementCategory baseCategory, MeasurementCategory unitCategory, bool allowed)
    {
        // Units without standard factors and whole-number bases, so only R3 can fail.
        var baseUnit = Info(TestUnits.New("Base " + baseCategory, "b", baseCategory, 0, null));
        var unit = Info(TestUnits.New("Unit " + unitCategory, "u", unitCategory, 0, null));
        var c = new Config(baseUnit).Base().Pack(unit, baseUnit, 10m);

        var r = c.Validate();

        if (baseCategory == MeasurementCategory.Packaging)
        {
            AssertError(r, "BaseUnitId", V.PackagingBaseUnitMessage);
            return;
        }
        Assert.Equal(!allowed, r.Errors.Any(e => e.Field == "Units[1].UnitId"));
        if (allowed) AssertValid(r);
    }

    [Fact]
    public void Validate_CountUnitOverWeightBase_MessageNamesCountRule()
    {
        AssertError(new Config(Kg).Base().Pack(Dozen, Kg, 12m).Validate(), "Units[1].UnitId",
            "Dozen is a count unit; it can only be used over a count base unit.");
    }

    [Fact]
    public void Validate_VolumeUnitOverWeightBase_Rejected()
    {
        AssertError(new Config(Kg).Base().Pack(Litre, Kg, 1m).Validate(), "Units[1].UnitId",
            "Litre is a volume unit and cannot be used with a weight base unit.");
    }

    // ------------------------------------------------------------------ R4: standard factors

    [Fact]
    public void Validate_GramDeclaredAs0_002Kg_ErrorOnInnerQuantity()
    {
        var c = new Config(Kg).Base().Pack(Gram, Kg, 0.002m);

        AssertError(c.Validate(), "Units[1].InnerQuantity", "1 Gram is 0.001 Kilogram; enter 0.001.");
    }

    [Fact]
    public void Validate_KgOverGramBase_Must1000()
    {
        AssertValid(new Config(Gram).Base().Pack(Kg, Gram, 1000m).Validate());
        AssertError(new Config(Gram).Base().Pack(Kg, Gram, 100m).Validate(), "Units[1].InnerQuantity", "1 Kilogram is 1000 Gram; enter 1000.");
    }

    [Fact]
    public void Validate_StandardFactorsThatDoNotDivideExactly_Rejected()
    {
        var third = Info(TestUnits.New("Third", "t", MeasurementCategory.Weight, 3, 3m));
        var c = new Config(third).Base().Pack(Kg, third, 0.333333m);

        AssertError(c.Validate(), "Units[1].InnerQuantity", "Kilogram cannot contain Third: their standard sizes do not divide exactly.");
    }

    // ------------------------------------------------------------------ R5: whole number of the base step

    [Fact]
    public void Validate_PackOf2_5Pieces_RejectedOnWholeNumberBase()
    {
        var pack = Info(TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null));

        AssertError(new Config(Piece).Base().Pack(pack, Piece, 2.5m).Validate(), "Units[1].InnerQuantity",
            "1 Pack would be 2.5 Piece; it must be a whole number of Piece.");
    }

    [Fact]
    public void Validate_PackOf0_0005Kg_RejectedBeyondKgDecimals()
    {
        var sachet = Info(TestUnits.New("Sachet", "sct", MeasurementCategory.Packaging, 0, null));

        AssertError(new Config(Kg).Base().Pack(sachet, Kg, 0.0005m).Validate(), "Units[1].InnerQuantity",
            "1 Sachet would be 0.0005 Kilogram; Kilogram allows at most 3 decimal places.");
    }

    [Fact]
    public void Validate_PackOf0_001Kg_Accepted()
    {
        var sachet = Info(TestUnits.New("Sachet", "sct", MeasurementCategory.Packaging, 0, null));

        AssertValid(new Config(Kg).Base().Pack(sachet, Kg, 0.001m).Validate());
    }

    [Fact]
    public void Validate_FractionalInnerQuantityWithWholeFactor_Accepted()
    {
        // 1 Half Box contains 0.5 Box = 5 Piece: only the resulting factor must be whole.
        var halfBox = Info(TestUnits.New("Half Box", "hbx", MeasurementCategory.Packaging, 0, null));

        AssertValid(new Config(Piece).Base().Pack(Box, Piece, 10m).Pack(halfBox, Box, 0.5m).Validate());
    }

    // ------------------------------------------------------------------ R6: bounds

    [Fact]
    public void Validate_ChainAboveOneBillion_ErrorOnInnerQuantity()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 1_000_000m).Pack(FullCarton, Box, 10_000m);

        var r = c.Validate();

        AssertError(r, "Units[2].InnerQuantity", "This unit would hold more than 1,000,000,000 base units.");
        Assert.DoesNotContain(r.Errors, e => e.Field.StartsWith("Units[1]"));
    }

    [Fact]
    public void Validate_FactorExactlyOneBillion_Accepted()
    {
        AssertValid(new Config(Piece).Base().Pack(Box, Piece, 1_000m).Pack(FullCarton, Box, 1_000_000m).Validate());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1000000001")]
    [InlineData("1.0000001")]
    public void Validate_InnerQuantityOutOfBoundsOrTooPrecise_ErrorOnInnerQuantity(string qty)
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture));

        AssertError(c.Validate(), "Units[1].InnerQuantity", "Quantity must be greater than 0 and at most 1,000,000,000, with at most 6 decimal places.");
    }

    [Fact]
    public void Validate_FactorBelowOneMillionth_ErrorOnInnerQuantity()
    {
        var micro = Info(TestUnits.New("Micro", "mc", MeasurementCategory.Packaging, 0, null));
        var nano = Info(TestUnits.New("Nano", "nn", MeasurementCategory.Packaging, 0, null));
        var c = new Config(Kg).Base().Pack(micro, Kg, 0.000001m).Pack(nano, micro, 0.5m);

        var r = c.Validate();

        Assert.Contains(r.Errors, e => e.Field == "Units[2].InnerQuantity" && e.Message.Contains("between 0.000001 and 1,000,000,000"));
    }

    // ------------------------------------------------------------------ R7: units, defaults, label, barcode

    [Fact]
    public void Validate_SameUnitOnTwoActiveRows_SecondRejectedOnUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m).Pack(Box, Piece, 20m);

        AssertError(c.Validate(), "Units[2].UnitId", "Box is already used by another active row of this product.");
    }

    [Fact]
    public void Validate_SameUnitOnceActiveOnceInactive_Allowed()
    {
        // Deactivate-and-replace: Box(10) kept inactive, Box(20) active.
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m, d => d with { IsActive = false }).Pack(Box, Piece, 20m);

        var r = c.Validate();

        AssertValid(r);
        Assert.Equal(20m, FactorAt(c, r, 2));
    }

    [Fact]
    public void Validate_InactiveUnitOnNewRow_ErrorOnUnitId()
    {
        var oldBox = Box with { IsActive = false };

        AssertError(new Config(Piece).Base().Pack(oldBox, Piece, 10m).Validate(), "Units[1].UnitId",
            "Box is inactive in the Unit Master; activate it or choose another unit.");
    }

    [Fact]
    public void Validate_InactiveUnitOnNewInactiveRow_Allowed()
    {
        AssertValid(new Config(Piece).Base().Pack(Box with { IsActive = false }, Piece, 10m, d => d with { IsActive = false }).Validate());
    }

    [Fact]
    public void Validate_InactiveUnitOnUnchangedStoredRow_WarningOnly()
    {
        var c = new Config(Piece).Base().Pack(Box with { IsActive = false }, Piece, 10m).Persist();

        var r = c.Validate();

        AssertValid(r);
        AssertWarning(r, "Units[1].UnitId", "Box is inactive in the Unit Master.");
    }

    [Fact]
    public void Validate_ReactivatingRowWhoseUnitIsInactive_ErrorOnUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box with { IsActive = false }, Piece, 10m, d => d with { IsActive = false }).Persist()
            .Edit(1, d => d with { IsActive = true });

        AssertError(c.Validate(), "Units[1].UnitId", "Box is inactive in the Unit Master; activate it or choose another unit.");
    }

    [Fact]
    public void Validate_UnknownUnit_ErrorOnUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m, d => d with { Unit = null, UnitId = Guid.NewGuid() });

        AssertError(c.Validate(), "Units[1].UnitId", "Select a valid unit.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_TwoDefaultUnits_SecondRejected(bool purchase)
    {
        Func<ProductUnitDraft, ProductUnitDraft> makeDefault = d => purchase ? d with { IsDefaultPurchaseUnit = true } : d with { IsDefaultSalesUnit = true };
        var c = new Config(Piece).Base(makeDefault).Pack(Box, Piece, 10m, makeDefault);

        var field = purchase ? "Units[1].IsDefaultPurchaseUnit" : "Units[1].IsDefaultSalesUnit";
        AssertError(c.Validate(), field, $"Only one unit can be the default {(purchase ? "purchase" : "sales")} unit.");
    }

    [Fact]
    public void Validate_DefaultSalesUnitNotSellable_Rejected()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m, d => d with { IsDefaultSalesUnit = true, IsSellable = false });

        AssertError(c.Validate(), "Units[1].IsDefaultSalesUnit", "The default sales unit must be active and sellable.");
    }

    [Fact]
    public void Validate_DefaultPurchaseUnitInactive_Rejected()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m, d => d with { IsDefaultPurchaseUnit = true, IsActive = false });

        AssertError(c.Validate(), "Units[1].IsDefaultPurchaseUnit", "The default purchase unit must be active and purchasable.");
    }

    [Fact]
    public void Validate_OneDefaultEach_Valid()
    {
        AssertValid(new Config(Piece)
            .Base(d => d with { IsDefaultSalesUnit = true })
            .Pack(Box, Piece, 10m, d => d with { IsDefaultPurchaseUnit = true })
            .Validate());
    }

    [Fact]
    public void Validate_LabelLongerThan60_ErrorOnLabel()
    {
        var c = new Config(Piece).Base(d => d with { Label = new string('x', 61) });

        AssertError(c.Validate(), "Units[0].Label", "Label can be at most 60 characters.");
        AssertValid(new Config(Piece).Base(d => d with { Label = new string('x', 60) }).Validate());
    }

    [Theory]
    [InlineData(51, null, "Barcode can be at most 50 characters.")]
    [InlineData(0, "890 123", "Barcode cannot contain spaces.")]
    [InlineData(0, "890\t123", "Barcode cannot contain spaces.")]
    public void Validate_BadBarcode_ErrorOnBarcode(int length, string? barcode, string message)
    {
        var c = new Config(Piece).Base(d => d with { Barcode = barcode ?? new string('1', length) });

        AssertError(c.Validate(), "Units[0].Barcode", message);
    }

    [Fact]
    public void Validate_SameBarcodeOnTwoActiveRows_SecondRejected()
    {
        var c = new Config(Piece).Base(d => d with { Barcode = "8901234" }).Pack(Box, Piece, 10m, d => d with { Barcode = "8901234" });

        AssertError(c.Validate(), "Units[1].Barcode", "This barcode is already used by another unit of this product.");
    }

    [Fact]
    public void Validate_SameBarcodeOnInactiveRow_Allowed()
    {
        AssertValid(new Config(Piece).Base(d => d with { Barcode = "8901234" })
            .Pack(Box, Piece, 10m, d => d with { Barcode = "8901234", IsActive = false }).Validate());
    }

    [Fact]
    public void Validate_BarcodeUsedByAnotherProduct_Rejected()
    {
        var c = new Config(Piece).Base(d => d with { Barcode = "8901234" });
        c.BarcodesElsewhere.Add("8901234");

        AssertError(c.Validate(), "Units[0].Barcode", "This barcode is already used by another product.");
    }

    [Fact]
    public void Validate_BarcodeExactly50Characters_Accepted()
    {
        AssertValid(new Config(Piece).Base(d => d with { Barcode = new string('7', 50) }).Validate());
    }

    // ------------------------------------------------------------------ R8: locked rows

    [Fact]
    public void Validate_LockedRowQuantityChanged_ErrorOnInnerQuantity()
    {
        var c = Pen().Persist((1, "Transactions")).Edit(1, d => d with { InnerQuantity = 12m });

        var r = c.Validate();

        AssertError(r, "Units[1].InnerQuantity", V.LockedMessage);
    }

    [Fact]
    public void Validate_LockedRowUnitChanged_ErrorOnUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m).Persist((1, "Prices"))
            .Edit(1, d => d with { Unit = Bag, UnitId = Bag.Id });

        AssertError(c.Validate(), "Units[1].UnitId", V.LockedMessage);
    }

    [Fact]
    public void Validate_LockedRowInnerChanged_ErrorOnInnerUnitId()
    {
        var c = Pen().Persist((2, "Transactions"), (1, "ContainedInLockedUnit"), (0, "ContainedInLockedUnit"))
            .Edit(2, d => d with { InnerUnitId = Piece.Id, InnerRef = null, InnerQuantity = 50m });

        AssertError(c.Validate(), "Units[2].InnerUnitId", V.LockedMessage);
    }

    [Fact]
    public void Validate_LockedRowWhoseFactorWouldChangeThroughItsInner_ErrorOnInnerQuantity()
    {
        // Half Carton is locked; Box (its inner) is not marked locked here, so only the factor comparison catches it.
        var c = Pen().Persist((2, "Transactions")).Edit(1, d => d with { InnerQuantity = 12m });

        AssertError(c.Validate(), "Units[2].InnerQuantity", V.LockedMessage);
    }

    [Fact]
    public void Validate_LockedRowMutableAttributesChanged_Valid()
    {
        var c = Pen().Persist((1, "Transactions"), (0, "ContainedInLockedUnit"))
            .Edit(1, d => d with { Label = "Box of 10", Barcode = "8900001", IsSellable = false, IsPurchasable = false, IsDefaultPurchaseUnit = false })
            .Edit(3, d => d with { IsDefaultSalesUnit = true });

        var r = c.Validate();

        AssertValid(r);
        Assert.False(r.Row(c[1].Id).IsStructuralChange);
    }

    [Fact]
    public void Validate_LockedRowDeactivated_Valid()
    {
        AssertValid(Pen().Persist((3, "Prices")).Edit(3, d => d with { IsActive = false }).Validate());
    }

    [Fact]
    public void Validate_UnchangedStoredRows_NotStructural()
    {
        var c = Pen().Persist();

        var r = c.Validate();

        AssertValid(r);
        Assert.All(r.Rows, row => Assert.False(row.IsStructuralChange));
    }

    // ------------------------------------------------------------------ R8b: active over inactive inner

    [Fact]
    public void Validate_UnlockedActiveRowOverInactiveInner_ErrorOnInnerUnitId()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m, d => d with { IsActive = false }).Pack(HalfCarton, Box, 5m);

        AssertError(c.Validate(), "Units[2].InnerUnitId", "Box is inactive; an active unit can only contain an active unit.");
    }

    [Fact]
    public void Validate_LockedActiveRowOverInactiveInner_WarningOnly()
    {
        var c = Pen().Persist((2, "Transactions"), (1, "ContainedInLockedUnit"), (0, "ContainedInLockedUnit"))
            .Edit(1, d => d with { IsActive = false });

        var r = c.Validate();

        AssertValid(r);
        AssertWarning(r, "Units[2].InnerUnitId", "Half Carton contains Box, which is inactive.");
    }

    // ------------------------------------------------------------------ R9: base unit change

    [Fact]
    public void Validate_BaseUnitChangeWithStockHistory_ErrorOnBaseUnitId()
    {
        var c = new Config(Kg).Base().Pack(Bag, Kg, 10m).Persist();
        c.HasStockHistory = true;
        c.BaseUnitId = Gram.Id;
        c.BaseUnit = Gram;
        c.Edit(0, d => d with { Unit = Gram, UnitId = Gram.Id }).Edit(1, d => d with { InnerUnitId = Gram.Id, InnerQuantity = 10000m });

        AssertError(c.Validate(), "BaseUnitId", V.BaseChangeWithHistoryMessage);
    }

    [Fact]
    public void Validate_BaseUnitChangeWithALockedRow_ErrorOnBaseUnitId()
    {
        var c = new Config(Kg).Base().Pack(Bag, Kg, 10m).Persist((1, "Prices"), (0, "ContainedInLockedUnit"));
        c.BaseUnitId = Gram.Id;
        c.BaseUnit = Gram;
        c.Edit(0, d => d with { Unit = Gram, UnitId = Gram.Id });

        AssertError(c.Validate(), "BaseUnitId", V.BaseChangeWithLockedUnitsMessage);
    }

    [Fact]
    public void Validate_BaseUnitChangeWithoutHistoryOrLocks_Valid()
    {
        // kg -> g: the old kg row becomes "1 kg contains 1000 g".
        var c = new Config(Kg).Base().Pack(Gram, Kg, 0.001m).Persist();
        c.BaseUnitId = Gram.Id;
        c.BaseUnit = Gram;
        c.Edit(0, d => d with { Unit = Gram, UnitId = Gram.Id })
         .Edit(1, d => d with { Unit = Kg, UnitId = Kg.Id, InnerUnitId = Gram.Id, InnerRef = null, InnerQuantity = 1000m });

        var r = c.Validate();

        AssertValid(r);
        Assert.Equal(1000m, FactorAt(c, r, 1));
    }

    // ------------------------------------------------------------------ legacy rows and warnings

    [Fact]
    public void Validate_UnchangedLegacyRowsViolatingR3R4R5_WarningsNotErrors()
    {
        var pack = Info(TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null));
        var c = new Config(Kg).Base()
            .Pack(Dozen, Kg, 12m)                // R3: count over weight
            .Pack(Gram, Kg, 0.002m)              // R4: 1 g != 0.002 kg
            .Pack(pack, Kg, 0.0005m)             // R5: beyond kg decimals
            .Persist();

        var r = c.Validate();

        AssertValid(r);
        AssertWarning(r, "Units[1].UnitId", "count unit");
        AssertWarning(r, "Units[2].InnerQuantity", "1 Gram is 0.001 Kilogram");
        AssertWarning(r, "Units[3].InnerQuantity", "at most 3 decimal places");
    }

    [Fact]
    public void Validate_LegacyRowChanged_RulesBecomeErrors()
    {
        var pack = Info(TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null));
        var c = new Config(Piece).Base().Pack(pack, Piece, 2.5m).Persist()
            .Edit(1, d => d with { InnerQuantity = 3.5m });

        AssertError(c.Validate(), "Units[1].InnerQuantity", "1 Pack would be 3.5 Piece; it must be a whole number of Piece.");
    }

    [Fact]
    public void Validate_LegacyRowOnlyLabelChanged_StillWarning()
    {
        var pack = Info(TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null));
        var c = new Config(Piece).Base().Pack(pack, Piece, 2.5m).Persist()
            .Edit(1, d => d with { Label = "Half-dozen-ish" });

        var r = c.Validate();

        AssertValid(r);
        AssertWarning(r, "Units[1].InnerQuantity", "whole number");
    }

    [Fact]
    public void Validate_InactiveLegacyRow_NoWarning()
    {
        var pack = Info(TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null));
        var c = new Config(Piece).Base().Pack(pack, Piece, 2.5m, d => d with { IsActive = false }).Persist();

        var r = c.Validate();

        AssertValid(r);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Validate_TwoActiveUnitsWithEqualFactors_Warning()
    {
        var pack = Info(TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null));
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m).Pack(pack, Piece, 10m);

        var r = c.Validate();

        AssertValid(r);
        AssertWarning(r, "Units[2].InnerQuantity", "1 Pack and 1 Box are both 10 Piece.");
    }

    [Fact]
    public void Validate_EqualFactorsButOneInactive_NoWarning()
    {
        var pack = Info(TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null));

        Assert.Empty(new Config(Piece).Base().Pack(Box, Piece, 10m, d => d with { IsActive = false }).Pack(pack, Piece, 10m).Validate().Warnings);
    }

    // ------------------------------------------------------------------ keys of kept, unsubmitted rows

    [Fact]
    public void Validate_KeptUnsubmittedRow_ErrorsKeyedOnUnits()
    {
        var c = new Config(Piece).Base().Pack(Box, Piece, 10m, d => d with { Index = -1, IsActive = false, Unit = null });

        var r = c.Validate();

        AssertError(r, "Units", "Select a valid unit.");
        Assert.DoesNotContain(r.Errors, e => e.Field.StartsWith("Units[-1]"));
    }

    [Fact]
    public void Key_SubmittedAndKeptRows()
    {
        Assert.Equal("Units[3].Barcode", V.Key(new ProductUnitDraft { Index = 3, Id = Guid.NewGuid(), UnitId = Guid.NewGuid() }, "Barcode"));
        Assert.Equal("Units", V.Key(new ProductUnitDraft { Index = -1, Id = Guid.NewGuid(), UnitId = Guid.NewGuid() }, "Barcode"));
    }
}
