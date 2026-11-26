using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Applies <c>20261104105655_ExtendUnitMaster</c> to legacy data written into the previous schema (Units."Type" with
/// values Weight/Volume/Count/Other) and checks the backfill, the V1–V3 validation queries and the Down path.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ExtendUnitMasterMigrationTests(PostgresFixture postgres)
{
    private const string PreviousMigration = "20260920114635_AddIdempotencyKeysAndCancelledBy";
    private const string ExtendUnitMaster = "20261104105655_ExtendUnitMaster";

    private static readonly Guid TaxId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    /// <summary>Legacy units (ShortCode, Name, Type).</summary>
    private static readonly (string Code, string Name, string Type)[] LegacyUnits =
    [
        ("kg", "Kilogram", "Weight"),
        ("litre", "Litre", "Volume"),
        ("pc", "Piece", "Count"),
        ("box", "Box", "Count"),
        ("tray", "Tray", "Other"),
        ("bndl", "Bundle", "Other"),
        ("gm", "Gram", "Weight"),
        ("mg", "Milligram", "Weight"),
        ("ML", "Millilitre", "Volume"),
        ("HCTN", "Half Carton", "Count"),
    ];

    private static async Task<TestDatabase> CreateLegacyDatabaseAsync(PostgresFixture postgres, params string[] baseUnitCodes)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.DoesNotContain(ExtendUnitMaster, await db.Database.GetAppliedMigrationsAsync());
        }

        var sql = new System.Text.StringBuilder();
        foreach (var (code, name, type) in LegacyUnits)
        {
            sql.AppendLine($"""INSERT INTO "Units" ("Id", "Name", "ShortCode", "Type", "IsActive") VALUES (gen_random_uuid(), '{name}', '{code}', '{type}', true);""");
        }
        sql.AppendLine($"""INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 5%', 5, true);""");
        var i = 0;
        foreach (var code in baseUnitCodes)
        {
            i++;
            sql.AppendLine($"""
                INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive")
                SELECT gen_random_uuid(), 'LEG-{i}', 'Legacy product {i}', NULL, u."Id", '{TaxId}', 0, true
                FROM "Units" u WHERE u."ShortCode" = '{code}';
                """);
        }
        await ExecuteAsync(database, sql.ToString());
        return database;
    }

    [Fact]
    public async Task ExtendUnitMaster_LegacyUnitsWithContainerUsedAsBase_BackfillsCategoryDecimalsAndFactor()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, "box", "bndl");
        var countBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Units" """);

        await using var db = database.CreateDbContext();
        await db.MigrateToHeadAsync();

        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.ShortCode);
        AssertUnit(units["kg"], MeasurementCategory.Weight, 3, 1m);
        AssertUnit(units["litre"], MeasurementCategory.Volume, 3, 1m);
        AssertUnit(units["gm"], MeasurementCategory.Weight, 0, 0.001m);    // sub-unit, not a base unit -> 0 dp
        AssertUnit(units["ML"], MeasurementCategory.Volume, 0, 0.001m);    // sub-unit, not a base unit -> 0 dp
        AssertUnit(units["mg"], MeasurementCategory.Weight, 0, 0.000001m); // sub-unit, not a base unit -> 0 dp
        AssertUnit(units["pc"], MeasurementCategory.Count, 0, null);
        AssertUnit(units["box"], MeasurementCategory.Count, 0, null);       // container kept as Count: used as base (V4 review)
        AssertUnit(units["tray"], MeasurementCategory.Packaging, 0, null);  // Other, unused -> Packaging
        AssertUnit(units["bndl"], MeasurementCategory.Count, 0, null);      // Other, used as base -> Count
        AssertUnit(units["HCTN"], MeasurementCategory.Packaging, 0, null);  // container code matched case-insensitively

        await UnitMasterValidation.AssertHoldAsync(db);
        Assert.Equal(countBefore, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Units" """)); // V5
        Assert.All(units.Values, u => Assert.True(u.IsActive));
    }

    [Fact]
    public async Task ExtendUnitMaster_ContainerNotUsedAsBase_BecomesPackaging()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, "pc", "kg");

        await using var db = database.CreateDbContext();
        await db.MigrateToHeadAsync();

        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.ShortCode);
        AssertUnit(units["box"], MeasurementCategory.Packaging, 0, null);
        AssertUnit(units["bndl"], MeasurementCategory.Packaging, 0, null);
        AssertUnit(units["pc"], MeasurementCategory.Count, 0, null);
        AssertUnit(units["kg"], MeasurementCategory.Weight, 3, 1m);
        await UnitMasterValidation.AssertHoldAsync(db);
    }

    [Fact]
    public async Task ExtendUnitMaster_EmptyUnitsTable_AppliesAndModelMatches()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await ExecuteAsync(database, """DELETE FROM "Units" """);

        await using var db = database.CreateDbContext();
        await db.MigrateToHeadAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ExtendUnitMaster_Down_RestoresTypeColumnAndMapsPackagingBackToOther()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, "box", "bndl");
        await using (var db = database.CreateDbContext())
        {
            await db.MigrateToHeadAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.DoesNotContain(ExtendUnitMaster, await db.Database.GetAppliedMigrationsAsync());
        }

        var columns = await ColumnsAsync(database, "Units");
        Assert.Contains("Type", columns);
        Assert.DoesNotContain("MeasurementCategory", columns);
        Assert.DoesNotContain("DecimalPlaces", columns);
        Assert.DoesNotContain("StandardFactor", columns);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_constraint WHERE conname LIKE 'CK_Units_%' """));

        var types = await TypesByCodeAsync(database);
        Assert.Equal(LegacyUnits.Length, types.Count);
        Assert.Equal("Other", types["tray"]);   // Packaging -> Other
        Assert.Equal("Other", types["HCTN"]);   // Packaging (was Count) -> Other: lossy, documented in the migration header
        Assert.Equal("Count", types["bndl"]);   // was Other, mapped to Count: stays Count (lossy, documented)
        Assert.Equal("Count", types["box"]);
        Assert.Equal("Count", types["pc"]);
        Assert.Equal("Weight", types["kg"]);
        Assert.Equal("Weight", types["gm"]);
        Assert.Equal("Volume", types["litre"]);
        Assert.Equal("Volume", types["ML"]);
    }

    [Fact]
    public async Task ExtendUnitMaster_DownThenUpAgain_ReappliesCleanly()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, "box", "bndl");
        await using (var db = database.CreateDbContext())
        {
            await db.MigrateToHeadAsync();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        await using (var db = database.CreateDbContext())
        {
            await db.MigrateToHeadAsync();

            var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.ShortCode);
            AssertUnit(units["tray"], MeasurementCategory.Packaging, 0, null);
            AssertUnit(units["kg"], MeasurementCategory.Weight, 3, 1m);
            await UnitMasterValidation.AssertHoldAsync(db);
        }
    }

    [Fact]
    public async Task ExtendUnitMaster_SubUnitUsedAsBase_KeepsThreeDecimalsAndIsListedForReview()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, "gm");

        await using var db = database.CreateDbContext();
        await db.MigrateToHeadAsync();

        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.ShortCode);
        AssertUnit(units["gm"], MeasurementCategory.Weight, 3, 0.001m);      // base unit of a product: keeps 3 dp
        AssertUnit(units["mg"], MeasurementCategory.Weight, 0, 0.000001m);   // not a base unit: 0 dp
        AssertUnit(units["ML"], MeasurementCategory.Volume, 0, 0.001m);
        AssertUnit(units["kg"], MeasurementCategory.Weight, 3, 1m);
        AssertUnit(units["box"], MeasurementCategory.Packaging, 0, null);
        await UnitMasterValidation.AssertHoldAsync(db);

        var review = await StockInvariants.QueryIdsAsync(db, UnitMasterValidation.V4ReviewListSql);
        Assert.Contains(units["gm"].Id, review);
        Assert.DoesNotContain(units["mg"].Id, review);
        Assert.DoesNotContain(units["kg"].Id, review);
    }

    [Fact]
    public async Task ExtendUnitMaster_ContainerKeptAsCountBecauseBase_ListedForReview()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, "box");

        await using var db = database.CreateDbContext();
        await db.MigrateToHeadAsync();

        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.ShortCode);
        var review = await StockInvariants.QueryIdsAsync(db, UnitMasterValidation.V4ReviewListSql);
        Assert.Equal([units["box"].Id], review);
    }

    private static void AssertUnit(Unit unit, MeasurementCategory category, short decimals, decimal? factor)
    {
        Assert.True(category == unit.MeasurementCategory, $"{unit.ShortCode}: category {unit.MeasurementCategory}, expected {category}");
        Assert.True(decimals == unit.DecimalPlaces, $"{unit.ShortCode}: decimals {unit.DecimalPlaces}, expected {decimals}");
        Assert.True(factor == unit.StandardFactor, $"{unit.ShortCode}: factor {unit.StandardFactor?.ToString() ?? "null"}, expected {factor?.ToString() ?? "null"}");
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<HashSet<string>> ColumnsAsync(TestDatabase database, string table)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t", connection);
        command.Parameters.AddWithValue("t", table);
        var columns = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<Dictionary<string, string>> TypesByCodeAsync(TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "ShortCode", "Type" FROM "Units" """, connection);
        var types = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) types[reader.GetString(0)] = reader.GetString(1);
        return types;
    }
}
