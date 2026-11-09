using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Database backstops of the Phase 3 adjustment snapshots (design §2): each CHECK rejects a bad row, the composite
/// (ProductId, ProductUnitId) FK rejects a unit of another product, and a ProductUnit referenced by an adjustment
/// cannot be deleted (Restrict) — the service deactivates such rows instead.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockAdjustmentConstraintTests(PostgresFixture postgres)
{
    private sealed record Seed(TestDatabase Database, StockScenario Rice, Guid AdjustmentId, Guid DecreaseId, Guid PenBoxId);

    /// <summary>Rice (kg base, 10 kg bag) with an opening of 2 bags @ 600/bag and a 1.5 kg damage decrease; a Pen with a Box row.</summary>
    private async Task<Seed> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var rice = await StockScenario.SeedAsync(db);
        var service = new StockAdjustmentService(db);
        var increase = await service.CreateAdjustmentAsync(new CreateAdjustmentInput(rice.ProductId, rice.BagModeId, AdjustmentType.OpeningStock,
            AdjustmentDirection.Increase, 2m, 600m, null), rice.UserId);
        var decrease = await rice.AdjustAsync(db, AdjustmentType.Damage, AdjustmentDirection.Decrease, 1.5m);

        var pc = new Unit { Id = Guid.NewGuid(), Name = "Piece", ShortCode = "pc", MeasurementCategory = MeasurementCategory.Count };
        var box = new Unit { Id = Guid.NewGuid(), Name = "Box", ShortCode = "box", MeasurementCategory = MeasurementCategory.Packaging };
        var taxId = await db.Products.Where(p => p.Id == rice.ProductId).Select(p => p.TaxId).SingleAsync();
        var pen = new Product { Id = Guid.NewGuid(), Sku = "STN-PEN", Name = "Ball Pen", BaseUnitId = pc.Id, TaxId = taxId };
        var penPc = new ProductUnit { Id = Guid.NewGuid(), ProductId = pen.Id, UnitId = pc.Id, IsBaseUnit = true, InnerQuantity = 1m, ConversionFactorToBase = 1m };
        var penBox = new ProductUnit { Id = Guid.NewGuid(), ProductId = pen.Id, UnitId = box.Id, InnerProductUnitId = penPc.Id, InnerQuantity = 10m, ConversionFactorToBase = 10m, SortOrder = 1 };
        pen.Units.Add(penPc);
        pen.Units.Add(penBox);
        db.AddRange(pc, box, pen);
        await db.SaveChangesAsync();
        return new Seed(database, rice, increase.Id, decrease.Id, penBox.Id);
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

    [Theory]
    [InlineData("""SET "EnteredQuantity" = 0, "QuantityBaseUnits" = 0""", "CK_StockAdjustments_EnteredQuantity_Positive")]
    [InlineData("""SET "EnteredQuantity" = -2, "QuantityBaseUnits" = 20, "ConversionFactorToBase" = -10""", "CK_StockAdjustments_EnteredQuantity_Positive")]
    [InlineData("""SET "ConversionFactorToBase" = 0, "QuantityBaseUnits" = 0""", "CK_StockAdjustments_Factor_Positive")]
    [InlineData("""SET "QuantityBaseUnits" = 21""", "CK_StockAdjustments_BaseQty_Arithmetic")]
    [InlineData("""SET "ConversionFactorToBase" = 12""", "CK_StockAdjustments_BaseQty_Arithmetic")]
    [InlineData("""SET "EnteredUnitCost" = NULL""", "CK_StockAdjustments_Cost_Pairing")]
    [InlineData("""SET "UnitCost" = NULL""", "CK_StockAdjustments_Cost_Pairing")]
    public async Task Update_IncreaseViolatingACheck_RejectedByThatConstraint(string set, string constraint)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "StockAdjustments" {set} WHERE "Id" = '{seed.AdjustmentId}'""", PostgresErrorCodes.CheckViolation, constraint);

        await using var check = seed.Database.CreateDbContext();
        var row = await check.StockAdjustments.AsNoTracking().SingleAsync(a => a.Id == seed.AdjustmentId);
        Assert.Equal((2m, 10m, 20m, (decimal?)600m, (decimal?)60m), (row.EnteredQuantity, row.ConversionFactorToBase, row.QuantityBaseUnits, row.EnteredUnitCost, row.UnitCost));
    }

    [Fact]
    public async Task Update_DecreaseGivenOnlyAnEnteredCost_RejectedByCostPairing()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "StockAdjustments" SET "EnteredUnitCost" = 5 WHERE "Id" = '{seed.DecreaseId}'""",
            PostgresErrorCodes.CheckViolation, "CK_StockAdjustments_Cost_Pairing");
    }

    [Fact]
    public async Task Update_FractionalFactorWithExactProduct_Accepted()
    {
        // Positive control for the arithmetic CHECK: numeric multiplication is exact, so 0.75 x 0.001 = 0.00075 would not
        // fit numeric(18,3), but 1.5 x 1 = 1.5 and 3 x 0.5 = 1.5 do.
        var seed = await SeedAsync();
        await using var _ = seed.Database;
        await using var connection = new NpgsqlConnection(seed.Database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"""UPDATE "StockAdjustments" SET "EnteredQuantity" = 3, "ConversionFactorToBase" = 0.5 WHERE "Id" = '{seed.DecreaseId}'""", connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Update_ProductUnitOfAnotherProduct_RejectedByCompositeForeignKey()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "StockAdjustments" SET "ProductUnitId" = '{seed.PenBoxId}' WHERE "Id" = '{seed.AdjustmentId}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_StockAdjustments_ProductUnits_ProductId_ProductUnitId");
    }

    [Fact]
    public async Task SaveChangesAsync_AdjustmentWithUnitOfAnotherProduct_RejectedByCompositeForeignKey()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;
        await using var db = seed.Database.CreateDbContext();
        var penBox = await db.ProductUnits.AsNoTracking().Include(u => u.Unit).SingleAsync(u => u.Id == seed.PenBoxId);
        db.StockAdjustments.Add(new StockAdjustment
        {
            Id = Guid.NewGuid(), AdjustmentNo = "ADJ-9999", ProductId = seed.Rice.ProductId, Type = AdjustmentType.Loss, Direction = AdjustmentDirection.Decrease,
            EnteredQuantity = 1, ProductUnitId = penBox.Id, UnitId = penBox.UnitId, UnitCode = penBox.Unit.ShortCode, ConversionFactorToBase = 10,
            QuantityBaseUnits = 10, UserId = seed.Rice.UserId,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal((PostgresErrorCodes.ForeignKeyViolation, "FK_StockAdjustments_ProductUnits_ProductId_ProductUnitId"), (pg.SqlState, pg.ConstraintName));
    }

    [Fact]
    public async Task Delete_ProductUnitReferencedByAdjustment_RefusedByRestrict()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        // The bag unit is referenced by the adjustment AND by the ledger movement the adjustment wrote (EnteredProductUnitId), both
        // ON DELETE RESTRICT. PostgreSQL reports whichever referencing FK it checks first (order is not specified and varied between
        // runs), so either legitimate FK proves the delete is refused; anything else (e.g. a cascade or a different table) still fails.
        string[] referencingForeignKeys =
        [
            "FK_StockAdjustments_ProductUnits_ProductId_ProductUnitId",
            "FK_StockMovements_ProductUnits_ProductId_EnteredProductUnitId",
        ];
        await using (var connection = new NpgsqlConnection(seed.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""DELETE FROM "ProductUnits" WHERE "Id" = '{seed.Rice.BagModeId}'""", connection);
            var violation = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, violation.SqlState);
            Assert.Contains(violation.ConstraintName, referencingForeignKeys);
        }

        await using var check = seed.Database.CreateDbContext();
        Assert.True(await check.ProductUnits.AnyAsync(u => u.Id == seed.Rice.BagModeId));
    }

    [Fact]
    public async Task Delete_UnitReferencedOnlyByAdjustmentSnapshot_RefusedByRestrict()
    {
        // The Bag unit's product row is re-pointed away so only the adjustment's UnitId snapshot still references it.
        var seed = await SeedAsync();
        await using var _ = seed.Database;
        Guid bagUnitId;
        await using (var db = seed.Database.CreateDbContext())
        {
            bagUnitId = await db.ProductUnits.Where(u => u.Id == seed.Rice.BagModeId).Select(u => u.UnitId).SingleAsync();
            var other = new Unit { Id = Guid.NewGuid(), Name = "Sack", ShortCode = "sack", MeasurementCategory = MeasurementCategory.Packaging };
            db.Units.Add(other);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "ProductUnits" SET "UnitId" = {other.Id} WHERE "Id" = {seed.Rice.BagModeId}""");
        }

        await AssertViolatesAsync(seed.Database, $"""DELETE FROM "Units" WHERE "Id" = '{bagUnitId}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_StockAdjustments_Units_UnitId");
    }
}
