using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task MigrateAsync_EmptyDatabase_AppliesAllMigrationsAndModelMatchesSnapshot()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using var db = database.CreateDbContext();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(),
            "The entity model differs from the migrations' model snapshot — a migration is missing.");
    }

    [Fact]
    public async Task SeedAsync_RunTwiceOnMigratedDatabase_DoesNotFailOrDuplicateRows()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var hasher = new PasswordHasher();

        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, hasher);
        }
        var afterFirst = await CountRowsAsync(database);

        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, hasher);
        }
        var afterSecond = await CountRowsAsync(database);

        Assert.True(afterFirst.Units > 0 && afterFirst.Products > 0, "The first seed run should insert units and products.");
        Assert.True(afterFirst.ProductUnits >= afterFirst.Products, "Every seeded product needs at least its base ProductUnit row.");
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task SeedAsync_FreshDatabase_SeedsUnitMasterWithCategoryDecimalsAndFactor()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, new PasswordHasher());
        }

        await using var check = database.CreateDbContext();
        var units = await check.Units.AsNoTracking().ToDictionaryAsync(u => u.ShortCode);
        var expected = new (string Code, string Name, MeasurementCategory Category, short Decimals, decimal? Factor)[]
        {
            ("kg", "Kilogram", MeasurementCategory.Weight, 3, 1m),
            ("litre", "Litre", MeasurementCategory.Volume, 3, 1m),
            ("pc", "Piece", MeasurementCategory.Count, 0, null),
            ("box", "Box", MeasurementCategory.Packaging, 0, null),
            ("hctn", "Half Carton", MeasurementCategory.Packaging, 0, null),
            ("fctn", "Full Carton", MeasurementCategory.Packaging, 0, null),
            ("g", "Gram", MeasurementCategory.Weight, 0, 0.001m),
            ("ml", "Millilitre", MeasurementCategory.Volume, 0, 0.001m),
            ("bag", "Bag", MeasurementCategory.Packaging, 0, null),
        };
        Assert.Equal(expected.Length, units.Count);
        foreach (var (code, name, category, decimals, factor) in expected)
        {
            Assert.True(units.TryGetValue(code, out var unit), $"Seeded unit '{code}' is missing.");
            Assert.Equal((name, category, decimals, factor, true), (unit!.Name, unit.MeasurementCategory, unit.DecimalPlaces, unit.StandardFactor, unit.IsActive));
        }

        var snackBase = await check.Products.Where(p => p.Sku == "SNK-MIX").Select(p => p.BaseUnit).SingleAsync();
        Assert.Equal("pc", snackBase.ShortCode);
        Assert.NotEqual(MeasurementCategory.Packaging, snackBase.MeasurementCategory);
        Assert.False(await check.Products.AnyAsync(p => p.BaseUnit.MeasurementCategory == MeasurementCategory.Packaging));
        await UnitMasterValidation.AssertHoldAsync(check);
    }

    [Fact]
    public async Task SeedAsync_FreshDatabase_EveryProductHasOneBaseRowAndConsistentUnitHierarchy()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, new PasswordHasher());
        }

        await using var check = database.CreateDbContext();
        var products = await check.Products.AsNoTracking().Include(p => p.Units).ThenInclude(u => u.Unit).ToListAsync();
        Assert.NotEmpty(products);
        foreach (var product in products)
        {
            var baseRow = Assert.Single(product.Units, u => u.IsBaseUnit);
            Assert.Equal((product.BaseUnitId, 1m, 1m, true), (baseRow.UnitId, baseRow.InnerQuantity, baseRow.ConversionFactorToBase, baseRow.IsActive));
            Assert.All(product.Units.Where(u => !u.IsBaseUnit), u =>
            {
                var inner = Assert.Single(product.Units, i => i.Id == u.InnerProductUnitId);
                Assert.Equal(u.InnerQuantity * inner.ConversionFactorToBase, u.ConversionFactorToBase);
            });
            Assert.All(product.Units.Where(u => u.IsActive), u => Assert.True(u.Unit.IsActive, $"{product.Sku}: active row on inactive unit {u.Unit.ShortCode}."));
            Assert.Equal(product.Units.Count(u => u.IsActive), product.Units.Where(u => u.IsActive).Select(u => u.UnitId).Distinct().Count());
        }
        await ProductUnitValidation.AssertHoldAsync(check);

        // Canonical Pen: Piece 1, Box 10, Half Carton 50, Full Carton 100, chained Box -> Piece, HCtn -> Box, FCtn -> HCtn.
        var pen = Assert.Single(products, p => p.Sku == "STN-PEN");
        var penByCode = pen.Units.ToDictionary(u => u.Unit.ShortCode);
        Assert.Equal(4, penByCode.Count);
        Assert.Equal((1m, (Guid?)null), (penByCode["pc"].ConversionFactorToBase, penByCode["pc"].InnerProductUnitId));
        Assert.Equal((10m, (Guid?)penByCode["pc"].Id), (penByCode["box"].ConversionFactorToBase, penByCode["box"].InnerProductUnitId));
        Assert.Equal((50m, (Guid?)penByCode["box"].Id), (penByCode["hctn"].ConversionFactorToBase, penByCode["hctn"].InnerProductUnitId));
        Assert.Equal((100m, (Guid?)penByCode["hctn"].Id), (penByCode["fctn"].ConversionFactorToBase, penByCode["fctn"].InnerProductUnitId));
    }

    [Fact]
    public async Task SeedAsync_UnitsAlreadyExistWithDifferentCaseOrName_DoesNotDuplicateOrOverwrite()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            // Code clash differing only in case, and a name clash under a different code.
            db.Units.Add(new Unit { Id = Guid.NewGuid(), Name = "Kilo", ShortCode = "KG", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 2, StandardFactor = 1m });
            db.Units.Add(new Unit { Id = Guid.NewGuid(), Name = "piece", ShortCode = "pcs", MeasurementCategory = MeasurementCategory.Count, DecimalPlaces = 0 });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, new PasswordHasher());
        }

        await using var check = database.CreateDbContext();
        var units = await check.Units.AsNoTracking().ToListAsync();
        Assert.Equal(9, units.Count);
        var kg = Assert.Single(units, u => u.ShortCode.Equals("kg", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("Kilo", "KG", (short)2), (kg.Name, kg.ShortCode, kg.DecimalPlaces));
        Assert.Single(units, u => u.Name.Equals("piece", StringComparison.OrdinalIgnoreCase));
        var snackBase = await check.Products.Where(p => p.Sku == "SNK-MIX").Select(p => p.BaseUnit.ShortCode).SingleAsync();
        Assert.Equal("pcs", snackBase);
    }

    [Fact]
    public async Task SeedAsync_ExistingBaseUnitInactiveOrPackaging_SkipsProductsThatWouldUseIt()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            // 'pc' exists but is inactive; 'litre' exists under the seed code but as a Packaging unit.
            db.Units.Add(new Unit { Id = Guid.NewGuid(), Name = "Piece", ShortCode = "pc", MeasurementCategory = MeasurementCategory.Count, IsActive = false });
            db.Units.Add(new Unit { Id = Guid.NewGuid(), Name = "Litre can", ShortCode = "litre", MeasurementCategory = MeasurementCategory.Packaging });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            await DbSeeder.SeedAsync(db, new PasswordHasher());
        }

        await using var check = database.CreateDbContext();
        var skus = await check.Products.Select(p => p.Sku).ToListAsync();
        Assert.DoesNotContain("SNK-MIX", skus);   // base 'pc' is inactive
        Assert.DoesNotContain("STN-PEN", skus);   // base 'pc' is inactive
        Assert.DoesNotContain("OIL-SUN", skus);   // base 'litre' is Packaging
        Assert.Contains("RICE-BP10", skus);       // kg was seeded normally
        Assert.False(await check.Products.AnyAsync(p => !p.BaseUnit.IsActive || p.BaseUnit.MeasurementCategory == MeasurementCategory.Packaging));
        var pc = await check.Units.AsNoTracking().SingleAsync(u => u.ShortCode == "pc");
        Assert.False(pc.IsActive);                // existing rows are never updated by the seeder
        Assert.Equal(9, await check.Units.CountAsync());
        await UnitMasterValidation.AssertHoldAsync(check);
    }

    [Fact]
    public async Task MigrateAsync_EmptyDatabase_CreatesCaseInsensitiveUniqueIndexesOnUnits()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = """
            SELECT i.relname, x.indisunique, pg_get_indexdef(x.indexrelid)
            FROM pg_index x
            JOIN pg_class i ON i.oid = x.indexrelid
            JOIN pg_class t ON t.oid = x.indrelid
            WHERE t.relname = 'Units' AND i.relname IN ('UX_Units_Name_Lower', 'UX_Units_ShortCode_Lower')
            """;
        var indexes = new Dictionary<string, (bool Unique, string Definition)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) indexes[reader.GetString(0)] = (reader.GetBoolean(1), reader.GetString(2));
        }

        foreach (var (name, column) in new[] { ("UX_Units_Name_Lower", "Name"), ("UX_Units_ShortCode_Lower", "ShortCode") })
        {
            Assert.True(indexes.TryGetValue(name, out var index), $"Index {name} is missing on \"Units\".");
            Assert.True(index.Unique, $"Index {name} is not unique: {index.Definition}");
            Assert.Contains($"lower((\"{column}\")", index.Definition);
        }
    }

    private sealed record RowCounts(
        int Units, int Products, int ProductUnits, int Taxes, int Suppliers, int Customers, int Users, int Permissions,
        int Roles, int RolePermissions, int Currencies, int ExchangeRates, int BusinessSettings, int FinancialAccounts,
        int ExpenseCategories);

    private static async Task<RowCounts> CountRowsAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return new RowCounts(
            await db.Units.CountAsync(), await db.Products.CountAsync(), await db.ProductUnits.CountAsync(),
            await db.Taxes.CountAsync(), await db.Suppliers.CountAsync(), await db.Customers.CountAsync(),
            await db.Users.CountAsync(), await db.Permissions.CountAsync(),
            await db.Roles.CountAsync(), await db.RolePermissions.CountAsync(), await db.Currencies.CountAsync(),
            await db.ExchangeRates.CountAsync(), await db.BusinessSettings.CountAsync(), await db.FinancialAccounts.CountAsync(),
            await db.ExpenseCategories.CountAsync());
    }
}
