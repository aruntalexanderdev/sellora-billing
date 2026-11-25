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
    private const string AddStockAdjustmentUnitSnapshots = "20261106030621_AddStockAdjustmentUnitSnapshots";

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
            // The script targets IntroduceProductUnits: exactly the migrations after it remain pending (none skipped,
            // none partially applied), in order.
            Assert.Equal(db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, IntroduceProductUnits) > 0),
                await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            // Multi-company: the head model's CompanyId filters read columns added by later migrations, so bring the schema to head
            // before the EF-model assertions below (the data assertions themselves are unchanged).
            await db.MigrateToHeadAsync();
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

    /// <summary>
    /// The release path through Phase 3: one idempotent script PrePhase1 → AddStockAdjustmentUnitSnapshots over legacy
    /// adjustments. The Phase 3 backfill points each adjustment at its product's base row, which for Turmeric (no factor-1
    /// selling mode) only exists because IntroduceProductUnits inserted it earlier in the same script.
    /// </summary>
    [Fact]
    public async Task IdempotentScript_PrePhase1ToStockAdjustmentSnapshots_LegacyAdjustmentsPointAtBaseRowsAndSecondRunIsNoOp()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        string script;
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PrePhase1);
            script = db.GetService<IMigrator>().GenerateScript(PrePhase1, AddStockAdjustmentUnitSnapshots, MigrationsSqlGenerationOptions.Idempotent);
        }
        Assert.Contains(IntroduceProductUnits, script);
        Assert.Contains(AddStockAdjustmentUnitSnapshots, script);
        Guid roleId = G(31), userId = G(32), adjRice = G(41), adjSpice = G(42), batchRice = G(51), batchSpice = G(52);

        await ExecuteAsync(database, $"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "Type", "IsActive") VALUES
              ('{UKg}', 'Kilogram', 'kg', 'Weight', true), ('{UGm}', 'Gram', 'gm', 'Weight', true);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 5%', 5, true);
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{PRice}', 'RICE', 'Rice', NULL, '{UKg}', '{TaxId}', 0, true),
              ('{PSpice}', 'SPICE', 'Turmeric', NULL, '{UKg}', '{TaxId}', 0, true);
            INSERT INTO "ProductSellingModes" ("Id", "ProductId", "Label", "ConversionFactorToBase", "IsActive") VALUES
              ('{MRiceLoose}', '{PRice}', 'Loose kg', 1, true), ('{MRiceBag}', '{PRice}', '10 kg bag', 10, true),
              ('{MSpiceGram}', '{PSpice}', 'Gram', 0.001, true);
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{roleId}', 'Administrator', NULL, true, true);
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{userId}', now(), 'admin@selloramart.in', 'Ravi', 'Krishnan', 'x', '{roleId}', 'Active', 'admin');
            INSERT INTO "StockAdjustments" ("Id", "AdjustmentNo", "AfterQty", "BeforeQty", "CreatedAt", "Direction", "ProductId", "QuantityBaseUnits", "Reason", "Type", "UnitCost", "UserId") VALUES
              ('{adjRice}', 'ADJ-0001', 25.5, 0, now(), 'Increase', '{PRice}', 25.5, NULL, 'OpeningStock', 58, '{userId}'),
              ('{adjSpice}', 'ADJ-0002', 2.25, 0, now(), 'Increase', '{PSpice}', 2.25, NULL, 'OpeningStock', 400, '{userId}');
            INSERT INTO "PurchaseBatches" ("Id", "CreatedAt", "ProductId", "PurchaseOrderLineId", "PurchasedBaseQty", "ReceivedDate", "RemainingBaseQty", "StockAdjustmentId", "SupplierBatchNumber", "UnitCost") VALUES
              ('{batchRice}', now(), '{PRice}', NULL, 25.5, DATE '2026-09-01', 25.5, '{adjRice}', NULL, 58),
              ('{batchSpice}', now(), '{PSpice}', NULL, 2.25, DATE '2026-09-01', 2.25, '{adjSpice}', NULL, 400);
            INSERT INTO "StockMovements" ("Id", "Notes", "OccurredAt", "ProductId", "PurchaseBatchId", "QuantityBaseUnits", "ReferenceId", "ReferenceNo", "ReferenceType", "Type", "UserId") VALUES
              (gen_random_uuid(), 'OpeningStock', now(), '{PRice}', '{batchRice}', 25.5, '{adjRice}', 'ADJ-0001', 'StockAdjustment', 'Adjustment', '{userId}'),
              (gen_random_uuid(), 'OpeningStock', now(), '{PSpice}', '{batchSpice}', 2.25, '{adjSpice}', 'ADJ-0002', 'StockAdjustment', 'Adjustment', '{userId}');
            INSERT INTO "StockItems" ("ProductId", "AverageCost", "QuantityOnHand") VALUES ('{PRice}', 58, 25.5), ('{PSpice}', 400, 2.25);
            """);

        await ExecuteAsync(database, script);

        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, AddStockAdjustmentUnitSnapshots) > 0),
                await db.Database.GetPendingMigrationsAsync());
            // Multi-company: the head model's CompanyId filters read columns added by later migrations, so bring the schema to head
            // before the EF-model assertions below (the data assertions themselves are unchanged).
            await db.MigrateToHeadAsync();
            await ProductUnitValidation.AssertHoldAsync(db);
            await StockInvariants.AssertHoldAsync(db);
            var adjustments = await db.StockAdjustments.AsNoTracking().Include(a => a.ProductUnit).ToDictionaryAsync(a => a.Id);
            var spiceBase = await db.ProductUnits.AsNoTracking().SingleAsync(u => u.ProductId == PSpice && u.IsBaseUnit);
            Assert.Equal((MRiceLoose, UKg, "kg", 1m, 25.5m, (decimal?)58m), (adjustments[adjRice].ProductUnitId, adjustments[adjRice].UnitId,
                adjustments[adjRice].UnitCode, adjustments[adjRice].ConversionFactorToBase, adjustments[adjRice].EnteredQuantity, adjustments[adjRice].EnteredUnitCost));
            Assert.Equal((spiceBase.Id, UKg, "kg", 1m, 2.25m), (adjustments[adjSpice].ProductUnitId, adjustments[adjSpice].UnitId,
                adjustments[adjSpice].UnitCode, adjustments[adjSpice].ConversionFactorToBase, adjustments[adjSpice].EnteredQuantity));
            Assert.NotEqual(MSpiceGram, adjustments[adjSpice].ProductUnitId); // never the factor-0.001 row
        }

        const string adjustmentSnapshot = """
            SELECT 'A ' || "Id" || ' ' || "ProductUnitId" || ' ' || "UnitCode" || ' ' || "EnteredQuantity" FROM "StockAdjustments"
            UNION ALL SELECT 'H ' || "MigrationId" FROM "__EFMigrationsHistory"
            ORDER BY 1
            """;
        var before = await RowsAsync(database, Snapshot);
        var adjustmentsBefore = await RowsAsync(database, adjustmentSnapshot);
        await ExecuteAsync(database, script);
        Assert.Equal(before, await RowsAsync(database, Snapshot));
        Assert.Equal(adjustmentsBefore, await RowsAsync(database, adjustmentSnapshot));
    }
}
