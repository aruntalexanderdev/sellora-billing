using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.PenScenario;

namespace Sellora.IntegrationTests.Products;

/// <summary>
/// <see cref="ProductService"/> saves that only work on PostgreSQL because of the partial unique indexes
/// (<c>IX_ProductUnits_Barcode</c>, <c>_DefaultSales</c>, <c>_ProductId_UnitId</c> WHERE active, <c>_Base</c>) and the
/// composite inner-unit FK, which are checked per row: values moving between rows, a base-unit swap, deactivate-and-
/// replace, and deleting/re-pointing rows in one save. Every test ends with the migration's V1–V6 queries.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProductUnitSaveTests(PostgresFixture postgres)
{
    private static async Task<List<ProductUnit>> RowsAsync(TestDatabase database, Guid productId)
    {
        await using var db = database.CreateDbContext();
        return await db.ProductUnits.AsNoTracking().Include(u => u.Unit).Where(u => u.ProductId == productId).OrderBy(u => u.SortOrder).ToListAsync();
    }

    private static async Task AssertValidationHoldsAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        await ProductUnitValidation.AssertHoldAsync(db);
    }

    [Fact]
    public async Task CreateProductAsync_Pen_InsertsChainInDependencyOrder()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext()) await SeedAsync(db);

        await using var check = database.CreateDbContext();
        var pen = await check.Products.AsNoTracking().Include(p => p.Units).ThenInclude(u => u.Unit).SingleAsync();
        var byCode = pen.Units.ToDictionary(u => u.Unit.ShortCode);
        Assert.Equal((1m, 10m, 50m, 100m), (byCode["pc"].ConversionFactorToBase, byCode["box"].ConversionFactorToBase, byCode["hctn"].ConversionFactorToBase, byCode["fctn"].ConversionFactorToBase));
        Assert.Equal(byCode["hctn"].Id, byCode["fctn"].InnerProductUnitId);
        await ProductUnitValidation.AssertHoldAsync(check);
    }

    [Fact]
    public async Task UpdateProductAsync_BarcodesSwappedBetweenRows_SavedDespiteUniqueIndex()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc] with { Barcode = "8901000000001" }, c[s.Box] with { Barcode = "8901000000010" }, c[s.HCtn], c[s.FCtn]);
        }

        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc] with { Barcode = "8901000000010" }, c[s.Box] with { Barcode = "8901000000001" }, c[s.HCtn], c[s.FCtn]);
        }

        var rows = await RowsAsync(database, s.PenId);
        Assert.Equal(("8901000000010", "8901000000001"), (rows[0].Barcode, rows[1].Barcode));
        await AssertValidationHoldsAsync(database);
    }

    [Fact]
    public async Task UpdateProductAsync_BarcodeMovedToAnotherRow_Saved()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc] with { Barcode = "8901000000001" }, c[s.Box], c[s.HCtn], c[s.FCtn]);
        }

        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc] with { Barcode = null }, c[s.Box], c[s.HCtn], c[s.FCtn] with { Barcode = "8901000000001" });
        }

        var rows = await RowsAsync(database, s.PenId);
        Assert.Equal(new string?[] { null, null, null, "8901000000001" }, rows.Select(r => r.Barcode));
    }

    [Fact]
    public async Task UpdateProductAsync_DefaultSalesUnitMovedBetweenRows_SavedDespiteUniqueIndex()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc] with { IsDefaultSalesUnit = true, IsDefaultPurchaseUnit = true }, c[s.Box], c[s.HCtn], c[s.FCtn]);
        }

        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc] with { IsDefaultSalesUnit = false, IsDefaultPurchaseUnit = false },
                c[s.Box] with { IsDefaultSalesUnit = true }, c[s.HCtn], c[s.FCtn] with { IsDefaultPurchaseUnit = true });
        }

        var rows = await RowsAsync(database, s.PenId);
        Assert.Equal(new[] { false, true, false, false }, rows.Select(r => r.IsDefaultSalesUnit));
        Assert.Equal(new[] { false, false, false, true }, rows.Select(r => r.IsDefaultPurchaseUnit));
    }

    [Fact]
    public async Task UpdateProductAsync_KgToGramBaseSwapKeepingRowIds_Saved()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Guid riceId;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            riceId = (await new ProductService(db).CreateProductAsync(s.Input("SPC-TUR", s.Kg,
                Row(s.Kg, null, 1m), Row(s.Gram, s.Kg, 0.001m), Row(s.Bag, s.Kg, 25m)))).Id;
        }
        var before = await RowsAsync(database, riceId);

        // The base row becomes Gram; the old Gram row becomes "1 kg contains 1000 g"; the bag is re-expressed in g.
        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, riceId);
            await new ProductService(db).UpdateProductAsync(riceId, s.Input("SPC-TUR", s.Gram,
                c[s.Kg] with { UnitId = s.Gram },
                c[s.Gram] with { UnitId = s.Kg, InnerUnitId = s.Gram, InnerQuantity = 1000m },
                c[s.Bag] with { InnerUnitId = s.Kg, InnerQuantity = 25m }));
        }

        var after = await RowsAsync(database, riceId);
        Assert.Equal(before.Select(r => r.Id).Order(), after.Select(r => r.Id).Order());
        var baseRow = Assert.Single(after, r => r.IsBaseUnit);
        Assert.Equal((before.Single(r => r.IsBaseUnit).Id, s.Gram), (baseRow.Id, baseRow.UnitId));
        Assert.Equal(1000m, after.Single(r => r.UnitId == s.Kg).ConversionFactorToBase);
        Assert.Equal(25_000m, after.Single(r => r.UnitId == s.Bag).ConversionFactorToBase);
        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(s.Gram, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == riceId)).BaseUnitId);
        }
        await AssertValidationHoldsAsync(database);
    }

    [Fact]
    public async Task UpdateProductAsync_KgToGramBaseSwapWithNewKgRow_DeletesOldGramRow()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Guid riceId;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            riceId = (await new ProductService(db).CreateProductAsync(s.Input("SPC-TUR", s.Kg, Row(s.Kg, null, 1m), Row(s.Gram, s.Kg, 0.001m)))).Id;
        }

        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, riceId);
            await new ProductService(db).UpdateProductAsync(riceId, s.Input("SPC-TUR", s.Gram,
                c[s.Kg] with { UnitId = s.Gram }, Row(s.Kg, s.Gram, 1000m)));
        }

        var after = await RowsAsync(database, riceId);
        Assert.Equal(new[] { (s.Gram, 1m), (s.Kg, 1000m) }, after.Select(r => (r.UnitId, r.ConversionFactorToBase)));
        await AssertValidationHoldsAsync(database);
    }

    [Fact]
    public async Task UpdateProductAsync_PricedBox10ReplacedByBox20_BothRowsStoredAndChainRePointed()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        Guid oldBoxId;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            oldBoxId = (await ActiveRowAsync(db, s.PenId, s.Box)).Id;
            await new PricingService(db).SetPriceAsync(oldBoxId, 95m, s.UserId);
        }

        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc], c[s.Box] with { IsActive = false }, Row(s.Box, s.Pc, 20m), c[s.HCtn], c[s.FCtn]);
        }

        var rows = await RowsAsync(database, s.PenId);
        var boxes = rows.Where(r => r.UnitId == s.Box).ToList();
        Assert.Equal(2, boxes.Count);
        Assert.Equal((false, 10m), (boxes.Single(b => b.Id == oldBoxId).IsActive, boxes.Single(b => b.Id == oldBoxId).ConversionFactorToBase));
        var newBox = boxes.Single(b => b.Id != oldBoxId);
        Assert.Equal((true, 20m), (newBox.IsActive, newBox.ConversionFactorToBase));
        Assert.Equal((newBox.Id, 100m), (rows.Single(r => r.UnitId == s.HCtn).InnerProductUnitId, rows.Single(r => r.UnitId == s.HCtn).ConversionFactorToBase));
        await AssertValidationHoldsAsync(database);
    }

    [Fact]
    public async Task UpdateProductAsync_OmittedUnlockedChain_DeletedTopDown()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
        }

        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc], c[s.Box]);
        }

        Assert.Equal(new[] { s.Pc, s.Box }, (await RowsAsync(database, s.PenId)).Select(r => r.UnitId));
        await AssertValidationHoldsAsync(database);
    }

    [Fact]
    public async Task UpdateProductAsync_FullCartonRePointedWhileItsOldInnerIsDeleted_Saved()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
        }

        // Half Carton dropped; Full Carton now contains 10 Box directly (factor unchanged, inner changed).
        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, s.PenId);
            await s.UpdatePenAsync(db, c[s.Pc], c[s.Box], c[s.FCtn] with { InnerUnitId = s.Box, InnerQuantity = 10m });
        }

        var rows = await RowsAsync(database, s.PenId);
        Assert.Equal(new[] { s.Pc, s.Box, s.FCtn }, rows.Select(r => r.UnitId));
        Assert.Equal((rows[1].Id, 100m), (rows[2].InnerProductUnitId, rows[2].ConversionFactorToBase));
        await AssertValidationHoldsAsync(database);
    }

    [Fact]
    public async Task UpdateProductAsync_BarcodeOfAnotherProduct_RejectedByPreCheckOnBarcodeField()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            await new ProductService(db).CreateProductAsync(s.Input("OTHER", s.Pc, Row(s.Pc, null, 1m, barcode: "8901000000099")));
        }

        await using var edit = database.CreateDbContext();
        var c = await CurrentAsync(edit, s.PenId);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            s.UpdatePenAsync(edit, c[s.Pc], c[s.Box], c[s.HCtn] with { Barcode = "8901000000099" }, c[s.FCtn]));

        Assert.Equal(("Units[2].Barcode", "This barcode is already used by another product."), (ex.Field, ex.Message));
    }

    [Fact]
    public async Task UpdateProductAsync_LockedStructuralChange_RolledBackNothingWritten()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        PenScenario s;
        await using (var db = database.CreateDbContext())
        {
            s = await SeedAsync(db);
            await new PricingService(db).SetPriceAsync((await ActiveRowAsync(db, s.PenId, s.HCtn)).Id, 450m, s.UserId);
        }
        var before = await RowsAsync(database, s.PenId);

        await using (var db = database.CreateDbContext())
        {
            var c = await CurrentAsync(db, s.PenId);
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
                s.UpdatePenAsync(db, c[s.Pc] with { Barcode = "1" }, c[s.Box] with { InnerQuantity = 12m }, c[s.HCtn], c[s.FCtn]));
            Assert.Equal("Units[1].InnerQuantity", ex.Field);
        }

        var after = await RowsAsync(database, s.PenId);
        Assert.Equal(before.Select(r => (r.Id, r.ConversionFactorToBase, r.Barcode)), after.Select(r => (r.Id, r.ConversionFactorToBase, r.Barcode)));
    }
}
