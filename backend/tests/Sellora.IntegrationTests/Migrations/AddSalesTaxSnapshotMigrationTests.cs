using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Data.Migrations;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.SalesTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// <c>AddSalesTaxSnapshot</c> + <c>ValidateSalesTaxConstraints</c> (docs/features/tax-profiles.md, "TP-4 design", Migration): purely additive, no table rewrite, the D2 catch-up of lines the TP-3 build saved without a
/// tax row (run before the lock), ONE short ACCESS EXCLUSIVE window that holds only metadata ALTERs, every constraint NOT VALID first and validated by the second migration, the partial indexes (pre-created CONCURRENTLY
/// by the runbook or built by the migration, a failed concurrent build refused), the old build still inserting, lock_timeout fail-fast, Down/Up and the idempotent script. All on
/// <see cref="SalesTaxWorld.Tp3ShapedCopyAsync"/>: the TP-3 schema with IN and AE sales (walk-in, other-state, no-state customers, a branch without a state), sales returns and gap lines of the TP-3 build.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddSalesTaxSnapshotMigrationTests(PostgresFixture postgres)
{
    private static readonly string[] LineTaxTables = ["PurchaseOrderLineTaxes", "PurchaseReturnLineTaxes", "SalesReturnLineTaxes", "SalesInvoiceLineTaxes"];
    private static readonly string[] RewriteWatch = ["SalesInvoices", "SalesInvoiceLines", "SalesReturns", "SalesReturnLines", "SalesInvoiceLineTaxes", "SalesReturnLineTaxes", "PurchaseOrders", "PurchaseOrderLines"];

    private static readonly string[] InvoiceColumns = ["BillDiscountPercent", "BillDiscountAmount", "BillCharges", "TaxableTotal", "PlaceOfSupply", "PlaceOfSupplyStateId", "SellerStateId", "PartyTaxStatus"];
    private static readonly string[] LineColumns = ["TaxProfileId", "TaxProfileVersionId", "PlaceOfSupply", "HsnCode", "PriceIncludesTax", "BillDiscountShare", "BillChargeShare"];
    private static readonly string[] ReturnLineColumns = ["TaxProfileId", "TaxProfileVersionId", "PlaceOfSupply"];

    /// <summary>Constraint to table, in the order the migration adds them.</summary>
    public static readonly (string Table, string Name)[] Constraints =
    [
        ("SalesInvoices", "CK_SalesInvoices_PlaceOfSupply"), ("SalesInvoices", "CK_SalesInvoices_PartyTaxStatus"), ("SalesInvoices", "CK_SalesInvoices_BillDiscountPercent_Range"),
        ("SalesInvoices", "CK_SalesInvoices_BillAmounts_NonNegative"), ("SalesInvoices", "FK_SalesInvoices_States_PlaceOfSupplyStateId"), ("SalesInvoices", "FK_SalesInvoices_States_SellerStateId"),
        ("SalesInvoiceLines", "CK_SalesInvoiceLines_PlaceOfSupply"), ("SalesInvoiceLines", "CK_SalesInvoiceLines_TaxProfile_Pair"), ("SalesInvoiceLines", "CK_SalesInvoiceLines_HsnCode_Format"),
        ("SalesInvoiceLines", "CK_SalesInvoiceLines_BillShares_NonNegative"), ("SalesInvoiceLines", "FK_SalesInvoiceLines_TaxProfiles_TaxProfileId"),
        ("SalesInvoiceLines", "FK_SalesInvoiceLines_TaxProfileVersions_TaxProfileVersionId"),
        ("SalesReturnLines", "CK_SalesReturnLines_PlaceOfSupply"), ("SalesReturnLines", "CK_SalesReturnLines_TaxProfile_Pair"),
        ("SalesReturnLines", "FK_SalesReturnLines_TaxProfiles_TaxProfileId"), ("SalesReturnLines", "FK_SalesReturnLines_TaxProfileVersions_TaxProfileVersionId"),
    ];

    private static readonly string[] PartialIndexes = ["IX_SalesInvoiceLines_TaxProfileId", "IX_SalesInvoiceLines_TaxProfileVersionId"];
    private static readonly string[] PlainIndexes =
        ["IX_SalesReturnLines_TaxProfileId", "IX_SalesReturnLines_TaxProfileVersionId", "IX_SalesInvoices_PlaceOfSupplyStateId", "IX_SalesInvoices_SellerStateId"];
    private static string[] AllIndexes => [.. PartialIndexes, .. PlainIndexes];

    private static Exception Root(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is PostgresException || e.InnerException is null) return e;
        return ex;
    }

    private static async Task<List<string>> RowsAsync(TestDatabase d, string sql) =>
        await QueryAsync(d, sql, r => string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "-" : Format(r.GetValue(i)))));

    private static string InList(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    private static Task<List<string>> FilenodesAsync(TestDatabase d) =>
        RowsAsync(d, $"""SELECT relname || '=' || relfilenode::text FROM pg_class WHERE relkind = 'r' AND relnamespace = 'public'::regnamespace AND relname IN ({InList(RewriteWatch)}) ORDER BY 1""");

    private static async Task<Dictionary<string, bool>> ConstraintStateAsync(TestDatabase d) =>
        (await QueryAsync(d, $"""SELECT conname, convalidated FROM pg_constraint WHERE conname IN ({InList(Constraints.Select(c => c.Name))})""", r => (r.GetString(0), r.GetBoolean(1)))).ToDictionary(x => x.Item1, x => x.Item2);

    private static Task<List<string>> NewColumnsAsync(TestDatabase d) => QueryAsync(d, $"""
        SELECT table_name || '.' || column_name FROM information_schema.columns
        WHERE table_schema = 'public' AND (
          (table_name = 'SalesInvoices' AND column_name IN ({InList(InvoiceColumns)})) OR (table_name = 'SalesInvoiceLines' AND column_name IN ({InList(LineColumns)}))
          OR (table_name = 'SalesReturnLines' AND column_name IN ({InList(ReturnLineColumns)})))
        ORDER BY 1
        """, r => r.GetString(0));

    private static Task<List<string>> LineTaxCountsAsync(TestDatabase d) => RowsAsync(d,
        string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}=' || count(*) FROM "{t}" """)) + " ORDER BY 1");

    private static Task<List<string>> IndexesAsync(TestDatabase d) => RowsAsync(d, $"""
        SELECT c.relname || '|' || i.indisvalid || '|' || c.relfilenode FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relname IN ({InList(AllIndexes)}) ORDER BY 1
        """);

    private static async Task CreateConcurrentlyAsync(TestDatabase d)
    {
        // exactly the two statements of the runbook (scripts/sql/tax-sales-release-notes.sql), each in its own autocommit call
        foreach (var statement in new[]
        {
            """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SalesInvoiceLines_TaxProfileId" ON "SalesInvoiceLines" ("TaxProfileId") WHERE "TaxProfileId" IS NOT NULL""",
            """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SalesInvoiceLines_TaxProfileVersionId" ON "SalesInvoiceLines" ("TaxProfileVersionId") WHERE "TaxProfileVersionId" IS NOT NULL""",
        })
        {
            await using var connection = new NpgsqlConnection(d.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static readonly (Guid Line, string Lines, string Rows, string Column)[] Gaps =
    [
        (GapSalesLine2, "SalesInvoiceLines", "SalesInvoiceLineTaxes", "SalesInvoiceLineId"), (GapSalesReturnLine2, "SalesReturnLines", "SalesReturnLineTaxes", "SalesReturnLineId"),
    ];

    // ------------------------------------------------------------------------------------------------------------------------------ the starting point

    [Fact]
    public async Task Tp3ShapedWorld_HasTheDocumentsTheMigrationTestsRelyOn()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);

        Assert.Contains(ValidatePurchase, await AppliedAsync(d));
        Assert.DoesNotContain(SalesSnapshot, await AppliedAsync(d));
        Assert.Equal(0L, (await NewColumnsAsync(d)).Count);
        foreach (var (line, lines, rows, column) in Gaps)
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{rows}" WHERE "{column}" = '{line}'"""));   // the gap
        // the customers and branches the TP-4 place-of-supply rules distinguish
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "Customers" WHERE "Id" = '{CustOther}' AND NOT "IsWalkIn" AND "StateId" IS NOT NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "Customers" WHERE "Id" = '{CustNoState}' AND NOT "IsWalkIn" AND "StateId" IS NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "Customers" WHERE "Id" = '{CustWalkState}' AND "IsWalkIn" AND "StateId" IS NOT NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "Branches" WHERE "Id" = '{BranchNoState}' AND "StateId" IS NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoices" WHERE "Id" = '{SiNoBranchState}' AND "BranchId" = '{BranchNoState}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoices" WHERE "Id" = '{SiB}' AND "CompanyId" = '{CoB}'"""));
        Assert.True(await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesReturns" """) >= 3);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ defaults and no rewrite

    [Fact]
    public async Task Up_HistoryKeepsItsValues_DefaultsAreConstant_AndEveryNullableColumnIsNull()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var before = await UnchangedSnapshotAsync(d);
        var invoices = await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesInvoices" """);
        var lines = await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesInvoiceLines" """);
        var returnLines = await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesReturnLines" """);
        Assert.True(invoices >= 8 && lines >= 12 && returnLines >= 3, $"{invoices}/{lines}/{returnLines}");

        await MigrateExactlyAsync(d, ValidateSales);

        Assert.Equal(before, await UnchangedSnapshotAsync(d));   // money columns, mirrors and TaxIds of history are untouched
        Assert.Equal(invoices, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "SalesInvoices" WHERE "BillDiscountAmount" = 0 AND "BillCharges" = 0 AND "PartyTaxStatus" = 'Unspecified' AND "BillDiscountPercent" IS NULL AND "TaxableTotal" IS NULL
              AND "PlaceOfSupply" IS NULL AND "PlaceOfSupplyStateId" IS NULL AND "SellerStateId" IS NULL
            """));   // TaxableTotal NULL = legacy: the DTO falls back to Subtotal - DiscountTotal
        Assert.Equal(lines, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "SalesInvoiceLines" WHERE NOT "PriceIncludesTax" AND "BillDiscountShare" = 0 AND "BillChargeShare" = 0 AND "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NULL
              AND "PlaceOfSupply" IS NULL AND "HsnCode" IS NULL
            """));   // history lines were exclusive: PriceIncludesTax defaults to false
        Assert.Equal(returnLines, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "SalesReturnLines" WHERE "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NULL AND "PlaceOfSupply" IS NULL
            """));
        Assert.Equal(InvoiceColumns.Length + LineColumns.Length + ReturnLineColumns.Length, (await NewColumnsAsync(d)).Count);
    }

    [Fact]
    public async Task Up_RewritesNoTable_NeitherTheFirstMigrationNorTheSecond()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var before = await FilenodesAsync(d);
        Assert.Equal(RewriteWatch.Length, before.Count);

        await MigrateExactlyAsync(d, SalesSnapshot);
        var afterFirst = await FilenodesAsync(d);
        await MigrateExactlyAsync(d, ValidateSales);
        var afterSecond = await FilenodesAsync(d);

        Assert.Equal(before, afterFirst);    // every column is nullable or has a constant default: ADD COLUMN is metadata only; the catch-up only INSERTs
        Assert.Equal(before, afterSecond);   // VALIDATE CONSTRAINT and CREATE INDEX do not rewrite the table
    }

    // ------------------------------------------------------------------------------------------------------------------------------ catch-up (D2)

    [Fact]
    public async Task Up_CatchUp_InsertsExactlyOneLegacyRowPerGapLine_AndNothingElse()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var before = await LineTaxCountsAsync(d);
        var existing = await RowsAsync(d, string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)));

        await MigrateExactlyAsync(d, SalesSnapshot);

        var after = await LineTaxCountsAsync(d);
        Assert.Equal(before.Select(b =>
        {
            var parts = b.Split('=');
            return parts[0] + "=" + (long.Parse(parts[1]) + (parts[0].StartsWith("Sales", StringComparison.Ordinal) ? 1 : 0));   // one gap line of each sales kind; the purchase kinds were caught up by TP-3
        }), after);
        foreach (var (line, lines, rows, column) in Gaps)
        {
            var row = await RowsAsync(d, $"""
                SELECT x."Id" || '|' || COALESCE(x."TaxComponentId"::text, '-') || '|' || x."Kind" || '|' || x."Method" || '|' || x."RatePercent" || '|' || COALESCE(x."Treatment", '-') || '|' || x."ComponentCode"
                FROM "{rows}" x WHERE x."{column}" = '{line}'
                """);
            Assert.Single(row);
            var f = row[0].Split('|');
            Assert.Equal(TaxSeedData.LegacyLineTaxId(line).ToString(), f[0]);     // the id the write paths use: an idempotent catch-up can never duplicate
            Assert.Equal(["-", "Other", "Percent"], f[1..4]);                    // legacy row: no component, Kind Other
            Assert.Equal("-", f[5]);                                            // no Treatment: a legacy row
        }
        var nowRows = await RowsAsync(d, string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)));
        Assert.Empty(existing.Except(nowRows));
        Assert.Equal(2, nowRows.Except(existing).Count());
        // the lines themselves are untouched: still no profile, version or place
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "Id" = '{GapSalesLine2}' AND ("TaxProfileId" IS NOT NULL OR "PlaceOfSupply" IS NOT NULL)"""));
    }

    [Fact]
    public async Task Up_CatchUp_ALegacyRowCarriesTheLineValues()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(d, SalesSnapshot);

        Assert.Equal(["5|50|2.5|GST"], await RowsAsync(d, $"""SELECT x."RatePercent", x."BaseAmount", x."TaxAmount", x."ComponentCode" FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = '{GapSalesLine2}'"""));
        Assert.Equal(["5|0|0|GST"], await RowsAsync(d, $"""SELECT x."RatePercent", x."BaseAmount", x."TaxAmount", x."ComponentCode" FROM "SalesReturnLineTaxes" x WHERE x."SalesReturnLineId" = '{GapSalesReturnLine2}'"""));
        // V__tax-line-rows-missing.sql is a BLOCK file from TP-4: after the migration it has nothing to report
        await ValidationScripts.AssertZeroRowsAsync(d, "tax-line-rows-missing.sql", minimumStatements: 1, "after AddSalesTaxSnapshot");
    }

    [Fact]
    public async Task Up_CatchUp_TheBlockingMissingRowsFileReportsTheGapBeforeTheMigration_AndNothingAfter()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var before = await InRolledBackTransactionAsync(d, async p => await p.ViolationsAsync("tax-line-rows-missing.sql"));
        Assert.Equal([$"SalesInvoiceLines|{GapSalesLine2}", $"SalesReturnLines|{GapSalesReturnLine2}"], before.Order(StringComparer.Ordinal).ToList());

        await MigrateExactlyAsync(d, SalesSnapshot);

        Assert.Empty(await InRolledBackTransactionAsync(d, async p => await p.ViolationsAsync("tax-line-rows-missing.sql")));
    }

    [Fact]
    public async Task Up_RerunningTheLegacyRowsFile_IsANoOp_AndItsShaIsUnchanged()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, ValidateSales);
        var counts = await LineTaxCountsAsync(d);
        var ids = await RowsAsync(d, string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)));

        await InRolledBackTransactionAsync(d, async p =>
        {
            await p.RunResourceAsync(LegacyRowsResource);
            Assert.Equal(counts, await p.RowsAsync(string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}=' || count(*) FROM "{t}" """)) + " ORDER BY 1"));
            Assert.Equal(ids.Order().ToList(), (await p.RowsAsync(string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)))).Order().ToList());
        });
        Assert.Equal(counts, await LineTaxCountsAsync(d));

        // the frozen resource is unchanged (same pin as backend/tests/Sellora.Tests/TaxProfiles/TaxMigrationSqlTests) and the migration runs exactly that text
        const string pin = "284ec03227940bea0c54bb137d0f4a85e21c2c2118233ed3a5a9643967068afe";
        var embedded = MigrationSql.Read(LegacyRowsResource);
        Assert.Equal(pin, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(embedded))).ToLowerInvariant());
    }

    [Fact]
    public async Task Up_CatchUpOfARerun_AfterTheTp4BuildSavedSales_NeverTouchesThem()
    {
        // a TP-4 sale (profile set, component rows) saved before the legacy-rows file is re-run by an operator: it has rows, so the file must not add a legacy row to it
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, ValidateSales);
        await ExecuteAsync(d, ValidSalesSql(SalesDocAt(0xD00), "SAL-TP4A", "SRT-TP4A"));
        var counts = await LineTaxCountsAsync(d);

        await InRolledBackTransactionAsync(d, async p =>
        {
            await p.RunResourceAsync(LegacyRowsResource);
            Assert.Equal(counts, await p.RowsAsync(string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}=' || count(*) FROM "{t}" """)) + " ORDER BY 1"));
        });
    }

    [Fact]
    public async Task Up_CatchUp_RunsBeforeTheLock_ItIsTheFirstWorkOfTheMigration()
    {
        // the order is part of the design (lock window = metadata only): lock_timeout, the frozen file, ONE LOCK TABLE, then nothing but ALTER TABLE
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var script = await ScriptAsync(d, ValidatePurchase, SalesSnapshot);

        var sharedAt = script.IndexOf("LOCK TABLE \"States\", \"TaxProfiles\", \"TaxProfileVersions\" IN SHARE ROW EXCLUSIVE MODE", StringComparison.Ordinal);   // referenced tables of the NOT VALID foreign keys
        var lockAt = script.IndexOf("LOCK TABLE \"SalesInvoices\"", StringComparison.Ordinal);
        var timeoutAt = script.IndexOf("lock_timeout", StringComparison.Ordinal);
        var catchUpAt = script.IndexOf("SalesInvoiceLineTaxes", StringComparison.Ordinal);
        Assert.True(timeoutAt >= 0 && timeoutAt < catchUpAt && catchUpAt < sharedAt && sharedAt < lockAt, $"order: lock_timeout {timeoutAt}, catch-up {catchUpAt}, shared lock {sharedAt}, exclusive lock {lockAt}");
        Assert.Equal(2, Regex.Matches(script, "LOCK TABLE", RegexOptions.None).Count);   // the referenced tables first (taken before the sales tables are frozen), then ONE exclusive window
        Assert.Contains("ACCESS EXCLUSIVE", script[lockAt..(lockAt + 200)], StringComparison.Ordinal);
        Assert.Matches(new Regex("LOCK TABLE \"SalesInvoices\", \"SalesInvoiceLines\", \"SalesReturnLines\""), script);   // the application's own lock order
        var afterLock = script[(lockAt + 1)..];
        var statements = afterLock.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(s => string.Join(' ', s.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("--", StringComparison.Ordinal) && l.Length > 0))).Where(s => s.Length > 0).ToList();
        // everything after the LOCK TABLE up to the history INSERT of the migrations table is catalog-only: ALTER TABLE (add column, add NOT VALID constraint), END IF / END $$ of the script wrapper, and the history row
        var offenders = statements.Where(s =>
            !s.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal)
            && !s.StartsWith("END", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("DO", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(offenders.Count == 0, "statements after the exclusive lock that are not ALTER TABLE: " + string.Join(" || ", offenders.Select(o => o[..Math.Min(o.Length, 100)])));
        Assert.DoesNotContain("VALIDATE CONSTRAINT", afterLock, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", afterLock, StringComparison.OrdinalIgnoreCase);
        Assert.All(statements.Where(s => s.Contains("ADD CONSTRAINT", StringComparison.Ordinal)), s => Assert.EndsWith("NOT VALID", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Up_WhileTheCatchUpWaitsForARowLock_SalesStillBeInsertedAtOnce_ThenTheMigrationFinishes()
    {
        // The catch-up runs BEFORE the ACCESS EXCLUSIVE window and takes row locks only. Hold the gap line's row (FOR UPDATE: the new tax row's foreign-key check waits for it):
        // the migration parks in its catch-up while a sale is inserted and committed on another connection without waiting.
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await using var holder = new NpgsqlConnection(d.ConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"""SELECT 1 FROM "SalesInvoiceLines" WHERE "Id" = '{GapSalesLine2}' FOR UPDATE""", holder, holderTx)) await hold.ExecuteNonQueryAsync();

        var migration = Task.Run(() => MigrateExactlyAsync(d, SalesSnapshot));
        var waiting = false;
        for (var i = 0; i < 40 && !waiting; i++)
        {
            await Task.Delay(100);
            waiting = await ScalarAsync<long>(d, """SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%SalesInvoiceLineTaxes%' AND pid <> pg_backend_pid()""") > 0;
        }
        Assert.True(waiting, "the migration never reached its catch-up row-lock wait");

        var clock = Stopwatch.StartNew();
        await ExecuteAsync(d, $"""
            {PurchaseTaxWorld.Clone("SalesInvoices", $"\"Id\" = '{Fx(82)}'", ("Id", Q(G(0xC80))), ("InvoiceNo", "'SAL-DURING'"))}
            {PurchaseTaxWorld.Clone("SalesInvoiceLines", $"\"Id\" = '{Fx(94)}'", ("Id", Q(G(0xC81))), ("SalesInvoiceId", Q(G(0xC80))))}
            """);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"a sales insert waited {clock.Elapsed.TotalSeconds:0.0}s for the migration's catch-up");

        await holderTx.RollbackAsync();
        await migration;

        Assert.Contains(SalesSnapshot, await AppliedAsync(d));
        Assert.Equal(1L, await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesInvoices" WHERE "InvoiceNo" = 'SAL-DURING' AND "PartyTaxStatus" = 'Unspecified' AND "TaxableTotal" IS NULL"""));
        // the sale saved during the catch-up has no row (it was not there when the file looked): the operator re-runs the file (R1 report), which is idempotent
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" l WHERE l."Id" = '{G(0xC81)}' AND NOT EXISTS (SELECT 1 FROM "SalesInvoiceLineTaxes" x WHERE x."SalesInvoiceLineId" = l."Id")"""));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ NOT VALID then VALIDATE

    [Fact]
    public async Task Constraints_AreNotValidAfterTheFirstMigration_AndValidatedAfterTheSecond_WithTheSixIndexes()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(d, SalesSnapshot);

        var first = await ConstraintStateAsync(d);
        Assert.Equal(Constraints.Length, first.Count);
        Assert.All(first, kv => Assert.False(kv.Value, kv.Key + " must be NOT VALID after AddSalesTaxSnapshot"));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(AllIndexes)})"""));
        // not validated does not mean not enforced: a new row violating one is refused
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, $"""UPDATE "SalesInvoices" SET "PlaceOfSupply" = 'Nowhere' WHERE "Id" = '{SiOther}'"""));
        Assert.Equal(("23514", "CK_SalesInvoices_PlaceOfSupply"), (ex.SqlState, ex.ConstraintName));

        await MigrateExactlyAsync(d, ValidateSales);

        var second = await ConstraintStateAsync(d);
        Assert.Equal(Constraints.Length, second.Count);
        Assert.All(second, kv => Assert.True(kv.Value, kv.Key + " must be validated after ValidateSalesTaxConstraints"));
        Assert.Equal(AllIndexes.Order().ToList(), (await RowsAsync(d, $"""SELECT indexname FROM pg_indexes WHERE indexname IN ({InList(AllIndexes)}) ORDER BY 1""")).ToList());
        foreach (var (table, name) in Constraints)
            Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE c.conname = '{name}' AND t.relname = '{table}'"""));
    }

    [Fact]
    public async Task Constraints_Foreign_AreRestrict()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, ValidateSales);

        var fks = await RowsAsync(d, """
            SELECT conname || '|' || confdeltype::text FROM pg_constraint
            WHERE conname IN ('FK_SalesInvoices_States_PlaceOfSupplyStateId', 'FK_SalesInvoices_States_SellerStateId', 'FK_SalesInvoiceLines_TaxProfiles_TaxProfileId',
                              'FK_SalesInvoiceLines_TaxProfileVersions_TaxProfileVersionId', 'FK_SalesReturnLines_TaxProfiles_TaxProfileId', 'FK_SalesReturnLines_TaxProfileVersions_TaxProfileVersionId') ORDER BY 1
            """);

        Assert.Equal(6, fks.Count);
        Assert.All(fks, f => Assert.EndsWith("|r", f, StringComparison.Ordinal));   // history never loses its profile, version or state
    }

    [Fact]
    public async Task Up_SecondMigration_ARowThatViolatesANotValidConstraint_FailsTheValidation_AndRollsBackEverything()
    {
        // NOT VALID adds without scanning, VALIDATE then checks the rows that existed: a non-conforming row (written while the check was absent) must stop the second migration with the constraint's name
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await ExecuteAsync(d, """ALTER TABLE "SalesInvoices" DROP CONSTRAINT "CK_SalesInvoices_PlaceOfSupply" """);
        await ExecuteAsync(d, $"""UPDATE "SalesInvoices" SET "PlaceOfSupply" = 'Nowhere' WHERE "Id" = '{SiOther}'""");
        await ExecuteAsync(d, """ALTER TABLE "SalesInvoices" ADD CONSTRAINT "CK_SalesInvoices_PlaceOfSupply" CHECK ("PlaceOfSupply" IS NULL OR "PlaceOfSupply" IN ('IntraState','InterState','NotApplicable')) NOT VALID""");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, ValidateSales));

        Assert.Equal(("23514", "CK_SalesInvoices_PlaceOfSupply"), (Assert.IsType<PostgresException>(Root(ex)).SqlState, ((PostgresException)Root(ex)).ConstraintName));
        Assert.DoesNotContain(ValidateSales, await AppliedAsync(d));   // one transaction: nothing validated, no index
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(AllIndexes)})"""));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.False(v));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ partial indexes (TP4-Q9)

    [Fact]
    public async Task Indexes_WhenNotPreCreated_TheSecondMigrationBuildsTheTwoPartialIndexes_WithTheDesignedPredicate()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);

        await MigrateExactlyAsync(d, ValidateSales);

        var defs = await RowsAsync(d, $"""SELECT indexdef FROM pg_indexes WHERE indexname IN ({InList(PartialIndexes)}) ORDER BY indexname""");
        Assert.Equal(2, defs.Count);
        Assert.Contains("(\"TaxProfileId\")", defs[0], StringComparison.Ordinal);
        Assert.Contains("WHERE (\"TaxProfileId\" IS NOT NULL)", defs[0], StringComparison.Ordinal);
        Assert.Contains("(\"TaxProfileVersionId\")", defs[1], StringComparison.Ordinal);
        Assert.Contains("WHERE (\"TaxProfileVersionId\" IS NOT NULL)", defs[1], StringComparison.Ordinal);
        Assert.All(await IndexesAsync(d), i => Assert.Contains("|true|", i, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Indexes_PreCreatedConcurrentlyBetweenTheTwoMigrations_AreKept_NotRebuilt_AndKeepThePredicate()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await CreateConcurrentlyAsync(d);   // the runbook step: sales keep flowing
        var before = await IndexesAsync(d);
        Assert.Equal(2, before.Count);
        Assert.All(before, i => Assert.Contains("|true|", i, StringComparison.Ordinal));

        await MigrateExactlyAsync(d, ValidateSales);

        var after = await IndexesAsync(d);
        Assert.Equal(PartialIndexes.Length + PlainIndexes.Length, after.Count);
        foreach (var kept in before) Assert.Contains(kept, after);   // same relfilenode: IF NOT EXISTS skipped the build
        var defs = await RowsAsync(d, $"""SELECT indexdef FROM pg_indexes WHERE indexname IN ({InList(PartialIndexes)})""");
        Assert.All(defs, def => Assert.Matches(new Regex(@"WHERE \(""TaxProfile(Version)?Id"" IS NOT NULL\)"), def));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Fact]
    public async Task Indexes_PreCreatedConcurrently_ThenDownOfTheSecondMigration_DropsThem_AndUpCreatesThemAgain()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await CreateConcurrentlyAsync(d);
        await MigrateExactlyAsync(d, ValidateSales);

        await MigrateExactlyAsync(d, SalesSnapshot);   // Down of ValidateSalesTaxConstraints

        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(AllIndexes)})"""));   // also the ones the runbook created (same names)
        await MigrateExactlyAsync(d, ValidateSales);
        Assert.Equal(AllIndexes.Length, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(AllIndexes)})"""));
    }

    [Theory]
    [InlineData("IX_SalesInvoiceLines_TaxProfileId")]
    [InlineData("IX_SalesInvoiceLines_TaxProfileVersionId")]
    public async Task Indexes_AnInvalidLeftoverOfAFailedConcurrentBuild_MakesTheSecondMigrationFail_AndNothingIsApplied(string invalidIndex)
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await CreateConcurrentlyAsync(d);
        // what a cancelled CREATE INDEX CONCURRENTLY leaves behind: an index of the same name with indisvalid = false (which IF NOT EXISTS alone would silently accept)
        await ExecuteAsync(d, $"""UPDATE pg_index SET indisvalid = false WHERE indexrelid = '"{invalidIndex}"'::regclass""");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, ValidateSales));

        var pg = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("P0001", pg.SqlState);
        Assert.Contains("INVALID index", pg.MessageText, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX", pg.MessageText, StringComparison.Ordinal);   // the message tells the operator what to do
        Assert.DoesNotContain(ValidateSales, await AppliedAsync(d));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.False(v));   // the VALIDATE statements before the guard rolled back with it
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(PlainIndexes)})"""));

        // the documented remedy: drop it, re-run the runbook statement, re-run the migration
        await ExecuteAsync(d, $"""DROP INDEX "{invalidIndex}" """);
        await CreateConcurrentlyAsync(d);
        await MigrateExactlyAsync(d, ValidateSales);

        Assert.Contains(ValidateSales, await AppliedAsync(d));
        Assert.All((await IndexesAsync(d)).Where(i => PartialIndexes.Any(n => i.StartsWith(n, StringComparison.Ordinal))), i => Assert.Contains("|true|", i, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Indexes_TheGuardLooksOnlyAtTheTwoNames_AnInvalidIndexOfAnotherName_DoesNotStopTheMigration()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await ExecuteAsync(d, """CREATE INDEX "IX_unrelated_lines" ON "SalesInvoiceLines" ("Qty")""");
        await ExecuteAsync(d, """UPDATE pg_index SET indisvalid = false WHERE indexrelid = '"IX_unrelated_lines"'::regclass""");

        await MigrateExactlyAsync(d, ValidateSales);

        Assert.Contains(ValidateSales, await AppliedAsync(d));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the old build keeps working

    private static readonly (string Table, string Where, (string Column, string Expr)[] Set)[] OldInserts =
    [
        ("SalesInvoices", $"\"Id\" = '{Fx(82)}'", [("Id", "gen_random_uuid()"), ("InvoiceNo", "'SAL-OLD1'")]),
        ("SalesInvoiceLines", $"\"Id\" = '{SiOtherLine}'", [("Id", "gen_random_uuid()")]),
        ("SalesReturns", $"\"Id\" = '{Fx(100)}'", [("Id", "gen_random_uuid()"), ("ReturnNo", "'SRT-OLD1'")]),
        ("SalesReturnLines", $"\"Id\" = '{SrOtherLine}'", [("Id", "gen_random_uuid()")]),
        ("SalesInvoiceLineTaxes", $"\"SalesInvoiceLineId\" = '{SiOtherLine}'", [("Id", "gen_random_uuid()")]),
        ("SalesReturnLineTaxes", $"\"SalesReturnLineId\" = '{SrOtherLine}'", [("Id", "gen_random_uuid()")]),
    ];

    private static async Task<Dictionary<string, string[]>> Tp3ColumnsAsync(TestDatabase d)
    {
        var map = new Dictionary<string, string[]>();
        foreach (var (table, _, _) in OldInserts)
            map[table] = (await QueryAsync(d, $"""SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' ORDER BY ordinal_position""", r => r.GetString(0))).ToArray();
        return map;
    }

    /// <summary>The TP-3 build's INSERT: only the columns that exist on the TP-3 schema (no new column named), values copied from an existing row.</summary>
    private static string OldBuildInsert(string table, string[] oldColumns, string where, (string Column, string Expr)[] set, string temp)
    {
        var cols = string.Join(", ", oldColumns.Select(c => $"\"{c}\""));
        var assignments = string.Join(", ", set.Select(s => $"\"{s.Column}\" = {s.Expr}"));
        return $"""
            CREATE TEMP TABLE {temp} AS SELECT {cols} FROM "{table}" WHERE {where};
            UPDATE {temp} SET {assignments};
            INSERT INTO "{table}" ({cols}) SELECT {cols} FROM {temp};
            DROP TABLE {temp};
            """;
    }

    [Theory]
    [InlineData(SalesSnapshot)]
    [InlineData(ValidateSales)]
    public async Task OldBuild_InsertingWithoutTheNewColumns_StillSucceeds_AndGetsTheDefaults(string migrateTo)
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var columns = await Tp3ColumnsAsync(d);   // read BEFORE the migration: exactly the columns the TP-3 build writes
        await MigrateExactlyAsync(d, migrateTo);
        var counts = new Dictionary<string, long>();
        foreach (var (table, _, _) in OldInserts) counts[table] = await ScalarAsync<long>(d, $"SELECT count(*) FROM \"{table}\"");

        var n = 0;
        foreach (var (table, where, set) in OldInserts) await ExecuteAsync(d, OldBuildInsert(table, columns[table], where, set, "_old_" + ++n));

        foreach (var (table, _, _) in OldInserts) Assert.Equal(counts[table] + 1, await ScalarAsync<long>(d, $"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(1L, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "SalesInvoices" WHERE "InvoiceNo" = 'SAL-OLD1' AND "BillDiscountAmount" = 0 AND "BillCharges" = 0 AND "PartyTaxStatus" = 'Unspecified'
              AND "TaxableTotal" IS NULL AND "PlaceOfSupply" IS NULL AND "BillDiscountPercent" IS NULL AND "PlaceOfSupplyStateId" IS NULL AND "SellerStateId" IS NULL
            """));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""
            SELECT count(*) FROM "SalesInvoiceLines" WHERE "SalesInvoiceId" = '{SiOther}' AND "Id" NOT IN ('{SiOtherLine}', '{GapSalesLine2}') AND NOT "PriceIncludesTax" AND "BillDiscountShare" = 0 AND "BillChargeShare" = 0
              AND "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NULL AND "PlaceOfSupply" IS NULL AND "HsnCode" IS NULL
            """));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""
            SELECT count(*) FROM "SalesReturnLines" WHERE "SalesInvoiceLineId" = '{SiOtherLine}' AND "Id" NOT IN ('{SrOtherLine}', '{GapSalesReturnLine2}') AND "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NULL AND "PlaceOfSupply" IS NULL
            """));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ lock_timeout

    [Theory]
    [InlineData("SalesInvoiceLines")]
    [InlineData("SalesInvoices")]
    [InlineData("SalesReturnLines")]
    [InlineData("SalesInvoiceLineTaxes")]
    [InlineData("SalesReturnLineTaxes")]
    [InlineData("TaxProfiles")]
    [InlineData("States")]
    public async Task Up_FirstMigration_WhenATouchedTableIsLocked_FailsFastWith55P03_LeavesNoPartialState_AndRetrySucceeds(string lockedTable)
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var counts = await LineTaxCountsAsync(d);
        var before = await UnchangedSnapshotAsync(d);
        await using var blocker = new NpgsqlConnection(d.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{lockedTable}\" IN ACCESS EXCLUSIVE MODE", blocker, tx)) await hold.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, SalesSnapshot));
        clock.Stop();

        var pg = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("55P03", pg.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s instead of failing after about 5s");
        await tx.RollbackAsync();
        Assert.Empty(await NewColumnsAsync(d));                                            // one transaction: no column, no constraint, no catch-up row
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Constraints.Select(c => c.Name))})"""));
        Assert.Equal(counts, await LineTaxCountsAsync(d));
        Assert.DoesNotContain(SalesSnapshot, await AppliedAsync(d));

        await MigrateExactlyAsync(d, ValidateSales);   // after the blocker released, a retry succeeds

        Assert.Contains(SalesSnapshot, await AppliedAsync(d));
        Assert.Contains(ValidateSales, await AppliedAsync(d));
        Assert.Equal(before, await UnchangedSnapshotAsync(d));
    }

    [Fact]
    public async Task Up_FirstMigration_AWriterInFlightOnASalesTable_MakesTheLockWindowFailFast_NotWaitForever()
    {
        // an uncommitted sale (ROW EXCLUSIVE on the three sales tables) conflicts with the one ACCESS EXCLUSIVE window: 55P03 after about 5 seconds, then a retry in a quiet moment works
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await using var writer = new NpgsqlConnection(d.ConnectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($"""
            {PurchaseTaxWorld.Clone("SalesInvoices", $"\"Id\" = '{Fx(82)}'", ("Id", Q(G(0xC90))), ("InvoiceNo", "'SAL-INFLIGHT'"))}
            {PurchaseTaxWorld.Clone("SalesInvoiceLines", $"\"Id\" = '{Fx(94)}'", ("Id", Q(G(0xC91))), ("SalesInvoiceId", Q(G(0xC90))))}
            """, writer, tx)) await insert.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, SalesSnapshot));
        clock.Stop();

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        Assert.InRange(clock.Elapsed.TotalSeconds, 3, 30);
        await tx.RollbackAsync();
        await MigrateExactlyAsync(d, SalesSnapshot);
        Assert.Contains(SalesSnapshot, await AppliedAsync(d));
    }

    [Theory]
    [InlineData("SalesInvoiceLines")]
    [InlineData("TaxProfiles")]
    [InlineData("States")]
    public async Task Up_SecondMigration_WhenATouchedTableIsLocked_FailsFastWith55P03_AndRetrySucceeds(string lockedTable)
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await using var blocker = new NpgsqlConnection(d.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{lockedTable}\" IN ACCESS EXCLUSIVE MODE", blocker, tx)) await hold.ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, ValidateSales));

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        await tx.RollbackAsync();
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.False(v));   // nothing validated halfway
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(AllIndexes)})"""));

        await MigrateExactlyAsync(d, ValidateSales);

        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Fact]
    public async Task Up_SecondMigration_ADocumentReadInFlight_DoesNotStopIt()
    {
        // VALIDATE CONSTRAINT takes SHARE UPDATE EXCLUSIVE and CREATE INDEX a SHARE lock: both compatible with readers (a report, the sale screen reading)
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await using var reader = new NpgsqlConnection(d.ConnectionString);
        await reader.OpenAsync();
        await using var tx = await reader.BeginTransactionAsync();
        await using (var select = new NpgsqlCommand("""SELECT count(*) FROM "SalesInvoiceLines"; """, reader, tx)) await select.ExecuteScalarAsync();

        var clock = Stopwatch.StartNew();
        await MigrateExactlyAsync(d, ValidateSales);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s for an open read");
        await tx.RollbackAsync();
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Fact]
    public async Task Up_SecondMigration_ARowWriteInFlightOnAnUntouchedTable_DoesNotStopIt()
    {
        // the second migration touches only the sales tables, States and the tax profile tables: a tax-row write (SalesInvoiceLineTaxes is not indexed or validated by it) is not waited for
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        await using var writer = new NpgsqlConnection(d.ConnectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "ComponentCode", "Kind", "Method", "RatePercent", "BaseAmount", "TaxAmount")
            VALUES (gen_random_uuid(), '{Fx(94)}', 'X', 'Other', 'Percent', 1, 1, 0)
            """, writer, tx)) await insert.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        await MigrateExactlyAsync(d, ValidateSales);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s for an open tax-row insert");
        await tx.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Up_SecondMigration_AnOpenSaleWrite_MakesItFailFastWith55P03_EvenWithPreCreatedIndexes_AndARetryAfterTheCommitSucceeds(bool preCreated)
    {
        // CREATE INDEX [IF NOT EXISTS] takes its SHARE lock on SalesInvoiceLines (and on SalesInvoices for the plain state indexes) before it looks at the index name, so the runbook removes the BUILD time,
        // not the lock itself: an uncommitted sale line conflicts with it. lock_timeout turns that into a fast 55P03 (nothing applied) instead of a stalled sales screen; the operator retries in a quiet moment.
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        if (preCreated) await CreateConcurrentlyAsync(d);
        await using var writer = new NpgsqlConnection(d.ConnectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($"""{PurchaseTaxWorld.Clone("SalesInvoiceLines", $"\"Id\" = '{Fx(94)}'", ("Id", Q(G(0xC91))), ("SalesInvoiceId", Q(SiOther)))}""", writer, tx)) await insert.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, ValidateSales));
        clock.Stop();

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        Assert.InRange(clock.Elapsed.TotalSeconds, 3, 30);
        Assert.DoesNotContain(ValidateSales, await AppliedAsync(d));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.False(v));
        await tx.CommitAsync();   // the sale finishes (it carries no tax row: the catch-up report R1 would list it)

        await MigrateExactlyAsync(d, ValidateSales);

        Assert.Contains(ValidateSales, await AppliedAsync(d));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Indexes_ASaleSavedAfterTheSecondMigration_LandsInThePartialIndexes(bool preCreated)
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, SalesSnapshot);
        if (preCreated) await CreateConcurrentlyAsync(d);
        await MigrateExactlyAsync(d, ValidateSales);

        await ExecuteAsync(d, ValidSalesSql(SalesDocAt(0xD40), "SAL-TP4I", "SRT-TP4I"));

        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "TaxProfileId" = '{PurchaseTaxWorld.ProfileA}' AND "SalesInvoiceId" = '{SalesDocAt(0xD40).Si}'"""));
        // the planner can use the partial index for a profile lookup (forced: tiny table)
        var plan = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync("SET LOCAL enable_seqscan = off");
            return string.Join("\n", await p.RowsAsync($"""EXPLAIN SELECT 1 FROM "SalesInvoiceLines" WHERE "TaxProfileId" = '{PurchaseTaxWorld.ProfileA}'"""));
        });
        Assert.Contains("IX_SalesInvoiceLines_TaxProfileId", plan, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Down / Up and the script

    [Fact]
    public async Task DownThenUp_RoundTrip_RestoresTheTp3Schema_KeepsTheCatchUpRows_AndComesBackIdentical()
    {
        await using var d = await Tp3ShapedCopyAsync(postgres);
        var tp3Schema = await SchemaSignatureAsync(d);
        var tp3Snapshot = await UnchangedSnapshotAsync(d);

        await MigrateExactlyAsync(d, ValidateSales);
        var headSchema = await SchemaSignatureAsync(d);
        var headData = await DataFingerprintAsync(d);
        Assert.NotEqual(tp3Schema, headSchema);

        await MigrateExactlyAsync(d, ValidatePurchase);   // Down: ValidateSalesTaxConstraints (indexes) then AddSalesTaxSnapshot (columns, constraints)

        Assert.DoesNotContain(SalesSnapshot, await AppliedAsync(d));
        Assert.DoesNotContain(ValidateSales, await AppliedAsync(d));
        Assert.Empty(await NewColumnsAsync(d));
        Assert.Equal(tp3Schema, await SchemaSignatureAsync(d));
        Assert.Equal(tp3Snapshot, await UnchangedSnapshotAsync(d));
        Assert.Equal(headData, await DataFingerprintAsync(d));   // the catch-up rows stay: they are the rows the TP-1 build would have written

        await MigrateExactlyAsync(d, ValidateSales);   // Up again

        Assert.Equal(headSchema, await SchemaSignatureAsync(d));
        Assert.Equal(headData, await DataFingerprintAsync(d));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Fact]
    public async Task Down_AfterTp4Sales_DropsTheirSnapshotColumns_ButKeepsEveryDocumentAndItsRows()
    {
        // the dev-only Down loses the tax snapshot columns of TP-4 sales; the documents, their money and their component rows stay (the TP-3 build can read them)
        await using var d = await Tp3ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, ValidateSales);
        await ExecuteAsync(d, ValidSalesSql(SalesDocAt(0xD00), "SAL-TP4A", "SRT-TP4A"));
        var snapshot = await UnchangedSnapshotAsync(d);
        var rows = await LineTaxCountsAsync(d);

        await MigrateExactlyAsync(d, ValidatePurchase);

        Assert.Equal(snapshot, await UnchangedSnapshotAsync(d));
        Assert.Equal(rows, await LineTaxCountsAsync(d));
        Assert.Empty(await NewColumnsAsync(d));
        await MigrateExactlyAsync(d, ValidateSales);   // and Up works again on that data (the new columns are back, empty)
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesInvoices" WHERE "TaxableTotal" IS NOT NULL"""));
    }

    [Fact]
    public async Task Up_IdempotentScriptFromTheTp3Head_AppliesThroughNpgsql_MatchesTheEfPath_AndRerunsAsANoOp()
    {
        await using var viaScript = await Tp3ShapedCopyAsync(postgres);
        await using var viaEf = await Tp3ShapedCopyAsync(postgres);
        var script = await ScriptAsync(viaScript, ValidatePurchase, ValidateSales);

        await ExecuteAsync(viaScript, script);   // as an operator runs it (EF wrapper around the migration's raw SQL, the SHA-pinned file included)
        await MigrateExactlyAsync(viaEf, ValidateSales);

        Assert.Contains(SalesSnapshot, await AppliedAsync(viaScript));
        Assert.Contains(ValidateSales, await AppliedAsync(viaScript));
        Assert.Equal(await SchemaSignatureAsync(viaEf), await SchemaSignatureAsync(viaScript));
        Assert.Equal(await DataFingerprintAsync(viaEf), await DataFingerprintAsync(viaScript));
        Assert.Equal(await ConstraintStateAsync(viaEf), await ConstraintStateAsync(viaScript));
        Assert.Equal((await IndexesAsync(viaEf)).Select(i => i.Split('|')[0]).ToList(), (await IndexesAsync(viaScript)).Select(i => i.Split('|')[0]).ToList());
        var data = await DataFingerprintAsync(viaScript);
        var schema = await SchemaSignatureAsync(viaScript);

        await ExecuteAsync(viaScript, script);   // a second run is a no-op

        Assert.Equal(data, await DataFingerprintAsync(viaScript));
        Assert.Equal(schema, await SchemaSignatureAsync(viaScript));
        Assert.Equal(await LineTaxCountsAsync(viaEf), await LineTaxCountsAsync(viaScript));
    }

    [Fact]
    public async Task Up_IdempotentScriptOfTheRunbookOrder_FirstMigrationOnly_ThenConcurrentIndexes_ThenTheSecond()
    {
        // the runbook (scripts/sql/tax-sales-release-notes.sql): script up to AddSalesTaxSnapshot, the two CONCURRENTLY statements, then the script of the second migration
        await using var d = await Tp3ShapedCopyAsync(postgres);

        await ExecuteAsync(d, await ScriptAsync(d, ValidatePurchase, SalesSnapshot));
        Assert.Contains(SalesSnapshot, await AppliedAsync(d));
        Assert.DoesNotContain(ValidateSales, await AppliedAsync(d));
        await CreateConcurrentlyAsync(d);
        var kept = await IndexesAsync(d);
        await ExecuteAsync(d, await ScriptAsync(d, SalesSnapshot, ValidateSales));

        Assert.Contains(ValidateSales, await AppliedAsync(d));
        foreach (var index in kept) Assert.Contains(index, await IndexesAsync(d));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Fact]
    public async Task Up_FromAnEmptyTp1Database_AndFromTheWholeHistory_BothReachTheSameSchema()
    {
        // an environment without any sale (nothing to catch up) migrates cleanly, and the head schema equals the one of the data-bearing world
        await using var empty = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(empty, ValidateFks);
        await using var tp3 = await Tp3ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(empty, ValidateSales);
        await MigrateExactlyAsync(tp3, ValidateSales);

        var emptySchema = await SchemaSignatureAsync(empty);
        var tp3Schema = await SchemaSignatureAsync(tp3);
        Assert.Empty(emptySchema.Except(tp3Schema));
        Assert.Empty(tp3Schema.Except(emptySchema));
    }
}
