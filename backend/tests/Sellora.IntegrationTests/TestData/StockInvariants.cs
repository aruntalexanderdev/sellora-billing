using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Stock reconciliation queries from <c>.claude/skills/stock-ledger-invariants/SKILL.md</c>. Each returns the ids
/// of offending rows; a consistent database returns none from any of them.
/// </summary>
public static class StockInvariants
{
    /// <summary>Products whose QuantityOnHand differs from the sum of their stock movements.</summary>
    public const string QohVsLedgerSql = """
        SELECT s."ProductId" FROM "StockItems" s
        LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
        WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
        """;

    /// <summary>Products whose QuantityOnHand differs from the sum of their batches' remaining quantity.</summary>
    public const string QohVsBatchesSql = """
        SELECT s."ProductId" FROM "StockItems" s
        LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
        WHERE s."QuantityOnHand" <> COALESCE(b.q, 0)
        """;

    /// <summary>Sales invoice lines whose BaseQty is not Qty × ConversionFactorToBase.</summary>
    public const string SalesLineArithmeticSql = """
        SELECT "Id" FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty"
        """;

    /// <summary>Purchase lines whose BaseQty is not Qty × ConversionFactorToBase.</summary>
    public const string PurchaseLineArithmeticSql = """
        SELECT "Id" FROM "PurchaseOrderLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty"
        """;

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["QOH vs ledger"] = QohVsLedgerSql,
        ["QOH vs batches"] = QohVsBatchesSql,
        ["Sales line arithmetic"] = SalesLineArithmeticSql,
        ["Purchase line arithmetic"] = PurchaseLineArithmeticSql,
    };

    public static async Task<IReadOnlyList<Guid>> QueryIdsAsync(SelloraDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
            return ids;
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Runs every invariant query; returns only those with offending rows (name → ids).</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindViolationsAsync(SelloraDbContext db)
    {
        var violations = new Dictionary<string, IReadOnlyList<Guid>>();
        foreach (var (name, sql) in All)
        {
            var ids = await QueryIdsAsync(db, sql);
            if (ids.Count > 0) violations[name] = ids;
        }
        return violations;
    }

    /// <summary>Fails with the offending invariant names and ids if any invariant query returns rows.</summary>
    public static async Task AssertHoldAsync(SelloraDbContext db)
    {
        var violations = await FindViolationsAsync(db);
        Assert.True(violations.Count == 0,
            "Stock invariants violated: " + string.Join("; ", violations.Select(v => $"{v.Key}: {string.Join(", ", v.Value)}")));
    }
}
