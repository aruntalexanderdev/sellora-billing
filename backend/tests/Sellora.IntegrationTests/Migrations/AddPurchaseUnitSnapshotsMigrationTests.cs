using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 4 migrations <c>AddPurchaseUnitSnapshots</c> (M6) and <c>WidenCostPrecision</c> (M7) on legacy purchase data
/// written into the <c>AddStockAdjustmentUnitSnapshots</c> schema: Pen purchased in Full Carton / Box / Piece with
/// partial and last returns (all in the line's own unit, as legacy returns were). Checks the backfill, the V-block from
/// the migration header, nullability and CHECK names, guards G1-G4 (each rolls back), Down (including the rewrite of
/// cross-unit returns and the 3 dp RAISE), Down then Up, the idempotent script (twice) through M7, and M7's scale 6.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddPurchaseUnitSnapshotsMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261106030621_AddStockAdjustmentUnitSnapshots";
    private const string M6 = "20261106044702_AddPurchaseUnitSnapshots";
    private const string M7 = "20261106044913_WidenCostPrecision";

    private static Guid G(int prefix, int n) => Guid.Parse($"{prefix:x8}-0000-0000-0000-{n:x12}");
    private static readonly Guid UPc = G(1, 1), UBox = G(1, 2), UFctn = G(1, 3), UDz = G(1, 4), UKg = G(1, 5);
    private static readonly Guid TaxId = G(2, 1), RoleId = G(2, 2), UserId = G(2, 3), SupplierId = G(2, 4);
    private static readonly Guid PPen = G(3, 1), PRice = G(3, 2);
    private static readonly Guid PenPc = G(4, 1), PenBox = G(4, 2), PenFctn = G(4, 3), PenDz = G(4, 4), RiceKg = G(4, 5);
    private static readonly Guid PoId = G(5, 1);
    private static readonly Guid L1 = G(6, 1), L2 = G(6, 2), L3 = G(6, 3), L4 = G(6, 4);   // FCtn 2, Box 5, Pc 10, FCtn 3
    private static readonly Guid R1 = G(7, 1), R2 = G(7, 2), R3 = G(7, 3);                // return headers
    private static readonly Guid RL1 = G(8, 1), RL2 = G(8, 2), RL3 = G(8, 3);             // return lines

    private static async Task<TestDatabase> CreateLegacyDatabaseAsync(PostgresFixture postgres, string? extraSql = null)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous);
            Assert.DoesNotContain(M6, await db.Database.GetAppliedMigrationsAsync());
        }
        var at = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var sql = $"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES
              ('{UPc}', 'Piece', 'pc', 'Count', 0, NULL, true), ('{UBox}', 'Box', 'box', 'Packaging', 0, NULL, true),
              ('{UFctn}', 'Full Carton', 'fctn', 'Packaging', 0, NULL, true), ('{UDz}', 'Dozen', 'dz', 'Packaging', 0, NULL, true),
              ('{UKg}', 'Kilogram', 'kg', 'Weight', 3, 1, true);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 0%', 0, true);
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{RoleId}', 'Administrator', NULL, true, true);
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{UserId}', now(), 'admin@selloramart.in', 'Ravi', 'Krishnan', 'x', '{RoleId}', 'Active', 'admin');
            INSERT INTO "Suppliers" ("Id", "Name", "CurrencyId", "IsActive") SELECT '{SupplierId}', 'Annapoorna Traders', c."Id", true FROM "Currencies" c WHERE c."Code" = 'INR';
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{PPen}', 'STN-PEN', 'Ball Pen', NULL, '{UPc}', '{TaxId}', 0, true), ('{PRice}', 'RICE', 'Rice', NULL, '{UKg}', '{TaxId}', 0, true);
            INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit",
              "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive") VALUES
              ('{PenPc}', '{PPen}', '{UPc}', NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true),
              ('{PenBox}', '{PPen}', '{UBox}', '{PenPc}', 10, 10, false, NULL, NULL, true, true, false, false, 1, true),
              ('{PenFctn}', '{PPen}', '{UFctn}', '{PenPc}', 100, 100, false, NULL, NULL, true, true, false, false, 2, true),
              ('{PenDz}', '{PPen}', '{UDz}', '{PenPc}', 12, 12, false, NULL, NULL, true, true, false, false, 3, true),
              ('{RiceKg}', '{PRice}', '{UKg}', NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true);

            INSERT INTO "PurchaseOrders" ("Id", "CreatedAt", "CreatedByUserId", "CurrencyId", "DiscountTotal", "ExchangeRateToBase",
              "GrandTotal", "OriginalGrandTotal", "OriginalPaidAmount", "OriginalReturnedAmount", "OtherChargesTotal", "PaidAmount",
              "PaymentMethod", "PurchaseDate", "PurchaseNo", "ReturnedAmount", "Status", "Subtotal", "SupplierId", "TaxTotal")
            SELECT '{PoId}', now(), '{UserId}', c."Id", 0, 1, 0, 0, 0, 0, 0, 0, 'Cash', DATE '2026-09-01', 'PUR-0001', 0, 'Completed', 0, '{SupplierId}', 0
            FROM "Currencies" c WHERE c."Code" = 'INR';
            INSERT INTO "PurchaseOrderLines" ("Id", "BaseQty", "ConversionFactorToBase", "Discount", "LineTotal", "OtherCharges", "ProductId",
              "PurchaseOrderId", "Qty", "Rate", "ProductUnitId", "TaxAmount", "TaxId", "TaxRatePercent", "TaxableValue", "UnitCost") VALUES
              ('{L1}', 200, 100, 0, 2000, 0, '{PPen}', '{PoId}', 2, 1000, '{PenFctn}', 0, '{TaxId}', 0, 2000, 10),
              ('{L2}', 50, 10, 0, 500, 0, '{PPen}', '{PoId}', 5, 100, '{PenBox}', 0, '{TaxId}', 0, 500, 10),
              ('{L3}', 10, 1, 0, 120, 0, '{PPen}', '{PoId}', 10, 12, '{PenPc}', 0, '{TaxId}', 0, 120, 12),
              ('{L4}', 300, 100, 0, 3000, 0, '{PPen}', '{PoId}', 3, 1000, '{PenFctn}', 0, '{TaxId}', 0, 3000, 10);
            INSERT INTO "PurchaseReturns" ("Id", "ReturnNo", "PurchaseOrderId", "ReturnDate", "Reason", "TaxableTotal", "TaxTotal", "Total", "OriginalTotal", "CreatedByUserId", "CreatedAt") VALUES
              ('{R1}', 'PRT-0001', '{PoId}', DATE '2026-09-02', NULL, 1000, 0, 1000, 1000, '{UserId}', '{at.AddDays(1):O}'),
              ('{R2}', 'PRT-0002', '{PoId}', DATE '2026-09-03', NULL, 1000, 0, 1000, 1000, '{UserId}', '{at.AddDays(2):O}'),
              ('{R3}', 'PRT-0003', '{PoId}', DATE '2026-09-04', NULL, 200, 0, 200, 200, '{UserId}', '{at.AddDays(3):O}');
            INSERT INTO "PurchaseReturnLines" ("Id", "PurchaseReturnId", "PurchaseOrderLineId", "ProductId", "Qty", "BaseQty", "TaxableValue", "TaxAmount", "LineTotal", "CostRemoved") VALUES
              ('{RL1}', '{R1}', '{L1}', '{PPen}', 1, 100, 1000, 0, 1000, 1000),
              ('{RL2}', '{R2}', '{L1}', '{PPen}', 1, 100, 1000, 0, 1000, 1000),
              ('{RL3}', '{R3}', '{L2}', '{PPen}', 2, 20, 200, 0, 200, 200);
            INSERT INTO "PurchaseBatches" ("Id", "CreatedAt", "ProductId", "PurchaseOrderLineId", "PurchasedBaseQty", "ReceivedDate", "RemainingBaseQty", "StockAdjustmentId", "SupplierBatchNumber", "UnitCost") VALUES
              ('{G(9, 1)}', '{at:O}', '{PPen}', '{L1}', 200, DATE '2026-09-01', 0, NULL, NULL, 10),
              ('{G(9, 2)}', '{at:O}', '{PPen}', '{L2}', 50, DATE '2026-09-01', 30, NULL, NULL, 10),
              ('{G(9, 3)}', '{at:O}', '{PPen}', '{L3}', 10, DATE '2026-09-01', 10, NULL, NULL, 12),
              ('{G(9, 4)}', '{at:O}', '{PPen}', '{L4}', 300, DATE '2026-09-01', 300, NULL, NULL, 10);
            INSERT INTO "StockMovements" ("Id", "Notes", "OccurredAt", "ProductId", "PurchaseBatchId", "QuantityBaseUnits", "ReferenceId", "ReferenceNo", "ReferenceType", "Type", "UserId") VALUES
              (gen_random_uuid(), NULL, '{at:O}', '{PPen}', '{G(9, 1)}', 200, '{PoId}', 'PUR-0001', 'PurchaseOrder', 'Purchase', '{UserId}'),
              (gen_random_uuid(), NULL, '{at:O}', '{PPen}', '{G(9, 2)}', 50, '{PoId}', 'PUR-0001', 'PurchaseOrder', 'Purchase', '{UserId}'),
              (gen_random_uuid(), NULL, '{at:O}', '{PPen}', '{G(9, 3)}', 10, '{PoId}', 'PUR-0001', 'PurchaseOrder', 'Purchase', '{UserId}'),
              (gen_random_uuid(), NULL, '{at:O}', '{PPen}', '{G(9, 4)}', 300, '{PoId}', 'PUR-0001', 'PurchaseOrder', 'Purchase', '{UserId}'),
              (gen_random_uuid(), NULL, '{at:O}', '{PPen}', '{G(9, 1)}', -100, '{R1}', 'PRT-0001', 'PurchaseReturn', 'PurchaseReturn', '{UserId}'),
              (gen_random_uuid(), NULL, '{at:O}', '{PPen}', '{G(9, 1)}', -100, '{R2}', 'PRT-0002', 'PurchaseReturn', 'PurchaseReturn', '{UserId}'),
              (gen_random_uuid(), NULL, '{at:O}', '{PPen}', '{G(9, 2)}', -20, '{R3}', 'PRT-0003', 'PurchaseReturn', 'PurchaseReturn', '{UserId}');
            INSERT INTO "StockItems" ("ProductId", "AverageCost", "QuantityOnHand") VALUES ('{PPen}', 10, 340);
            """;
        await ExecuteAsync(database, sql + (extraSql ?? ""));
        return database;
    }

    private const string V1Sql = """SELECT (SELECT COUNT(*) FROM "PurchaseOrderLines")::text || '/' || (SELECT COUNT(*) FROM "PurchaseReturnLines")::text""";

    /// <summary>V2-V6 from the migration header; each must return no rows.</summary>
    private static readonly Dictionary<string, string> VBlock = new()
    {
        ["V2 PO arithmetic"] = """SELECT "Id"::text FROM "PurchaseOrderLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty" """,
        ["V2 return arithmetic"] = """SELECT "Id"::text FROM "PurchaseReturnLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty" """,
        ["V3 PO snapshot"] = """
            SELECT x."Id"::text FROM "PurchaseOrderLines" x JOIN "ProductUnits" pu ON pu."Id" = x."ProductUnitId"
            WHERE pu."ProductId" <> x."ProductId" OR pu."UnitId" <> x."UnitId" OR x."ConversionFactorToBase" <> pu."ConversionFactorToBase"
            """,
        ["V3 return snapshot"] = """
            SELECT x."Id"::text FROM "PurchaseReturnLines" x JOIN "ProductUnits" pu ON pu."Id" = x."ProductUnitId"
            WHERE pu."ProductId" <> x."ProductId" OR pu."UnitId" <> x."UnitId"
            """,
        ["V4 legacy returns use the parent unit"] = """
            SELECT r."Id"::text FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId"
            WHERE r."ProductUnitId" <> l."ProductUnitId" OR r."ConversionFactorToBase" <> l."ConversionFactorToBase"
            """,
        ["V5 return product = parent product"] = """
            SELECT r."Id"::text FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId" WHERE r."ProductId" <> l."ProductId"
            """,
        ["V6 no over-return"] = """
            SELECT l."Id"::text FROM "PurchaseOrderLines" l
            JOIN (SELECT "PurchaseOrderLineId" id, SUM("BaseQty") q FROM "PurchaseReturnLines" GROUP BY 1) r ON r.id = l."Id" WHERE r.q > l."BaseQty"
            """,
        ["Empty UnitCode"] = """
            SELECT "Id"::text FROM "PurchaseOrderLines" WHERE btrim("UnitCode") = ''
            UNION ALL SELECT "Id"::text FROM "PurchaseReturnLines" WHERE btrim("UnitCode") = ''
            """,
    };

    private static async Task AssertVBlockEmptyAsync(TestDatabase database, string v1Before)
    {
        Assert.Equal(v1Before, (await QueryAsync(database, V1Sql, r => r.GetString(0))).Single());
        foreach (var (name, sql) in VBlock)
        {
            var rows = await QueryAsync(database, sql, r => r.GetString(0));
            Assert.True(rows.Count == 0, $"{name} returned rows: {string.Join(", ", rows)}");
        }
        Assert.Empty(await QueryAsync(database, StockInvariants.QohVsLedgerSql, r => r.GetGuid(0)));
        Assert.Empty(await QueryAsync(database, StockInvariants.QohVsBatchesSql, r => r.GetGuid(0)));
    }

    private static async Task MigrateToAsync(TestDatabase database, string target)
    {
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    [Fact]
    public async Task AddPurchaseUnitSnapshots_LegacyLines_BackfillsSnapshotsEnforcesAndVBlockIsEmpty()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = (await QueryAsync(database, V1Sql, r => r.GetString(0))).Single();
        Assert.Equal("4/3", v1);

        await MigrateToAsync(database, M6);

        await AssertVBlockEmptyAsync(database, v1);
        var po = await QueryAsync(database, """SELECT "Id"::text || '|' || "UnitId"::text || '|' || "UnitCode" || '|' || "ConversionFactorToBase"::text FROM "PurchaseOrderLines" ORDER BY "Id" """, r => r.GetString(0));
        Assert.Equal([$"{L1}|{UFctn}|fctn|100.0000000000", $"{L2}|{UBox}|box|10.0000000000", $"{L3}|{UPc}|pc|1.0000000000", $"{L4}|{UFctn}|fctn|100.0000000000"], po);
        var rl = await QueryAsync(database, """SELECT "Id"::text || '|' || "ProductUnitId"::text || '|' || "UnitCode" || '|' || "ConversionFactorToBase"::text || '|' || "Qty"::text FROM "PurchaseReturnLines" ORDER BY "Id" """, r => r.GetString(0));
        Assert.Equal([$"{RL1}|{PenFctn}|fctn|100.0000000000|1.000", $"{RL2}|{PenFctn}|fctn|100.0000000000|1.000", $"{RL3}|{PenBox}|box|10.0000000000|2.000"], rl);

        var nullability = await QueryAsync(database, """
            SELECT table_name || '.' || column_name || '=' || is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND ((table_name = 'PurchaseOrderLines' AND column_name IN ('UnitId','UnitCode'))
              OR (table_name = 'PurchaseReturnLines' AND column_name IN ('ProductUnitId','UnitId','UnitCode','ConversionFactorToBase'))) ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["PurchaseOrderLines.UnitCode=NO", "PurchaseOrderLines.UnitId=NO", "PurchaseReturnLines.ConversionFactorToBase=NO",
            "PurchaseReturnLines.ProductUnitId=NO", "PurchaseReturnLines.UnitCode=NO", "PurchaseReturnLines.UnitId=NO"], nullability);

        var checks = await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
            WHERE c.contype = 'c' AND t.relname IN ('PurchaseOrderLines','PurchaseReturnLines') ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["CK_PurchaseOrderLines_BaseQty_Arithmetic", "CK_PurchaseOrderLines_Factor_Positive", "CK_PurchaseOrderLines_Qty_Positive",
            "CK_PurchaseOrderLines_UnitCode_NotEmpty", "CK_PurchaseReturnLines_BaseQty_Arithmetic", "CK_PurchaseReturnLines_Factor_Positive", "CK_PurchaseReturnLines_Qty_Positive",
            "CK_PurchaseReturnLines_UnitCode_NotEmpty"], checks);

        var factorScale = await QueryAsync(database, """
            SELECT table_name || '.' || numeric_precision || ',' || numeric_scale FROM information_schema.columns
            WHERE column_name = 'ConversionFactorToBase' AND table_name IN ('PurchaseOrderLines','PurchaseReturnLines') ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["PurchaseOrderLines.28,10", "PurchaseReturnLines.28,10"], factorScale);

        await using var db = database.CreateDbContext();
        Assert.Contains(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData("G1", "run P2")]
    [InlineData("G2", "run P3")]
    [InlineData("G3", "run P4")]
    [InlineData("G4", "run P5")]
    public async Task AddPurchaseUnitSnapshots_GuardViolation_RaisesNamingTheQueryAndRollsBack(string guard, string expected)
    {
        var bad = guard switch
        {
            // PO line L3 points at a unit of another product (the old simple FK allows it).
            "G1" => $"""UPDATE "PurchaseOrderLines" SET "ProductUnitId" = '{RiceKg}' WHERE "Id" = '{L3}';""",
            // 2.0004 is stored as 2.000 by numeric(18,3): Qty x factor no longer equals BaseQty.
            "G2" => $"""UPDATE "PurchaseOrderLines" SET "Qty" = 2.0004, "BaseQty" = 200.4 WHERE "Id" = '{L4}';""",
            "G3" => $"""UPDATE "PurchaseReturnLines" SET "ProductId" = '{PRice}' WHERE "Id" = '{RL3}';""",
            _ => $"""UPDATE "PurchaseReturnLines" SET "BaseQty" = 21 WHERE "Id" = '{RL3}';""",
        };
        await using var database = await CreateLegacyDatabaseAsync(postgres, bad);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(database, M6));

        Assert.Contains(expected, ex.MessageText);
        Assert.DoesNotContain("UnitCode", await ColumnsAsync(database, "PurchaseOrderLines"));
        Assert.DoesNotContain("ProductUnitId", await ColumnsAsync(database, "PurchaseReturnLines"));
        Assert.Equal(18L, await ScalarAsync<long>(database, """SELECT numeric_precision FROM information_schema.columns WHERE table_name = 'PurchaseOrderLines' AND column_name = 'ConversionFactorToBase'"""));
        await using var db = database.CreateDbContext();
        Assert.DoesNotContain(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>Down keeps every stock row, restores the simple FK and the 18,3 factor, and leaves exactly the schema of a
    /// database migrated only to the previous migration.</summary>
    [Fact]
    public async Task AddPurchaseUnitSnapshots_Down_RestoresPreviousSchemaAndKeepsData()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        const string data = """
            SELECT 'L ' || "Id" || ' ' || "Qty" || ' ' || "BaseQty" || ' ' || "ConversionFactorToBase" || ' ' || "ProductUnitId" || ' ' || "UnitCost" FROM "PurchaseOrderLines"
            UNION ALL SELECT 'R ' || "Id" || ' ' || "Qty" || ' ' || "BaseQty" || ' ' || "TaxableValue" || ' ' || "CostRemoved" FROM "PurchaseReturnLines"
            UNION ALL SELECT 'B ' || "Id" || ' ' || "PurchasedBaseQty" || ' ' || "RemainingBaseQty" || ' ' || "UnitCost" FROM "PurchaseBatches"
            UNION ALL SELECT 'S ' || "ProductId" || ' ' || "QuantityOnHand" || ' ' || "AverageCost" FROM "StockItems"
            UNION ALL SELECT 'M ' || "Id" || ' ' || "QuantityBaseUnits" FROM "StockMovements"
            ORDER BY 1
            """;
        var before = await QueryAsync(database, data, r => r.GetString(0));
        await MigrateToAsync(database, M6);

        await MigrateToAsync(database, Previous);

        Assert.Equal(before, await QueryAsync(database, data, r => r.GetString(0)));
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(reference, Previous);
        Assert.Equal(await SchemaSignatureAsync(reference), await SchemaSignatureAsync(database));
        await using var db = database.CreateDbContext();
        Assert.DoesNotContain(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>A return posted after M6 in another unit than its line (37 pc against a Full Carton line) is rewritten to the
    /// line's unit on Down: Qty = BaseQty / parent factor = 0.37 Full Carton, BaseQty unchanged.</summary>
    [Fact]
    public async Task AddPurchaseUnitSnapshots_Down_RewritesCrossUnitReturnsToTheLineUnit()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await MigrateToAsync(database, M6);
        await InsertCrossUnitReturnAsync(database, L4, PenPc, UPc, "pc", factor: 1, qty: 37, baseQty: 37, no: "PRT-0004");

        await MigrateToAsync(database, Previous);

        var row = (await QueryAsync(database, """SELECT "Qty"::text || '|' || "BaseQty"::text FROM "PurchaseReturnLines" WHERE "PurchaseOrderLineId" = '""" + L4 + "'", r => r.GetString(0))).Single();
        Assert.Equal("0.370|37.000", row);
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(reference, Previous);
        Assert.Equal(await SchemaSignatureAsync(reference), await SchemaSignatureAsync(database));
    }

    /// <summary>1 pc against a Dozen line is 1/12 = 0.0833...: not expressible at 3 dp, so Down RAISES and changes nothing.</summary>
    [Fact]
    public async Task AddPurchaseUnitSnapshots_Down_ReturnNotExpressibleInLineUnitRaisesAndRollsBack()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, $"""
            INSERT INTO "PurchaseOrderLines" ("Id", "BaseQty", "ConversionFactorToBase", "Discount", "LineTotal", "OtherCharges", "ProductId",
              "PurchaseOrderId", "Qty", "Rate", "ProductUnitId", "TaxAmount", "TaxId", "TaxRatePercent", "TaxableValue", "UnitCost")
            VALUES ('{G(6, 5)}', 12, 12, 0, 120, 0, '{PPen}', '{PoId}', 1, 120, '{PenDz}', 0, '{TaxId}', 0, 120, 10);
            """);
        await MigrateToAsync(database, M6);
        await InsertCrossUnitReturnAsync(database, G(6, 5), PenPc, UPc, "pc", factor: 1, qty: 1, baseQty: 1, no: "PRT-0005");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(database, Previous));

        Assert.Contains("restore the snapshot", ex.MessageText);
        Assert.Contains("UnitCode", await ColumnsAsync(database, "PurchaseReturnLines"));
        Assert.Equal("1.000", (await QueryAsync(database, """SELECT "Qty"::text FROM "PurchaseReturnLines" WHERE "BaseQty" = 1""", r => r.GetString(0))).Single());
        await using var db = database.CreateDbContext();
        Assert.Contains(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task AddPurchaseUnitSnapshots_DownThenUpAgain_ReBackfillsAndVBlockIsEmpty()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = (await QueryAsync(database, V1Sql, r => r.GetString(0))).Single();
        await MigrateToAsync(database, M6);
        await InsertCrossUnitReturnAsync(database, L4, PenPc, UPc, "pc", factor: 1, qty: 37, baseQty: 37, no: "PRT-0004");
        await MigrateToAsync(database, Previous);

        await MigrateToAsync(database, M6);

        // The cross-unit return came back as a return in its line's unit (0.37 FCtn): V4 and the arithmetic hold.
        await AssertVBlockEmptyAsync(database, "4/4");
        Assert.Equal("4/3", v1);

        // Re-Up re-derives the return line's unit snapshot from its parent purchase line.
        var mismatches = await QueryAsync(database,
            """
            SELECT r."Id"::text FROM "PurchaseReturnLines" r
            JOIN "PurchaseOrderLines" l ON l."Id" = r."PurchaseOrderLineId"
            WHERE r."ProductUnitId" <> l."ProductUnitId" OR r."UnitId" <> l."UnitId" OR r."UnitCode" <> l."UnitCode"
               OR r."ConversionFactorToBase" <> l."ConversionFactorToBase"
            """, r => r.GetString(0));
        Assert.Empty(mismatches);
    }

    /// <summary>Applies M6 + M7 as the production idempotent script (plain connection, DO $EF$ wrapper) to legacy data, then
    /// runs it again: schema, data and history are unchanged.</summary>
    [Fact]
    public async Task IdempotentScript_M6ThroughM7_AppliesToLegacyDataAndSecondRunIsNoOp()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = (await QueryAsync(database, V1Sql, r => r.GetString(0))).Single();
        string script;
        await using (var db = database.CreateDbContext())
        {
            script = db.GetService<IMigrator>().GenerateScript(Previous, M7, MigrationsSqlGenerationOptions.Idempotent);
        }
        Assert.Contains("DO $EF$", script);
        Assert.Contains(M6, script);
        Assert.Contains(M7, script);

        await ExecuteAsync(database, script);

        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, M7) > 0), await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(M7, await db.Database.GetAppliedMigrationsAsync());
        }
        await AssertVBlockEmptyAsync(database, v1);
        Assert.Equal(6L, await ScalarAsync<long>(database, """SELECT numeric_scale FROM information_schema.columns WHERE table_name = 'PurchaseBatches' AND column_name = 'UnitCost'"""));

        const string snapshot = """
            SELECT 'L ' || "Id" || ' ' || "UnitId" || ' ' || "UnitCode" || ' ' || "ConversionFactorToBase" || ' ' || "UnitCost" FROM "PurchaseOrderLines"
            UNION ALL SELECT 'R ' || "Id" || ' ' || "ProductUnitId" || ' ' || "UnitCode" || ' ' || "ConversionFactorToBase" FROM "PurchaseReturnLines"
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
    public async Task IdempotentScript_WithInexactLegacyLine_RaisesThroughWrapperAndLeavesSchemaUnchanged()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, $"""UPDATE "PurchaseOrderLines" SET "Qty" = 2.0004, "BaseQty" = 200.4 WHERE "Id" = '{L4}';""");
        string script;
        await using (var db = database.CreateDbContext())
        {
            script = db.GetService<IMigrator>().GenerateScript(Previous, M7, MigrationsSqlGenerationOptions.Idempotent);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, script));

        Assert.Contains("run P3", ex.MessageText);
        Assert.DoesNotContain("UnitCode", await ColumnsAsync(database, "PurchaseOrderLines"));
        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(M6, await check.Database.GetAppliedMigrationsAsync());
    }

    // ------------------------------------------------------------------ M7

    private static readonly (string Table, string Column)[] CostColumns =
    [
        ("PurchaseBatches", "UnitCost"), ("PurchaseOrderLines", "UnitCost"), ("StockItems", "AverageCost"),
        ("SalesInvoiceLineBatchConsumptions", "UnitCost"), ("StockAdjustments", "UnitCost"),
    ];

    private static async Task<string[]> CostScalesAsync(TestDatabase database) =>
        (await QueryAsync(database, "SELECT table_name || '.' || column_name || '=' || numeric_precision || ',' || numeric_scale FROM information_schema.columns WHERE "
            + string.Join(" OR ", CostColumns.Select(c => $"(table_name = '{c.Table}' AND column_name = '{c.Column}')")) + " ORDER BY 1", r => r.GetString(0))).ToArray();

    [Fact]
    public async Task WidenCostPrecision_Up_AllFiveColumnsScaleSixAndExistingValuesKeptThenSixDpRoundTrips()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await MigrateToAsync(database, M6);
        Assert.All(await CostScalesAsync(database), s => Assert.EndsWith(",4", s));

        await MigrateToAsync(database, M7);

        Assert.Equal(["PurchaseBatches.UnitCost=18,6", "PurchaseOrderLines.UnitCost=18,6", "SalesInvoiceLineBatchConsumptions.UnitCost=18,6",
            "StockAdjustments.UnitCost=18,6", "StockItems.AverageCost=18,6"], await CostScalesAsync(database));
        Assert.Equal(["10.000000", "10.000000", "10.000000", "12.000000"],
            await QueryAsync(database, """SELECT "UnitCost"::text FROM "PurchaseBatches" ORDER BY "UnitCost" """, r => r.GetString(0)));
        await ExecuteAsync(database, $"""
            UPDATE "PurchaseBatches" SET "UnitCost" = 7.129769 WHERE "Id" = '{G(9, 1)}';
            UPDATE "PurchaseOrderLines" SET "UnitCost" = 333.333333 WHERE "Id" = '{L1}';
            UPDATE "StockItems" SET "AverageCost" = 169.999999 WHERE "ProductId" = '{PPen}';
            """);
        Assert.Equal("7.129769", await ScalarStringAsync(database, $"""SELECT "UnitCost"::text FROM "PurchaseBatches" WHERE "Id" = '{G(9, 1)}'"""));
        Assert.Equal("333.333333", await ScalarStringAsync(database, $"""SELECT "UnitCost"::text FROM "PurchaseOrderLines" WHERE "Id" = '{L1}'"""));
        Assert.Equal("169.999999", await ScalarStringAsync(database, $"""SELECT "AverageCost"::text FROM "StockItems" WHERE "ProductId" = '{PPen}'"""));
    }

    [Fact]
    public async Task WidenCostPrecision_Down_RefusesLossyNarrowingThenNarrowsExactValues()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await MigrateToAsync(database, M7);
        await ExecuteAsync(database, $"""UPDATE "PurchaseBatches" SET "UnitCost" = 7.129769 WHERE "Id" = '{G(9, 1)}'""");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(database, M6));
        Assert.Contains("PurchaseBatches.UnitCost", ex.MessageText);

        await ExecuteAsync(database, $"""UPDATE "PurchaseBatches" SET "UnitCost" = 7.1298 WHERE "Id" = '{G(9, 1)}'; UPDATE "StockItems" SET "AverageCost" = 0.0001 WHERE "ProductId" = '{PPen}';""");
        await MigrateToAsync(database, M6);

        Assert.All(await CostScalesAsync(database), s => Assert.EndsWith(",4", s));
        Assert.Equal("7.1298", await ScalarStringAsync(database, $"""SELECT "UnitCost"::text FROM "PurchaseBatches" WHERE "Id" = '{G(9, 1)}'"""));
    }

    // ------------------------------------------------------------------ helpers

    private static Task InsertCrossUnitReturnAsync(TestDatabase database, Guid poLineId, Guid productUnitId, Guid unitId, string code, decimal factor, decimal qty, decimal baseQty, string no)
    {
        var header = Guid.NewGuid();
        return ExecuteAsync(database, $"""
            INSERT INTO "PurchaseReturns" ("Id", "ReturnNo", "PurchaseOrderId", "ReturnDate", "Reason", "TaxableTotal", "TaxTotal", "Total", "OriginalTotal", "CreatedByUserId", "CreatedAt")
            VALUES ('{header}', '{no}', '{PoId}', DATE '2026-09-05', NULL, 0, 0, 0, 0, '{UserId}', now());
            INSERT INTO "PurchaseReturnLines" ("Id", "PurchaseReturnId", "PurchaseOrderLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase",
              "Qty", "BaseQty", "TaxableValue", "TaxAmount", "LineTotal", "CostRemoved")
            VALUES (gen_random_uuid(), '{header}', '{poLineId}', '{PPen}', '{productUnitId}', '{unitId}', '{code}', {factor}, {qty}, {baseQty}, 0, 0, 0, 0);
            """);
    }

    private static async Task<HashSet<string>> SchemaSignatureAsync(TestDatabase database)
    {
        const string tables = "'PurchaseOrderLines','PurchaseReturnLines','ProductUnits','Units'";
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
            $"""SELECT 'idx ' || tablename || '.' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ({tables})""",
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
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private static Task<string> ScalarStringAsync(TestDatabase database, string sql) => ScalarAsync<string>(database, sql);

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
        (await QueryAsync(database, $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'", r => r.GetString(0))).ToHashSet();
}
