using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Applies <c>AddStockAdjustmentUnitSnapshots</c> (Phase 3 design §2) to legacy adjustments written into the
/// <c>IntroduceProductUnits</c> schema — increases with their batch and a decrease drawn from two batches with one
/// movement per batch — and checks the backfill (base row, factor 1, entered = base, code = ShortCode, entered cost =
/// cost), the V1–V6 validation queries from the migration header, both in-migration guards, Down (columns gone, data
/// intact), Down → Up again, and the production path: the idempotent script over a plain connection, run twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddStockAdjustmentUnitSnapshotsMigrationTests(PostgresFixture postgres)
{
    private const string IntroduceProductUnits = "20261104121850_IntroduceProductUnits";
    private const string AddStockAdjustmentUnitSnapshots = "20261106030621_AddStockAdjustmentUnitSnapshots";

    private static Guid G(int prefix, int n) => Guid.Parse($"{prefix:x8}-0000-0000-0000-{n:x12}");

    private static readonly Guid UKg = G(1, 1), UPc = G(1, 2), UBox = G(1, 3);
    private static readonly Guid TaxId = G(2, 1), RoleId = G(2, 2), UserId = G(2, 3);
    private static readonly Guid PRice = G(3, 1), PPen = G(3, 2), PNoBase = G(3, 3);
    private static readonly Guid RiceKg = G(4, 1), PenPc = G(4, 2), PenBox = G(4, 3);
    private static readonly Guid A1 = G(5, 1), A2 = G(5, 2), A3 = G(5, 3), A4 = G(5, 4);
    private static readonly Guid B1 = G(6, 1), B2 = G(6, 2), B3 = G(6, 3);

    /// <summary>(Id, No, Product, Type, Direction, Qty, UnitCost, Before, After) as posted before this migration.</summary>
    private static readonly (Guid Id, string No, Guid ProductId, string Type, string Direction, decimal Qty, decimal? Cost, decimal Before, decimal After)[] Legacy =
    [
        (A1, "ADJ-0001", PRice, "OpeningStock", "Increase", 30m, 58m, 0m, 30m),
        (A2, "ADJ-0002", PRice, "Correction", "Increase", 20m, 62m, 30m, 50m),
        (A3, "ADJ-0003", PRice, "Damage", "Decrease", 35.5m, null, 50m, 14.5m),
        (A4, "ADJ-0004", PPen, "OpeningStock", "Increase", 100m, 2.5m, 0m, 100m),
    ];

    private static readonly Dictionary<Guid, (Guid BaseRow, Guid Unit, string Code)> BaseOf = new()
    {
        [PRice] = (RiceKg, UKg, "kg"),
        [PPen] = (PenPc, UPc, "pc"),
    };

    private static string N(decimal? d) => d is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";

    private static async Task<TestDatabase> CreateLegacyDatabaseAsync(PostgresFixture postgres, string? extraSql = null)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(IntroduceProductUnits);
            Assert.DoesNotContain(AddStockAdjustmentUnitSnapshots, await db.Database.GetAppliedMigrationsAsync());
        }

        var sql = new System.Text.StringBuilder($"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES
              ('{UKg}', 'Kilogram', 'kg', 'Weight', 3, 1, true),
              ('{UPc}', 'Piece', 'pc', 'Count', 0, NULL, true),
              ('{UBox}', 'Box', 'box', 'Packaging', 0, NULL, true);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 0%', 0, true);
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{RoleId}', 'Administrator', NULL, true, true);
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{UserId}', now(), 'admin@selloramart.in', 'Ravi', 'Krishnan', 'x', '{RoleId}', 'Active', 'admin');
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{PRice}', 'RICE', 'Ponni Rice', NULL, '{UKg}', '{TaxId}', 0, true),
              ('{PPen}', 'STN-PEN', 'Ball Pen', NULL, '{UPc}', '{TaxId}', 0, true);
            INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit",
              "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive") VALUES
              ('{RiceKg}', '{PRice}', '{UKg}', NULL, 1, 1, true, 'Loose kg', NULL, true, true, false, false, 0, true),
              ('{PenPc}', '{PPen}', '{UPc}', NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true),
              ('{PenBox}', '{PPen}', '{UBox}', '{PenPc}', 10, 10, false, NULL, NULL, true, true, false, false, 1, true);

            """);
        var at = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        foreach (var (a, i) in Legacy.Select((a, i) => (a, i)))
        {
            sql.AppendLine($"""
                INSERT INTO "StockAdjustments" ("Id", "AdjustmentNo", "AfterQty", "BeforeQty", "CreatedAt", "Direction", "ProductId", "QuantityBaseUnits", "Reason", "Type", "UnitCost", "UserId")
                VALUES ('{a.Id}', '{a.No}', {N(a.After)}, {N(a.Before)}, '{at.AddHours(i):O}', '{a.Direction}', '{a.ProductId}', {N(a.Qty)}, 'Legacy', '{a.Type}', {N(a.Cost)}, '{UserId}');
                """);
        }
        sql.AppendLine($"""
            -- Batches created by the increases (A3 drew 30 from B1 and 5.5 from B2).
            INSERT INTO "PurchaseBatches" ("Id", "CreatedAt", "ProductId", "PurchaseOrderLineId", "PurchasedBaseQty", "ReceivedDate", "RemainingBaseQty", "StockAdjustmentId", "SupplierBatchNumber", "UnitCost") VALUES
              ('{B1}', '{at:O}', '{PRice}', NULL, 30, DATE '2026-09-01', 0, '{A1}', NULL, 58),
              ('{B2}', '{at.AddHours(1):O}', '{PRice}', NULL, 20, DATE '2026-09-01', 14.5, '{A2}', NULL, 62),
              ('{B3}', '{at.AddHours(3):O}', '{PPen}', NULL, 100, DATE '2026-09-01', 100, '{A4}', NULL, 2.5);
            INSERT INTO "StockMovements" ("Id", "Notes", "OccurredAt", "ProductId", "PurchaseBatchId", "QuantityBaseUnits", "ReferenceId", "ReferenceNo", "ReferenceType", "Type", "UserId") VALUES
              (gen_random_uuid(), 'OpeningStock', '{at:O}', '{PRice}', '{B1}', 30, '{A1}', 'ADJ-0001', 'StockAdjustment', 'Adjustment', '{UserId}'),
              (gen_random_uuid(), 'Correction', '{at.AddHours(1):O}', '{PRice}', '{B2}', 20, '{A2}', 'ADJ-0002', 'StockAdjustment', 'Adjustment', '{UserId}'),
              (gen_random_uuid(), 'Damage', '{at.AddHours(2):O}', '{PRice}', '{B1}', -30, '{A3}', 'ADJ-0003', 'StockAdjustment', 'Adjustment', '{UserId}'),
              (gen_random_uuid(), 'Damage', '{at.AddHours(2):O}', '{PRice}', '{B2}', -5.5, '{A3}', 'ADJ-0003', 'StockAdjustment', 'Adjustment', '{UserId}'),
              (gen_random_uuid(), 'OpeningStock', '{at.AddHours(3):O}', '{PPen}', '{B3}', 100, '{A4}', 'ADJ-0004', 'StockAdjustment', 'Adjustment', '{UserId}');
            INSERT INTO "StockItems" ("ProductId", "AverageCost", "QuantityOnHand") VALUES ('{PRice}', 59.6, 14.5), ('{PPen}', 2.5, 100);
            """);
        if (extraSql is not null) sql.AppendLine(extraSql);
        await ExecuteAsync(database, sql.ToString());
        return database;
    }

    /// <summary>A product with history but no ProductUnits row at all (cannot happen after Phase 2 validation; the guard
    /// is for hand-edited data).</summary>
    private static readonly string ProductWithoutBaseRow = $"""
        INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive")
        VALUES ('{PNoBase}', 'NOBASE', 'No base row', NULL, '{UKg}', '{TaxId}', 0, true);
        INSERT INTO "StockAdjustments" ("Id", "AdjustmentNo", "AfterQty", "BeforeQty", "CreatedAt", "Direction", "ProductId", "QuantityBaseUnits", "Reason", "Type", "UnitCost", "UserId")
        VALUES (gen_random_uuid(), 'ADJ-0005', 0, 0, now(), 'Decrease', '{PNoBase}', 1, NULL, 'Loss', NULL, '{UserId}');
        """;

    private const string V1Sql = """SELECT "Type" || '|' || "Direction" || '|' || COUNT(*) FROM "StockAdjustments" GROUP BY "Type", "Direction" ORDER BY 1""";

    /// <summary>V2–V5 from the migration header (V6 = <see cref="StockInvariants"/>), each must return no rows.</summary>
    private static readonly Dictionary<string, string> VBlock = new()
    {
        ["V2 arithmetic"] = """SELECT "Id" FROM "StockAdjustments" WHERE "EnteredQuantity" * "ConversionFactorToBase" <> "QuantityBaseUnits" """,
        ["V3 unit snapshot"] = """
            SELECT a."Id" FROM "StockAdjustments" a JOIN "ProductUnits" pu ON pu."Id" = a."ProductUnitId"
            WHERE pu."ProductId" <> a."ProductId" OR pu."UnitId" <> a."UnitId"
            """,
        ["V4 legacy base entries"] = """
            SELECT a."Id" FROM "StockAdjustments" a JOIN "ProductUnits" pu ON pu."Id" = a."ProductUnitId"
            WHERE NOT pu."IsBaseUnit" OR a."ConversionFactorToBase" <> 1
            """,
        ["V5 batches"] = """
            SELECT a."Id" FROM "StockAdjustments" a JOIN "PurchaseBatches" b ON b."StockAdjustmentId" = a."Id"
            WHERE b."PurchasedBaseQty" <> a."QuantityBaseUnits" OR b."UnitCost" <> a."UnitCost"
            """,
    };

    private static async Task AssertVBlockEmptyAsync(TestDatabase database, IReadOnlyList<string> v1Before, bool includeV4 = true)
    {
        Assert.Equal(v1Before, await QueryAsync(database, V1Sql, r => r.GetString(0)));
        foreach (var (name, sql) in VBlock)
        {
            if (!includeV4 && name.StartsWith("V4")) continue;
            Assert.True((await QueryAsync(database, sql, r => r.GetGuid(0))).Count == 0, $"{name} returned rows.");
        }
        await using var db = database.CreateDbContext();
        await StockInvariants.AssertHoldAsync(db);
    }

    private static async Task AssertBackfilledAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var rows = await db.StockAdjustments.AsNoTracking().Include(a => a.ProductUnit).Include(a => a.Unit).ToDictionaryAsync(a => a.Id);
        Assert.Equal(Legacy.Length, rows.Count);
        foreach (var legacy in Legacy)
        {
            var row = rows[legacy.Id];
            var (baseRow, unit, code) = BaseOf[legacy.ProductId];
            Assert.Equal((baseRow, unit, code, 1m, legacy.Qty, legacy.Qty, legacy.Cost, legacy.Cost),
                (row.ProductUnitId, row.UnitId, row.UnitCode, row.ConversionFactorToBase, row.EnteredQuantity, row.QuantityBaseUnits, row.EnteredUnitCost, row.UnitCost));
            Assert.Equal((legacy.Before, legacy.After, legacy.No), (row.BeforeQty, row.AfterQty, row.AdjustmentNo));
            Assert.True(row.ProductUnit.IsBaseUnit);
            Assert.Equal(code, row.Unit.ShortCode);
        }
    }

    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_LegacyAdjustments_BackfilledAsBaseUnitEntriesAndVBlockEmpty()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = await QueryAsync(database, V1Sql, r => r.GetString(0));
        Assert.Equal(new[] { "Correction|Increase|1", "Damage|Decrease|1", "OpeningStock|Increase|2" }, v1);
        await using (var check = database.CreateDbContext()) await StockInvariants.AssertHoldAsync(check); // the fixture itself reconciles

        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        await AssertBackfilledAsync(database);
        await AssertVBlockEmptyAsync(database, v1);

        // Enforced: the five snapshot columns are NOT NULL, EnteredUnitCost stays nullable.
        var nullability = (await QueryAsync(database, """
            SELECT column_name || '=' || is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'StockAdjustments'
              AND column_name IN ('EnteredQuantity','ProductUnitId','UnitId','UnitCode','ConversionFactorToBase','EnteredUnitCost')
            """, r => r.GetString(0))).Order();
        Assert.Equal(new[] { "ConversionFactorToBase=NO", "EnteredQuantity=NO", "EnteredUnitCost=YES", "ProductUnitId=NO", "UnitCode=NO", "UnitId=NO" }, nullability);
        var constraints = await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname = 'StockAdjustments' AND c.contype IN ('c','f')
            """, r => r.GetString(0));
        Assert.Superset(new HashSet<string>
        {
            "CK_StockAdjustments_EnteredQuantity_Positive", "CK_StockAdjustments_Factor_Positive", "CK_StockAdjustments_BaseQty_Arithmetic",
            "CK_StockAdjustments_Cost_Pairing", "FK_StockAdjustments_ProductUnits_ProductId_ProductUnitId", "FK_StockAdjustments_Units_UnitId",
        }, constraints.ToHashSet());
    }

    /// <summary>After the release the table takes pack-unit adjustments next to the backfilled ones; the ledger still reconciles.</summary>
    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_AfterMigration_NewPackAdjustmentPostsAndInvariantsHold()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await using (var db = database.CreateDbContext()) await db.Database.MigrateAsync();

        await using (var db = database.CreateDbContext())
        {
            await new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(PPen, PenBox, AdjustmentType.Damage, AdjustmentDirection.Decrease, 3m, null, "Crushed"), UserId);
        }

        await using var check = database.CreateDbContext();
        var posted = await check.StockAdjustments.AsNoTracking().SingleAsync(a => a.AdjustmentNo == "ADJ-0005");
        Assert.Equal((PenBox, UBox, "box", 10m, 3m, 30m, 100m, 70m), (posted.ProductUnitId, posted.UnitId, posted.UnitCode, posted.ConversionFactorToBase,
            posted.EnteredQuantity, posted.QuantityBaseUnits, posted.BeforeQty, posted.AfterQty));
        await StockInvariants.AssertHoldAsync(check);
        Assert.Equal(70m, (await check.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == PPen)).QuantityOnHand);
    }

    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_AdjustedProductWithoutBaseRow_RaisesAndRollsBackAtomically()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, ProductWithoutBaseRow);

        await using (var db = database.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("adjustment product without base ProductUnit", ex.MessageText);
        }

        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(AddStockAdjustmentUnitSnapshots, await check.Database.GetAppliedMigrationsAsync());
        Assert.DoesNotContain("EnteredQuantity", await ColumnsAsync(database, "StockAdjustments"));
        Assert.Equal(Legacy.Length + 1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockAdjustments" """));
    }

    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_NonPositiveLegacyQuantity_RaisesAndRollsBack()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, $"""UPDATE "StockAdjustments" SET "QuantityBaseUnits" = 0 WHERE "Id" = '{A2}';""");

        await using (var db = database.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("non-positive adjustment quantity", ex.MessageText);
        }

        Assert.DoesNotContain("ProductUnitId", await ColumnsAsync(database, "StockAdjustments"));
    }

    /// <summary>Down after a pack-unit adjustment was posted: the snapshot columns go (the documented loss), every base-unit
    /// figure, batch and movement stays, and the table's schema equals a database that never saw the migration.</summary>
    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_Down_DropsSnapshotColumnsKeepsStockDataAndRestoresSchema()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await using (var db = database.CreateDbContext()) await db.Database.MigrateAsync();
        await using (var db = database.CreateDbContext())
        {
            await new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(PPen, PenBox, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 2m, 30m, null), UserId);
        }
        const string stockData = """
            SELECT 'A ' || "AdjustmentNo" || ' ' || "QuantityBaseUnits" || ' ' || COALESCE("UnitCost"::text, '-') || ' ' || "BeforeQty" || ' ' || "AfterQty" FROM "StockAdjustments"
            UNION ALL SELECT 'B ' || "Id" || ' ' || "PurchasedBaseQty" || ' ' || "RemainingBaseQty" || ' ' || "UnitCost" || ' ' || COALESCE("StockAdjustmentId"::text, '-') FROM "PurchaseBatches"
            UNION ALL SELECT 'M ' || "ReferenceNo" || ' ' || COALESCE("PurchaseBatchId"::text, '-') || ' ' || "QuantityBaseUnits" FROM "StockMovements"
            UNION ALL SELECT 'S ' || "ProductId" || ' ' || "QuantityOnHand" || ' ' || "AverageCost" FROM "StockItems"
            ORDER BY 1
            """;
        var before = await QueryAsync(database, stockData, r => r.GetString(0));

        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(IntroduceProductUnits);
            Assert.DoesNotContain(AddStockAdjustmentUnitSnapshots, await db.Database.GetAppliedMigrationsAsync());
        }

        Assert.Equal(before, await QueryAsync(database, stockData, r => r.GetString(0)));
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = reference.CreateDbContext()) await db.GetService<IMigrator>().MigrateAsync(IntroduceProductUnits);
        var expected = await SchemaSignatureAsync(reference);
        var actual = await SchemaSignatureAsync(database);
        Assert.Contains(expected, x => x.StartsWith("idx StockAdjustments.IX_StockAdjustments_ProductId "));
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
    }

    /// <summary>Down then Up again re-runs the backfill: every row (including the one posted in Boxes, whose entered unit was
    /// lost on Down) comes back as a base-unit entry, so the arithmetic CHECK and V2/V3/V5 hold.</summary>
    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_DownThenUpAgain_ReBackfillsAsBaseUnitEntries()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await using (var db = database.CreateDbContext()) await db.Database.MigrateAsync();
        await using (var db = database.CreateDbContext())
        {
            await new StockAdjustmentService(db).CreateAdjustmentAsync(
                new CreateAdjustmentInput(PPen, PenBox, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 2m, 30m, null), UserId);
        }
        var v1 = await QueryAsync(database, V1Sql, r => r.GetString(0));

        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(IntroduceProductUnits);
            await db.Database.MigrateAsync();
        }

        await AssertVBlockEmptyAsync(database, v1);
        await using var check = database.CreateDbContext();
        var reposted = await check.StockAdjustments.AsNoTracking().SingleAsync(a => a.AdjustmentNo == "ADJ-0005");
        Assert.Equal((PenPc, "pc", 1m, 20m, 20m, (decimal?)3m, (decimal?)3m), (reposted.ProductUnitId, reposted.UnitCode, reposted.ConversionFactorToBase,
            reposted.EnteredQuantity, reposted.QuantityBaseUnits, reposted.EnteredUnitCost, reposted.UnitCost));
    }

    /// <summary>
    /// Production path (D10): the idempotent script IntroduceProductUnits → AddStockAdjustmentUnitSnapshots over a plain
    /// connection. EF wraps each statement in <c>DO $EF$ … END $EF$</c>; this proves the migration's nested DO blocks, temp
    /// table and RAISE guards work inside that wrapper, and that a second run is a no-op.
    /// </summary>
    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_IdempotentScript_BackfillsLegacyDataAndSecondRunIsNoOp()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = await QueryAsync(database, V1Sql, r => r.GetString(0));
        var script = await ScriptAsync(database);
        Assert.Contains("DO $EF$", script);
        Assert.Contains(AddStockAdjustmentUnitSnapshots, script);

        await ExecuteAsync(database, script);

        await using (var db = database.CreateDbContext())
        {
            Assert.Contains(AddStockAdjustmentUnitSnapshots, await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        await AssertBackfilledAsync(database);
        await AssertVBlockEmptyAsync(database, v1);

        const string snapshot = """
            SELECT 'A ' || "Id" || ' ' || "ProductUnitId" || ' ' || "UnitId" || ' ' || "UnitCode" || ' ' || "ConversionFactorToBase" || ' ' || "EnteredQuantity"
                   || ' ' || COALESCE("EnteredUnitCost"::text, '-') FROM "StockAdjustments"
            UNION ALL SELECT 'H ' || "MigrationId" FROM "__EFMigrationsHistory"
            ORDER BY 1
            """;
        var before = await QueryAsync(database, snapshot, r => r.GetString(0));
        var schema = await SchemaSignatureAsync(database);

        await ExecuteAsync(database, script); // second run: every block is skipped

        Assert.Equal(before, await QueryAsync(database, snapshot, r => r.GetString(0)));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task AddStockAdjustmentUnitSnapshots_IdempotentScriptWithProductWithoutBaseRow_RaisesThroughWrapperAndLeavesSchemaUnchanged()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, ProductWithoutBaseRow);
        var script = await ScriptAsync(database);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, script));

        Assert.Contains("adjustment product without base ProductUnit", ex.MessageText);
        Assert.DoesNotContain("EnteredQuantity", await ColumnsAsync(database, "StockAdjustments"));
        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(AddStockAdjustmentUnitSnapshots, await check.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<string> ScriptAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return db.GetService<IMigrator>().GenerateScript(IntroduceProductUnits, AddStockAdjustmentUnitSnapshots, MigrationsSqlGenerationOptions.Idempotent);
    }

    /// <summary>Columns, constraints and indexes of the tables this migration touches, one string per item.</summary>
    private static async Task<HashSet<string>> SchemaSignatureAsync(TestDatabase database)
    {
        const string tables = "'StockAdjustments','ProductUnits','Units'";
        var items = new HashSet<string>();
        foreach (var sql in new[]
        {
            $"""
            SELECT 'col ' || table_name || '.' || column_name || ' ' || data_type || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '')
                   || COALESCE(' len ' || character_maximum_length, '') || ' null=' || is_nullable || ' default=' || COALESCE(column_default, '-')
            FROM information_schema.columns WHERE table_schema = 'public' AND table_name IN ({tables})
            """,
            $"""
            SELECT 'con ' || t.relname || '.' || c.conname || ' ' || c.contype::text || ' del=' || c.confdeltype::text || ' ' || pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname IN ({tables})
            """,
            $"""
            SELECT 'idx ' || tablename || '.' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ({tables})
            """,
        })
        {
            foreach (var item in await QueryAsync(database, sql, r => r.GetString(0))) items.Add(item);
        }
        return items;
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

    private static async Task<List<T>> QueryAsync<T>(TestDatabase database, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    private static async Task<HashSet<string>> ColumnsAsync(TestDatabase database, string table) =>
        (await QueryAsync(database, $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'",
            r => r.GetString(0))).ToHashSet();
}
