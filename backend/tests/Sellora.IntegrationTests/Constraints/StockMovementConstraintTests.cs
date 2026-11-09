using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Database backstops of the Phase 6a movement snapshot (design §4): each of the eight CHECKs rejects a bad row with its own
/// constraint name, the composite (ProductId, EnteredProductUnitId) FK rejects another product's unit, and the allowed shapes
/// (reason code on other types, Adjustment of either sign, split rows without EnteredQuantity) are accepted. Rows are written by
/// the real services and then corrupted with raw UPDATEs, so the CHECKs are the only thing standing in the way.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockMovementConstraintTests(PostgresFixture postgres)
{
    private sealed record Seed(TestDatabase Database, Guid Opening, Guid Purchase, Guid Sale, Guid Damage, Guid Correction, Guid SplitRow, Guid PenBoxId);

    /// <summary>Rice: opening 2 bags (+20 kg), purchase 3 bags (+30 kg), sale 5 kg, Damage -1.5 kg (one batch), Correction +4 kg,
    /// then a 40 kg Loss that spans batches (split rows, EnteredQuantity null). Plus a Pen with a unit row of another product.</summary>
    private async Task<Seed> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var rice = await StockScenario.SeedAsync(db);
        await new StockAdjustmentService(db).CreateAdjustmentAsync(new CreateAdjustmentInput(rice.ProductId, rice.BagModeId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 2m, 600m, null), rice.UserId);
        await rice.PurchaseBagsAsync(db, 3);
        await rice.SellLooseKgAsync(db, 5m);
        await rice.AdjustAsync(db, AdjustmentType.Damage, AdjustmentDirection.Decrease, 1.5m);
        await rice.AdjustAsync(db, AdjustmentType.Correction, AdjustmentDirection.Increase, 4m, 50m);
        await rice.AdjustAsync(db, AdjustmentType.Loss, AdjustmentDirection.Decrease, 40m);

        var pc = new Unit { Id = Guid.NewGuid(), Name = "Piece", ShortCode = "pc", MeasurementCategory = MeasurementCategory.Count };
        var taxId = await db.Products.Where(p => p.Id == rice.ProductId).Select(p => p.TaxId).SingleAsync();
        var pen = new Product { Id = Guid.NewGuid(), Sku = "STN-PEN", Name = "Ball Pen", BaseUnitId = pc.Id, TaxId = taxId };
        var penPc = new ProductUnit { Id = Guid.NewGuid(), ProductId = pen.Id, UnitId = pc.Id, IsBaseUnit = true, InnerQuantity = 1m, ConversionFactorToBase = 1m };
        pen.Units.Add(penPc);
        db.AddRange(pc, pen);
        await db.SaveChangesAsync();

        var all = await db.StockMovements.AsNoTracking().ToListAsync();
        Guid One(Func<StockMovement, bool> p) => all.First(p).Id;
        return new Seed(database, One(m => m.Type == StockMovementType.OpeningStock), One(m => m.Type == StockMovementType.Purchase),
            One(m => m.Type == StockMovementType.Sale), One(m => m.ReasonCode == AdjustmentType.Damage), One(m => m.ReasonCode == AdjustmentType.Correction),
            One(m => m.ReasonCode == AdjustmentType.Loss && m.EnteredQuantity == null), penPc.Id);
    }

    private static async Task<PostgresException> AssertViolatesAsync(TestDatabase database, string sql, string sqlState, string constraint)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal((sqlState, constraint), (ex.SqlState, ex.ConstraintName));
        return ex;
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ServiceWrittenRows_CarryTheSnapshotThatSatisfiesEveryCheck()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await using var db = seed.Database.CreateDbContext();
        var opening = await db.StockMovements.AsNoTracking().SingleAsync(m => m.Id == seed.Opening);
        Assert.Equal((StockMovementType.OpeningStock, AdjustmentType.OpeningStock, 20m, (decimal?)2m, "bag", (decimal?)10m),
            (opening.Type, opening.ReasonCode, opening.QuantityBaseUnits, opening.EnteredQuantity, opening.EnteredUnitCode, opening.ConversionFactorSnapshot));
        var split = await db.StockMovements.AsNoTracking().SingleAsync(m => m.Id == seed.SplitRow);
        Assert.Null(split.EnteredQuantity);
        Assert.NotNull(split.EnteredProductUnitId);
    }

    // The CHECK names are evaluated alphabetically by PostgreSQL, so each case is built to fail exactly one of them first.
    [Theory]
    [InlineData("""SET "EnteredUnitCode" = NULL""", "CK_StockMovements_EnteredUnit_AllOrNone")]
    [InlineData("""SET "ConversionFactorSnapshot" = NULL, "EnteredQuantity" = NULL""", "CK_StockMovements_EnteredUnit_AllOrNone")]
    [InlineData("""SET "EnteredProductUnitId" = NULL, "EnteredQuantity" = NULL""", "CK_StockMovements_EnteredUnit_AllOrNone")]
    [InlineData("""SET "EnteredProductUnitId" = NULL, "EnteredUnitCode" = NULL, "ConversionFactorSnapshot" = NULL""", "CK_StockMovements_EnteredQuantity_RequiresUnit")]
    [InlineData("""SET "EnteredQuantity" = -2, "ConversionFactorSnapshot" = -10""", "CK_StockMovements_EnteredQuantity_Positive")]
    [InlineData("""SET "EnteredQuantity" = NULL, "ConversionFactorSnapshot" = 0""", "CK_StockMovements_Factor_Positive")]
    [InlineData("""SET "EnteredQuantity" = NULL, "ConversionFactorSnapshot" = -10""", "CK_StockMovements_Factor_Positive")]
    [InlineData("""SET "EnteredUnitCode" = ''""", "CK_StockMovements_UnitCode_NotEmpty")]
    [InlineData("""SET "EnteredQuantity" = 3""", "CK_StockMovements_Entered_Arithmetic")]
    [InlineData("""SET "ConversionFactorSnapshot" = 12""", "CK_StockMovements_Entered_Arithmetic")]
    public async Task Update_OpeningStockRowViolatingAnEnteredUnitCheck_RejectedByThatConstraint(string set, string constraint)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "StockMovements" {set} WHERE "Id" = '{seed.Opening}'""", PostgresErrorCodes.CheckViolation, constraint);

        await using var check = seed.Database.CreateDbContext();
        var row = await check.StockMovements.AsNoTracking().SingleAsync(m => m.Id == seed.Opening);
        Assert.Equal(((decimal?)2m, (decimal?)10m, "bag"), (row.EnteredQuantity, row.ConversionFactorSnapshot, row.EnteredUnitCode));
    }

    [Theory]
    [InlineData("Adjustment", """SET "ReasonCode" = NULL""")]
    [InlineData("Adjustment", """SET "ReasonCode" = 'OpeningStock'""")]
    [InlineData("OpeningStock", """SET "ReasonCode" = 'Damage'""")]
    [InlineData("OpeningStock", """SET "ReasonCode" = NULL""")]
    [InlineData("Adjustment", """SET "Type" = 'OpeningStock'""")] // Damage reason on an OpeningStock row
    public async Task Update_ReasonCodeRuleBroken_RejectedByReasonCodeCheck(string rowType, string set)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;
        var id = rowType == "Adjustment" ? seed.Damage : seed.Opening;
        var positive = rowType == "Adjustment" && set.Contains("'OpeningStock'") && set.StartsWith("""SET "Type" """) ? seed.Correction : id;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "StockMovements" {set} WHERE "Id" = '{positive}'""", PostgresErrorCodes.CheckViolation, "CK_StockMovements_ReasonCode");
    }

    [Fact]
    public async Task Update_ReasonCodeOnNonAdjustmentTypes_Accepted()
    {
        // Deliberately allowed: Phase 6b damage/shortage reasons will sit on transfer rows.
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await ExecuteAsync(seed.Database, $"""UPDATE "StockMovements" SET "ReasonCode" = 'Damage' WHERE "Id" IN ('{seed.Purchase}', '{seed.Sale}')""");
        await ExecuteAsync(seed.Database, $"""UPDATE "StockMovements" SET "ReasonCode" = 'Correction' WHERE "Id" = '{seed.Damage}'""");
    }

    [Theory]
    [InlineData("Opening", "Sale")]
    [InlineData("Opening", "PurchaseReturn")]
    [InlineData("Opening", "PurchaseCancellation")]
    [InlineData("Damage", "Purchase")]
    [InlineData("Damage", "SaleReturn")]
    [InlineData("Damage", "SaleCancellation")]
    [InlineData("Purchase", "Sale")]
    [InlineData("Sale", "Purchase")]
    [InlineData("Sale", "SaleReturn")]
    [InlineData("Sale", "SaleCancellation")]
    public async Task Update_TypeContradictingTheSignOfTheQuantity_RejectedBySignCheck(string row, string newType)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;
        var id = row switch { "Opening" => seed.Opening, "Damage" => seed.Damage, "Purchase" => seed.Purchase, _ => seed.Sale };

        await AssertViolatesAsync(seed.Database, $"""UPDATE "StockMovements" SET "Type" = '{newType}' WHERE "Id" = '{id}'""", PostgresErrorCodes.CheckViolation, "CK_StockMovements_Type_Sign");
    }

    [Theory]
    [InlineData("Opening", 0)]
    [InlineData("Purchase", -5)]
    [InlineData("Sale", 5)]
    [InlineData("Sale", 0)]
    public async Task Update_QuantityWithTheWrongSignForItsType_RejectedBySignCheck(string row, decimal quantity)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;
        var id = row switch { "Opening" => seed.Opening, "Purchase" => seed.Purchase, _ => seed.Sale };

        // EnteredQuantity is cleared so the arithmetic CHECK (which sorts first) does not fire.
        await AssertViolatesAsync(seed.Database,
            $"""UPDATE "StockMovements" SET "EnteredQuantity" = NULL, "QuantityBaseUnits" = {quantity} WHERE "Id" = '{id}'""", PostgresErrorCodes.CheckViolation, "CK_StockMovements_Type_Sign");
    }

    [Fact]
    public async Task Update_AdjustmentOfEitherSign_Accepted()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await ExecuteAsync(seed.Database, $"""UPDATE "StockMovements" SET "QuantityBaseUnits" = -"QuantityBaseUnits" WHERE "Id" IN ('{seed.Damage}', '{seed.Correction}') AND "EnteredQuantity" IS NULL""");
        await using var check = seed.Database.CreateDbContext();
        Assert.True(await check.StockMovements.AnyAsync(m => m.Type == StockMovementType.Adjustment && m.QuantityBaseUnits < 0));
        Assert.True(await check.StockMovements.AnyAsync(m => m.Type == StockMovementType.Adjustment && m.QuantityBaseUnits > 0));
    }

    [Fact]
    public async Task Update_SplitRowWithUnitContextAndNoEnteredQuantity_Accepted()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await using var db = seed.Database.CreateDbContext();
        var split = await db.StockMovements.AsNoTracking().Where(m => m.ReasonCode == AdjustmentType.Loss).ToListAsync();
        Assert.True(split.Count >= 2);
        Assert.All(split, m => { Assert.Null(m.EnteredQuantity); Assert.NotNull(m.EnteredProductUnitId); });
    }

    [Fact]
    public async Task Update_LegacyStyleRowWithAllUnitFieldsNull_Accepted()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await ExecuteAsync(seed.Database, $"""UPDATE "StockMovements" SET "EnteredQuantity" = NULL, "EnteredProductUnitId" = NULL, "EnteredUnitCode" = NULL, "ConversionFactorSnapshot" = NULL WHERE "Id" = '{seed.Sale}'""");
    }

    [Fact]
    public async Task Update_EnteredProductUnitOfAnotherProduct_RejectedByCompositeForeignKey()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "StockMovements" SET "EnteredProductUnitId" = '{seed.PenBoxId}' WHERE "Id" = '{seed.Opening}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_StockMovements_ProductUnits_ProductId_EnteredProductUnitId");
    }
}
