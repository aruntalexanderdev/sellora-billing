using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Inventory;

public class UnitServiceTests
{
    private sealed record Fixture(SelloraDbContext Db, UnitService Service, Unit Kg, Unit Gram, Unit Piece, Unit Box);

    /// <summary>kg is the base unit of an active product; g, pc and box are unused.</summary>
    private static async Task<Fixture> SeedAsync(bool kgUsedByInactiveProductOnly = false)
    {
        var db = TestDb.NewInMemory();
        var kg = TestUnits.Kilogram();
        var gram = TestUnits.Gram();
        var piece = TestUnits.Piece();
        var box = TestUnits.Box();
        var rice = new ProductBuilder().WithSku("RICE-BP10").WithBaseUnit(kg);
        if (kgUsedByInactiveProductOnly) rice.Inactive();

        db.AddRange(kg, gram, piece, box, rice.Build());
        await db.SaveChangesAsync();
        return new Fixture(db, new UnitService(db), kg, gram, piece, box);
    }

    private static UpsertUnitInput Same(Unit u) => new(u.Name, u.ShortCode, u.MeasurementCategory, u.DecimalPlaces, u.StandardFactor);

    private static async Task<DomainValidationException> AssertRejected(Func<Task> act, string field)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        Assert.Equal(field, ex.Field);
        return ex;
    }

    // ---------- Create: shape rules ----------

    [Theory]
    [InlineData(MeasurementCategory.Count, (short)0, null)]
    [InlineData(MeasurementCategory.Packaging, (short)0, null)]
    [InlineData(MeasurementCategory.Weight, (short)3, "1")]
    [InlineData(MeasurementCategory.Weight, (short)0, "0.000001")]
    [InlineData(MeasurementCategory.Volume, (short)2, null)]
    [InlineData(MeasurementCategory.Length, (short)1, "0.01")]
    [InlineData(MeasurementCategory.Length, (short)3, "1000000000")]
    public async Task CreateUnitAsync_ValidShape_PersistsAllAttributes(MeasurementCategory category, short decimals, string? factor)
    {
        var f = await SeedAsync();
        var standardFactor = factor is null ? (decimal?)null : decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);

        var unit = await f.Service.CreateUnitAsync(new UpsertUnitInput("New unit", "nu", category, decimals, standardFactor));

        var stored = await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == unit.Id);
        Assert.Equal(category, stored.MeasurementCategory);
        Assert.Equal(decimals, stored.DecimalPlaces);
        Assert.Equal(standardFactor, stored.StandardFactor);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task CreateUnitAsync_PaddedNameAndCode_StoresTrimmedValues()
    {
        var f = await SeedAsync();

        var unit = await f.Service.CreateUnitAsync(new UpsertUnitInput("  Dozen  ", "  dz ", MeasurementCategory.Count, 0, null));

        var stored = await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == unit.Id);
        Assert.Equal("Dozen", stored.Name);
        Assert.Equal("dz", stored.ShortCode);
    }

    [Theory]
    [InlineData((short)-1)]
    [InlineData((short)4)]
    public async Task CreateUnitAsync_DecimalPlacesOutOfRange_RejectedOnDecimalPlaces(short decimals)
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput("Ounce", "oz", MeasurementCategory.Weight, decimals, null)),
            nameof(UpsertUnitInput.DecimalPlaces));
        Assert.Equal(4, await f.Db.Units.CountAsync());
    }

    [Theory]
    [InlineData(MeasurementCategory.Count)]
    [InlineData(MeasurementCategory.Packaging)]
    public async Task CreateUnitAsync_WholeCategoryWithDecimals_RejectedOnDecimalPlaces(MeasurementCategory category)
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput("Dozen", "dz", category, 1, null)),
            nameof(UpsertUnitInput.DecimalPlaces));
    }

    [Theory]
    [InlineData(MeasurementCategory.Count)]
    [InlineData(MeasurementCategory.Packaging)]
    public async Task CreateUnitAsync_WholeCategoryWithStandardFactor_RejectedOnStandardFactor(MeasurementCategory category)
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput("Dozen", "dz", category, 0, 12m)),
            nameof(UpsertUnitInput.StandardFactor));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.001")]
    [InlineData("1000000000.0001")]
    [InlineData("2000000000")]
    public async Task CreateUnitAsync_StandardFactorOutOfRange_RejectedOnStandardFactor(string factor)
    {
        var f = await SeedAsync();
        var value = decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput("Tonne", "t", MeasurementCategory.Weight, 3, value)),
            nameof(UpsertUnitInput.StandardFactor));
    }

    [Fact]
    public async Task CreateUnitAsync_StandardFactorExactlyOneBillion_Accepted()
    {
        var f = await SeedAsync();

        var unit = await f.Service.CreateUnitAsync(new UpsertUnitInput("Megatonne", "Mt", MeasurementCategory.Weight, 3, 1_000_000_000m));

        Assert.Equal(1_000_000_000m, unit.StandardFactor);
    }

    // ---------- Create: uniqueness ----------

    [Theory]
    [InlineData("KILOGRAM", "kgx")]   // name clash, different case
    [InlineData("Kilo", "KG")]        // code clash, different case
    [InlineData(" Kilogram ", "kgx")] // name clash after trimming
    [InlineData("Kilo", " kg ")]      // code clash after trimming
    public async Task CreateUnitAsync_DuplicateNameOrCode_RejectedOnShortCode(string name, string code)
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput(name, code, MeasurementCategory.Weight, 3, null)),
            nameof(UpsertUnitInput.ShortCode));
        Assert.Equal(4, await f.Db.Units.CountAsync());
    }

    [Fact]
    public async Task CreateUnitAsync_DuplicateOfInactiveUnit_StillRejected()
    {
        var f = await SeedAsync();
        f.Box.IsActive = false;
        await f.Db.SaveChangesAsync();

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput("Box", "bx", MeasurementCategory.Packaging, 0, null)),
            nameof(UpsertUnitInput.ShortCode));
    }

    // ---------- Update: unused unit ----------

    [Fact]
    public async Task UpdateUnitAsync_MissingId_ReturnsNull()
    {
        var f = await SeedAsync();

        Assert.Null(await f.Service.UpdateUnitAsync(Guid.NewGuid(), Same(f.Piece)));
    }

    [Fact]
    public async Task UpdateUnitAsync_SameNameAndCodeAsItself_NotTreatedAsDuplicate()
    {
        var f = await SeedAsync();

        var updated = await f.Service.UpdateUnitAsync(f.Piece.Id, Same(f.Piece) with { Name = "PIECE", ShortCode = "PC" });

        Assert.Equal("PIECE", updated!.Name);
        Assert.Equal("PC", updated.ShortCode);
    }

    [Fact]
    public async Task UpdateUnitAsync_NameOfAnotherUnit_RejectedOnShortCode()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Piece.Id, Same(f.Piece) with { Name = "gram" }),
            nameof(UpsertUnitInput.ShortCode));
        Assert.Equal("Piece", (await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Piece.Id)).Name);
    }

    [Fact]
    public async Task UpdateUnitAsync_CodeOfAnotherUnit_RejectedOnShortCode()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Piece.Id, Same(f.Piece) with { ShortCode = "BOX" }),
            nameof(UpsertUnitInput.ShortCode));
    }

    [Fact]
    public async Task UpdateUnitAsync_PaddedValues_StoresTrimmed()
    {
        var f = await SeedAsync();

        var updated = await f.Service.UpdateUnitAsync(f.Piece.Id, Same(f.Piece) with { Name = "  Pieces ", ShortCode = " pcs  " });

        var stored = await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Piece.Id);
        Assert.Equal("Pieces", stored.Name);
        Assert.Equal("pcs", stored.ShortCode);
        Assert.Equal("Pieces", updated!.Name);
    }

    [Fact]
    public async Task UpdateUnitAsync_UnusedUnit_CategoryDecimalsAndFactorMayChange()
    {
        var f = await SeedAsync();

        var updated = await f.Service.UpdateUnitAsync(f.Gram.Id, new UpsertUnitInput("Gram", "g", MeasurementCategory.Volume, 2, 0.5m));

        var stored = await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Gram.Id);
        Assert.Equal(MeasurementCategory.Volume, stored.MeasurementCategory);
        Assert.Equal((short)2, stored.DecimalPlaces);
        Assert.Equal(0.5m, stored.StandardFactor);
        Assert.NotNull(updated);
    }

    [Fact]
    public async Task UpdateUnitAsync_UnusedPieceToPackaging_Allowed()
    {
        var f = await SeedAsync();

        var updated = await f.Service.UpdateUnitAsync(f.Piece.Id, Same(f.Piece) with { MeasurementCategory = MeasurementCategory.Packaging });

        Assert.Equal(MeasurementCategory.Packaging, updated!.MeasurementCategory);
    }

    [Fact]
    public async Task UpdateUnitAsync_ShapeRulesApplyOnUpdate_RejectedOnField()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Piece.Id, Same(f.Piece) with { DecimalPlaces = 2 }),
            nameof(UpsertUnitInput.DecimalPlaces));
        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Box.Id, Same(f.Box) with { StandardFactor = 1m }),
            nameof(UpsertUnitInput.StandardFactor));
        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Gram.Id, Same(f.Gram) with { DecimalPlaces = 4 }),
            nameof(UpsertUnitInput.DecimalPlaces));
        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Gram.Id, Same(f.Gram) with { StandardFactor = 0m }),
            nameof(UpsertUnitInput.StandardFactor));
    }

    // ---------- Update: in-use unit (kg is the base unit of a product) ----------

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitResentUnchanged_Accepted()
    {
        var f = await SeedAsync();

        var updated = await f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg));

        Assert.NotNull(updated);
        Assert.Equal(1m, updated!.StandardFactor);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitNameAndCodeChange_Allowed()
    {
        var f = await SeedAsync();

        await f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { Name = "Kilo", ShortCode = "KGS" });

        var stored = await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Kg.Id);
        Assert.Equal("Kilo", stored.Name);
        Assert.Equal("KGS", stored.ShortCode);
        Assert.Equal(MeasurementCategory.Weight, stored.MeasurementCategory);
    }

    [Theory]
    [InlineData(MeasurementCategory.Volume)]
    [InlineData(MeasurementCategory.Length)]
    public async Task UpdateUnitAsync_InUseUnitCategoryChange_RejectedOnMeasurementCategory(MeasurementCategory category)
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { MeasurementCategory = category }),
            nameof(UpsertUnitInput.MeasurementCategory));
        await AssertUnchangedAsync(f, f.Kg.Id, MeasurementCategory.Weight, 3, 1m);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitToPackaging_RejectedOnMeasurementCategory()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Kg.Id, new UpsertUnitInput("Kilogram", "kg", MeasurementCategory.Packaging, 0, null)),
            nameof(UpsertUnitInput.MeasurementCategory));
        await AssertUnchangedAsync(f, f.Kg.Id, MeasurementCategory.Weight, 3, 1m);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitDecimalsChange_RejectedOnDecimalPlaces()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { DecimalPlaces = 2 }),
            nameof(UpsertUnitInput.DecimalPlaces));
        await AssertUnchangedAsync(f, f.Kg.Id, MeasurementCategory.Weight, 3, 1m);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitFactorChange_RejectedOnStandardFactor()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { StandardFactor = 0.5m }),
            nameof(UpsertUnitInput.StandardFactor));
        await AssertUnchangedAsync(f, f.Kg.Id, MeasurementCategory.Weight, 3, 1m);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitFactorValueToNull_RejectedOnStandardFactor()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { StandardFactor = null }),
            nameof(UpsertUnitInput.StandardFactor));
        await AssertUnchangedAsync(f, f.Kg.Id, MeasurementCategory.Weight, 3, 1m);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitFactorNullToValue_RejectedOnStandardFactor()
    {
        var f = await SeedAsync();
        var ounce = TestUnits.New("Ounce", "oz", MeasurementCategory.Weight, 2, null);
        f.Db.AddRange(ounce, new ProductBuilder().WithBaseUnit(ounce).Build());
        await f.Db.SaveChangesAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(ounce.Id, Same(ounce) with { StandardFactor = 0.0283495m }),
            nameof(UpsertUnitInput.StandardFactor));
        await AssertUnchangedAsync(f, ounce.Id, MeasurementCategory.Weight, 2, null);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitFactorSameValueDifferentScale_Accepted()
    {
        // 1 and 1.000 are the same decimal value; a form resend with trailing zeros must not count as a change.
        var f = await SeedAsync();

        var updated = await f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { StandardFactor = 1.000m });

        Assert.NotNull(updated);
    }

    [Fact]
    public async Task UpdateUnitAsync_UsedOnlyByInactiveProduct_StillLocked()
    {
        var f = await SeedAsync(kgUsedByInactiveProductOnly: true);

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { DecimalPlaces = 1 }),
            nameof(UpsertUnitInput.DecimalPlaces));
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitInactive_LockStillApplies()
    {
        var f = await SeedAsync();
        f.Kg.IsActive = false;
        await f.Db.SaveChangesAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Kg.Id, Same(f.Kg) with { MeasurementCategory = MeasurementCategory.Volume }),
            nameof(UpsertUnitInput.MeasurementCategory));
    }

    [Fact]
    public async Task UpdateUnitAsync_UpdatingDoesNotChangeIsActive()
    {
        var f = await SeedAsync();
        f.Gram.IsActive = false;
        await f.Db.SaveChangesAsync();

        await f.Service.UpdateUnitAsync(f.Gram.Id, Same(f.Gram) with { Name = "Grams" });

        Assert.False((await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Gram.Id)).IsActive);
    }

    // ---------- IsInUse ----------

    [Fact]
    public async Task IsInUseAsync_BaseUnitOfActiveProduct_True()
    {
        var f = await SeedAsync();

        Assert.True(await f.Service.IsInUseAsync(f.Kg.Id));
        Assert.False(await f.Service.IsInUseAsync(f.Gram.Id));
        Assert.False(await f.Service.IsInUseAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task IsInUseAsync_BaseUnitOfInactiveProductOnly_True()
    {
        var f = await SeedAsync(kgUsedByInactiveProductOnly: true);

        Assert.True(await f.Service.IsInUseAsync(f.Kg.Id));
    }

    // ---------- SetActive ----------

    [Fact]
    public async Task SetActiveAsync_MissingId_ReturnsNull()
    {
        var f = await SeedAsync();

        Assert.Null(await f.Service.SetActiveAsync(Guid.NewGuid(), false));
        Assert.Null(await f.Service.SetActiveAsync(Guid.NewGuid(), true));
    }

    [Fact]
    public async Task SetActiveAsync_DeactivateUnusedUnit_Deactivates()
    {
        var f = await SeedAsync();

        var unit = await f.Service.SetActiveAsync(f.Box.Id, false);

        Assert.False(unit!.IsActive);
        Assert.False((await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Box.Id)).IsActive);
    }

    [Fact]
    public async Task SetActiveAsync_DeactivateInUseUnit_RejectedOnIsActive()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.SetActiveAsync(f.Kg.Id, false), nameof(Unit.IsActive));
        Assert.True((await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Kg.Id)).IsActive);
    }

    [Fact]
    public async Task SetActiveAsync_DeactivateUnitUsedByInactiveProductOnly_RejectedOnIsActive()
    {
        var f = await SeedAsync(kgUsedByInactiveProductOnly: true);

        await AssertRejected(() => f.Service.SetActiveAsync(f.Kg.Id, false), nameof(Unit.IsActive));
    }

    [Fact]
    public async Task SetActiveAsync_ActivateInactiveUnit_Activates()
    {
        var f = await SeedAsync();
        f.Gram.IsActive = false;
        await f.Db.SaveChangesAsync();

        var unit = await f.Service.SetActiveAsync(f.Gram.Id, true);

        Assert.True(unit!.IsActive);
    }

    [Fact]
    public async Task SetActiveAsync_ActivateInUseInactiveUnit_AlwaysAllowed()
    {
        // Legacy data: a product's base unit was deactivated before the in-use rule existed.
        var f = await SeedAsync();
        f.Kg.IsActive = false;
        await f.Db.SaveChangesAsync();

        var unit = await f.Service.SetActiveAsync(f.Kg.Id, true);

        Assert.True(unit!.IsActive);
    }

    [Fact]
    public async Task SetActiveAsync_ActivateAlreadyActiveUnit_IsNoOp()
    {
        var f = await SeedAsync();

        var unit = await f.Service.SetActiveAsync(f.Kg.Id, true);

        Assert.True(unit!.IsActive);
    }

    // ---------- Review fixes: enum domain, factor scale, compare at stored scale ----------

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(5)]
    public async Task CreateUnitAsync_UndefinedCategory_RejectedOnMeasurementCategory(int category)
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput("Odd", "odd", (MeasurementCategory)category, 0, null)),
            nameof(UpsertUnitInput.MeasurementCategory));
        Assert.Equal(4, await f.Db.Units.CountAsync());
    }

    [Fact]
    public async Task UpdateUnitAsync_UndefinedCategory_RejectedOnMeasurementCategory()
    {
        var f = await SeedAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Piece.Id, Same(f.Piece) with { MeasurementCategory = (MeasurementCategory)99 }),
            nameof(UpsertUnitInput.MeasurementCategory));
        Assert.Equal(MeasurementCategory.Count, (await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Piece.Id)).MeasurementCategory);
    }

    [Theory]
    [InlineData("0.00000000001")]   // 11 dp, below the numeric(28,10) resolution
    [InlineData("1.12345678901")]   // 11 significant decimals
    [InlineData("0.00000000005")]
    public async Task CreateUnitAsync_StandardFactorMoreThanTenDecimals_RejectedOnStandardFactor(string factor)
    {
        var f = await SeedAsync();
        var value = decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);

        await AssertRejected(() => f.Service.CreateUnitAsync(new UpsertUnitInput("Micro", "ug", MeasurementCategory.Weight, 0, value)),
            nameof(UpsertUnitInput.StandardFactor));
    }

    [Theory]
    [InlineData("0.0000000001")]      // exactly 10 dp
    [InlineData("0.50000000000000")]  // trailing zeros beyond 10 dp are not significant
    [InlineData("1.00000000000000000000")]
    public async Task CreateUnitAsync_StandardFactorTenSignificantDecimalsOrTrailingZeros_Accepted(string factor)
    {
        var f = await SeedAsync();
        var value = decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);

        var unit = await f.Service.CreateUnitAsync(new UpsertUnitInput("Half", "half", MeasurementCategory.Volume, 3, value));

        Assert.Equal(value, (await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == unit.Id)).StandardFactor);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitFactorResentWithExtraTrailingZeros_NotAChange()
    {
        var f = await SeedAsync();
        f.Db.Add(new ProductBuilder().WithBaseUnit(f.Gram).Build());
        await f.Db.SaveChangesAsync();

        var updated = await f.Service.UpdateUnitAsync(f.Gram.Id, Same(f.Gram) with { StandardFactor = 0.00100000000000m, Name = "Grams" });

        Assert.Equal("Grams", updated!.Name);
        Assert.Equal(0.001m, (await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Gram.Id)).StandardFactor);
    }

    [Fact]
    public async Task UpdateUnitAsync_InUseUnitFactorDiffersAtTenthDecimal_RejectedOnStandardFactor()
    {
        var f = await SeedAsync();
        f.Db.Add(new ProductBuilder().WithBaseUnit(f.Gram).Build());
        await f.Db.SaveChangesAsync();

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Gram.Id, Same(f.Gram) with { StandardFactor = 0.0010000001m }),
            nameof(UpsertUnitInput.StandardFactor));
    }

    // ---------- Product units (Phase 2): in use via ProductUnits, deactivation guard, UnitDto projection ----------

    /// <summary>A Piece-based product whose Box row ("1 Box contains 10 Piece") is active or inactive.</summary>
    private static async Task<ProductUnit> AddBoxedProductAsync(Fixture f, bool boxRowActive)
    {
        ProductUnit? box = null;
        var product = new ProductBuilder().WithSku("STN-PEN").WithBaseUnit(f.Piece)
            .WithUnits(p => { var pc = TestProductUnits.Base(p); box = TestProductUnits.Pack(p, f.Box, pc, 10m); box.IsActive = boxRowActive; })
            .Build();
        f.Db.Add(product);
        await f.Db.SaveChangesAsync();
        return box!;
    }

    [Fact]
    public async Task IsInUseAsync_UnitUsedOnlyAsNonBaseProductUnit_True()
    {
        var f = await SeedAsync();
        await AddBoxedProductAsync(f, boxRowActive: true);

        Assert.True(await f.Service.IsInUseAsync(f.Box.Id));
        Assert.True(await f.Service.IsUsedByActiveProductAsync(f.Box.Id));
    }

    [Fact]
    public async Task UpdateUnitAsync_UnitUsedByProductUnit_CategoryFrozen()
    {
        var f = await SeedAsync();
        await AddBoxedProductAsync(f, boxRowActive: true);

        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Box.Id, Same(f.Box) with { MeasurementCategory = MeasurementCategory.Count }),
            nameof(UpsertUnitInput.MeasurementCategory));
        await AssertUnchangedAsync(f, f.Box.Id, MeasurementCategory.Packaging, 0, null);
    }

    [Fact]
    public async Task UpdateUnitAsync_UnitUsedOnlyByInactiveProductUnit_CategoryStillFrozen()
    {
        // History rows are interpreted through the unit, so an inactive row keeps it frozen.
        var f = await SeedAsync();
        await AddBoxedProductAsync(f, boxRowActive: false);

        Assert.True(await f.Service.IsInUseAsync(f.Box.Id));
        await AssertRejected(() => f.Service.UpdateUnitAsync(f.Box.Id, Same(f.Box) with { MeasurementCategory = MeasurementCategory.Count }),
            nameof(UpsertUnitInput.MeasurementCategory));
    }

    [Fact]
    public async Task SetActiveAsync_UnitUsedByActiveProductUnit_RejectedOnIsActive()
    {
        var f = await SeedAsync();
        await AddBoxedProductAsync(f, boxRowActive: true);

        await AssertRejected(() => f.Service.SetActiveAsync(f.Box.Id, false), nameof(Unit.IsActive));
        Assert.True((await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == f.Box.Id)).IsActive);
    }

    [Fact]
    public async Task SetActiveAsync_UnitUsedOnlyByInactiveProductUnit_Deactivates()
    {
        var f = await SeedAsync();
        await AddBoxedProductAsync(f, boxRowActive: false);

        Assert.False(await f.Service.IsUsedByActiveProductAsync(f.Box.Id));
        var unit = await f.Service.SetActiveAsync(f.Box.Id, false);

        Assert.False(unit!.IsActive);
    }

    [Theory]
    [InlineData("unused", false, true)]
    [InlineData("activeRow", true, false)]
    [InlineData("inactiveRow", true, true)]
    [InlineData("productBase", true, false)]
    public async Task UnitsControllerGet_IsInUseAndCanDeactivate_MatchServiceRules(string usage, bool isInUse, bool canDeactivate)
    {
        var f = await SeedAsync();
        var unitId = usage switch
        {
            "unused" => f.Box.Id,
            "productBase" => f.Kg.Id,
            _ => (await AddBoxedProductAsync(f, boxRowActive: usage == "activeRow")).UnitId,
        };
        var controller = new Sellora.Api.Controllers.UnitsController(f.Db, f.Service);

        var dto = (Sellora.Api.Contracts.UnitDto)((Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.Get(unitId, default)).Result!).Value!;

        Assert.Equal((isInUse, canDeactivate), (dto.IsInUse, dto.CanDeactivate));
        Assert.Equal(isInUse, await f.Service.IsInUseAsync(unitId));
        Assert.Equal(!canDeactivate, await f.Service.IsUsedByActiveProductAsync(unitId));
    }

    private static async Task AssertUnchangedAsync(Fixture f, Guid id, MeasurementCategory category, short decimals, decimal? factor)
    {
        var stored = await f.Db.Units.AsNoTracking().SingleAsync(u => u.Id == id);
        Assert.Equal(category, stored.MeasurementCategory);
        Assert.Equal(decimals, stored.DecimalPlaces);
        Assert.Equal(factor, stored.StandardFactor);
    }
}
