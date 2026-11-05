using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Post-deploy validation queries V1–V5 from the header of migration <c>20261104121850_IntroduceProductUnits</c>
/// (copied verbatim). Each returns the offending rows; a correctly migrated database returns none.
/// </summary>
public static class ProductUnitValidation
{
    /// <summary>V1 not exactly one base row.</summary>
    public const string V1NotExactlyOneBaseSql = """
        SELECT p."Id" FROM "Products" p WHERE (SELECT COUNT(*) FROM "ProductUnits" x WHERE x."ProductId" = p."Id" AND x."IsBaseUnit") <> 1;
        """;

    /// <summary>V2 base row unit differs from the product base unit.</summary>
    public const string V2BaseUnitMismatchSql = """
        SELECT x."Id" FROM "ProductUnits" x JOIN "Products" p ON p."Id" = x."ProductId" WHERE x."IsBaseUnit" AND x."UnitId" <> p."BaseUnitId";
        """;

    /// <summary>V3 factor parity along the chain.</summary>
    public const string V3FactorParitySql = """
        SELECT x."Id" FROM "ProductUnits" x JOIN "ProductUnits" i ON i."Id" = x."InnerProductUnitId"
        WHERE i."ProductId" <> x."ProductId" OR x."ConversionFactorToBase" <> x."InnerQuantity" * i."ConversionFactorToBase";
        """;

    /// <summary>V4 duplicate active unit per product.</summary>
    public const string V4DuplicateActiveUnitSql = """
        SELECT "ProductId", "UnitId" FROM "ProductUnits" WHERE "IsActive" GROUP BY 1, 2 HAVING COUNT(*) > 1;
        """;

    /// <summary>V5 line/price references resolve and belong to the same product.</summary>
    public const string V5ReferencesResolveSql = """
        SELECT 'PO', l."Id" FROM "PurchaseOrderLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" WHERE x."Id" IS NULL OR x."ProductId" <> l."ProductId"
        UNION ALL SELECT 'SI', l."Id" FROM "SalesInvoiceLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" WHERE x."Id" IS NULL OR x."ProductId" <> l."ProductId"
        UNION ALL SELECT 'SP', s."Id" FROM "SellingPrices" s LEFT JOIN "ProductUnits" x ON x."Id" = s."ProductUnitId" WHERE x."Id" IS NULL;
        """;

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["V1 not exactly one base row"] = V1NotExactlyOneBaseSql,
        ["V2 base unit mismatch"] = V2BaseUnitMismatchSql,
        ["V3 factor parity"] = V3FactorParitySql,
        ["V4 duplicate active unit"] = V4DuplicateActiveUnitSql,
        ["V5 references resolve"] = V5ReferencesResolveSql,
    };

    /// <summary>Fails with the offending query names and rows if any of V1–V5 (and, unless disabled, the stock
    /// reconciliation queries = V6) returns rows.</summary>
    public static async Task AssertHoldAsync(SelloraDbContext db, bool includeStockInvariants = true)
    {
        var queries = All.AsEnumerable();
        if (includeStockInvariants) queries = queries.Concat(StockInvariants.All.Select(q => KeyValuePair.Create("V6 " + q.Key, q.Value)));

        var violations = new List<string>();
        foreach (var (name, sql) in queries)
        {
            var rows = await QueryRowsAsync(db, sql);
            if (rows.Count > 0) violations.Add($"{name}: {string.Join(", ", rows)}");
        }
        Assert.True(violations.Count == 0, "Product unit validation queries returned rows: " + string.Join("; ", violations));
    }

    /// <summary>Runs <paramref name="sql"/> and renders each row as "col1|col2|...".</summary>
    public static async Task<IReadOnlyList<string>> QueryRowsAsync(SelloraDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++) values[i] = Convert.ToString(reader.GetValue(i)) ?? "";
                rows.Add(string.Join("|", values));
            }
            return rows;
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }
}
