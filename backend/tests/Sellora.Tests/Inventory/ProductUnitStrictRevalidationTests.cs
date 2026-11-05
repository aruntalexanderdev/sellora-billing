using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Inventory;

/// <summary>
/// Rows whose own fields are unchanged are still validated strictly (R3/R4/R5) when the product's base unit changes or
/// when a row on their inner chain changes; only truly untouched legacy rows get warnings. Also the 50-row limit.
/// </summary>
public class ProductUnitStrictRevalidationTests
{
    // ---------- validator ----------

    private static ProductUnitDraft Stored(int index, Unit unit, ProductUnitDraft? inner, decimal qty, decimal factor) => new()
    {
        Index = index, Id = Guid.NewGuid(), Unit = UnitInfo.From(unit), UnitId = unit.Id,
        InnerUnitId = inner?.UnitId, InnerRef = inner?.Id, InnerQuantity = qty, IsActive = true,
        StoredIsBase = inner is null, StoredUnitId = unit.Id, StoredInnerId = inner?.Id, StoredInnerUnitId = inner?.UnitId,
        StoredInnerQuantity = qty, StoredFactor = factor, StoredIsActive = true,
    };

    private static ProductUnitConfigurationResult Validate(Unit baseUnit, Guid? storedBaseUnitId, params ProductUnitDraft[] drafts) =>
        ProductUnitConfigurationValidator.Validate(
            new ProductUnitValidationContext(baseUnit.Id, UnitInfo.From(baseUnit), storedBaseUnitId, false, new HashSet<string>()), drafts);

    [Fact]
    public void Validate_BaseChangedKgToPieceWithUnchangedGramRow_RejectsCategoryAndDecimals()
    {
        var kg = TestUnits.Kilogram(); var pc = TestUnits.Piece(); var g = TestUnits.Gram();
        var storedBase = Stored(0, kg, null, 1m, 1m);
        var gram = Stored(1, g, storedBase, 0.001m, 0.001m);
        var newBase = storedBase with { UnitId = pc.Id, Unit = UnitInfo.From(pc) };

        var result = Validate(pc, kg.Id, newBase, gram with { InnerUnitId = pc.Id });

        Assert.Contains(result.Errors, e => e.Field == "Units[1].UnitId");        // Weight over a Count base (R3)
        Assert.Contains(result.Errors, e => e.Field == "Units[1].InnerQuantity"); // 0.001 on a 0-dp base (R5)
    }

    [Fact]
    public void Validate_BaseChangedPieceToKgWithUnchangedDozenRow_RejectsCountOverWeight()
    {
        var kg = TestUnits.Kilogram(); var pc = TestUnits.Piece();
        var dozen = TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null);
        var storedBase = Stored(0, pc, null, 1m, 1m);
        var dz = Stored(1, dozen, storedBase, 12m, 12m);
        var newBase = storedBase with { UnitId = kg.Id, Unit = UnitInfo.From(kg) };

        var result = Validate(kg, pc.Id, newBase, dz with { InnerUnitId = kg.Id });

        Assert.Contains(result.Errors, e => e.Field == "Units[1].UnitId");
    }

    [Fact]
    public void Validate_InnerUnitChanged_AppliesStandardFactorRuleStrictlyToOuterRow()
    {
        // mg base; Gram = 1000 mg; Kilogram = 1000 Gram. The inner row changes its unit to Decagram (same quantity), so
        // the untouched Kilogram row now claims 1 kg = 1000 dag, which its standard factors contradict (100).
        var mg = TestUnits.New("Milligram", "mg", MeasurementCategory.Weight, 0, 0.000001m);
        var dag = TestUnits.New("Decagram", "dag", MeasurementCategory.Weight, 0, 0.01m);
        var kg = TestUnits.Kilogram(); var g = TestUnits.Gram();
        var baseRow = Stored(0, mg, null, 1m, 1m);
        var gram = Stored(1, g, baseRow, 1000m, 1000m);
        var kilo = Stored(2, kg, gram, 1000m, 1_000_000m);

        var unchanged = Validate(mg, mg.Id, baseRow, gram, kilo);
        Assert.True(unchanged.IsValid, string.Join("; ", unchanged.Errors));

        var changedInner = gram with { UnitId = dag.Id, Unit = UnitInfo.From(dag) };
        var result = Validate(mg, mg.Id, baseRow, changedInner, kilo with { InnerUnitId = dag.Id });

        Assert.Contains(result.Errors, e => e.Field == "Units[2].InnerQuantity");
    }

    [Fact]
    public void Validate_UntouchedLegacyRow_StaysAWarning()
    {
        var pc = TestUnits.Piece();
        var dozen = TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null);
        var baseRow = Stored(0, pc, null, 1m, 1m);
        var legacy = Stored(1, dozen, baseRow, 2.5m, 2.5m); // non-integer factor on a 0-dp base, grandfathered

        var result = Validate(pc, pc.Id, baseRow, legacy);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Field == "Units[1].InnerQuantity");
    }

    [Fact]
    public void Validate_ReactivatedUnlockedLegacyRow_RejectsNonWholeFactor()
    {
        var pc = TestUnits.Piece();
        var dozen = TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null);
        var baseRow = Stored(0, pc, null, 1m, 1m);
        var legacy = Stored(1, dozen, baseRow, 2.5m, 2.5m) with { StoredIsActive = false }; // inactive, now resubmitted active

        var result = Validate(pc, pc.Id, baseRow, legacy);

        Assert.Contains(result.Errors, e => e.Field == "Units[1].InnerQuantity");
    }

    [Fact]
    public void Validate_ReactivatedLockedLegacyRow_OnlyWarns()
    {
        var pc = TestUnits.Piece();
        var dozen = TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null);
        var baseRow = Stored(0, pc, null, 1m, 1m) with { LockReason = "ContainedInLockedUnit" };
        var legacy = Stored(1, dozen, baseRow, 2.5m, 2.5m) with { StoredIsActive = false, LockReason = "Transactions" };

        var result = Validate(pc, pc.Id, baseRow, legacy);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Field == "Units[1].InnerQuantity");
    }

    // ---------- service ----------

    private sealed record Fixture(ProductService Service, Product Product, ProductUnit Base, ProductUnit Child, Tax Tax);

    private static async Task<Fixture> SeedAsync(Unit baseUnit, Unit childUnit, decimal childQty, params Unit[] extraUnits)
    {
        var db = TestDb.NewInMemory();
        var tax = TestTaxes.Gst5();
        var product = new ProductBuilder().WithBaseUnit(baseUnit).WithTax(tax).Build();
        var baseRow = TestProductUnits.Base(product);
        var child = TestProductUnits.Pack(product, childUnit, baseRow, childQty);
        db.AddRange(extraUnits);
        db.Add(product);
        await db.SaveChangesAsync();
        return new Fixture(new ProductService(db), product, baseRow, child, tax);
    }

    private static ProductUnitInputItem Input(Guid? id, Guid unitId, Guid? innerUnitId, decimal? qty) =>
        new(id, unitId, innerUnitId, qty, null, null, true, true, false, false, true);

    private static UpsertProductInput Upsert(Fixture f, Guid baseUnitId, params ProductUnitInputItem[] units) =>
        new(f.Product.Sku, f.Product.Name, null, baseUnitId, f.Tax.Id, 0m, units);

    [Fact]
    public async Task UpdateProductAsync_BaseKgToPieceWithGramRow_Rejected()
    {
        var pc = TestUnits.Piece();
        var f = await SeedAsync(TestUnits.Kilogram(), TestUnits.Gram(), 0.001m, pc);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(f.Product.Id,
            Upsert(f, pc.Id, Input(f.Base.Id, pc.Id, null, 1m), Input(f.Child.Id, f.Child.UnitId, pc.Id, 0.001m))));

        Assert.Contains(ex.Errors, e => e.Field == "Units[1].UnitId");
        Assert.Contains(ex.Errors, e => e.Field == "Units[1].InnerQuantity");
    }

    [Fact]
    public async Task UpdateProductAsync_BasePieceToKgWithDozenRow_Rejected()
    {
        var kg = TestUnits.Kilogram();
        var f = await SeedAsync(TestUnits.Piece(), TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null), 12m, kg);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(f.Product.Id,
            Upsert(f, kg.Id, Input(f.Base.Id, kg.Id, null, 1m), Input(f.Child.Id, f.Child.UnitId, kg.Id, 12m))));

        Assert.Contains(ex.Errors, e => e.Field == "Units[1].UnitId");
    }

    [Fact]
    public async Task UpdateProductAsync_MoreThan50Units_RejectedOnUnits()
    {
        var f = await SeedAsync(TestUnits.Piece(), TestUnits.Box(), 10m);
        var rows = Enumerable.Range(0, ProductUnitConfigurationValidator.MaxUnits + 1)
            .Select(_ => Input(null, Guid.NewGuid(), f.Base.UnitId, 2m)).Prepend(Input(f.Base.Id, f.Base.UnitId, null, 1m)).ToArray();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(f.Product.Id, Upsert(f, f.Base.UnitId, rows)));

        Assert.Equal("Units", ex.Field);
    }
}
