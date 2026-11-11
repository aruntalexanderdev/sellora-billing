using System.Text.RegularExpressions;

namespace Sellora.Tests.Infrastructure;

/// <summary>
/// T4 (second half): raw SQL bypasses the EF query filter, so every raw statement in production code that touches a company
/// table must state <c>"CompanyId"</c> itself. Source scan over Sellora.Infrastructure (except migrations) and Sellora.Api.
/// Platform tables (Units, Currencies, Users, …) are exempt.
/// </summary>
public class RawSqlCompanyPredicateTests
{
    private static readonly string[] CompanyTables =
    [
        "Taxes", "ExchangeRates", "Suppliers", "Customers", "ExpenseCategories", "Expenses", "FinancialAccounts", "FinancialTransactions",
        "Products", "ProductUnits", "PurchaseOrders", "PurchaseReturns", "PurchaseBatches", "SalesInvoices", "SalesReturns",
        "StockAdjustments", "StockItems", "StockMovements", "IdempotencyKeys", "BusinessSettings", "DocumentSequences",
    ];

    private static readonly Regex RawSqlCall = new(@"\b(FromSql\w*|SqlQuery\w*|SqlQueryRaw|ExecuteSql\w*|CommandText)\b", RegexOptions.Compiled);
    private static readonly Regex TableRef = new(@"\\?""(?<t>[A-Za-z]+)\\?""", RegexOptions.Compiled);

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Sellora.Infrastructure"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("backend/src not found") : Path.Combine(dir.FullName, "src");
    }

    /// <summary>Raw-SQL statements (call site up to the terminating semicolon) that reference a company table.</summary>
    internal static List<(string Text, string Table, bool HasCompanyId)> Scan(string source)
    {
        var found = new List<(string, string, bool)>();
        foreach (Match call in RawSqlCall.Matches(source))
        {
            var end = source.IndexOf(';', call.Index);
            var statement = end < 0 ? source[call.Index..] : source[call.Index..end];
            var tables = TableRef.Matches(statement).Select(m => m.Groups["t"].Value).Where(CompanyTables.Contains).Distinct().ToList();
            foreach (var table in tables) found.Add((statement, table, statement.Contains("CompanyId")));
        }
        return found;
    }

    [Fact]
    public void ProductionRawSql_OnCompanyTables_AlwaysStatesCompanyId()
    {
        var root = SrcRoot();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var statements = files.SelectMany(f => Scan(File.ReadAllText(f)).Select(s => (File: Path.GetRelativePath(root, f), s.Table, s.HasCompanyId, s.Text))).ToList();

        Assert.True(statements.Count >= 6, $"The scan found only {statements.Count} raw statements on company tables; the detector is probably broken.");
        var offenders = statements.Where(s => !s.HasCompanyId).Select(s => $"{s.File} ({s.Table}): {s.Text.ReplaceLineEndings(" ")[..Math.Min(120, s.Text.Length)]}").ToList();
        Assert.True(offenders.Count == 0, "Raw SQL on a company table without a \"CompanyId\" predicate:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Detector_FlagsRawSqlWithoutCompanyIdAndAcceptsOneWithIt()
    {
        const string bad = "db.Products.FromSqlInterpolated($\"SELECT * FROM \\\"Products\\\" WHERE \\\"Id\\\" = {id} FOR UPDATE\").ToList();";
        const string good = "db.Products.FromSqlInterpolated($\"SELECT * FROM \\\"Products\\\" WHERE \\\"CompanyId\\\" = {c} AND \\\"Id\\\" = {id} FOR UPDATE\").ToList();";
        const string platform = "db.Units.FromSqlInterpolated($\"SELECT * FROM \\\"Units\\\" WHERE \\\"Id\\\" = {id} FOR UPDATE\").ToList();";

        Assert.False(Assert.Single(Scan(bad)).HasCompanyId);
        Assert.True(Assert.Single(Scan(good)).HasCompanyId);
        Assert.Empty(Scan(platform));
    }
}
