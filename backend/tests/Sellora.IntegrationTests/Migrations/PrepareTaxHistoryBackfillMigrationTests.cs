using System.Diagnostics;
using Npgsql;
using Sellora.Infrastructure.Data.Migrations;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.HistoryBackfillWorld;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.SalesTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// The three TP-5a migrations (docs/features/tax-profiles.md, "TP-5 design", "History backfill (M3a, additive)", T35). <c>AddTaxReportSettings</c>: <c>Units.Uqc</c> with its seeded GST mapping and format check,
/// <c>BusinessSettings.HsnRequiredFrom</c> (NULL for existing companies), the <c>Reports.Tax</c> permission granted to every <c>Reports.Sales</c> holder; no line table. <c>PrepareTaxHistoryBackfill</c>: only the ONE short
/// ACCESS EXCLUSIVE window on the four line tables and the swap of the "profile and version both or neither" constraints to "a version needs a profile" (NOT VALID). <c>ValidateTaxHistoryBackfill</c>: validates them.
/// Also lock_timeout fail-fast (55P03) with a clean retry, no deadlock with a sale, the TP-4 build still inserting, Down/Up and the idempotent script. All on <see cref="HistoryBackfillWorld.Tp4ShapedCopyAsync"/>: the TP-4 schema with IN and AE history lines (TaxId, no profile), TP-3/TP-4 lines (profile and version), returns of both and unmappable lines.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PrepareTaxHistoryBackfillMigrationTests(PostgresFixture postgres)
{
    private static readonly string[] Tables = ["PurchaseReturnLines", "PurchaseOrderLines", "SalesInvoiceLines", "SalesReturnLines"];
    private static readonly string[] RewriteWatch = [.. Tables, "Units", "BusinessSettings", "Permissions", "RolePermissions", "SalesInvoices", "PurchaseOrders"];

    private static string Pair(string table) => $"CK_{table}_TaxProfile_Pair";
    private static string Needs(string table) => $"CK_{table}_TaxProfile_VersionNeedsProfile";

    /// <summary>A line of each table that has no profile in the TP-4 world (the legacy fixture's lines) and the id of its fixture row.</summary>
    private static readonly (string Table, Guid Id)[] HistoryLine =
        [("SalesInvoiceLines", Fx(94)), ("SalesReturnLines", Fx(101)), ("PurchaseOrderLines", Fx(50)), ("PurchaseReturnLines", Fx(71))];

    private static Exception Root(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is PostgresException || e.InnerException is null) return e;
        return ex;
    }

    private static string InList(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    private static async Task<List<string>> RowsAsync(TestDatabase d, string sql) =>
        await QueryAsync(d, sql, r => string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "-" : Format(r.GetValue(i)))));

    private static Task<List<string>> FilenodesAsync(TestDatabase d) =>
        RowsAsync(d, $"""SELECT relname || '=' || relfilenode::text FROM pg_class WHERE relkind = 'r' AND relnamespace = 'public'::regnamespace AND relname IN ({InList(RewriteWatch)}) ORDER BY 1""");

    /// <summary>Constraint name to validated flag, for the old pair and the new rule on the four tables.</summary>
    private static async Task<Dictionary<string, bool>> StateAsync(TestDatabase d) =>
        (await QueryAsync(d, $"""SELECT conname, convalidated FROM pg_constraint WHERE conname IN ({InList(Tables.SelectMany(t => new[] { Pair(t), Needs(t) }))})""",
            r => (r.GetString(0), r.GetBoolean(1)))).ToDictionary(x => x.Item1, x => x.Item2);

    private static Task<List<string>> NewColumnsAsync(TestDatabase d) => QueryAsync(d, """
        SELECT table_name || '.' || column_name FROM information_schema.columns
        WHERE table_schema = 'public' AND ((table_name = 'Units' AND column_name = 'Uqc') OR (table_name = 'BusinessSettings' AND column_name = 'HsnRequiredFrom')) ORDER BY 1
        """, r => r.GetString(0));

    private static Task<long> ReportsTaxAsync(TestDatabase d) => ScalarAsync<long>(d, """SELECT count(*) FROM "Permissions" WHERE "Code" = 'Reports.Tax'""");

    /// <summary>Line profile and version of every row of the four tables: history lines and TP-3/TP-4 lines alike.</summary>
    private static async Task<List<string>> LineSnapshotAsync(TestDatabase d)
    {
        var all = new List<string>();
        foreach (var table in LineTables) all.Add(table + "=" + await FingerprintAsync(d, table));
        return all;
    }

    /// <summary>One statement in a rolled-back transaction that must be refused with <paramref name="state"/> on <paramref name="constraint"/>.</summary>
    private static async Task FailsAsync(TestDatabase d, string sql, string state, string constraint)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InRolledBackTransactionAsync(d, p => p.ExecAsync(sql)));
        Assert.Equal((state, constraint), (ex.SqlState, ex.ConstraintName));
    }

    private static string Set(string table, Guid id, string assignments) => $"""UPDATE "{table}" SET {assignments} WHERE "Id" = '{id}'""";

    // ------------------------------------------------------------------------------------------------------------------------------ the starting point

    [Fact]
    public async Task Tp4ShapedWorld_HoldsEveryKindOfLineTheBackfillMeets()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);

        Assert.Contains(ValidateSales, await AppliedAsync(d));
        Assert.DoesNotContain(PrepareHistory, await AppliedAsync(d));
        Assert.Equal(4L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Pair))}) AND convalidated"));
        Assert.Empty(await NewColumnsAsync(d));
        Assert.Equal(0L, await ReportsTaxAsync(d));
        var counts = await CountsAsync(d);
        foreach (var table in LineTables)
        {
            Assert.True(counts[table].WithVersion >= 1, $"{table}: no TP-3/TP-4 line ({counts[table]})");
            Assert.True(counts[table].WithoutProfile >= 3, $"{table}: too few history lines ({counts[table]})");
            Assert.Equal(0L, counts[table].ProfileOnly);   // the pair constraint forbids a profile without a version
        }
        // the unmappable lines and the rollback-era return (TP-3 build on a TP-4 line: no profile, no version, no place)
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "Id" = '{OrphanSalesLine}' AND "TaxId" = '{TaxOrphan}' AND "TaxProfileId" IS NULL"""));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "TaxProfiles" WHERE "LegacyTaxId" = '{TaxOrphan}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesReturnLines" WHERE "Id" = '{Rollback.ReturnLine}' AND "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NULL AND "PlaceOfSupply" IS NULL"""));
        // both countries: AE history (company B) next to IN
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" l JOIN "SalesInvoices" i ON i."Id" = l."SalesInvoiceId" WHERE l."Id" = '{SiBLine}' AND i."CompanyId" = '{CoB}' AND l."TaxProfileId" IS NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesReturnLines" WHERE "Id" = '{Vat.ReturnLine}' AND "TaxProfileVersionId" IS NOT NULL"""));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the constraint swap

    [Fact]
    public async Task Constraints_AfterPrepare_TheOldPairIsGone_AndTheNewRuleIsNotValidYet_AfterValidateItIsValidated()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(d, PrepareHistory);

        var first = await StateAsync(d);
        Assert.Equal(Tables.Select(Needs).Order().ToList(), first.Keys.Order().ToList());   // no *_TaxProfile_Pair left, four new ones
        Assert.All(first, kv => Assert.False(kv.Value, kv.Key + " must be NOT VALID after PrepareTaxHistoryBackfill"));
        foreach (var table in Tables)
            Assert.Equal("CHECK (((\"TaxProfileVersionId\" IS NULL) OR (\"TaxProfileId\" IS NOT NULL))) NOT VALID", await ScalarAsync<string>(d, $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = '{Needs(table)}'"));

        await MigrateExactlyAsync(d, ValidateHistory);

        var second = await StateAsync(d);
        Assert.Equal(Tables.Select(Needs).Order().ToList(), second.Keys.Order().ToList());
        Assert.All(second, kv => Assert.True(kv.Value, kv.Key + " must be validated after ValidateTaxHistoryBackfill"));
        // the constraints stay on their own table
        Assert.Equal(Tables.Order().ToList(), (await QueryAsync(d, $"""SELECT t.relname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE c.conname IN ({InList(Tables.Select(Needs))})""", r => r.GetString(0))).Order().ToList());
    }

    [Theory]
    [InlineData(PrepareHistory)]
    [InlineData(ValidateHistory)]
    public async Task Constraints_AProfileWithoutAVersionIsAccepted_AVersionWithoutAProfileIsRefused_WhileNotValidAndWhenValidated(string migrateTo)
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, migrateTo);

        foreach (var (table, id) in HistoryLine)
        {
            await InRolledBackTransactionAsync(d, async p =>
            {
                await p.ExecAsync(Set(table, id, "\"TaxProfileId\" = NULL, \"TaxProfileVersionId\" = NULL"));
                await p.ExecAsync(Set(table, id, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = NULL"));            // backfilled history: the profile only
                await p.ExecAsync(Set(table, id, $"\"TaxProfileId\" = '{ProfileA}', \"TaxProfileVersionId\" = '{VersionA}'"));   // a TP-3/TP-4 line
            });
            await FailsAsync(d, Set(table, id, $"\"TaxProfileId\" = NULL, \"TaxProfileVersionId\" = '{VersionA}'"), "23514", Needs(table));
            // a fresh insert is held to the rule as well (the table is never checked only for old rows)
            await FailsAsync(d, PurchaseTaxWorld.Clone(table, $"\"Id\" = '{id}'", ("Id", $"'{G(0xF900)}'"), ("TaxProfileId", "NULL"), ("TaxProfileVersionId", $"'{VersionA}'")), "23514", Needs(table));
            await InRolledBackTransactionAsync(d, p => p.ExecAsync(PurchaseTaxWorld.Clone(table, $"\"Id\" = '{id}'", ("Id", $"'{G(0xF900)}'"), ("TaxProfileId", $"'{ProfileA}'"), ("TaxProfileVersionId", "NULL"))));
        }
    }

    [Fact]
    public async Task Constraints_EveryRowOfTheTp4World_SatisfiesTheNewRule_SoValidationCannotFail()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, PrepareHistory);

        foreach (var table in Tables)
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{table}" WHERE NOT ("TaxProfileVersionId" IS NULL OR "TaxProfileId" IS NOT NULL)"""));
        await MigrateExactlyAsync(d, ValidateHistory);   // does not throw
        Assert.Contains(ValidateHistory, await AppliedAsync(d));
    }

    [Fact]
    public async Task Up_RewritesNoTable_NoneOfTheThreeMigrations_AndHistoryKeepsItsValues()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var nodes = await FilenodesAsync(d);
        var money = await UnchangedSnapshotAsync(d);
        var lines = await LineSnapshotAsync(d);
        Assert.Equal(RewriteWatch.Length, nodes.Count);

        await MigrateExactlyAsync(d, AddReportSettings);
        var afterSettings = await FilenodesAsync(d);
        await MigrateExactlyAsync(d, PrepareHistory);
        var afterFirst = await FilenodesAsync(d);
        await MigrateExactlyAsync(d, ValidateHistory);
        var afterSecond = await FilenodesAsync(d);

        // ADD COLUMN without a default, NOT VALID constraints, VALIDATE: metadata only. (Units gets its UQC through UPDATEs, which write new row versions in the same file.)
        Assert.Equal(nodes, afterSettings);
        Assert.Equal(nodes, afterFirst);
        Assert.Equal(nodes, afterSecond);
        Assert.Equal(money, await UnchangedSnapshotAsync(d));   // money columns, mirrors and TaxIds of history are untouched ...
        Assert.Equal(lines, await LineSnapshotAsync(d));        // ... and so are the profile and version of every line (the backfill is a separate step)
    }

    // ------------------------------------------------------------------------------------------------------------------------------ order and lock window

    [Fact]
    public async Task Up_AddTaxReportSettingsScript_DoesRowWorkFirst_ThenTheTwoSmallDdlTargets_AndNeverLocksALineTable()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var script = await ScriptAsync(d, ValidateSales, AddReportSettings);

        var timeoutAt = script.IndexOf("lock_timeout", StringComparison.Ordinal);
        var permissionAt = script.IndexOf("Reports.Tax", StringComparison.Ordinal);
        var uqcAt = script.IndexOf("ADD \"Uqc\"", StringComparison.Ordinal) >= 0 ? script.IndexOf("ADD \"Uqc\"", StringComparison.Ordinal) : script.IndexOf("\"Uqc\"", StringComparison.Ordinal);
        var hsnAt = script.IndexOf("HsnRequiredFrom", StringComparison.Ordinal);
        Assert.True(timeoutAt >= 0 && timeoutAt < permissionAt && permissionAt < uqcAt && uqcAt < hsnAt, $"order: lock_timeout {timeoutAt}, permission {permissionAt}, Uqc {uqcAt}, HsnRequiredFrom {hsnAt}");
        Assert.DoesNotContain("LOCK TABLE", script, StringComparison.Ordinal);
        foreach (var table in Tables)
        {
            Assert.DoesNotContain($"ALTER TABLE \"{table}\"", script, StringComparison.Ordinal);   // the hot line tables are not touched here
            Assert.DoesNotContain("TaxProfile_Pair", script, StringComparison.Ordinal);
            Assert.DoesNotContain("TaxProfile_VersionNeedsProfile", script, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("VALIDATE CONSTRAINT", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_PrepareTaxHistoryBackfillScript_HoldsNothingButTheFourLineTables_OneAccessExclusiveLockThenOnlyNotValidAlters()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var script = await ScriptAsync(d, AddReportSettings, PrepareHistory);

        var timeoutAt = script.IndexOf("lock_timeout", StringComparison.Ordinal);
        var lockAt = script.IndexOf("LOCK TABLE", StringComparison.Ordinal);
        Assert.True(timeoutAt >= 0 && timeoutAt < lockAt, $"order: lock_timeout {timeoutAt}, lock {lockAt}");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "LOCK TABLE"));   // ONE window
        // T35: no DDL, no row work and no mention of Units, BusinessSettings, Uqc, HsnRequiredFrom or the permission: nothing a sale reads is held while the line tables are awaited
        foreach (var forbidden in new[] { "\"Units\"", "\"BusinessSettings\"", "Uqc", "HsnRequiredFrom", "Reports.Tax", "\"Permissions\"", "\"RolePermissions\"" })
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        var lockStatement = script[lockAt..script.IndexOf(';', lockAt)];
        Assert.Contains("ACCESS EXCLUSIVE", lockStatement, StringComparison.Ordinal);
        // the application's own lock order: purchase returns, purchases, sales, sales returns
        Assert.True(lockStatement.IndexOf("\"PurchaseReturnLines\"", StringComparison.Ordinal) < lockStatement.IndexOf("\"PurchaseOrderLines\"", StringComparison.Ordinal)
            && lockStatement.IndexOf("\"PurchaseOrderLines\"", StringComparison.Ordinal) < lockStatement.IndexOf("\"SalesInvoiceLines\"", StringComparison.Ordinal)
            && lockStatement.IndexOf("\"SalesInvoiceLines\"", StringComparison.Ordinal) < lockStatement.IndexOf("\"SalesReturnLines\"", StringComparison.Ordinal), lockStatement);
        var afterLock = script[(lockAt + 1)..];
        var statements = afterLock.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(s => string.Join(' ', s.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("--", StringComparison.Ordinal) && l.Length > 0))).Where(s => s.Length > 0).ToList();
        var offenders = statements.Where(s => !s.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal)
            && !s.StartsWith("END", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("DO", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(offenders.Count == 0, "statements after the exclusive lock that are not ALTER TABLE: " + string.Join(" || ", offenders.Select(o => o[..Math.Min(o.Length, 100)])));
        Assert.Equal(4, statements.Count(s => s.Contains("ADD CONSTRAINT", StringComparison.Ordinal)));
        Assert.All(statements.Where(s => s.Contains("ADD CONSTRAINT", StringComparison.Ordinal)), s => Assert.Contains("NOT VALID", s, StringComparison.Ordinal));
        Assert.DoesNotContain("VALIDATE CONSTRAINT", afterLock, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", afterLock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Up_TheThreeMigrations_RunInTheirOwnTransactionsInOrder_SettingsFirst_LineTablesSecond_ValidationLast()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var script = await ScriptAsync(d, ValidateSales, ValidateHistory);

        var settingsAt = script.IndexOf(AddReportSettings, StringComparison.Ordinal);
        var prepareAt = script.IndexOf(PrepareHistory, StringComparison.Ordinal);
        var validateAt = script.IndexOf(ValidateHistory, StringComparison.Ordinal);
        Assert.True(0 <= settingsAt && settingsAt < prepareAt && prepareAt < validateAt, $"{settingsAt}, {prepareAt}, {validateAt}");
        // the only LOCK TABLE sits after the settings migration's history row: the Units and BusinessSettings locks were released with its transaction
        var lockAt = script.IndexOf("LOCK TABLE", StringComparison.Ordinal);
        Assert.True(lockAt > settingsAt, $"{lockAt} > {settingsAt}");
        Assert.True(script.LastIndexOf("\"Uqc\"", StringComparison.Ordinal) < lockAt);
        Assert.True(script.LastIndexOf("HsnRequiredFrom", StringComparison.Ordinal) < lockAt);
    }

    [Fact]
    public async Task Up_SecondMigrationScript_OnlyValidates_ItTakesNoExplicitLock()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var script = await ScriptAsync(d, PrepareHistory, ValidateHistory);

        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(script, "VALIDATE CONSTRAINT").Count);
        Assert.DoesNotContain("LOCK TABLE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD COLUMN", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", script, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Units.Uqc

    [Fact]
    public async Task Uqc_StandardUnitsGetTheirGstCode_UnmappedUnitsStayNull_InEverySpelling()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        Assert.Empty(await NewColumnsAsync(d));

        await MigrateExactlyAsync(d, AddReportSettings);

        foreach (var (code, uqc) in UnitCodes)
            Assert.Equal(uqc ?? "-", (await RowsAsync(d, $"""SELECT COALESCE("Uqc", '-') FROM "Units" WHERE "Id" = '{UnitOf(code)}'""")).Single());
        // the fixture's own standard units ('pc', 'box' ...) are mapped as the seeder maps them
        Assert.Equal(["PCS"], await RowsAsync(d, $"""SELECT "Uqc" FROM "Units" WHERE "Id" = '{Fx(1)}'"""));
        Assert.Equal("character varying|3|YES", (await RowsAsync(d, """SELECT data_type || '|' || character_maximum_length || '|' || is_nullable FROM information_schema.columns WHERE table_name = 'Units' AND column_name = 'Uqc'""")).Single());
        // no unit outside the mapping got a code: every non-null code comes from the mapping set
        var codes = await QueryAsync(d, """SELECT DISTINCT "Uqc" FROM "Units" WHERE "Uqc" IS NOT NULL""", r => r.GetString(0));
        Assert.Empty(codes.Except(["PCS", "NOS", "KGS", "GMS", "LTR", "MLT", "MTR", "BOX", "BAG", "CTN", "DOZ", "PAC"]));
    }

    [Fact]
    public async Task Uqc_TheStandardUnitsOfTheUnitMaster_GetTheSameCodesAsTheSeeder()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);

        // DbSeeder.UnitSeed (TP-5a): the same mapping for databases created from scratch
        var expected = new Dictionary<string, string> { ["kg"] = "KGS", ["litre"] = "LTR", ["pc"] = "PCS", ["box"] = "BOX", ["hctn"] = "CTN", ["fctn"] = "CTN", ["g"] = "GMS", ["ml"] = "MLT", ["bag"] = "BAG" };
        var actual = (await QueryAsync(d, """SELECT lower("ShortCode"), COALESCE("Uqc", '-') FROM "Units" WHERE lower("ShortCode") IN ('kg', 'litre', 'pc', 'box', 'hctn', 'fctn', 'g', 'ml', 'bag')""", r => (r.GetString(0), r.GetString(1)))).ToDictionary(x => x.Item1, x => x.Item2);

        Assert.True(actual.Count >= 3, "the legacy unit master holds pc, kg and box at least: " + string.Join(",", actual.Keys));
        Assert.All(actual, kv => Assert.Equal(expected[kv.Key], kv.Value));
    }

    [Theory]
    [InlineData("PCS", true)]
    [InlineData("KGS", true)]
    [InlineData(null, true)]
    [InlineData("pcs", false)]
    [InlineData("Pcs", false)]
    [InlineData("PC", false)]
    [InlineData("P1S", false)]
    [InlineData("P S", false)]
    [InlineData("", false)]
    public async Task Uqc_FormatCheck_AcceptsNullAndThreeCapitalLetters_Only(string? value, bool accepted)
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);
        var sql = $"""UPDATE "Units" SET "Uqc" = {(value is null ? "NULL" : $"'{value}'")} WHERE "Id" = '{UnitOf("weird")}'""";

        if (accepted) await InRolledBackTransactionAsync(d, p => p.ExecAsync(sql));
        else await FailsAsync(d, sql, "23514", "CK_Units_Uqc_Format");
    }

    [Fact]
    public async Task Uqc_ALongerCodeIsRefusedByTheColumnLength()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InRolledBackTransactionAsync(d, p => p.ExecAsync($"""UPDATE "Units" SET "Uqc" = 'ABCD' WHERE "Id" = '{UnitOf("weird")}'""")));

        Assert.Equal("22001", ex.SqlState);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ HsnRequiredFrom

    [Fact]
    public async Task HsnRequiredFrom_IsANullableDate_NullForEveryExistingCompany()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var companies = await ScalarAsync<long>(d, """SELECT count(*) FROM "BusinessSettings" """);
        Assert.True(companies >= 8, companies.ToString());   // the nine-company tax world (IN, AE, QA, ... ) has a settings row each except one

        await MigrateExactlyAsync(d, AddReportSettings);

        Assert.Equal("date|YES|-", (await RowsAsync(d, """SELECT data_type || '|' || is_nullable || '|' || COALESCE(column_default, '-') FROM information_schema.columns WHERE table_name = 'BusinessSettings' AND column_name = 'HsnRequiredFrom'""")).Single());
        Assert.Equal(companies, await ScalarAsync<long>(d, """SELECT count(*) FROM "BusinessSettings" WHERE "HsnRequiredFrom" IS NULL"""));
        // a value can be set and cleared (the settings screen writes it)
        await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""UPDATE "BusinessSettings" SET "HsnRequiredFrom" = DATE '2027-04-01' WHERE "CompanyId" = '{CoA}'""");
            Assert.Equal(["2027-04-01"], await p.RowsAsync($"""SELECT "HsnRequiredFrom" FROM "BusinessSettings" WHERE "CompanyId" = '{CoA}'"""));
        });
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Reports.Tax

    [Fact]
    public async Task ReportsTax_IsCreatedWithTheSeederId_AndGrantedToEveryReportsSalesHolderOnly()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var holders = await QueryAsync(d, """SELECT rp."RoleId" FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" = 'Reports.Sales'""", r => r.GetGuid(0));
        Assert.Contains(RoleSalesA, holders);
        Assert.Contains(RoleSalesB, holders);
        Assert.DoesNotContain(RoleNoSales, holders);
        var others = await RowsAsync(d, """SELECT p."Code" || '>' || count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" GROUP BY p."Code" ORDER BY 1""");

        await MigrateExactlyAsync(d, AddReportSettings);

        Assert.Equal(["Reports|View|View the tax reports: GST or VAT summary and the HSN summary."], await RowsAsync(d, """SELECT "Module" || '|' || "Action" || '|' || "Description" FROM "Permissions" WHERE "Code" = 'Reports.Tax'"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, """SELECT count(*) FROM "Permissions" WHERE "Code" = 'Reports.Tax' AND "Id" = md5('perm:Reports.Tax')::uuid"""));   // the id DbSeeder / R__seed-permissions use
        var granted = await QueryAsync(d, """SELECT rp."RoleId" FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" = 'Reports.Tax'""", r => r.GetGuid(0));
        Assert.Equal(holders.Order().ToList(), granted.Order().ToList());
        // nothing else changed: every other permission keeps its holder count
        Assert.Equal(others, (await RowsAsync(d, """SELECT p."Code" || '>' || count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" <> 'Reports.Tax' GROUP BY p."Code" ORDER BY 1""")));
    }

    [Fact]
    public async Task ReportsTax_NoReportsSalesHolder_MeansNoGrant_AndTheSeederPermissionRowIsKept()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await ExecuteAsync(d, """DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'Reports.Sales')""");
        // the seeder (or a previous run of the repeatable seed) already created the permission with its own description
        await ExecuteAsync(d, """INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES (md5('perm:Reports.Tax')::uuid, 'Reports.Tax', 'Reports', 'View', 'seeded text')""");

        await MigrateExactlyAsync(d, AddReportSettings);

        Assert.Equal(["seeded text"], await RowsAsync(d, """SELECT "Description" FROM "Permissions" WHERE "Code" = 'Reports.Tax'"""));   // ON CONFLICT DO NOTHING
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" = 'Reports.Tax'"""));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ AddTaxReportSettings on its own

    [Fact]
    public async Task AddTaxReportSettings_OnItsOwn_AddsTheColumns_TheCheck_TheSeed_AndThePermission_AndTouchesNoLineTable()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var lines = await LineSnapshotAsync(d);
        var money = await UnchangedSnapshotAsync(d);
        Assert.Empty(await NewColumnsAsync(d));

        await MigrateExactlyAsync(d, AddReportSettings);

        Assert.Equal(["BusinessSettings.HsnRequiredFrom", "Units.Uqc"], await NewColumnsAsync(d));
        Assert.Equal(["CK_Units_Uqc_Format"], await QueryAsync(d, """SELECT conname FROM pg_constraint WHERE conname = 'CK_Units_Uqc_Format'""", r => r.GetString(0)));
        Assert.Equal(1L, await ReportsTaxAsync(d));
        Assert.Contains(AddReportSettings, await AppliedAsync(d));
        Assert.DoesNotContain(PrepareHistory, await AppliedAsync(d));
        // the constraint swap has not happened: the old pair is still there, validated
        Assert.Equal(4L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Pair))}) AND convalidated"));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Needs))})"));
        Assert.Equal(lines, await LineSnapshotAsync(d));
        Assert.Equal(money, await UnchangedSnapshotAsync(d));
        Assert.Equal("PCS", (await RowsAsync(d, $"""SELECT "Uqc" FROM "Units" WHERE "Id" = '{UnitOf("Piece")}'""")).Single());
    }

    [Fact]
    public async Task AddTaxReportSettings_TheUqcSeedFillsOnlyNullCodes_AnOperatorsCodeIsKept_AndARerunOfTheSeedChangesNothing()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);
        // an operator sets a code on a mapped unit and fills one that has no mapping
        await ExecuteAsync(d, $"""UPDATE "Units" SET "Uqc" = 'OTH' WHERE "Id" = '{UnitOf("PCS")}'; UPDATE "Units" SET "Uqc" = 'QTL' WHERE "Id" = '{UnitOf("Quintal")}'; UPDATE "Units" SET "Uqc" = NULL WHERE "Id" = '{UnitOf("KGS")}';""");
        var customised = await RowsAsync(d, """SELECT "ShortCode" || '=' || COALESCE("Uqc", '-') FROM "Units" ORDER BY 1""");

        // the seed statement of the migration (as the idempotent script runs it again) only touches rows still NULL
        await ExecuteAsync(d, """
            UPDATE "Units" u SET "Uqc" = m.uqc
            FROM (VALUES ('pcs', 'PCS'), ('kgs', 'KGS'), ('quintal', 'QTL')) AS m(code, uqc)
            WHERE lower(u."ShortCode") = m.code AND u."Uqc" IS NULL;
            """);

        Assert.Equal(["OTH"], await RowsAsync(d, $"""SELECT "Uqc" FROM "Units" WHERE "Id" = '{UnitOf("PCS")}'"""));   // not overwritten
        Assert.Equal(["KGS"], await RowsAsync(d, $"""SELECT "Uqc" FROM "Units" WHERE "Id" = '{UnitOf("KGS")}'"""));   // a NULL row is filled
        Assert.Equal(["QTL"], await RowsAsync(d, $"""SELECT "Uqc" FROM "Units" WHERE "Id" = '{UnitOf("Quintal")}'"""));
        Assert.Contains("PCS=OTH", customised);
    }

    [Fact]
    public async Task AddTaxReportSettings_Down_RemovesTheColumns_TheCheck_ThePermissionAndItsGrants_AndUpWorksAgain()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var schema = await SchemaSignatureAsync(d);
        var grantsOfOthers = await RowsAsync(d, """SELECT p."Code" || '>' || count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" GROUP BY p."Code" ORDER BY 1""");
        await MigrateExactlyAsync(d, AddReportSettings);
        var up = await SettingsStateAsync(d);
        Assert.Contains("grants=2", up);

        await MigrateExactlyAsync(d, ValidateSales);

        Assert.DoesNotContain(AddReportSettings, await AppliedAsync(d));
        Assert.Empty(await NewColumnsAsync(d));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM pg_constraint WHERE conname = 'CK_Units_Uqc_Format'"""));
        Assert.Equal(0L, await ReportsTaxAsync(d));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" = 'Reports.Tax'"""));
        Assert.Equal(schema, await SchemaSignatureAsync(d));
        Assert.Equal(grantsOfOthers, await RowsAsync(d, """SELECT p."Code" || '>' || count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" GROUP BY p."Code" ORDER BY 1"""));

        await MigrateExactlyAsync(d, AddReportSettings);

        Assert.Equal(up, await SettingsStateAsync(d));
    }

    [Fact]
    public async Task AddTaxReportSettings_Rerun_GrantsEveryReportsSalesHolderOnce_NoDuplicateKeyOnTheSecondRun()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);
        var first = await SettingsStateAsync(d);

        await MigrateExactlyAsync(d, ValidateSales);
        await MigrateExactlyAsync(d, AddReportSettings);   // Down and Up leave exactly the same grants

        Assert.Equal(first, await SettingsStateAsync(d));
    }

    [Fact]
    public async Task PrepareTaxHistoryBackfill_OnItsOwn_OnlySwapsTheConstraints_SettingsColumnsPermissionAndUqcStayAsTheFirstMigrationLeftThem()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);
        var settings = await SettingsStateAsync(d);
        var before = await SchemaSignatureAsync(d);

        await MigrateExactlyAsync(d, PrepareHistory);

        Assert.Equal(settings, await SettingsStateAsync(d));
        var after = await SchemaSignatureAsync(d);
        var changed = before.Except(after).Concat(after.Except(before)).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, c => Assert.Contains("TaxProfile", c, StringComparison.Ordinal));   // nothing but the four pairs and their replacements differs
        Assert.DoesNotContain(changed, c => c.Contains("Units", StringComparison.Ordinal) || c.Contains("BusinessSettings", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ lock_timeout

    [Theory]
    [InlineData("Units")]
    [InlineData("BusinessSettings")]
    public async Task Up_AddTaxReportSettings_WhenUnitsOrBusinessSettingsIsInUse_FailsFastWith55P03_LeavesNoPartialState_AndRetrySucceeds(string lockedTable)
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var before = await UnchangedSnapshotAsync(d);
        var lines = await LineSnapshotAsync(d);
        await using var reader = new NpgsqlConnection(d.ConnectionString);
        await reader.OpenAsync();
        await using var tx = await reader.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{lockedTable}\" IN ACCESS SHARE MODE", reader, tx)) await hold.ExecuteNonQueryAsync();   // a plain open read is enough to stop an ACCESS EXCLUSIVE

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, AddReportSettings));
        clock.Stop();

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s instead of failing after about 5s");
        await tx.RollbackAsync();
        Assert.Empty(await NewColumnsAsync(d));                                           // one transaction: no column, no permission, no UQC, no check
        Assert.Equal(0L, await ReportsTaxAsync(d));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM pg_constraint WHERE conname = 'CK_Units_Uqc_Format'"""));
        Assert.DoesNotContain(AddReportSettings, await AppliedAsync(d));
        Assert.Equal(4L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Pair))}) AND convalidated"));   // the line tables were never reached

        await MigrateExactlyAsync(d, ValidateHistory);   // after the reader released, a retry succeeds (and so do the two following migrations)

        Assert.Contains(AddReportSettings, await AppliedAsync(d));
        Assert.Contains(ValidateHistory, await AppliedAsync(d));
        Assert.Equal(2, (await NewColumnsAsync(d)).Count);
        Assert.Equal(before, await UnchangedSnapshotAsync(d));
        Assert.Equal(lines, await LineSnapshotAsync(d));
    }

    [Theory]
    [InlineData("PurchaseReturnLines")]
    [InlineData("PurchaseOrderLines")]
    [InlineData("SalesInvoiceLines")]
    [InlineData("SalesReturnLines")]
    public async Task Up_PrepareTaxHistoryBackfill_WhenALineTableIsInUse_FailsFastWith55P03_LeavesNoPartialState_AndRetrySucceeds(string lockedTable)
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);
        var before = await UnchangedSnapshotAsync(d);
        var lines = await LineSnapshotAsync(d);
        var settings = await SettingsStateAsync(d);
        await using var reader = new NpgsqlConnection(d.ConnectionString);
        await reader.OpenAsync();
        await using var tx = await reader.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{lockedTable}\" IN ACCESS SHARE MODE", reader, tx)) await hold.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, PrepareHistory));
        clock.Stop();

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s instead of failing after about 5s");
        await tx.RollbackAsync();
        Assert.Equal(4L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Pair))}) AND convalidated"));   // no constraint swap
        Assert.Equal(0L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Needs))})"));
        Assert.DoesNotContain(PrepareHistory, await AppliedAsync(d));
        Assert.Contains(AddReportSettings, await AppliedAsync(d));                          // the earlier migration stays applied: its own transaction
        Assert.Equal(settings, await SettingsStateAsync(d));

        await MigrateExactlyAsync(d, ValidateHistory);   // after the reader released, a retry succeeds

        Assert.Contains(PrepareHistory, await AppliedAsync(d));
        Assert.Contains(ValidateHistory, await AppliedAsync(d));
        Assert.Equal(settings, await SettingsStateAsync(d));
        Assert.Equal(before, await UnchangedSnapshotAsync(d));
        Assert.Equal(lines, await LineSnapshotAsync(d));
    }

    /// <summary>Columns, permission and UQC values of AddTaxReportSettings: what PrepareTaxHistoryBackfill must leave alone.</summary>
    private static async Task<List<string>> SettingsStateAsync(TestDatabase d)
    {
        var all = await NewColumnsAsync(d);
        all.Add("perm=" + await ReportsTaxAsync(d));
        all.AddRange(await RowsAsync(d, """SELECT "ShortCode" || '=' || COALESCE("Uqc", '-') FROM "Units" ORDER BY 1"""));
        all.AddRange(await RowsAsync(d, """SELECT "CompanyId"::text || '=' || COALESCE("HsnRequiredFrom"::text, '-') FROM "BusinessSettings" ORDER BY 1"""));
        all.Add("grants=" + await ScalarAsync<long>(d, """SELECT count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" = 'Reports.Tax'"""));
        return all;
    }

    [Fact]
    public async Task Up_PrepareTaxHistoryBackfill_ASaleHoldingALineRowLockThatThenReadsUnits_CannotDeadlockWithIt_ItFailsFastWith55P03_HoldsNoLockOnUnitsOrBusinessSettings_AndARetryAfterTheSaleSucceeds()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);
        var settings = await SettingsStateAsync(d);
        await using var sale = new NpgsqlConnection(d.ConnectionString);
        await sale.OpenAsync();
        await using var tx = await sale.BeginTransactionAsync();
        var salePid = sale.ProcessID;
        // a sale: FOR UPDATE on a line row (ROW SHARE on the table, held until commit) ...
        await using (var rowLock = new NpgsqlCommand($"""SELECT "Id" FROM "SalesInvoiceLines" WHERE "Id" = '{Fx(94)}' FOR UPDATE""", sale, tx)) Assert.NotNull(await rowLock.ExecuteScalarAsync());

        var migration = Task.Run(() => MigrateExactlyAsync(d, PrepareHistory));
        // ... the migration queues its ACCESS EXCLUSIVE request on that table
        var migratorPid = 0;
        for (var i = 0; i < 100 && migratorPid == 0; i++)
        {
            migratorPid = await ScalarAsync<int>(d, """SELECT COALESCE(max(l.pid), 0) FROM pg_locks l JOIN pg_class c ON c.oid = l.relation WHERE c.relname = 'SalesInvoiceLines' AND NOT l.granted AND l.mode = 'AccessExclusiveLock'""");
            if (migratorPid == 0) await Task.Delay(50);
        }
        Assert.True(migratorPid != 0 && migratorPid != salePid, "the migration never queued for the line table");
        Assert.False(migration.IsCompleted);

        // it holds no lock on Units or BusinessSettings (or anything a sale reads) while it waits: only the line tables ahead of the contended one
        var held = await QueryAsync(d, $"""SELECT c.relname || ':' || l.mode FROM pg_locks l JOIN pg_class c ON c.oid = l.relation WHERE l.pid = {migratorPid} AND l.granted AND c.relnamespace = 'public'::regnamespace AND c.relkind = 'r' ORDER BY 1""", r => r.GetString(0));
        Assert.Empty(held.Where(h => h.StartsWith("Units:", StringComparison.Ordinal) || h.StartsWith("BusinessSettings:", StringComparison.Ordinal) || h.StartsWith("Permissions:", StringComparison.Ordinal) || h.StartsWith("RolePermissions:", StringComparison.Ordinal)));
        Assert.All(held.Select(h => h.Split(':')[0]), t => Assert.Contains(t, Tables.Append("__EFMigrationsHistory")));   // EF's own history table aside
        Assert.Contains("PurchaseReturnLines:AccessExclusiveLock", held);   // the first of the lock list is taken, the third (the sale's) is awaited

        // the sale now reads Units, as a sale does: with the old shape this is the deadlock; now it returns at once
        await using (var read = new NpgsqlCommand("""SELECT count(*) FROM "Units" """, sale, tx) { CommandTimeout = 10 })
            Assert.True((long)(await read.ExecuteScalarAsync())! > 0);
        await using (var read = new NpgsqlCommand("""SELECT count(*) FROM "BusinessSettings" """, sale, tx) { CommandTimeout = 10 })
            Assert.True((long)(await read.ExecuteScalarAsync())! > 0);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => migration);
        var root = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("55P03", root.SqlState);   // lock_timeout, not 40P01 deadlock_detected
        Assert.NotEqual("40P01", root.SqlState);
        // nothing applied, the sale is unharmed
        Assert.DoesNotContain(PrepareHistory, await AppliedAsync(d));
        Assert.Equal(4L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Pair))}) AND convalidated"));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Needs))})"));
        await tx.CommitAsync();

        await MigrateExactlyAsync(d, ValidateHistory);   // a retry after the sale committed succeeds
        Assert.Contains(PrepareHistory, await AppliedAsync(d));
        Assert.All(await StateAsync(d), kv => Assert.True(kv.Value, kv.Key));
        Assert.Equal(settings, await SettingsStateAsync(d));
    }

    [Fact]
    public async Task Up_PrepareTaxHistoryBackfill_ASaleBeingWrittenMakesTheLockWindowFailFast_NotWaitForever_ThenARetryInAQuietMomentWorks()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, AddReportSettings);
        await using var writer = new NpgsqlConnection(d.ConnectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(PurchaseTaxWorld.Clone("SalesInvoiceLines", $"\"Id\" = '{Fx(94)}'", ("Id", $"'{G(0xF901)}'")), writer, tx)) await insert.ExecuteNonQueryAsync();   // ROW EXCLUSIVE, uncommitted

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, PrepareHistory));
        clock.Stop();

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        Assert.InRange(clock.Elapsed.TotalSeconds, 3, 30);
        await tx.CommitAsync();
        await MigrateExactlyAsync(d, ValidateHistory);
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "Id" = '{G(0xF901)}'"""));   // the sale survived
    }

    [Fact]
    public async Task Up_SecondMigration_ASaleBeingWrittenDoesNotStopIt_ItOnlyTakesShareUpdateExclusive()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, PrepareHistory);
        await using var writer = new NpgsqlConnection(d.ConnectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        foreach (var (table, id) in HistoryLine)
            await using (var insert = new NpgsqlCommand(PurchaseTaxWorld.Clone(table, $"\"Id\" = '{id}'", ("Id", $"'{G(0xF910 + Array.IndexOf(Tables, table))}'"), ("TaxProfileId", $"'{ProfileA}'"), ("TaxProfileVersionId", "NULL")), writer, tx))
                await insert.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        await MigrateExactlyAsync(d, ValidateHistory);   // validates while four writes are in flight
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"validation waited {clock.Elapsed.TotalSeconds:0.0}s for the writer");
        await tx.CommitAsync();
        Assert.All(await StateAsync(d), kv => Assert.True(kv.Value, kv.Key));
    }

    [Theory]
    [InlineData("SalesInvoiceLines")]
    [InlineData("PurchaseReturnLines")]
    public async Task Up_SecondMigration_WhenAnotherMaintenanceHoldsTheTable_FailsFastWith55P03_AndRetrySucceeds(string lockedTable)
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, PrepareHistory);
        await using var holder = new NpgsqlConnection(d.ConnectionString);
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{lockedTable}\" IN SHARE UPDATE EXCLUSIVE MODE", holder, tx)) await hold.ExecuteNonQueryAsync();   // e.g. a running VACUUM or another VALIDATE

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, ValidateHistory));

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        await tx.RollbackAsync();
        Assert.DoesNotContain(ValidateHistory, await AppliedAsync(d));
        Assert.All(await StateAsync(d), kv => Assert.False(kv.Value, kv.Key));   // nothing was half validated
        await MigrateExactlyAsync(d, ValidateHistory);
        Assert.All(await StateAsync(d), kv => Assert.True(kv.Value, kv.Key));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the TP-4 build keeps working

    [Theory]
    [InlineData(AddReportSettings)]
    [InlineData(PrepareHistory)]
    [InlineData(ValidateHistory)]
    public async Task OldBuild_InsertingWhatTheTp4BuildWrites_StillSucceeds_WithoutTheNewColumns(string migrateTo)
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var unitColumns = await QueryAsync(d, """SELECT '"' || column_name || '"' FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Units' ORDER BY ordinal_position""", r => r.GetString(0));
        var settingsColumns = await QueryAsync(d, """SELECT '"' || column_name || '"' FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'BusinessSettings' ORDER BY ordinal_position""", r => r.GetString(0));
        await MigrateExactlyAsync(d, migrateTo);
        var counts = await CountsAsync(d);

        // TP-4 build: a sale line and a return line with profile AND version, and a history-shaped line (neither), exactly the shapes the old pair allowed
        await ExecuteAsync(d, PurchaseTaxWorld.Clone("SalesInvoiceLines", $"\"Id\" = '{Intra.Line}'", ("Id", $"'{G(0xF920)}'")));
        await ExecuteAsync(d, PurchaseTaxWorld.Clone("SalesReturnLines", $"\"Id\" = '{Intra.ReturnLine}'", ("Id", $"'{G(0xF921)}'")));
        await ExecuteAsync(d, PurchaseTaxWorld.Clone("SalesInvoiceLines", $"\"Id\" = '{Fx(94)}'", ("Id", $"'{G(0xF922)}'")));
        await ExecuteAsync(d, PurchaseTaxWorld.Clone("PurchaseOrderLines", $"\"Id\" = '{Purchase.Line}'", ("Id", $"'{G(0xF923)}'")));
        await ExecuteAsync(d, PurchaseTaxWorld.Clone("PurchaseReturnLines", $"\"Id\" = '{Purchase.ReturnLine}'", ("Id", $"'{G(0xF924)}'")));
        // the same build knows no Units.Uqc and no BusinessSettings.HsnRequiredFrom: its inserts omit them
        await ExecuteAsync(d, OldInsert("Units", $"\"Id\" = '{Fx(1)}'", unitColumns, "Uqc", ("Id", $"'{G(0xF925)}'"), ("ShortCode", "'oldb'"), ("Name", "'Old build unit'")));
        await ExecuteAsync(d, OldInsert("BusinessSettings", $"\"CompanyId\" = '{CoA}'", settingsColumns, "HsnRequiredFrom", ("Id", $"'{G(0xF926)}'"), ("CompanyId", $"'{CoE}'")));

        var after = await CountsAsync(d);
        Assert.Equal(counts["SalesInvoiceLines"].Total + 2, after["SalesInvoiceLines"].Total);
        Assert.Equal(counts["SalesInvoiceLines"].WithVersion + 1, after["SalesInvoiceLines"].WithVersion);
        Assert.Equal(counts["SalesReturnLines"].WithVersion + 1, after["SalesReturnLines"].WithVersion);
        Assert.Equal(counts["PurchaseOrderLines"].WithVersion + 1, after["PurchaseOrderLines"].WithVersion);
        Assert.Equal(counts["PurchaseReturnLines"].WithVersion + 1, after["PurchaseReturnLines"].WithVersion);
        Assert.Equal(["-"], await RowsAsync(d, $"""SELECT COALESCE("Uqc", '-') FROM "Units" WHERE "Id" = '{G(0xF925)}'"""));   // no UQC: the report falls back to the short code
        Assert.Equal(["-"], await RowsAsync(d, $"""SELECT COALESCE("HsnRequiredFrom"::text, '-') FROM "BusinessSettings" WHERE "Id" = '{G(0xF926)}'"""));   // a new company: warning only
    }

    /// <summary>An INSERT the previous build could write: the columns of the table BEFORE the migration (the new one is omitted).</summary>
    private static string OldInsert(string table, string where, List<string> columns, string omitted, params (string Column, string Expr)[] set)
    {
        var list = string.Join(", ", columns.Where(c => c != $"\"{omitted}\""));
        var assignments = string.Join(", ", set.Select(s => $"\"{s.Column}\" = {s.Expr}"));
        return $"""
            CREATE TEMP TABLE _old_insert AS SELECT * FROM "{table}" WHERE {where};
            UPDATE _old_insert SET {assignments};
            INSERT INTO "{table}" ({list}) SELECT {list} FROM _old_insert;
            DROP TABLE _old_insert;
            """;
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Down / Up and the script

    [Fact]
    public async Task DownThenUp_RoundTrip_RestoresTheTp4Schema_AndComesBackIdentical()
    {
        await using var d = await Tp4ShapedCopyAsync(postgres);
        var tp4Schema = await SchemaSignatureAsync(d);
        var tp4Lines = await LineSnapshotAsync(d);
        var tp4Money = await UnchangedSnapshotAsync(d);
        var tp4Data = await DataFingerprintAsync(d);

        await MigrateExactlyAsync(d, ValidateHistory);
        var headSchema = await SchemaSignatureAsync(d);
        var headData = await DataFingerprintAsync(d);
        Assert.NotEqual(tp4Schema, headSchema);

        await MigrateExactlyAsync(d, ValidateSales);   // Down: ValidateTaxHistoryBackfill (nothing to undo), PrepareTaxHistoryBackfill, then AddTaxReportSettings

        Assert.DoesNotContain(AddReportSettings, await AppliedAsync(d));
        Assert.DoesNotContain(PrepareHistory, await AppliedAsync(d));
        Assert.DoesNotContain(ValidateHistory, await AppliedAsync(d));
        Assert.Empty(await NewColumnsAsync(d));
        Assert.Equal(0L, await ReportsTaxAsync(d));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" WHERE p."Code" = 'Reports.Tax'"""));
        Assert.Equal(tp4Schema, await SchemaSignatureAsync(d));   // the old pair is back, VALIDATED
        Assert.Equal(4L, await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Tables.Select(Pair))}) AND convalidated"));
        Assert.Equal(tp4Lines, await LineSnapshotAsync(d));
        Assert.Equal(tp4Money, await UnchangedSnapshotAsync(d));
        Assert.Equal(tp4Data, await DataFingerprintAsync(d));

        await MigrateExactlyAsync(d, ValidateHistory);   // Up again

        Assert.Equal(headSchema, await SchemaSignatureAsync(d));
        Assert.Equal(headData, await DataFingerprintAsync(d));
        Assert.All(await StateAsync(d), kv => Assert.True(kv.Value, kv.Key));
    }

    [Fact]
    public async Task Down_AfterTheBackfill_ClearsTheProfileOfHistoryLines_ButNeverTheTp3Tp4Lines_AndTheBackfillCanRunAgain()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await BackfillScript.RunAsync(d);
        var counts = await CountsAsync(d);
        Assert.All(LineTables, t => Assert.True(counts[t].ProfileOnly > 0, t));
        var withVersion = new Dictionary<string, string>();
        foreach (var table in LineTables) withVersion[table] = await FingerprintAsync(d, table, "\"TaxProfileVersionId\" IS NOT NULL");
        var filled = await LineSnapshotAsync(d);

        await MigrateExactlyAsync(d, ValidateSales);

        var down = await CountsAsync(d);
        foreach (var table in LineTables)
        {
            Assert.Equal(0L, down[table].ProfileOnly);                                                          // the pair constraint is back: a profile alone is impossible
            Assert.Equal(counts[table].WithVersion, down[table].WithVersion);
            Assert.Equal(withVersion[table], await FingerprintAsync(d, table, "\"TaxProfileVersionId\" IS NOT NULL"));
        }

        await MigrateExactlyAsync(d, ValidateHistory);
        await BackfillScript.RunAsync(d);
        Assert.Equal(filled, await LineSnapshotAsync(d));   // the same result as the first run
    }

    [Fact]
    public async Task Up_IdempotentScriptOfAllThreeMigrations_AppliesThroughNpgsql_MatchesTheEfPath_AndRerunsAsANoOp()
    {
        await using var viaScript = await Tp4ShapedCopyAsync(postgres);
        await using var viaEf = await Tp4ShapedCopyAsync(postgres);
        var script = await ScriptAsync(viaScript, ValidateSales, ValidateHistory);

        await ExecuteAsync(viaScript, script);
        await MigrateExactlyAsync(viaEf, ValidateHistory);

        Assert.Contains(AddReportSettings, await AppliedAsync(viaScript));
        Assert.Contains(PrepareHistory, await AppliedAsync(viaScript));
        Assert.Contains(ValidateHistory, await AppliedAsync(viaScript));
        Assert.Equal(await SchemaSignatureAsync(viaEf), await SchemaSignatureAsync(viaScript));
        Assert.Equal(await DataFingerprintAsync(viaEf), await DataFingerprintAsync(viaScript));
        Assert.Equal(await StateAsync(viaEf), await StateAsync(viaScript));
        Assert.Equal(await RowsAsync(viaEf, """SELECT "ShortCode" || '=' || COALESCE("Uqc", '-') FROM "Units" ORDER BY 1"""), await RowsAsync(viaScript, """SELECT "ShortCode" || '=' || COALESCE("Uqc", '-') FROM "Units" ORDER BY 1"""));
        var data = await DataFingerprintAsync(viaScript);
        var schema = await SchemaSignatureAsync(viaScript);

        await ExecuteAsync(viaScript, script);   // a second run is a no-op

        Assert.Equal(data, await DataFingerprintAsync(viaScript));
        Assert.Equal(schema, await SchemaSignatureAsync(viaScript));
        Assert.Equal(await LineSnapshotAsync(viaEf), await LineSnapshotAsync(viaScript));
    }

    [Fact]
    public async Task Up_FromAnEmptyTp1Database_AndFromTheWholeHistory_BothReachTheSameSchema()
    {
        await using var empty = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(empty, ValidateFks);
        await using var tp4 = await Tp4ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(empty, ValidateHistory);
        await MigrateExactlyAsync(tp4, ValidateHistory);

        var emptySchema = await SchemaSignatureAsync(empty);
        var tp4Schema = await SchemaSignatureAsync(tp4);
        Assert.Empty(emptySchema.Except(tp4Schema));
        Assert.Empty(tp4Schema.Except(emptySchema));
        Assert.Equal(1L, await ReportsTaxAsync(empty));   // the migration creates the permission even without any role to grant it to
    }
}
