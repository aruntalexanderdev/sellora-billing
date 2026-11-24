using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Infrastructure;

/// <summary>scripts/sql/dev-reset-fresh-setup.sql is a hand-run, destructive operator script. These tests keep the committed copy
/// disarmed, keep it plain SQL (it is pasted into DBeaver and similar tools) and make sure every table of the model is classified:
/// a new migration that adds a table fails here until the table is either added to the script's reset list or to the kept list below.</summary>
public partial class FreshSetupResetScriptTests
{
    /// <summary>Tables the reset never empties (setup, identity, reference data, audit). DocumentSequences only has its counters reset;
    /// PlatformAuditEvents only receives one row.</summary>
    private static readonly string[] NeverEmptied =
    [
        "AuditEvents", "Branches", "BusinessSettings", "Companies", "CompanyGovernanceMembers", "Countries", "Currencies", "DocumentSequences",
        "ExchangeRates", "GovernanceMemberPermissions", "GovernancePermissions", "Permissions", "PlatformAdministrators", "PlatformAuditEvents",
        "RefreshTokens", "RolePermissions", "Roles", "States", "StockLocations", "UserBranchAssignments", "UserCompanyMemberships",
        "UserRoleAssignments", "Users", "Vehicles", "Warehouses",
    ];

    private static string ScriptText()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "sql", "dev-reset-fresh-setup.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("scripts/sql/dev-reset-fresh-setup.sql not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Rows of the reset list: ('2 Inventory', 'Products', keep token, 'truncate' or 'delete', note).</summary>
    private static List<(string Table, string? KeepToken, string Via)> ResetList() =>
        ListRow().Matches(ScriptText())
            .Select(m => (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, m.Groups[3].Value))
            .ToList();

    [GeneratedRegex(@"^\s*\('\d [A-Za-z]+',\s*'([A-Za-z]+)',\s*(?:'([a-z_]+)'|NULL|cast\(NULL AS text\)),\s*'(truncate|delete)',", RegexOptions.Multiline)]
    private static partial Regex ListRow();

    /// <summary>The real table names, from the relational model (no connection is opened).</summary>
    private static List<string> ModelTables()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>().UseNpgsql("Host=unused;Database=unused").Options;
        using var db = new SelloraDbContext(options, TestCompany.Context, TestCompany.Branch);
        return db.Model.GetEntityTypes().Select(t => t.GetTableName()!).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Script_IsCommittedDisarmed()
    {
        var script = ScriptText();

        Assert.Matches(@"v_confirm_database\s+constant text\s+:= 'TYPE-THE-DATABASE-NAME-HERE';", script);
        Assert.Matches(@"v_apply\s+constant boolean\s+:= false;", script);
        Assert.Matches(@"v_wipe\s+constant text\s+:= '';", script);
        Assert.Matches(@"v_keep\s+constant text\s+:= '';", script);
        // The session switch must be typed by the operator: the script never sets it itself. Outside comments the name may appear on
        // exactly two lines: where the guard reads it and inside the "refused" message. Any other mention (SET, SET SESSION, set_config,
        // ALTER DATABASE, however it is spelled) fails here.
        var mentions = script.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal) && l.Contains("allow_dev_reset", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim()).ToList();
        Assert.Equal(2, mentions.Count);
        Assert.StartsWith("IF coalesce(current_setting('sellora.allow_dev_reset', true), '') <> 'on' THEN", mentions[0]);
        Assert.StartsWith("v_status := 'REFUSED - this session is not allowed to reset.", mentions[1]);
    }

    [Fact]
    public void Script_ClassifiesEveryTableOfTheModel()
    {
        var listed = ResetList().Select(r => r.Table).ToList();

        Assert.Equal(53, listed.Count);
        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(listed.Intersect(NeverEmptied, StringComparer.Ordinal));
        Assert.Equal(ModelTables(), listed.Concat(NeverEmptied).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Script_KeepTokensAndTheWalkInRuleAreTheDocumentedOnes()
    {
        var list = ResetList();

        Assert.Equal(
            ["Customers:customers", "ExpenseCategories:expense_categories", "FinancialAccounts:accounts", "ProductCategories:categories",
             "ProductSubcategories:categories", "Suppliers:suppliers", "TaxComponents:taxes", "TaxProfileVersionComponents:taxes", "TaxProfileVersions:taxes",
             "TaxProfiles:taxes", "Taxes:taxes"],
            list.Where(r => r.KeepToken is not null).Select(r => r.Table + ":" + r.KeepToken).Order(StringComparer.Ordinal).ToList());
        // Customers is the only table that is not emptied outright: the walk-in customer must survive.
        Assert.Equal(["Customers"], list.Where(r => r.Via == "delete").Select(r => r.Table).ToList());
        Assert.Contains("DELETE FROM \"Customers\" WHERE NOT \"IsWalkIn\";", ScriptText());
        // Customers and suppliers are the masters kept by default.
        Assert.Contains("ARRAY['customers', 'suppliers'] || v_k", ScriptText());
    }

    /// <summary>The script is pasted into GUI SQL tools: no psql meta-commands, and nothing those tools would treat as a client-side
    /// parameter (a question mark, or a colon followed by a name) or as a statement boundary (a blank line) inside the reset statement.</summary>
    [Fact]
    public void Script_IsPlainSql_SafeToPasteIntoGuiSqlTools()
    {
        var lines = ScriptText().Replace("\r\n", "\n").Split('\n');
        var code = lines.Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith('\\'));
        Assert.DoesNotContain(lines, l => l.Contains('?'));
        Assert.DoesNotContain(code, l => Regex.IsMatch(l, ":[A-Za-z_]"));
        Assert.DoesNotContain(code, l => l.Contains("::", StringComparison.Ordinal));

        var start = Array.FindIndex(lines, l => l.StartsWith("DO $$", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, l => l.StartsWith("$$;", StringComparison.Ordinal));
        Assert.True(start >= 0 && end > start, "the reset must be one DO $$ ... $$; statement");
        Assert.DoesNotContain(lines[start..end], string.IsNullOrWhiteSpace);
        // Everything that touches application tables is inside that one statement, so the reset is all or nothing in auto-commit
        // mode and nothing runs outside the guards. Outside the block only the session result sheet may be used: every other
        // statement fails here.
        string[] allowedOutside =
        [
            "CREATE TEMP TABLE IF NOT EXISTS sellora_reset_report (",
            "DELETE FROM pg_temp.sellora_reset_report;",
            "INSERT INTO pg_temp.sellora_reset_report (seq, area, action)",
            "VALUES (0, 'RESULT', 'NOT COMPLETED - ",
            "SELECT area AS \"Area\", table_name AS \"Table\", rows_before AS \"Rows before\", rows_after AS \"Rows after\", action AS \"Result / action\"",
            "FROM pg_temp.sellora_reset_report ORDER BY seq;",
        ];
        var outside = lines[..start].Concat(lines[(end + 1)..])
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Equal(allowedOutside.Length, outside.Count);
        Assert.All(outside.Zip(allowedOutside), pair => Assert.StartsWith(pair.Second, pair.First));
    }
}
