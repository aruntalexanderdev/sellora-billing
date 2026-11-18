using System.Text.RegularExpressions;
using Sellora.Domain.Permissions;

namespace Sellora.Tests.Permissions;

/// <summary>Phase 4 O-4 — backend/db/repeatable/R__seed-permissions.sql must mirror <see cref="PermissionCatalog.All"/> exactly (code, module, action, description), so production seeding cannot drift from the application's catalogue.</summary>
public partial class PermissionSeedScriptTests
{
    internal static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "db", "repeatable", "R__seed-permissions.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("backend/db/repeatable/R__seed-permissions.sql not found above " + AppContext.BaseDirectory);
    }

    internal static List<(string Code, string Module, string Action, string Description)> ParseScript() =>
        RowPattern().Matches(File.ReadAllText(ScriptPath()))
            .Select(m => (Unescape(m.Groups[1].Value), Unescape(m.Groups[2].Value), Unescape(m.Groups[3].Value), Unescape(m.Groups[4].Value)))
            .ToList();

    private static string Unescape(string value) => value.Replace("''", "'");

    [GeneratedRegex(@"^\s*\('((?:[^']|'')*)',\s*'((?:[^']|'')*)',\s*'((?:[^']|'')*)',\s*'((?:[^']|'')*)'\),?\s*$", RegexOptions.Multiline)]
    private static partial Regex RowPattern();

    [Fact]
    public void Script_RowsEqualThePermissionCatalogue_CodeModuleActionDescription()
    {
        var script = ParseScript().OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
        var catalogue = PermissionCatalog.All.Select(p => (p.Code, p.Module, p.Action, p.Description)).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();

        Assert.Equal(catalogue.Select(r => r.Code), script.Select(r => r.Code));
        Assert.Equal(catalogue, script);
    }

    [Fact]
    public void Script_HasNoDuplicateCodes_AndEverySingleQuoteIsEscaped()
    {
        var rows = ParseScript();

        Assert.Equal(rows.Count, rows.Select(r => r.Code).Distinct().Count());
        Assert.Equal(PermissionCatalog.All.Count, rows.Count);
    }

    [Fact]
    public void Script_IsInsertOnlyAndIdempotent_NoUpdateDeleteOrRoleGrants()
    {
        var sql = File.ReadAllText(ScriptPath());

        Assert.Contains("ON CONFLICT (\"Code\") DO NOTHING", sql);
        Assert.Contains("gen_random_uuid()", sql);
        Assert.DoesNotMatch(@"(?im)^\s*(UPDATE|DELETE|TRUNCATE|DROP)\b", sql);
        Assert.DoesNotContain("RolePermissions", sql);
    }

    [Fact]
    public void Catalogue_ContainsTheEightPhase4Codes_InTheAdministrationModule()
    {
        string[] codes =
        [
            "Company.Warehouses.View", "Company.Warehouses.Create", "Company.Warehouses.Edit", "Company.Warehouses.Disable",
            "Company.Vehicles.View", "Company.Vehicles.Create", "Company.Vehicles.Edit", "Company.Vehicles.Disable",
        ];
        var catalogue = PermissionCatalog.All.ToDictionary(p => p.Code);

        foreach (var code in codes)
        {
            Assert.True(catalogue.ContainsKey(code), code + " missing from the catalogue.");
            Assert.Equal("Administration", catalogue[code].Module);
        }
        Assert.Equal(codes.Length, PermissionCatalog.All.Count(p => p.Code.StartsWith("Company.Warehouses.") || p.Code.StartsWith("Company.Vehicles.")));
    }
}
