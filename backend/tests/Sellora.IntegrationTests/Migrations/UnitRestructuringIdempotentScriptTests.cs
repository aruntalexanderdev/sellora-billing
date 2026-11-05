using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// The release path for the whole unit restructuring so far (D10: idempotent script, never startup auto-migrate):
/// one idempotent script from the last pre-Phase-1 migration through <c>ExtendUnitMaster</c> and
/// <c>IntroduceProductUnits</c>, applied over a plain connection to data in the pre-Phase-1 shape (Units with the old
/// <c>Type</c> column, selling modes with labels), then applied again as a no-op.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UnitRestructuringIdempotentScriptTests(PostgresFixture postgres)
{
    private const string PrePhase1 = "20260920114635_AddIdempotencyKeysAndCancelledBy";
    private const string IntroduceProductUnits = "20261104121850_IntroduceProductUnits";

    private static readonly Guid UKg = G(1), UPc = G(2), UBox = G(3), UGm = G(4), TaxId = G(5);
    private static readonly Guid PRice = G(11), PPen = G(12), PSpice = G(13);
    private static readonly Guid MRiceLoose = G(21), MRiceBag = G(22), MPenPiece = G(23), MPenBox = G(24), MSpiceGram = G(25), MSpiceBox = G(26);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> RowsAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private const string Snapshot = """
        SELECT 'U ' || "Id" || ' ' || "Name" || ' ' || "ShortCode" || ' ' || "MeasurementCategory" || ' ' || "DecimalPlaces" || ' ' || COALESCE("StandardFactor"::text, '-') FROM "Units"
        UNION ALL SELECT 'PU ' || "Id" || ' ' || "UnitId" || ' ' || COALESCE("InnerProductUnitId"::text, '-') || ' ' || "InnerQuantity" || ' ' || "ConversionFactorToBase"
               || ' ' || "IsBaseUnit" || ' ' || "IsActive" || ' ' || "SortOrder" FROM "ProductUnits"
        UNION ALL SELECT 'H ' || "MigrationId" FROM "__EFMigrationsHistory"
        ORDER BY 1
        """;

    [Fact]
    public async Task IdempotentScript_PrePhase1ToIntroduceProductUnits_MigratesLegacyDataAndSecondRunIsNoOp()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        string script;
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PrePhase1);
            script = db.GetService<IMigrator>().GenerateScript(PrePhase1, IntroduceProductUnits, MigrationsSqlGenerationOptions.Idempotent);
        }
        Assert.Contains("20261104105655_ExtendUnitMaster", script);
        Assert.Contains(IntroduceProductUnits, script);

        await ExecuteAsync(database, $"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "Type", "IsActive") VALUES
              ('{UKg}', 'Kilogram', 'kg', 'Weight', true), ('{UPc}', 'Piece', 'pc', 'Count', true),
              ('{UBox}', 'Box', 'box', 'Count', true), ('{UGm}', 'Gram', 'gm', 'Weight', true);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 5%', 5, true);
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{PRice}', 'RICE', 'Rice', NULL, '{UKg}', '{TaxId}', 0, true),
              ('{PPen}', 'PEN', 'Pen', NULL, '{UPc}', '{TaxId}', 0, true),
              ('{PSpice}', 'SPICE', 'Turmeric', NULL, '{UKg}', '{TaxId}', 0, true);
            INSERT INTO "ProductSellingModes" ("Id", "ProductId", "Label", "ConversionFactorToBase", "IsActive") VALUES
              ('{MRiceLoose}', '{PRice}', 'Loose kg', 1, true), ('{MRiceBag}', '{PRice}', '10 kg bag', 10, true),
              ('{MPenPiece}', '{PPen}', 'Piece', 1, true), ('{MPenBox}', '{PPen}', 'Box', 10, true),
              ('{MSpiceGram}', '{PSpice}', 'Gram', 0.001, true), ('{MSpiceBox}', '{PSpice}', '200 g box', 0.2, true);
            """);

        await ExecuteAsync(database, script);

        await using (var db = database.CreateDbContext())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            await ProductUnitValidation.AssertHoldAsync(db);
            await UnitMasterValidation.AssertHoldAsync(db);

            var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Id);
            Assert.Equal((MeasurementCategory.Weight, (short)3, (decimal?)1m), (units[UKg].MeasurementCategory, units[UKg].DecimalPlaces, units[UKg].StandardFactor));
            Assert.Equal((decimal?)0.001m, units[UGm].StandardFactor);

            var rows = await db.ProductUnits.AsNoTracking().ToDictionaryAsync(u => u.Id);
            Assert.Equal(7, rows.Count); // 6 modes + a new base row for Turmeric (no factor-1 mode)
            Assert.Equal((UKg, true), (rows[MRiceLoose].UnitId, rows[MRiceLoose].IsBaseUnit));
            Assert.Equal((UBox, 10m, MPenPiece), (rows[MPenBox].UnitId, rows[MPenBox].ConversionFactorToBase, rows[MPenBox].InnerProductUnitId!.Value));
            Assert.Equal((UGm, 0.001m), (rows[MSpiceGram].UnitId, rows[MSpiceGram].ConversionFactorToBase));
            Assert.Equal(0.2m, rows[MSpiceBox].ConversionFactorToBase);
            Assert.Equal("PKG-", units[rows[MRiceBag].UnitId].ShortCode[..4]);
            var spiceBase = Assert.Single(rows.Values, r => r.ProductId == PSpice && r.IsBaseUnit);
            Assert.Equal((UKg, false, false), (spiceBase.UnitId, spiceBase.IsSellable, spiceBase.IsPurchasable));
        }

        var before = await RowsAsync(database, Snapshot);
        await ExecuteAsync(database, script);
        Assert.Equal(before, await RowsAsync(database, Snapshot));
    }
}
