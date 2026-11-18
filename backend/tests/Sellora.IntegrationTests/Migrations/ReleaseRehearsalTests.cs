using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using Xunit.Abstractions;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.LegacyRetailSeed;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 8 (release &amp; migration) full rehearsal of the production path (D10, analysis 22.7 / 26 #8): origin/main baseline with realistic legacy
/// business data -> scripts/sql/unit-restructuring-profile.sql -> ONE idempotent script baseline -> head (psql, ON_ERROR_STOP) -> the same script again
/// (no-op) -> the four validation scripts (every query zero rows) -> historical quantities, prices and totals unchanged -> seed-permissions.sql twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReleaseRehearsalTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private const string Baseline = "20260920114635_AddIdempotencyKeysAndCancelledBy";

    // Deployable scripts live under backend/db (applied by Sellora.DbMigrator); manual/operator ones stay in scripts/sql.
    private static string SqlFile(string name)
    {
        string[] relative = name switch
        {
            "seed-permissions.sql" => ["backend", "db", "repeatable", "R__" + name],
            "unit-restructuring-profile.sql" => ["scripts", "sql", name],
            _ => ["backend", "db", "validation", "V__" + name],
        };
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"{string.Join('/', relative)} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>Splits a read-only report script into its statements (a statement ends with a line ending in ';'; full-line comments dropped) keyed by its first <c>'ID' AS chk</c>.</summary>
    private static List<(string Id, string Sql)> Statements(string file)
    {
        var result = new List<(string, string)>();
        var current = new List<string>();
        foreach (var line in SqlFile(file).Split('\n'))
        {
            if (line.TrimStart().StartsWith("--")) continue;
            if (string.IsNullOrWhiteSpace(line) && current.Count == 0) continue;
            current.Add(line);
            if (!line.TrimEnd().EndsWith(';')) continue;
            var sql = string.Join('\n', current);
            var id = Regex.Match(sql, @"'([\w-]+)'\s+AS\s+chk", RegexOptions.IgnoreCase);
            result.Add((id.Success ? id.Groups[1].Value : $"stmt#{result.Count + 1}", sql));
            current.Clear();
        }
        Assert.Empty(current);
        return result;
    }

    private static async Task<List<string[]>> RowsAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i))!).ToArray());
        return rows;
    }

    /// <summary>Runs every statement of a validation script; each must return zero rows. Returns the statement ids that ran.</summary>
    private async Task<List<string>> AssertZeroRowsAsync(TestDatabase database, string file, int minimumStatements)
    {
        var statements = Statements(file);
        Assert.True(statements.Count >= minimumStatements, $"{file}: only {statements.Count} statements parsed (expected >= {minimumStatements}).");
        foreach (var (id, sql) in statements)
        {
            var rows = await RowsAsync(database, sql);
            Assert.True(rows.Count == 0, $"{file} {id} returned {rows.Count} row(s): " + string.Join(" | ", rows.Take(5).Select(r => string.Join(",", r))));
        }
        output.WriteLine($"{file}: {statements.Count} queries, all zero rows");
        return statements.Select(s => s.Id).ToList();
    }

    private static string T(string column) => $"COALESCE(trim_scale(\"{column}\")::text, '-')";

    /// <summary>Everything the release must leave untouched, expressed in columns that exist before and after the release (numerics scale-trimmed:
    /// WidenCostPrecision changes the scale, never the value). One string per row; ordering is by the string itself.</summary>
    private static readonly string[] BeforeAfterQueries =
    [
        $"""SELECT 'price ' || "Id" || ' ' || {T("Price")} || ' ' || "EffectiveFrom"::text || ' ' || COALESCE("EffectiveTo"::text, '-') FROM "SellingPrices" """,
        """SELECT 'count SellingPrices ' || COUNT(*) FROM "SellingPrices" """,
        """SELECT 'count PurchaseOrderLines ' || COUNT(*) FROM "PurchaseOrderLines" """,
        """SELECT 'count PurchaseReturnLines ' || COUNT(*) FROM "PurchaseReturnLines" """,
        """SELECT 'count SalesInvoiceLines ' || COUNT(*) FROM "SalesInvoiceLines" """,
        """SELECT 'count SalesReturnLines ' || COUNT(*) FROM "SalesReturnLines" """,
        """SELECT 'count StockAdjustments ' || COUNT(*) FROM "StockAdjustments" """,
        """SELECT 'count StockMovements ' || COUNT(*) FROM "StockMovements" """,
        """SELECT 'count PurchaseBatches ' || COUNT(*) FROM "PurchaseBatches" """,
        """SELECT 'count Consumptions ' || COUNT(*) FROM "SalesInvoiceLineBatchConsumptions" """,
        """SELECT 'count Products ' || COUNT(*) FROM "Products" """,
        """SELECT 'count Users ' || COUNT(*) FROM "Users" """,
        """SELECT 'count BusinessSettings ' || COUNT(*) FROM "BusinessSettings" """,
        // sum of base quantities per product, per document kind
        """SELECT 'baseqty purchase ' || "ProductId" || ' ' || trim_scale(SUM("BaseQty"))::text FROM "PurchaseOrderLines" GROUP BY "ProductId" """,
        """SELECT 'baseqty purchase-return ' || "ProductId" || ' ' || trim_scale(SUM("BaseQty"))::text FROM "PurchaseReturnLines" GROUP BY "ProductId" """,
        """SELECT 'baseqty sales ' || "ProductId" || ' ' || trim_scale(SUM("BaseQty"))::text FROM "SalesInvoiceLines" GROUP BY "ProductId" """,
        """SELECT 'baseqty sales-return ' || "ProductId" || ' ' || trim_scale(SUM("BaseQty"))::text FROM "SalesReturnLines" GROUP BY "ProductId" """,
        """SELECT 'baseqty adjustment ' || "ProductId" || ' ' || trim_scale(SUM("QuantityBaseUnits"))::text FROM "StockAdjustments" GROUP BY "ProductId" """,
        """SELECT 'baseqty movements ' || "ProductId" || ' ' || trim_scale(SUM("QuantityBaseUnits"))::text FROM "StockMovements" GROUP BY "ProductId" """,
        """SELECT 'baseqty batches ' || "ProductId" || ' ' || trim_scale(SUM("PurchasedBaseQty"))::text || ' ' || trim_scale(SUM("RemainingBaseQty"))::text FROM "PurchaseBatches" GROUP BY "ProductId" """,
        // QOH per product (one row per product before Phase B and after: a single default location)
        """SELECT 'qoh ' || "ProductId" || ' ' || trim_scale(SUM("QuantityOnHand"))::text FROM "StockItems" GROUP BY "ProductId" """,
        // line rows
        $"""SELECT 'PL ' || "Id" || ' ' || "ProductId" || ' ' || {T("Qty")} || ' ' || {T("ConversionFactorToBase")} || ' ' || {T("BaseQty")} || ' ' || {T("Rate")} || ' ' || {T("TaxableValue")} || ' ' || {T("TaxAmount")} || ' ' || {T("LineTotal")} || ' ' || {T("UnitCost")} FROM "PurchaseOrderLines" """,
        $"""SELECT 'PRL ' || "Id" || ' ' || {T("Qty")} || ' ' || {T("BaseQty")} || ' ' || {T("LineTotal")} || ' ' || {T("CostRemoved")} FROM "PurchaseReturnLines" """,
        $"""SELECT 'SL ' || "Id" || ' ' || "ProductId" || ' ' || {T("Qty")} || ' ' || {T("ConversionFactorToBase")} || ' ' || {T("BaseQty")} || ' ' || {T("UnitPrice")} || ' ' || {T("Discount")} || ' ' || {T("TaxableValue")} || ' ' || {T("TaxAmount")} || ' ' || {T("LineTotal")} || ' ' || {T("CostOfGoodsSold")} FROM "SalesInvoiceLines" """,
        $"""SELECT 'SRL ' || "Id" || ' ' || {T("Qty")} || ' ' || {T("BaseQty")} || ' ' || {T("LineTotal")} || ' ' || {T("CostRestored")} FROM "SalesReturnLines" """,
        $"""SELECT 'ADJ ' || "Id" || ' ' || "ProductId" || ' ' || {T("QuantityBaseUnits")} || ' ' || {T("UnitCost")} || ' ' || {T("BeforeQty")} || ' ' || {T("AfterQty")} FROM "StockAdjustments" """,
        $"""SELECT 'MOV ' || "Id" || ' ' || "ProductId" || ' ' || {T("QuantityBaseUnits")} || ' ' || COALESCE("PurchaseBatchId"::text, '-') FROM "StockMovements" """,
        $"""SELECT 'BATCH ' || "Id" || ' ' || {T("PurchasedBaseQty")} || ' ' || {T("RemainingBaseQty")} || ' ' || {T("UnitCost")} FROM "PurchaseBatches" """,
        $"""SELECT 'CONS ' || "Id" || ' ' || "SalesInvoiceLineId" || ' ' || "PurchaseBatchId" || ' ' || {T("QtyBaseUnits")} || ' ' || {T("UnitCost")} || ' ' || {T("ReturnedQtyBaseUnits")} FROM "SalesInvoiceLineBatchConsumptions" """,
        // document totals
        $"""SELECT 'PO ' || "Id" || ' ' || "PurchaseNo" || ' ' || {T("Subtotal")} || ' ' || {T("TaxTotal")} || ' ' || {T("OtherChargesTotal")} || ' ' || {T("DiscountTotal")} || ' ' || {T("GrandTotal")} || ' ' || {T("PaidAmount")} || ' ' || {T("ReturnedAmount")} || ' ' || "Status" FROM "PurchaseOrders" """,
        $"""SELECT 'PR ' || "Id" || ' ' || "ReturnNo" || ' ' || {T("TaxableTotal")} || ' ' || {T("TaxTotal")} || ' ' || {T("Total")} || ' ' || {T("OriginalTotal")} FROM "PurchaseReturns" """,
        $"""SELECT 'SI ' || "Id" || ' ' || "InvoiceNo" || ' ' || {T("Subtotal")} || ' ' || {T("TaxTotal")} || ' ' || {T("DiscountTotal")} || ' ' || {T("GrandTotal")} || ' ' || {T("PaidAmount")} || ' ' || {T("ReturnedAmount")} || ' ' || "Status" FROM "SalesInvoices" """,
        $"""SELECT 'SR ' || "Id" || ' ' || "ReturnNo" || ' ' || {T("TaxableTotal")} || ' ' || {T("TaxTotal")} || ' ' || {T("Total")} FROM "SalesReturns" """,
        // stock cost state
        $"""SELECT 'SIT ' || "ProductId" || ' ' || {T("AverageCost")} FROM "StockItems" """,
    ];

    private static async Task<List<string>> BeforeAfterAsync(TestDatabase database)
    {
        var all = new List<string>();
        foreach (var sql in BeforeAfterQueries) all.AddRange((await RowsAsync(database, sql)).Select(r => r[0]));
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    [Fact]
    public async Task Rehearsal_LegacyProductionCopy_ProfileThenOneIdempotentScriptTwiceThenValidationScripts_AllClean_HistoryUnchanged()
    {
        var total = Stopwatch.StartNew();
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);

        // 1. Production baseline (= origin/main) + legacy business data.
        var sw = Stopwatch.StartNew();
        string script;
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Baseline);
            script = db.GetService<IMigrator>().GenerateScript(Baseline, null, MigrationsSqlGenerationOptions.Idempotent);
        }
        await ExecuteAsync(database, Sql);
        output.WriteLine($"baseline + seed + script generation: {sw.ElapsedMilliseconds} ms; idempotent script: {script.Split('\n').Length} lines, {script.Length} chars");
        Assert.Contains("20261104121850_IntroduceProductUnits", script);
        Assert.Contains("20261111040145_AddStockTransfers", script);
        Assert.Equal(["20260920114635_AddIdempotencyKeysAndCancelledBy"], (await AppliedAsync(database)).Where(m => string.CompareOrdinal(m, Baseline) >= 0).ToList());

        // 2. Pre-release profile (analysis 22.1): informative counts, P9* guard-copy queries and P10 reconciliation return zero rows.
        sw.Restart();
        var profile = Statements("unit-restructuring-profile.sql");
        Assert.True(profile.Count >= 18, $"profile statements parsed: {profile.Count}");
        var profileRows = new Dictionary<string, List<string[]>>();
        foreach (var (id, sql) in profile)
        {
            var rows = await RowsAsync(database, sql);
            profileRows[id] = rows;
            if (id.StartsWith("P9") && id != "P9g" || id.StartsWith("P10") || id == "P3")
                Assert.True(rows.Count == 0, $"profile {id} returned {rows.Count} row(s): " + string.Join(" | ", rows.Take(5).Select(r => string.Join(",", r))));
        }
        Assert.Equal([PSpice.ToString()], profileRows["P2"].Select(r => r[1])); // Turmeric: the only product without a factor-1 mode
        Assert.Equal("1", profileRows["P9g"].Single()[1]);                       // ... and it has an adjustment
        Assert.Equal("1", profileRows["P8"].Single()[1]);                        // single BusinessSettings row
        var p7 = profileRows["P7"].Single();                                     // selling_prices, modes, purchase lines, pr lines, sales lines, sr lines, adjustments, movements, stock items, batches
        Assert.Equal(["11", "11", "5", "1", "4", "1", "1", "13", "4", "6"], p7.Skip(1).ToArray());
        output.WriteLine($"profile: {profile.Count} queries in {sw.ElapsedMilliseconds} ms");

        var before = await BeforeAfterAsync(database);
        Assert.Contains(before, r => r == "qoh " + PRice + " 45");
        Assert.Contains(before, r => r == "qoh " + PSpice + " 1.75");
        Assert.Contains(before, r => r == "count SellingPrices 11");
        var modesBefore = long.Parse(p7[2]);

        // 3. The one idempotent script, as an operator runs it (psql -v ON_ERROR_STOP=1 -f).
        sw.Restart();
        var first = await postgres.RunPsqlScriptAsync(database.Name, script);
        output.WriteLine($"script run 1: {sw.ElapsedMilliseconds} ms, exit {first.ExitCode}");
        Assert.True(first.ExitCode == 0, "first run failed:\n" + first.Stderr + "\n" + first.Stdout);
        await using (var db = database.CreateDbContext())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var afterFirstSchema = await SchemaSignatureAsync(database);
        var afterFirstHistory = await AppliedAsync(database);
        var afterFirstData = await BeforeAfterAsync(database);
        var afterFirstUnits = await RowsAsync(database, """SELECT "Id"::text || ' ' || "UnitId" || ' ' || COALESCE("InnerProductUnitId"::text, '-') || ' ' || "InnerQuantity" || ' ' || "ConversionFactorToBase" || ' ' || "IsBaseUnit" FROM "ProductUnits" ORDER BY 1""");
        var afterFirstSeq = await RowsAsync(database, """SELECT COUNT(*)::text FROM "StockLocations" UNION ALL SELECT COUNT(*)::text FROM "BatchLocationBalances" UNION ALL SELECT COUNT(*)::text FROM "Branches" UNION ALL SELECT COUNT(*)::text FROM "Companies" """);

        // 4. Second run: no-op, no error.
        sw.Restart();
        var second = await postgres.RunPsqlScriptAsync(database.Name, script);
        output.WriteLine($"script run 2: {sw.ElapsedMilliseconds} ms, exit {second.ExitCode}");
        Assert.True(second.ExitCode == 0, "second run failed:\n" + second.Stderr + "\n" + second.Stdout);
        Assert.Equal(afterFirstHistory, await AppliedAsync(database));
        Assert.True(afterFirstSchema.SetEquals(await SchemaSignatureAsync(database)), "schema changed on the second run");
        Assert.Equal(afterFirstData, await BeforeAfterAsync(database));
        Assert.Equal(afterFirstUnits.Select(r => r[0]), (await RowsAsync(database, """SELECT "Id"::text || ' ' || "UnitId" || ' ' || COALESCE("InnerProductUnitId"::text, '-') || ' ' || "InnerQuantity" || ' ' || "ConversionFactorToBase" || ' ' || "IsBaseUnit" FROM "ProductUnits" ORDER BY 1""")).Select(r => r[0]));
        Assert.Equal(afterFirstSeq.Select(r => r[0]), (await RowsAsync(database, """SELECT COUNT(*)::text FROM "StockLocations" UNION ALL SELECT COUNT(*)::text FROM "BatchLocationBalances" UNION ALL SELECT COUNT(*)::text FROM "Branches" UNION ALL SELECT COUNT(*)::text FROM "Companies" """)).Select(r => r[0]));

        // 5. Validation scripts: every query zero rows.
        sw.Restart();
        var unitIds = await AssertZeroRowsAsync(database, "unit-restructuring-validation.sql", 12);
        Assert.Contains("U12-open", unitIds);
        var stockIds = await AssertZeroRowsAsync(database, "stock-reconciliation.sql", 8);
        Assert.Contains("S1", stockIds);
        await AssertZeroRowsAsync(database, "stock-transfers-validation.sql", 8);
        await AssertZeroRowsAsync(database, "van-operations-validation.sql", 3);
        output.WriteLine($"validation scripts: {sw.ElapsedMilliseconds} ms");

        // 6. Zero change to history (analysis 22.7 / 26 #8).
        Assert.True(before.SequenceEqual(afterFirstData), "history changed by the release:\n-" + string.Join("\n-", before.Except(afterFirstData)) + "\n+" + string.Join("\n+", afterFirstData.Except(before)));
        Assert.Equal(modesBefore + 1, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ProductUnits" """)); // + a new base row for Turmeric
        Assert.Equal(11L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "SellingPrices" """));
        // the one documented relabel (AddMovementTypesAndReasonCode B3): Adjustment + OpeningStock reason -> OpeningStock; every other legacy type is untouched
        Assert.Equal(["OpeningStock=1", "Purchase=5", "PurchaseReturn=1", "Sale=5", "SaleReturn=1"],
            (await RowsAsync(database, """SELECT "Type" || '=' || COUNT(*) FROM "StockMovements" GROUP BY "Type" ORDER BY 1""")).Select(r => r[0]).ToList());
        // legacy rows keep their ids (history immutable), every legacy snapshot is stamped
        Assert.Equal(new[] { MRiceBag, MPenBox, MSpiceGram, MRiceLoose }.Select(g => g.ToString()),
            (await RowsAsync(database, """SELECT "ProductUnitId"::text FROM "SalesInvoiceLines" ORDER BY "Id" """)).Select(r => r[0]));
        Assert.Equal(new[] { MRiceBag, MPenBox, MOilBottle, MRiceLoose, MPenCarton }.Select(g => g.ToString()),
            (await RowsAsync(database, """SELECT "ProductUnitId"::text FROM "PurchaseOrderLines" ORDER BY "Id" """)).Select(r => r[0]));

        // 7. seed-permissions.sql twice (idempotent), after the migration.
        sw.Restart();
        var permissions = SqlFile("seed-permissions.sql");
        await ExecuteAsync(database, permissions);
        var ids = await RowsAsync(database, """SELECT "Id"::text || ':' || "Code" FROM "Permissions" ORDER BY "Code" """);
        await ExecuteAsync(database, permissions);
        Assert.Equal(ids.Select(r => r[0]), (await RowsAsync(database, """SELECT "Id"::text || ':' || "Code" FROM "Permissions" ORDER BY "Code" """)).Select(r => r[0]));
        Assert.Equal(PermissionCatalog.All.Count, ids.Count);
        Assert.Empty(await RowsAsync(database, $"""SELECT c FROM unnest(ARRAY[{string.Join(",", PermissionCatalog.All.Select(p => $"'{p.Code.Replace("'", "''")}'"))}]) c WHERE c NOT IN (SELECT "Code" FROM "Permissions")"""));
        output.WriteLine($"seed-permissions x2: {sw.ElapsedMilliseconds} ms; rehearsal total {total.ElapsedMilliseconds} ms");

        // 8. Head model reads the migrated legacy data (EF filters on CompanyId, product units, stock).
        await using (var db = database.CreateDbContext())
        {
            await ProductUnitValidation.AssertHoldAsync(db);
            await UnitMasterValidation.AssertHoldAsync(db);
            await StockInvariants.AssertHoldAsync(db);
        }
    }
}
