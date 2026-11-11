using System.Text.RegularExpressions;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 8a architecture guard (design D5): <c>StockPosting</c> (with <c>BatchAllocator</c>) is the only code that mutates
/// <c>StockItem.QuantityOnHand</c>, <c>StockItem.AverageCost</c>, <c>PurchaseBatch.RemainingBaseQty</c> and the
/// <c>BatchLocationBalance</c> rows. The one sanctioned exception is the zero-initialised new row in <c>StockLocks</c>.
/// A source scan: cheap, and it fails the day someone adds a second writer.
/// </summary>
public class StockPostingSourceScanTests
{
    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Sellora.Infrastructure"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    private static IEnumerable<(string File, int Line, string Text)> ProductionLines()
    {
        foreach (var file in Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(SrcRoot(), file).Replace('\\', '/');
            if (rel.Contains("/Migrations/") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++) yield return (rel, i + 1, lines[i]);
        }
    }

    private static bool IsPostingFile(string rel) =>
        rel.EndsWith("Sellora.Infrastructure/Services/StockPosting.cs") || rel.EndsWith("Sellora.Infrastructure/Services/BatchAllocator.cs");

    // Qualified assignment or compound assignment: x.QuantityOnHand = / += / -= (not ==, not =>).
    private static readonly Regex MemberAssignment = new(@"\.(QuantityOnHand|AverageCost|RemainingBaseQty)\s*(=(?![=>])|[+\-*/]=)", RegexOptions.Compiled);

    // Object-initializer assignment inside Infrastructure (Api projections into DTO records are reads of StockItems, not writes).
    private static readonly Regex InitializerAssignment = new(@"(?<![.\w])(QuantityOnHand|AverageCost|RemainingBaseQty)\s*=(?![=>])", RegexOptions.Compiled);

    [Fact]
    public void ProductionCode_AssignsStockQuantitiesOnlyInStockPostingAndBatchAllocator()
    {
        var offenders = ProductionLines()
            .Where(l => !IsPostingFile(l.File) && !l.Text.TrimStart().StartsWith("//") && !l.Text.TrimStart().StartsWith("///") && !l.Text.TrimStart().StartsWith("*"))
            .Where(l => MemberAssignment.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Stock quantities/costs must only change in StockPosting/BatchAllocator:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Infrastructure_InitializesStockQuantitiesOnlyInPostingAndTheZeroRowOfStockLocks()
    {
        var offenders = ProductionLines()
            .Where(l => l.File.StartsWith("Sellora.Infrastructure/") && !l.File.Contains("/Data/Configurations/") && !IsPostingFile(l.File))
            .Where(l => !l.Text.TrimStart().StartsWith("//") && !l.Text.TrimStart().StartsWith("*"))
            .Where(l => InitializerAssignment.IsMatch(l.Text))
            .Where(l => !(l.File.EndsWith("Services/StockLocks.cs") && Regex.IsMatch(l.Text, @"QuantityOnHand = 0, AverageCost = 0 \}")))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Only the zero-initialised new row in StockLocks may set quantities outside StockPosting:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void StockLocks_ZeroInitialisedNewRow_IsTheOnlyStockItemConstructionOutsideStockPosting()
    {
        var creations = ProductionLines()
            .Where(l => Regex.IsMatch(l.Text, @"new\s+StockItem\b|new\(\)\s*\{[^}]*StockLocationId") && !l.Text.TrimStart().StartsWith("//") && !l.Text.TrimStart().StartsWith("*"))
            .Select(l => l.File)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { "Sellora.Infrastructure/Services/StockLocks.cs" }, creations);
    }

    [Fact]
    public void ProductionCode_BatchLocationBalanceRowsAreCreatedOnlyInStockPosting()
    {
        var offenders = ProductionLines()
            .Where(l => l.File.StartsWith("Sellora.") && !l.File.EndsWith("Entities/BatchLocationBalance.cs") && !l.File.Contains("/Data/Configurations/"))
            .Where(l => Regex.IsMatch(l.Text, @"new\s+BatchLocationBalance\b|BatchLocationBalances\.(Add|AddRange|Remove|RemoveRange|Update)\b|ExecuteUpdate|ExecuteDelete") && l.Text.Contains("BatchLocationBalance"))
            .Where(l => !l.File.EndsWith("Services/StockPosting.cs"))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "BatchLocationBalance rows must only be written by StockPosting:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ProductionCode_NoRawSqlWritesToStockStateOutsideStockLocksAndMigrations()
    {
        var offenders = ProductionLines()
            .Where(l => !l.File.EndsWith("Services/StockLocks.cs"))
            .Where(l => Regex.IsMatch(l.Text, @"(UPDATE|DELETE\s+FROM|INSERT\s+INTO)\s+\\?""(StockItems|BatchLocationBalances|PurchaseBatches)\\?""", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(l.Text, @"SetProperty\(\s*\w+\s*=>\s*\w+\.(QuantityOnHand|AverageCost|RemainingBaseQty)"))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Stock state must not be written by raw SQL / ExecuteUpdate:\n" + string.Join("\n", offenders));
    }
}
