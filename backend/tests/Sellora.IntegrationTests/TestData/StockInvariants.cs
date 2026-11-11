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

    /// <summary>Phase 8 (V3): per (location, product) QuantityOnHand differs from the sum of the movements stamped with that location
    /// (also: movements at a pair that has no StockItems row). Returns the offending location ids.</summary>
    public const string QohVsLedgerPerLocationSql = """
        SELECT s."StockLocationId" FROM "StockItems" s
        LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1, 2) m USING ("StockLocationId", "ProductId")
        WHERE s."QuantityOnHand" <> COALESCE(m.q, 0)
        UNION ALL
        SELECT m."StockLocationId" FROM "StockMovements" m LEFT JOIN "StockItems" s USING ("StockLocationId", "ProductId")
        WHERE s."ProductId" IS NULL GROUP BY m."StockLocationId", m."ProductId" HAVING SUM(m."QuantityBaseUnits") <> 0
        """;

    /// <summary>Phase 8 (V4): per (location, product) QuantityOnHand differs from the sum of the BatchLocationBalances there.</summary>
    public const string QohVsBalancesPerLocationSql = """
        SELECT s."StockLocationId" FROM "StockItems" s
        LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2) b USING ("StockLocationId", "ProductId")
        WHERE s."QuantityOnHand" <> COALESCE(b.q, 0)
        """;

    /// <summary>Phase 8 dual-write (V5): a batch's RemainingBaseQty differs from the sum of its balances over locations.</summary>
    public const string BatchVsBalancesSql = """
        SELECT pb."Id" FROM "PurchaseBatches" pb
        LEFT JOIN (SELECT "PurchaseBatchId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1) b ON b."PurchaseBatchId" = pb."Id"
        WHERE pb."RemainingBaseQty" <> COALESCE(b.q, 0)
        """;

    /// <summary>Phase 8 (V6): a balance whose denormalised product differs from its batch's product.</summary>
    public const string BalanceProductVsBatchSql = """
        SELECT bl."PurchaseBatchId" FROM "BatchLocationBalances" bl JOIN "PurchaseBatches" pb ON pb."Id" = bl."PurchaseBatchId" WHERE bl."ProductId" <> pb."ProductId"
        """;

    /// <summary>Phase 8a (owner O1 = A): any stock row or document that is NOT at its company's default store (the BranchStore of the IsDefault branch).
    /// Only valid until 8b lets documents post elsewhere.</summary>
    public const string NotAtDefaultStoreSql = """
        SELECT x."Id" FROM (
          SELECT "ProductId" AS "Id", "CompanyId", "StockLocationId" AS loc FROM "StockItems"
          UNION ALL SELECT "Id", "CompanyId", "StockLocationId" FROM "StockMovements"
          UNION ALL SELECT "Id", "CompanyId", "OriginStockLocationId" FROM "PurchaseBatches"
          UNION ALL SELECT "PurchaseBatchId", "CompanyId", "StockLocationId" FROM "BatchLocationBalances"
          UNION ALL SELECT "Id", "CompanyId", "StockLocationId" FROM "PurchaseOrders"
          UNION ALL SELECT "Id", "CompanyId", "StockLocationId" FROM "PurchaseReturns"
          UNION ALL SELECT "Id", "CompanyId", "StockLocationId" FROM "SalesInvoices"
          UNION ALL SELECT "Id", "CompanyId", "StockLocationId" FROM "SalesReturns"
          UNION ALL SELECT "Id", "CompanyId", "StockLocationId" FROM "StockAdjustments") x
        JOIN (SELECT b."CompanyId", l."Id" AS loc FROM "Branches" b
              JOIN "StockLocations" l ON l."CompanyId" = b."CompanyId" AND l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore' WHERE b."IsDefault") d ON d."CompanyId" = x."CompanyId"
        WHERE x.loc <> d.loc
        """;

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["QOH vs ledger"] = QohVsLedgerSql,
        ["QOH vs batches"] = QohVsBatchesSql,
        ["Sales line arithmetic"] = SalesLineArithmeticSql,
        ["Purchase line arithmetic"] = PurchaseLineArithmeticSql,
    };

    /// <summary>The Phase 8 invariants; only valid on a schema that has the location dimension (B9 onwards).</summary>
    public static readonly IReadOnlyDictionary<string, string> PerLocation = new Dictionary<string, string>
    {
        ["QOH vs ledger per location"] = QohVsLedgerPerLocationSql,
        ["QOH vs balances per location"] = QohVsBalancesPerLocationSql,
        ["Batch remaining vs balances"] = BatchVsBalancesSql,
        ["Balance product vs batch product"] = BalanceProductVsBatchSql,
    };

    /// <summary>True when the database has the Phase 8 location dimension (older-schema migration tests also use these helpers).</summary>
    public static async Task<bool> HasLocationDimensionAsync(SelloraDbContext db)
    {
        var ids = await QueryIdsAsync(db, """
            SELECT '00000000-0000-0000-0000-000000000001'::uuid FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'StockItems' AND column_name = 'StockLocationId'
            """);
        return ids.Count > 0;
    }

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

    /// <summary>Phase 8a: nothing sits outside the default store (rows + documents). Fails with the offending ids.</summary>
    public static async Task AssertAllAtDefaultStoreAsync(SelloraDbContext db)
    {
        Assert.True(await HasLocationDimensionAsync(db), "The database has no stock location dimension.");
        var ids = await QueryIdsAsync(db, NotAtDefaultStoreSql);
        Assert.True(ids.Count == 0, "Rows outside the default store (8a posts everything there): " + string.Join(", ", ids));
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
        if (await HasLocationDimensionAsync(db))
        {
            foreach (var (name, sql) in PerLocation)
            {
                var ids = await QueryIdsAsync(db, sql);
                if (ids.Count > 0) violations[name] = ids;
            }
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
