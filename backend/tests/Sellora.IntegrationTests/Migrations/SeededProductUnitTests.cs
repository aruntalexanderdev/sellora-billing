using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Seeded product hierarchies (Phase 2 design §5.7) beyond the structural checks in <c>MigrationTests</c>: sort order,
/// sellable/purchasable flags, the fractional Turmeric chain, and that a seeded product can be re-saved unchanged
/// through <see cref="ProductService"/> (the seed passes the same rules as the editor).
/// </summary>
[Collection(PostgresCollection.Name)]
public class SeededProductUnitTests(PostgresFixture postgres)
{
    private static async Task<Product> LoadAsync(SelloraDbContext db, string sku) =>
        await db.Products.AsNoTracking().Include(p => p.Units).ThenInclude(u => u.Unit).SingleAsync(p => p.Sku == sku);

    [Fact]
    public async Task SeedAsync_BallPen_Piece1Box10HalfCarton50FullCarton100InSortOrder0To3()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext()) await DbSeeder.SeedAsync(db, new PasswordHasher());

        await using var check = database.CreateDbContext();
        var pen = await LoadAsync(check, "STN-PEN");

        var ordered = pen.Units.OrderBy(u => u.SortOrder).ToList();
        Assert.Equal(new[] { "pc", "box", "hctn", "fctn" }, ordered.Select(u => u.Unit.ShortCode));
        Assert.Equal(new short[] { 0, 1, 2, 3 }, ordered.Select(u => u.SortOrder));
        Assert.Equal(new[] { 1m, 10m, 50m, 100m }, ordered.Select(u => u.ConversionFactorToBase));
        Assert.Equal(new[] { 1m, 10m, 5m, 2m }, ordered.Select(u => u.InnerQuantity));
        Assert.All(ordered, u => Assert.True(u.IsActive && u.IsSellable && u.IsPurchasable));
        Assert.Equal(("pc", MeasurementCategory.Count), (ordered[0].Unit.ShortCode, ordered[0].Unit.MeasurementCategory));
        Assert.Equal(12m, (await check.Taxes.AsNoTracking().SingleAsync(t => t.Id == pen.TaxId)).RatePercent);
    }

    [Fact]
    public async Task SeedAsync_Turmeric_Kg1Gram0_001Box0_2_GramNeitherSellableNorPurchasable()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext()) await DbSeeder.SeedAsync(db, new PasswordHasher());

        await using var check = database.CreateDbContext();
        var turmeric = await LoadAsync(check, "SPC-TUR");
        var byCode = turmeric.Units.ToDictionary(u => u.Unit.ShortCode);

        Assert.Equal(3, byCode.Count);
        Assert.Equal((1m, true, "Loose kg"), (byCode["kg"].ConversionFactorToBase, byCode["kg"].IsBaseUnit, byCode["kg"].Label));
        Assert.Equal((0.001m, byCode["kg"].Id), (byCode["g"].ConversionFactorToBase, byCode["g"].InnerProductUnitId!.Value));
        Assert.Equal((false, false, true), (byCode["g"].IsSellable, byCode["g"].IsPurchasable, byCode["g"].IsActive));
        Assert.Equal((0.2m, 200m, byCode["g"].Id, "200 g box"),
            (byCode["box"].ConversionFactorToBase, byCode["box"].InnerQuantity, byCode["box"].InnerProductUnitId!.Value, byCode["box"].Label));
        Assert.True(byCode["box"].IsSellable && byCode["box"].IsPurchasable);
        Assert.Equal(new[] { "kg", "g", "box" }, turmeric.Units.OrderBy(u => u.SortOrder).Select(u => u.Unit.ShortCode));
    }

    [Theory]
    [InlineData("STN-PEN")]
    [InlineData("SPC-TUR")]
    [InlineData("RICE-BP10")]
    public async Task SeededProduct_ResavedUnchangedThroughProductService_SucceedsAndKeepsRows(string sku)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext()) await DbSeeder.SeedAsync(db, new PasswordHasher());

        List<(Guid, Guid, decimal, short)> before;
        await using (var db = database.CreateDbContext())
        {
            var product = await LoadAsync(db, sku);
            before = product.Units.OrderBy(u => u.Id).Select(u => (u.Id, u.UnitId, u.ConversionFactorToBase, u.SortOrder)).ToList();
            var byId = product.Units.ToDictionary(u => u.Id);
            var input = new Sellora.Domain.Inventory.UpsertProductInput(product.Sku, product.Name, product.Category, product.BaseUnitId, product.TaxId, product.ReorderLevel,
                product.Units.Select(u => new Sellora.Domain.Inventory.ProductUnitInputItem(u.Id, u.UnitId,
                    u.InnerProductUnitId is { } i ? byId[i].UnitId : null, u.InnerQuantity, u.Label, u.Barcode,
                    u.IsPurchasable, u.IsSellable, u.IsDefaultPurchaseUnit, u.IsDefaultSalesUnit, u.IsActive)).ToList());

            await new ProductService(db).UpdateProductAsync(product.Id, input);
        }

        await using var check = database.CreateDbContext();
        var after = (await LoadAsync(check, sku)).Units.OrderBy(u => u.Id).Select(u => (u.Id, u.UnitId, u.ConversionFactorToBase, u.SortOrder)).ToList();
        Assert.Equal(before, after);
    }
}
