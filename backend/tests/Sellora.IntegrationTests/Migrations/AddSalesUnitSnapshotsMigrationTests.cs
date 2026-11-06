using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 5 migration <c>AddSalesUnitSnapshots</c> (M6, sales part) on legacy sales data written into the
/// <c>WidenCostPrecision</c> schema: Pen sold in Box and Piece, Rice in kg, a partial return per line (in the sold line's
/// unit, as legacy returns were), and a cancelled invoice with its positive 'Sale' movement. Checks the backfill, the
/// relabel to SaleCancellation, the V-block from the migration header, nullability / CHECK / FK / index names, guards
/// P1-P4 and P6 (each rolls back), Down (data kept, schema restored, cross-unit return rewritten, 3 dp RAISEs),
/// Down then Up, the idempotent script (twice) and the DO-wrapper raise.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddSalesUnitSnapshotsMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261106044913_WidenCostPrecision";
    private const string M6 = "20261106085008_AddSalesUnitSnapshots";

    private static Guid G(int prefix, int n) => Guid.Parse($"{prefix:x8}-0000-0000-0000-{n:x12}");
    private static readonly Guid UPc = G(1, 1), UBox = G(1, 2), UDz = G(1, 3), UKg = G(1, 4);
    private static readonly Guid TaxId = G(2, 1), RoleId = G(2, 2), UserId = G(2, 3), CustomerId = G(2, 4);
    private static readonly Guid PPen = G(3, 1), PRice = G(3, 2);
    private static readonly Guid PenPc = G(4, 1), PenBox = G(4, 2), PenDz = G(4, 3), RiceKg = G(4, 4);
    private static readonly Guid S1 = G(5, 1), S2 = G(5, 2);                         // completed / cancelled invoice
    private static readonly Guid LA = G(6, 1), LB = G(6, 2), LC = G(6, 3), LR = G(6, 4); // 2 Box, 5 pc, 4 pc (cancelled), 1.5 kg
    private static readonly Guid Ret1 = G(7, 1);
    private static readonly Guid RLA = G(8, 1), RLB = G(8, 2);                       // 1 Box (10 pc), 2 pc
    private static readonly Guid BatchPen = G(9, 1), BatchRice = G(9, 2);

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
              ('{UDz}', 'Dozen', 'dz', 'Packaging', 0, NULL, true), ('{UKg}', 'Kilogram', 'kg', 'Weight', 3, 1, true);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 5%', 5, true);
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{RoleId}', 'Administrator', NULL, true, true);
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{UserId}', now(), 'admin@selloramart.in', 'Ravi', 'Krishnan', 'x', '{RoleId}', 'Active', 'admin');
            INSERT INTO "Customers" ("Id", "Name", "IsActive", "IsWalkIn") VALUES ('{CustomerId}', 'Walk-in', true, true);
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{PPen}', 'STN-PEN', 'Ball Pen', NULL, '{UPc}', '{TaxId}', 0, true), ('{PRice}', 'RICE', 'Rice', NULL, '{UKg}', '{TaxId}', 0, true);
            INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit",
              "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive") VALUES
              ('{PenPc}', '{PPen}', '{UPc}', NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true),
              ('{PenBox}', '{PPen}', '{UBox}', '{PenPc}', 10, 10, false, NULL, NULL, true, true, false, false, 1, true),
              ('{PenDz}', '{PPen}', '{UDz}', '{PenPc}', 12, 12, false, NULL, NULL, true, true, false, false, 2, true),
              ('{RiceKg}', '{PRice}', '{UKg}', NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true);

            INSERT INTO "PurchaseBatches" ("Id", "CreatedAt", "ProductId", "PurchaseOrderLineId", "PurchasedBaseQty", "ReceivedDate", "RemainingBaseQty", "StockAdjustmentId", "SupplierBatchNumber", "UnitCost") VALUES
              ('{BatchPen}', '{at:O}', '{PPen}', NULL, 100, DATE '2026-09-01', 87, NULL, NULL, 10),
              ('{BatchRice}', '{at:O}', '{PRice}', NULL, 10, DATE '2026-09-01', 8.5, NULL, NULL, 40);
            INSERT INTO "SalesInvoices" ("Id", "InvoiceNo", "CustomerId", "InvoiceDate", "PaymentMethod", "PaidAmount", "ReturnedAmount", "Subtotal", "TaxTotal", "DiscountTotal", "GrandTotal", "Status", "CreatedByUserId", "CreatedAt") VALUES
              ('{S1}', 'SAL-0001', '{CustomerId}', DATE '2026-09-02', 'Cash', 0, 0, 0, 0, 0, 0, 'Completed', '{UserId}', '{at.AddDays(1):O}'),
              ('{S2}', 'SAL-0002', '{CustomerId}', DATE '2026-09-03', 'Cash', 0, 0, 0, 0, 0, 0, 'Cancelled', '{UserId}', '{at.AddDays(2):O}');
            INSERT INTO "SalesInvoiceLines" ("Id", "SalesInvoiceId", "ProductId", "ProductUnitId", "Qty", "ConversionFactorToBase", "BaseQty", "UnitPrice", "TaxId", "TaxRatePercent",
              "Discount", "TaxableValue", "TaxAmount", "LineTotal", "CostOfGoodsSold") VALUES
              ('{LA}', '{S1}', '{PPen}', '{PenBox}', 2, 10, 20, 50, '{TaxId}', 5, 5, 95, 4.75, 99.75, 200),
              ('{LB}', '{S1}', '{PPen}', '{PenPc}', 5, 1, 5, 6, '{TaxId}', 5, 0, 30, 1.5, 31.5, 50),
              ('{LC}', '{S2}', '{PPen}', '{PenPc}', 4, 1, 4, 6, '{TaxId}', 5, 0, 24, 1.2, 25.2, 40),
              ('{LR}', '{S1}', '{PRice}', '{RiceKg}', 1.5, 1, 1.5, 40, '{TaxId}', 5, 0, 60, 3, 63, 60);
            INSERT INTO "SalesInvoiceLineBatchConsumptions" ("Id", "SalesInvoiceLineId", "PurchaseBatchId", "QtyBaseUnits", "ReturnedQtyBaseUnits", "UnitCost") VALUES
              (gen_random_uuid(), '{LA}', '{BatchPen}', 20, 10, 10), (gen_random_uuid(), '{LB}', '{BatchPen}', 5, 2, 10),
              (gen_random_uuid(), '{LC}', '{BatchPen}', 4, 0, 10), (gen_random_uuid(), '{LR}', '{BatchRice}', 1.5, 0, 40);
            INSERT INTO "SalesReturns" ("Id", "ReturnNo", "SalesInvoiceId", "ReturnDate", "Reason", "TaxableTotal", "TaxTotal", "Total", "CreatedByUserId", "CreatedAt")
            VALUES ('{Ret1}', 'SRT-0001', '{S1}', DATE '2026-09-04', NULL, 59.5, 2.975, 62.475, '{UserId}', '{at.AddDays(3):O}');
            INSERT INTO "SalesReturnLines" ("Id", "SalesReturnId", "SalesInvoiceLineId", "ProductId", "Qty", "BaseQty", "TaxableValue", "TaxAmount", "LineTotal", "CostRestored") VALUES
              ('{RLA}', '{Ret1}', '{LA}', '{PPen}', 1, 10, 47.5, 2.375, 49.875, 100),
              ('{RLB}', '{Ret1}', '{LB}', '{PPen}', 2, 2, 12, 0.6, 12.6, 20);
            INSERT INTO "StockMovements" ("Id", "Notes", "OccurredAt", "ProductId", "PurchaseBatchId", "QuantityBaseUnits", "ReferenceId", "ReferenceNo", "ReferenceType", "Type", "UserId") VALUES
              ('{G(10, 1)}', NULL, '{at:O}', '{PPen}', '{BatchPen}', 100, '{G(11, 1)}', 'PUR-0001', 'PurchaseOrder', 'Purchase', '{UserId}'),
              ('{G(10, 2)}', NULL, '{at:O}', '{PRice}', '{BatchRice}', 10, '{G(11, 1)}', 'PUR-0001', 'PurchaseOrder', 'Purchase', '{UserId}'),
              ('{G(10, 3)}', NULL, '{at.AddDays(1):O}', '{PPen}', NULL, -25, '{S1}', 'SAL-0001', 'SalesInvoice', 'Sale', '{UserId}'),
              ('{G(10, 4)}', NULL, '{at.AddDays(1):O}', '{PRice}', NULL, -1.5, '{S1}', 'SAL-0001', 'SalesInvoice', 'Sale', '{UserId}'),
              ('{G(10, 5)}', NULL, '{at.AddDays(2):O}', '{PPen}', NULL, -4, '{S2}', 'SAL-0002', 'SalesInvoice', 'Sale', '{UserId}'),
              ('{G(10, 6)}', NULL, '{at.AddDays(2):O}', '{PPen}', NULL, 4, '{S2}', 'SAL-0002', 'SalesInvoice', 'Sale', '{UserId}'),
              ('{G(10, 7)}', NULL, '{at.AddDays(3):O}', '{PPen}', NULL, 12, '{Ret1}', 'SRT-0001', 'SalesReturn', 'SaleReturn', '{UserId}');
            INSERT INTO "StockItems" ("ProductId", "AverageCost", "QuantityOnHand") VALUES ('{PPen}', 10, 87), ('{PRice}', 40, 8.5);
            """;
        await ExecuteAsync(database, sql + (extraSql ?? ""));
        return database;
    }

    private const string V1Sql = """
        SELECT (SELECT COUNT(*) FROM "SalesInvoiceLines")::text || '/' || (SELECT COUNT(*) FROM "SalesReturnLines")::text || '/' || (SELECT COUNT(*) FROM "StockMovements")::text
        """;

    /// <summary>V2-V7 from the migration header; each must return no rows.</summary>
    private static readonly Dictionary<string, string> VBlock = new()
    {
        ["V2 sales arithmetic"] = """SELECT "Id"::text FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty" """,
        ["V2 return arithmetic"] = """SELECT "Id"::text FROM "SalesReturnLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty" """,
        ["V3 sales snapshot"] = """
            SELECT l."Id"::text FROM "SalesInvoiceLines" l JOIN "ProductUnits" pu ON pu."Id" = l."ProductUnitId"
            WHERE pu."ProductId" <> l."ProductId" OR pu."UnitId" <> l."UnitId" OR pu."ConversionFactorToBase" <> l."ConversionFactorToBase"
            """,
        ["V4 legacy returns use the parent unit"] = """
            SELECT r."Id"::text FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId"
            WHERE r."ProductUnitId" <> l."ProductUnitId" OR r."ConversionFactorToBase" <> l."ConversionFactorToBase"
            """,
        ["V5 no over-return"] = """
            SELECT l."Id"::text FROM "SalesInvoiceLines" l
            JOIN (SELECT "SalesInvoiceLineId" id, SUM("BaseQty") q FROM "SalesReturnLines" GROUP BY 1) r ON r.id = l."Id" WHERE r.q > l."BaseQty"
            """,
        ["V6 consumption returned = returned base qty"] = """
            SELECT l."Id"::text FROM "SalesInvoiceLines" l
            WHERE COALESCE((SELECT SUM("ReturnedQtyBaseUnits") FROM "SalesInvoiceLineBatchConsumptions" c WHERE c."SalesInvoiceLineId" = l."Id"), 0)
               <> COALESCE((SELECT SUM("BaseQty") FROM "SalesReturnLines" r WHERE r."SalesInvoiceLineId" = l."Id"), 0)
            """,
        ["V7 no positive Sale movement"] = """SELECT "Id"::text FROM "StockMovements" WHERE "Type" = 'Sale' AND "QuantityBaseUnits" > 0""",
        ["V7 SaleCancellation count = cancelled (invoice, product) pairs"] = """
            SELECT 'difference ' || d::text FROM (SELECT (SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'SaleCancellation')
              - (SELECT COUNT(*) FROM (SELECT DISTINCT l."SalesInvoiceId", l."ProductId" FROM "SalesInvoiceLines" l
                 JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId" WHERE i."Status" = 'Cancelled') x) AS d) y WHERE d <> 0
            """,
        ["Empty UnitCode"] = """
            SELECT "Id"::text FROM "SalesInvoiceLines" WHERE btrim("UnitCode") = ''
            UNION ALL SELECT "Id"::text FROM "SalesReturnLines" WHERE btrim("UnitCode") = ''
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
    public async Task AddSalesUnitSnapshots_LegacyLines_BackfillsSnapshotsRelabelsCancellationEnforcesAndVBlockIsEmpty()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = (await QueryAsync(database, V1Sql, r => r.GetString(0))).Single();
        Assert.Equal("4/2/7", v1);

        await MigrateToAsync(database, M6);

        await AssertVBlockEmptyAsync(database, v1);
        var lines = await QueryAsync(database, """
            SELECT "Id"::text || '|' || "UnitId"::text || '|' || "UnitCode" || '|' || "ConversionFactorToBase"::text || '|' || COALESCE("ListUnitPrice"::text, 'null') || '|' || "PriceOverridden"::text
            FROM "SalesInvoiceLines" ORDER BY "Id"
            """, r => r.GetString(0));
        Assert.Equal([$"{LA}|{UBox}|box|10.0000000000|null|false", $"{LB}|{UPc}|pc|1.0000000000|null|false",
            $"{LC}|{UPc}|pc|1.0000000000|null|false", $"{LR}|{UKg}|kg|1.0000000000|null|false"], lines);
        var returns = await QueryAsync(database, """
            SELECT "Id"::text || '|' || "ProductUnitId"::text || '|' || "UnitId"::text || '|' || "UnitCode" || '|' || "ConversionFactorToBase"::text || '|' || "Qty"::text FROM "SalesReturnLines" ORDER BY "Id"
            """, r => r.GetString(0));
        Assert.Equal([$"{RLA}|{PenBox}|{UBox}|box|10.0000000000|1.000", $"{RLB}|{PenPc}|{UPc}|pc|1.0000000000|2.000"], returns);

        // Only the positive 'Sale' row written by Cancel is relabelled; negative sales and other types are untouched.
        var movements = await QueryAsync(database, """SELECT "Type" || '|' || "QuantityBaseUnits"::text FROM "StockMovements" WHERE "ReferenceType" IN ('SalesInvoice','SalesReturn') ORDER BY "Id" """, r => r.GetString(0));
        Assert.Equal(["Sale|-25.000", "Sale|-1.500", "Sale|-4.000", "SaleCancellation|4.000", "SaleReturn|12.000"], movements);

        var nullability = await QueryAsync(database, """
            SELECT table_name || '.' || column_name || '=' || is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND ((table_name = 'SalesInvoiceLines' AND column_name IN ('UnitId','UnitCode','ListUnitPrice','PriceOverridden'))
              OR (table_name = 'SalesReturnLines' AND column_name IN ('ProductUnitId','UnitId','UnitCode','ConversionFactorToBase'))) ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["SalesInvoiceLines.ListUnitPrice=YES", "SalesInvoiceLines.PriceOverridden=NO", "SalesInvoiceLines.UnitCode=NO", "SalesInvoiceLines.UnitId=NO",
            "SalesReturnLines.ConversionFactorToBase=NO", "SalesReturnLines.ProductUnitId=NO", "SalesReturnLines.UnitCode=NO", "SalesReturnLines.UnitId=NO"], nullability);

        var checks = await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
            WHERE c.contype = 'c' AND t.relname IN ('SalesInvoiceLines','SalesReturnLines') ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["CK_SalesInvoiceLines_BaseQty_Arithmetic", "CK_SalesInvoiceLines_Discount_WithinGross", "CK_SalesInvoiceLines_Factor_Positive",
            "CK_SalesInvoiceLines_Override_HasList", "CK_SalesInvoiceLines_Price_MatchesList", "CK_SalesInvoiceLines_Qty_Positive", "CK_SalesInvoiceLines_UnitCode_NotEmpty",
            "CK_SalesReturnLines_BaseQty_Arithmetic", "CK_SalesReturnLines_Factor_Positive", "CK_SalesReturnLines_Qty_Positive", "CK_SalesReturnLines_UnitCode_NotEmpty"], checks);

        var fks = await QueryAsync(database, """
            SELECT c.conname || ' del=' || c.confdeltype::text FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
            WHERE c.contype = 'f' AND t.relname IN ('SalesInvoiceLines','SalesReturnLines') AND (c.conname LIKE '%ProductUnits%' OR c.conname LIKE '%Units_UnitId') ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["FK_SalesInvoiceLines_ProductUnits_ProductId_ProductUnitId del=r", "FK_SalesInvoiceLines_Units_UnitId del=r",
            "FK_SalesReturnLines_ProductUnits_ProductId_ProductUnitId del=r", "FK_SalesReturnLines_Units_UnitId del=r"], fks);

        var indexes = await QueryAsync(database, """
            SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('SalesInvoiceLines','SalesReturnLines')
              AND indexname IN ('IX_SalesInvoiceLines_ProductId_ProductUnitId','IX_SalesInvoiceLines_UnitId','IX_SalesReturnLines_ProductId_ProductUnitId','IX_SalesReturnLines_UnitId',
                                'IX_SalesInvoiceLines_ProductId','IX_SalesInvoiceLines_ProductUnitId','IX_SalesReturnLines_ProductId') ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["IX_SalesInvoiceLines_ProductId_ProductUnitId", "IX_SalesInvoiceLines_UnitId", "IX_SalesReturnLines_ProductId_ProductUnitId", "IX_SalesReturnLines_UnitId"], indexes);

        var factorScale = await QueryAsync(database, """
            SELECT table_name || '.' || numeric_precision || ',' || numeric_scale FROM information_schema.columns
            WHERE column_name = 'ConversionFactorToBase' AND table_name IN ('SalesInvoiceLines','SalesReturnLines') ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(["SalesInvoiceLines.28,10", "SalesReturnLines.28,10"], factorScale);

        await using var db = database.CreateDbContext();
        Assert.Contains(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData("P1", "run P1")]
    [InlineData("P2", "run P2")]
    [InlineData("P3", "run P3")]
    [InlineData("P4", "run P4")]
    [InlineData("P6", "run P6")]
    public async Task AddSalesUnitSnapshots_GuardViolation_RaisesNamingTheQueryAndRollsBack(string guard, string expected)
    {
        var bad = guard switch
        {
            // Sales line LB points at a unit of another product (the old simple FK allows it).
            "P1" => $"""UPDATE "SalesInvoiceLines" SET "ProductUnitId" = '{RiceKg}' WHERE "Id" = '{LB}';""",
            // 2.0004 is stored as 2.000 by numeric(18,3): Qty x factor no longer equals BaseQty.
            "P2" => $"""UPDATE "SalesInvoiceLines" SET "Qty" = 2.0004, "BaseQty" = 20.004 WHERE "Id" = '{LA}';""",
            "P3" => $"""UPDATE "SalesReturnLines" SET "ProductId" = '{PRice}' WHERE "Id" = '{RLB}';""",
            "P4" => $"""UPDATE "SalesReturnLines" SET "BaseQty" = 3 WHERE "Id" = '{RLB}';""",
            // Discount 101 exceeds the gross 2 x 50 = 100.
            _ => $"""UPDATE "SalesInvoiceLines" SET "Discount" = 101 WHERE "Id" = '{LA}';""",
        };
        await using var database = await CreateLegacyDatabaseAsync(postgres, bad);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(database, M6));

        Assert.Contains(expected, ex.MessageText);
        Assert.DoesNotContain("UnitCode", await ColumnsAsync(database, "SalesInvoiceLines"));
        Assert.DoesNotContain("ProductUnitId", await ColumnsAsync(database, "SalesReturnLines"));
        Assert.Equal(18L, await ScalarAsync<long>(database, """SELECT numeric_precision FROM information_schema.columns WHERE table_name = 'SalesInvoiceLines' AND column_name = 'ConversionFactorToBase'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'Sale' AND "QuantityBaseUnits" > 0"""));
        await using var db = database.CreateDbContext();
        Assert.DoesNotContain(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    private const string DataSnapshotSql = """
        SELECT 'L ' || "Id" || ' ' || "ProductUnitId" || ' ' || "Qty" || ' ' || "BaseQty" || ' ' || "ConversionFactorToBase" || ' ' || "UnitPrice" || ' ' || "Discount" FROM "SalesInvoiceLines"
        UNION ALL SELECT 'R ' || "Id" || ' ' || "Qty" || ' ' || "BaseQty" || ' ' || "TaxableValue" || ' ' || "CostRestored" FROM "SalesReturnLines"
        UNION ALL SELECT 'C ' || "Id" || ' ' || "QtyBaseUnits" || ' ' || "ReturnedQtyBaseUnits" FROM "SalesInvoiceLineBatchConsumptions"
        UNION ALL SELECT 'S ' || "ProductId" || ' ' || "QuantityOnHand" || ' ' || "AverageCost" FROM "StockItems"
        UNION ALL SELECT 'M ' || "Id" || ' ' || "Type" || ' ' || "QuantityBaseUnits" FROM "StockMovements"
        ORDER BY 1
        """;

    /// <summary>Down keeps every row, restores the simple FK and the 18,3 factor, relabels SaleCancellation back to Sale and
    /// leaves exactly the schema of a database migrated only to the previous migration.</summary>
    [Fact]
    public async Task AddSalesUnitSnapshots_Down_RestoresPreviousSchemaKeepsDataAndRelabelsBack()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var before = await QueryAsync(database, DataSnapshotSql, r => r.GetString(0));
        await MigrateToAsync(database, M6);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'SaleCancellation'"""));

        await MigrateToAsync(database, Previous);

        Assert.Equal(before, await QueryAsync(database, DataSnapshotSql, r => r.GetString(0)));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'SaleCancellation'"""));
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(reference, Previous);
        Assert.Equal(await SchemaSignatureAsync(reference), await SchemaSignatureAsync(database));
        await using var db = database.CreateDbContext();
        Assert.DoesNotContain(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>A return posted after the migration in another unit than its line (3 pc against a Box line) is rewritten to
    /// the line's unit on Down: Qty = BaseQty / parent factor = 0.3 Box, BaseQty unchanged.</summary>
    [Fact]
    public async Task AddSalesUnitSnapshots_Down_RewritesCrossUnitReturnsToTheLineUnit()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await MigrateToAsync(database, M6);
        var returnId = await InsertCrossUnitReturnAsync(database, LA, PenPc, UPc, "pc", factor: 1, qty: 3, baseQty: 3, no: "SRT-0002");

        await MigrateToAsync(database, Previous);

        var row = (await QueryAsync(database, $"""SELECT "Qty"::text || '|' || "BaseQty"::text FROM "SalesReturnLines" WHERE "Id" = '{returnId}'""", r => r.GetString(0))).Single();
        Assert.Equal("0.300|3.000", row);
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(reference, Previous);
        Assert.Equal(await SchemaSignatureAsync(reference), await SchemaSignatureAsync(database));
    }

    /// <summary>1 pc against a Dozen line is 1/12 = 0.0833...: not expressible at 3 dp, so Down RAISES and changes nothing.</summary>
    [Fact]
    public async Task AddSalesUnitSnapshots_Down_ReturnNotExpressibleInLineUnitRaisesAndRollsBack()
    {
        var dozenLine = G(6, 5);
        await using var database = await CreateLegacyDatabaseAsync(postgres, $"""
            INSERT INTO "SalesInvoiceLines" ("Id", "SalesInvoiceId", "ProductId", "ProductUnitId", "Qty", "ConversionFactorToBase", "BaseQty", "UnitPrice", "TaxId", "TaxRatePercent",
              "Discount", "TaxableValue", "TaxAmount", "LineTotal", "CostOfGoodsSold")
            VALUES ('{dozenLine}', '{S1}', '{PPen}', '{PenDz}', 1, 12, 12, 100, '{TaxId}', 5, 0, 100, 5, 105, 120);
            """);
        await MigrateToAsync(database, M6);
        await InsertCrossUnitReturnAsync(database, dozenLine, PenPc, UPc, "pc", factor: 1, qty: 1, baseQty: 1, no: "SRT-0002");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(database, Previous));

        Assert.Contains("restore the snapshot", ex.MessageText);
        Assert.Contains("UnitCode", await ColumnsAsync(database, "SalesReturnLines"));
        Assert.Equal("1.000", (await QueryAsync(database, """SELECT "Qty"::text FROM "SalesReturnLines" WHERE "BaseQty" = 1""", r => r.GetString(0))).Single());
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'SaleCancellation'"""));
        await using var db = database.CreateDbContext();
        Assert.Contains(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>A line factor with more than 3 decimals cannot narrow to numeric(18,3): Down RAISES and changes nothing.</summary>
    [Fact]
    public async Task AddSalesUnitSnapshots_Down_FactorWithMoreThanThreeDecimalsRaisesAndRollsBack()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await MigrateToAsync(database, M6);
        // 2000 x 0.0005 = 1.000: arithmetic holds, but the factor needs 4 dp.
        await ExecuteAsync(database, $"""UPDATE "SalesInvoiceLines" SET "Qty" = 2000, "ConversionFactorToBase" = 0.0005, "BaseQty" = 1 WHERE "Id" = '{LB}'""");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(database, Previous));

        Assert.Contains("more than 3 decimals", ex.MessageText);
        Assert.Equal(28L, await ScalarAsync<long>(database, """SELECT numeric_precision FROM information_schema.columns WHERE table_name = 'SalesInvoiceLines' AND column_name = 'ConversionFactorToBase'"""));
        Assert.Contains("UnitCode", await ColumnsAsync(database, "SalesInvoiceLines"));
        await using var db = database.CreateDbContext();
        Assert.Contains(M6, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task AddSalesUnitSnapshots_DownThenUpAgain_ReBackfillsRelabelsAndVBlockIsEmpty()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = (await QueryAsync(database, V1Sql, r => r.GetString(0))).Single();
        await MigrateToAsync(database, M6);
        await InsertCrossUnitReturnAsync(database, LA, PenPc, UPc, "pc", factor: 1, qty: 3, baseQty: 3, no: "SRT-0002");
        // The return restored 3 pc into the batch the line consumed (what the service does): keeps V6 true.
        await ExecuteAsync(database, $"""UPDATE "SalesInvoiceLineBatchConsumptions" SET "ReturnedQtyBaseUnits" = 13 WHERE "SalesInvoiceLineId" = '{LA}'""");
        await MigrateToAsync(database, Previous);

        await MigrateToAsync(database, M6);

        // The cross-unit return came back as a return in its line's unit (0.3 Box): V4 and the arithmetic hold.
        await AssertVBlockEmptyAsync(database, "4/3/7");
        Assert.Equal("4/2/7", v1);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'SaleCancellation'"""));
        var mismatches = await QueryAsync(database, """
            SELECT r."Id"::text FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" l ON l."Id" = r."SalesInvoiceLineId"
            WHERE r."ProductUnitId" <> l."ProductUnitId" OR r."UnitId" <> l."UnitId" OR r."UnitCode" <> l."UnitCode" OR r."ConversionFactorToBase" <> l."ConversionFactorToBase"
            """, r => r.GetString(0));
        Assert.Empty(mismatches);
    }

    /// <summary>Applies the migration as the production idempotent script (plain connection, DO $EF$ wrapper) to legacy data,
    /// then runs it again: schema, data and history are unchanged.</summary>
    [Fact]
    public async Task IdempotentScript_ThroughSalesSnapshots_AppliesToLegacyDataAndSecondRunIsNoOp()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var v1 = (await QueryAsync(database, V1Sql, r => r.GetString(0))).Single();
        string script;
        await using (var db = database.CreateDbContext())
        {
            script = db.GetService<IMigrator>().GenerateScript(Previous, M6, MigrationsSqlGenerationOptions.Idempotent);
        }
        Assert.Contains("DO $EF$", script);
        Assert.Contains(M6, script);

        await ExecuteAsync(database, script);

        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, M6) > 0), await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(M6, await db.Database.GetAppliedMigrationsAsync());
        }
        await AssertVBlockEmptyAsync(database, v1);

        const string snapshot = """
            SELECT 'L ' || "Id" || ' ' || "UnitId" || ' ' || "UnitCode" || ' ' || "ConversionFactorToBase" || ' ' || "PriceOverridden" FROM "SalesInvoiceLines"
            UNION ALL SELECT 'R ' || "Id" || ' ' || "ProductUnitId" || ' ' || "UnitCode" || ' ' || "ConversionFactorToBase" FROM "SalesReturnLines"
            UNION ALL SELECT 'M ' || "Id" || ' ' || "Type" FROM "StockMovements"
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
    public async Task IdempotentScript_WithDiscountAboveGross_RaisesThroughWrapperAndLeavesSchemaUnchanged()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, $"""UPDATE "SalesInvoiceLines" SET "Discount" = 101 WHERE "Id" = '{LA}';""");
        string script;
        await using (var db = database.CreateDbContext())
        {
            script = db.GetService<IMigrator>().GenerateScript(Previous, M6, MigrationsSqlGenerationOptions.Idempotent);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, script));

        Assert.Contains("run P6", ex.MessageText);
        Assert.DoesNotContain("UnitCode", await ColumnsAsync(database, "SalesInvoiceLines"));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" WHERE "Type" = 'Sale' AND "QuantityBaseUnits" > 0"""));
        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(M6, await check.Database.GetAppliedMigrationsAsync());
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Guid> InsertCrossUnitReturnAsync(TestDatabase database, Guid lineId, Guid productUnitId, Guid unitId, string code, decimal factor, decimal qty, decimal baseQty, string no)
    {
        var header = Guid.NewGuid();
        var lineRow = Guid.NewGuid();
        await ExecuteAsync(database, $"""
            INSERT INTO "SalesReturns" ("Id", "ReturnNo", "SalesInvoiceId", "ReturnDate", "Reason", "TaxableTotal", "TaxTotal", "Total", "CreatedByUserId", "CreatedAt")
            VALUES ('{header}', '{no}', '{S1}', DATE '2026-09-05', NULL, 0, 0, 0, '{UserId}', now());
            INSERT INTO "SalesReturnLines" ("Id", "SalesReturnId", "SalesInvoiceLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase",
              "Qty", "BaseQty", "TaxableValue", "TaxAmount", "LineTotal", "CostRestored")
            VALUES ('{lineRow}', '{header}', '{lineId}', '{PPen}', '{productUnitId}', '{unitId}', '{code}', {factor}, {qty}, {baseQty}, 0, 0, 0, 0);
            """);
        return lineRow;
    }

    private static async Task<HashSet<string>> SchemaSignatureAsync(TestDatabase database)
    {
        const string tables = "'SalesInvoiceLines','SalesReturnLines','ProductUnits','Units'";
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
