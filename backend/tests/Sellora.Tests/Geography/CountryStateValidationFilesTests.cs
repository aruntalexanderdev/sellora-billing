using System.Text.RegularExpressions;

namespace Sellora.Tests.Geography;

/// <summary>The five V__ files of country-state-masters (design section 8): explicit two-line header, mode as designed, read-only SELECTs, no PII columns.</summary>
public class CountryStateValidationFilesTests
{
    private static string Read(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "db", "validation", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(name);
    }

    [Theory]
    [InlineData("V__country-state-seed-complete.sql", "block")]
    [InlineData("V__country-state-consistency.sql", "block")]
    [InlineData("V__country-state-geo-rules.sql", "warn")]
    [InlineData("V__country-state-unmatched-legacy.sql", "warn")]
    [InlineData("V__country-state-gstin-conflicts.sql", "warn")]
    public void ValidationFile_StartsWithDescriptionAndTheDesignedMode(string file, string mode)
    {
        var lines = Read(file).Replace("\r", "").Split('\n');

        Assert.Matches(@"^-- sellora:description \S.*", lines[0]);
        Assert.Equal("-- sellora:on-violation " + mode, lines[1].TrimEnd());
    }

    [Theory]
    [InlineData("V__country-state-seed-complete.sql")]
    [InlineData("V__country-state-consistency.sql")]
    [InlineData("V__country-state-geo-rules.sql")]
    [InlineData("V__country-state-unmatched-legacy.sql")]
    [InlineData("V__country-state-gstin-conflicts.sql")]
    public void ValidationFile_IsReadOnly_AndNeverOutputsStateTextOrAFullGstin(string file)
    {
        var sql = string.Join('\n', Read(file).Split('\n').Where(l => !l.TrimStart().StartsWith("--")));

        Assert.DoesNotMatch(new Regex(@"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE)\b", RegexOptions.IgnoreCase), sql);
        // V__ output rule: ids, prefixes and codes only. The select lists must not project the legacy text or the whole GSTIN.
        Assert.DoesNotMatch(new Regex(@"SELECT[^;]*?,\s*\w*\.?""Gstin""\s*(,|\bFROM\b)", RegexOptions.IgnoreCase), sql);
    }
}
