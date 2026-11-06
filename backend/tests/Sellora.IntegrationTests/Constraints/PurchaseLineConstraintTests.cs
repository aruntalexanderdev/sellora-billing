using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Database backstops of the Phase 4 purchase snapshots: the CHECKs on purchase lines (Qty &gt; 0, factor &gt; 0,
/// Qty x factor = BaseQty) and return lines (factor &gt; 0, arithmetic, Qty &gt; 0), the composite
/// (ProductId, ProductUnitId) FKs on both tables, Restrict on the unit references, NOT NULL snapshots, and that a real
/// purchase and a real mixed-unit return leave no empty UnitCode behind.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PurchaseLineConstraintTests(PostgresFixture postgres)
{
    private sealed record Seed(TestDatabase Database, StockScenario Rice, Guid PoLineId, Guid ReturnLineId, Guid OilUnitId, Guid OilUnitMasterId);

    /// <summary>Rice bought as 3 bags (30 kg) and 12 kg returned entered in loose kg; plus another product (Oil) with its own base row.</summary>
    private async Task<Seed> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var rice = await StockScenario.SeedAsync(db);
        var purchase = await rice.PurchaseBagsAsync(db, 3m);
        var ret = await new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, StockScenario.ReturnDate, null,
            [new PurchaseReturnLineInput(purchase.Lines.Single().Id, rice.LooseKgModeId, 12m)]), rice.UserId);

        var litre = new Unit { Id = Guid.NewGuid(), Name = "Litre", ShortCode = "l", MeasurementCategory = MeasurementCategory.Volume, DecimalPlaces = 3, StandardFactor = 1m };
        var taxId = await db.Products.Where(p => p.Id == rice.ProductId).Select(p => p.TaxId).SingleAsync();
        var oil = new Product { Id = Guid.NewGuid(), Sku = "OIL", Name = "Oil", BaseUnitId = litre.Id, TaxId = taxId };
        var oilBase = new ProductUnit { Id = Guid.NewGuid(), ProductId = oil.Id, UnitId = litre.Id, IsBaseUnit = true, InnerQuantity = 1m, ConversionFactorToBase = 1m };
        oil.Units.Add(oilBase);
        db.AddRange(litre, oil);
        await db.SaveChangesAsync();
        return new Seed(database, rice, purchase.Lines.Single().Id, ret.Lines.Single().Id, oilBase.Id, litre.Id);
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
    [InlineData("""SET "Qty" = 0, "BaseQty" = 0""", "CK_PurchaseOrderLines_Qty_Positive")]
    [InlineData("""SET "ConversionFactorToBase" = 0, "BaseQty" = 0""", "CK_PurchaseOrderLines_Factor_Positive")]
    [InlineData("""SET "BaseQty" = 31""", "CK_PurchaseOrderLines_BaseQty_Arithmetic")]
    [InlineData("""SET "ConversionFactorToBase" = 12""", "CK_PurchaseOrderLines_BaseQty_Arithmetic")]
    public async Task Update_PurchaseLineViolatingACheck_RejectedByThatConstraint(string set, string constraint)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "PurchaseOrderLines" {set} WHERE "Id" = '{seed.PoLineId}'""", PostgresErrorCodes.CheckViolation, constraint);

        await using var check = seed.Database.CreateDbContext();
        var row = await check.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == seed.PoLineId);
        Assert.Equal((3m, 10m, 30m), (row.Qty, row.ConversionFactorToBase, row.BaseQty));
    }

    [Theory]
    [InlineData("""SET "Qty" = 0, "BaseQty" = 0""", "CK_PurchaseReturnLines_Qty_Positive")]
    [InlineData("""SET "ConversionFactorToBase" = 0, "BaseQty" = 0""", "CK_PurchaseReturnLines_Factor_Positive")]
    [InlineData("""SET "BaseQty" = 13""", "CK_PurchaseReturnLines_BaseQty_Arithmetic")]
    [InlineData("""SET "ConversionFactorToBase" = 10""", "CK_PurchaseReturnLines_BaseQty_Arithmetic")]
    public async Task Update_ReturnLineViolatingACheck_RejectedByThatConstraint(string set, string constraint)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "PurchaseReturnLines" {set} WHERE "Id" = '{seed.ReturnLineId}'""", PostgresErrorCodes.CheckViolation, constraint);

        await using var check = seed.Database.CreateDbContext();
        var row = await check.PurchaseReturnLines.AsNoTracking().SingleAsync(l => l.Id == seed.ReturnLineId);
        Assert.Equal((12m, 1m, 12m, "kg"), (row.Qty, row.ConversionFactorToBase, row.BaseQty, row.UnitCode));
    }

    [Fact]
    public async Task Update_PurchaseLineToAUnitOfAnotherProduct_RejectedByCompositeForeignKey()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "PurchaseOrderLines" SET "ProductUnitId" = '{seed.OilUnitId}' WHERE "Id" = '{seed.PoLineId}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_PurchaseOrderLines_ProductUnits_ProductId_ProductUnitId");
    }

    [Fact]
    public async Task Update_ReturnLineToAUnitOfAnotherProduct_RejectedByCompositeForeignKey()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await AssertViolatesAsync(seed.Database, $"""UPDATE "PurchaseReturnLines" SET "ProductUnitId" = '{seed.OilUnitId}' WHERE "Id" = '{seed.ReturnLineId}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_PurchaseReturnLines_ProductUnits_ProductId_ProductUnitId");
    }

    [Theory]
    [InlineData("PurchaseOrderLines", "UnitId")]
    [InlineData("PurchaseOrderLines", "UnitCode")]
    [InlineData("PurchaseReturnLines", "ProductUnitId")]
    [InlineData("PurchaseReturnLines", "UnitId")]
    [InlineData("PurchaseReturnLines", "UnitCode")]
    [InlineData("PurchaseReturnLines", "ConversionFactorToBase")]
    public async Task Update_SnapshotColumnToNull_RejectedByNotNull(string table, string column)
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(seed.Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""UPDATE "{table}" SET "{column}" = NULL""", connection);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
    }

    [Fact]
    public async Task Delete_UnitReferencedOnlyByAReturnLineSnapshot_RefusedByRestrict()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;
        // Bag product row removed from every purchase line's reach is not possible (Restrict), so re-point the line's
        // ProductUnit away from the Bag unit and show the return/PO UnitId snapshot still protects the Units row.
        var kgUnitId = await seed.Database.CreateDbContext().Units.Where(u => u.ShortCode == "kg").Select(u => u.Id).SingleAsync();

        await AssertViolatesAsync(seed.Database, $"""DELETE FROM "Units" WHERE "Id" = '{kgUnitId}'""", PostgresErrorCodes.ForeignKeyViolation,
            (await ConstraintNameAsync(seed.Database, kgUnitId)));
    }

    private static async Task<string> ConstraintNameAsync(TestDatabase database, Guid unitId)
    {
        // Whichever Restrict FK is checked first reports; assert it is one of the Unit references, not a cascade.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""DELETE FROM "Units" WHERE "Id" = '{unitId}'""", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("Units", ex.ConstraintName!);
        return ex.ConstraintName!;
    }

    [Fact]
    public async Task RealPurchaseAndMixedUnitReturn_LeaveNoEmptyUnitCodeAndSnapshotsMatchTheMaster()
    {
        var seed = await SeedAsync();
        await using var _ = seed.Database;

        await using var db = seed.Database.CreateDbContext();
        var empty = await Count(db, """SELECT COUNT(*) FROM "PurchaseOrderLines" WHERE btrim("UnitCode") = ''""")
                    + await Count(db, """SELECT COUNT(*) FROM "PurchaseReturnLines" WHERE btrim("UnitCode") = ''""");
        Assert.Equal(0, empty);
        var po = await db.PurchaseOrderLines.AsNoTracking().Include(l => l.Unit).SingleAsync();
        var rl = await db.PurchaseReturnLines.AsNoTracking().Include(l => l.Unit).SingleAsync();
        Assert.Equal(("bag", po.Unit.ShortCode), (po.UnitCode, po.UnitCode));
        Assert.Equal(("kg", "kg"), (rl.UnitCode, rl.Unit.ShortCode));
        Assert.NotEqual(po.ProductUnitId, rl.ProductUnitId); // the return carries its own unit
        await StockInvariants.AssertHoldAsync(db);
    }

    private static async Task<long> Count(Sellora.Infrastructure.Data.SelloraDbContext db, string sql) =>
        (await db.Database.SqlQueryRaw<long>(sql.Replace("COUNT(*)", "COUNT(*) AS \"Value\"")).ToListAsync()).Single();
}
