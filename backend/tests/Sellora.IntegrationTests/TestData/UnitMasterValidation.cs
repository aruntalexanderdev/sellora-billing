using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Post-deploy validation queries V1–V3 from the header of migration <c>20261104105655_ExtendUnitMaster</c>
/// (copied verbatim). Each returns the offending unit ids; a correctly migrated database returns none.
/// </summary>
public static class UnitMasterValidation
{
    /// <summary>V1 unmapped category (any value outside the new enum).</summary>
    public const string V1UnmappedCategorySql = """
        SELECT "Id", "Name", "ShortCode", "MeasurementCategory" FROM "Units"
        WHERE "MeasurementCategory" NOT IN ('Count','Weight','Volume','Length','Packaging')
        """;

    /// <summary>V2 Packaging unit used as a product base unit.</summary>
    public const string V2PackagingBaseUnitSql = """
        SELECT u."Id", u."ShortCode", p."Id" AS "ProductId", p."Sku" FROM "Units" u
        JOIN "Products" p ON p."BaseUnitId" = u."Id"
        WHERE u."MeasurementCategory" = 'Packaging'
        """;

    /// <summary>V3 Count/Packaging with non-zero decimals.</summary>
    public const string V3WholeCategoryDecimalsSql = """
        SELECT "Id", "ShortCode", "MeasurementCategory", "DecimalPlaces" FROM "Units"
        WHERE "MeasurementCategory" IN ('Count','Packaging') AND "DecimalPlaces" <> 0
        """;

    /// <summary>V4 review list (not errors): containers kept as Count, sub-units kept at 3 dp because they are a base
    /// unit, measurable units without a StandardFactor. First column is the unit id.</summary>
    public const string V4ReviewListSql = """
        SELECT u."Id", u."Name", u."ShortCode", u."MeasurementCategory", u."DecimalPlaces", u."StandardFactor",
               CASE WHEN u."MeasurementCategory" = 'Count' THEN 'container kept as Count (used as base unit)'
                    WHEN u."StandardFactor" IS NULL THEN 'measurable unit without StandardFactor'
                    ELSE 'sub-unit kept at 3 dp because it is a base unit' END AS "Reason"
        FROM "Units" u
        WHERE (u."MeasurementCategory" = 'Count'
               AND (lower(u."ShortCode") IN ('box','hctn','fctn','ctn','carton','bag','pack','pkt','case','crate','bottle','jar','tin','can')
                    OR lower(u."Name") ~ '(box|carton|bag|pack|case|crate|bottle|jar|tin|can)'))
           OR (u."MeasurementCategory" IN ('Weight','Volume','Length') AND u."StandardFactor" IS NULL)
           OR (lower(u."ShortCode") IN ('g','gm','mg','ml') AND u."DecimalPlaces" <> 0
               AND EXISTS (SELECT 1 FROM "Products" p WHERE p."BaseUnitId" = u."Id"))
        ORDER BY "Reason", u."ShortCode"
        """;

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["V1 unmapped category"] = V1UnmappedCategorySql,
        ["V2 packaging base unit"] = V2PackagingBaseUnitSql,
        ["V3 whole category with decimals"] = V3WholeCategoryDecimalsSql,
    };

    /// <summary>Fails with the offending query names and ids if any of V1–V3 returns rows.</summary>
    public static async Task AssertHoldAsync(SelloraDbContext db)
    {
        var violations = new List<string>();
        foreach (var (name, sql) in All)
        {
            var ids = await StockInvariants.QueryIdsAsync(db, sql);
            if (ids.Count > 0) violations.Add($"{name}: {string.Join(", ", ids)}");
        }
        Assert.True(violations.Count == 0, "Unit Master validation queries returned rows: " + string.Join("; ", violations));
    }
}
