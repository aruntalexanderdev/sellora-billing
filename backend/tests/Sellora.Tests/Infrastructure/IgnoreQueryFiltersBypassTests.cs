namespace Sellora.Tests.Infrastructure;

/// <summary>
/// T4 skeleton (design D3/§4): source scan guarding the multi-company query filter. <c>IgnoreQueryFilters</c> is a tenant
/// bypass and may appear only at the approved platform sites. Raw-SQL predicate scanning is added in WP4 once WP3 lands.
/// </summary>
public class IgnoreQueryFiltersBypassTests
{
    private static readonly string[] Approved = ["UnitService.cs", "UnitsController.cs", "CurrenciesController.cs"];

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Sellora.Infrastructure"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("backend/src not found from " + AppContext.BaseDirectory)
                           : Path.Combine(dir.FullName, "src");
    }

    [Fact]
    public void ProductionSource_IgnoreQueryFilters_OnlyInApprovedBypassFiles()
    {
        var offenders = Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllLines(f).Any(l => l.Contains("IgnoreQueryFilters") && !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")))
            .Select(Path.GetFileName)
            .Where(n => !Approved.Contains(n))
            .ToList();

        Assert.True(offenders.Count == 0, "IgnoreQueryFilters is a tenant bypass; unapproved use in: " + string.Join(", ", offenders));
    }
}
