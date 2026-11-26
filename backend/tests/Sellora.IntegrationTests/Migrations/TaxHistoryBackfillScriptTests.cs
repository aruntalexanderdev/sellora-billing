using System.Diagnostics;
using System.Text.RegularExpressions;
using Npgsql;
using Sellora.DbMigrator;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.HistoryBackfillWorld;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.SalesTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// The data script <c>backend/db/scripts/2026.11.25.001__tax-history-profile-backfill.sql</c> (docs/features/tax-profiles.md, "TP-5 design", "History backfill (M3a)"): ONE DO block outside a transaction, a commit per batch,
/// SKIP LOCKED, keyset paging. Sets <c>TaxProfileId</c> (never a version) on history sales and purchase lines from their mirror <c>TaxId</c>, then on return lines from their original line.
/// Run through the house migrator (Npgsql, no psql) and directly; on the small TP-4 shaped world (every kind of line, IN and AE, unmappable lines) and on thousands of history lines with batch size 5-50 (the
/// start-up option <c>sellora.backfill_batch_size</c>): complete, windows as expected, killed mid-run and resumed, re-run a no-op, concurrent sales unharmed, locked rows skipped and picked up later.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TaxHistoryBackfillScriptTests(PostgresFixture postgres)
{
    private static readonly string[] Snapshots = ["tax-sales-snapshot.sql", "tax-purchase-snapshot.sql"];

    private static string InList(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    private static async Task<List<string>> RowsAsync(TestDatabase d, string sql) =>
        await QueryAsync(d, sql, r => string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "-" : Format(r.GetValue(i)))));

    /// <summary>Every row of the four line tables with its transaction id: a row the script rewrote has a new xmin, so equal snapshots prove a run changed nothing.</summary>
    private static async Task<string> XminAsync(TestDatabase d) => await ScalarAsync<string>(d, "SELECT " + string.Join(" || '#' || ", LineTables.Select(t =>
        $"""(SELECT COALESCE(md5(string_agg("Id"::text || ':' || xmin::text, ',' ORDER BY "Id"::text)), '-') FROM "{t}")""")));

    private static async Task<List<string>> WithVersionAsync(TestDatabase d)
    {
        var all = new List<string>();
        foreach (var table in LineTables) all.Add(table + "=" + await FingerprintAsync(d, table, "\"TaxProfileVersionId\" IS NOT NULL"));
        return all;
    }

    private static async Task<List<string>> ViolationsAsync(TestDatabase d, params string[] files)
    {
        var rows = new List<string>();
        foreach (var file in files)
            foreach (var (_, sql) in ValidationScripts.Statements(file))
                rows.AddRange(await RowsAsync(d, sql));
        return rows;
    }

    /// <summary>The "done" NOTICE of each table: rows set, windows, rows left.</summary>
    private static Dictionary<string, (long Set, long Windows, long Left)> Done(IEnumerable<string> notices) =>
        notices.Select(n => Regex.Match(n, @"^(\w+) done: (\d+) rows set in (\d+) windows; (\d+) rows still without a profile$")).Where(m => m.Success)
            .ToDictionary(m => m.Groups[1].Value, m => (long.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value), long.Parse(m.Groups[4].Value)));

    private static long Windows(long rows, int batch) => (rows + batch - 1) / batch;

    private static async Task<long> ProfileOnlyAsync(TestDatabase d, string table = "SalesInvoiceLines") =>
        await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{table}" WHERE "TaxProfileId" IS NOT NULL AND "TaxProfileVersionId" IS NULL""");

    private static async Task<long> TotalAsync(TestDatabase d, string table) => await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{table}" """);

    private const int Orphans = 1;   // per table (OrphanSalesLine, OrphanPurchaseLine, and the return of each)

    // ------------------------------------------------------------------------------------------------------------------------------ the small world

    [Fact]
    public async Task Backfill_SetsTheMirrorProfileOnEveryMappableHistoryLine_NeverAVersion_AndLeavesTheTp3Tp4LinesAlone()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        var before = await CountsAsync(d);
        var versioned = await WithVersionAsync(d);
        var money = await UnchangedSnapshotAsync(d);
        var taxRows = await ScalarAsync<string>(d, """SELECT (SELECT count(*) FROM "SalesInvoiceLineTaxes") || ':' || (SELECT count(*) FROM "PurchaseOrderLineTaxes") || ':' || (SELECT count(*) FROM "SalesReturnLineTaxes") || ':' || (SELECT count(*) FROM "PurchaseReturnLineTaxes")""");

        var run = await BackfillScript.RunAsync(d);

        var after = await CountsAsync(d);
        foreach (var table in LineTables)
        {
            Assert.Equal(before[table].Total, after[table].Total);
            Assert.Equal(before[table].WithVersion, after[table].WithVersion);                                   // TP-3/TP-4 lines: same count ...
            Assert.Equal(before[table].WithoutProfile - Orphans, after[table].ProfileOnly);                     // ... every history line got a profile except the unmappable one ...
            Assert.Equal(Orphans, after[table].WithoutProfile);                                                 // ... which stays NULL (nothing is invented)
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{table}" WHERE "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NOT NULL"""));
        }
        Assert.Equal(versioned, await WithVersionAsync(d));                                                      // identical ids, profiles and versions
        Assert.Equal(money, await UnchangedSnapshotAsync(d));                                                    // amounts, mirrors and TaxIds
        Assert.Equal(taxRows, await ScalarAsync<string>(d, """SELECT (SELECT count(*) FROM "SalesInvoiceLineTaxes") || ':' || (SELECT count(*) FROM "PurchaseOrderLineTaxes") || ':' || (SELECT count(*) FROM "SalesReturnLineTaxes") || ':' || (SELECT count(*) FROM "PurchaseReturnLineTaxes")"""));
        // the final NOTICE of each table says what is left
        var done = Done(run.Notices);
        Assert.Equal(LineTables.Order().ToList(), done.Keys.Order().ToList());
        Assert.All(done, kv => Assert.Equal(Orphans, kv.Value.Left));
    }

    [Fact]
    public async Task Backfill_ProfileComesFromTheDocumentsCompanyAndTheMirrorTax_InBothCountries()
    {
        await using var d = await AtHeadCopyAsync(postgres);

        await BackfillScript.RunAsync(d);

        // company A (IN): the fixture's GST 5% lines; company B (AE): the Local VAT line
        Assert.Equal([TaxSeedData.LegacyProfileId(TaxA5).ToString()], await RowsAsync(d, $"""SELECT "TaxProfileId" FROM "SalesInvoiceLines" WHERE "Id" = '{Fx(94)}'"""));
        Assert.Equal([TaxSeedData.LegacyProfileId(TaxB5).ToString()], await RowsAsync(d, $"""SELECT "TaxProfileId" FROM "SalesInvoiceLines" WHERE "Id" = '{SiBLine}'"""));
        Assert.Equal([TaxSeedData.LegacyProfileId(TaxA5).ToString()], await RowsAsync(d, $"""SELECT "TaxProfileId" FROM "PurchaseOrderLines" WHERE "Id" = '{Fx(50)}'"""));
        // generic: the profile is the mirror profile (LegacyTaxId = the line's TaxId) of the company of the document, for all four tables
        foreach (var (table, header, link) in new[] { ("SalesInvoiceLines", "SalesInvoices", "SalesInvoiceId"), ("PurchaseOrderLines", "PurchaseOrders", "PurchaseOrderId") })
        {
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""
                SELECT count(*) FROM "{table}" l JOIN "{header}" h ON h."Id" = l."{link}" JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId"
                WHERE p."LegacyTaxId" <> l."TaxId" OR p."CompanyId" <> h."CompanyId"
                """));
        }
        Assert.True(await ScalarAsync<long>(d, $"""SELECT count(DISTINCT h."CompanyId") FROM "SalesInvoiceLines" l JOIN "SalesInvoices" h ON h."Id" = l."SalesInvoiceId" WHERE l."TaxProfileId" IS NOT NULL AND l."TaxProfileVersionId" IS NULL""") >= 2);
    }

    [Fact]
    public async Task Backfill_ReturnLinesCopyTheirOriginalsProfile_IncludingTheRollbackEraReturn_AndNeverTouchTp4Returns()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        var tp4Returns = await FingerprintAsync(d, "SalesReturnLines", "\"TaxProfileVersionId\" IS NOT NULL");
        var tp3Returns = await FingerprintAsync(d, "PurchaseReturnLines", "\"TaxProfileVersionId\" IS NOT NULL");

        await BackfillScript.RunAsync(d);

        foreach (var (returns, originals, link) in new[] { ("SalesReturnLines", "SalesInvoiceLines", "SalesInvoiceLineId"), ("PurchaseReturnLines", "PurchaseOrderLines", "PurchaseOrderLineId") })
        {
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{returns}" r JOIN "{originals}" o ON o."Id" = r."{link}" WHERE r."TaxProfileId" IS DISTINCT FROM o."TaxProfileId" """));
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{returns}" r WHERE r."TaxProfileId" IS NOT NULL AND r."TaxProfileVersionId" IS NULL AND r."PlaceOfSupply" IS NOT NULL"""));   // no place invented
        }
        // the return the TP-3 build made on a TP-4 line: the original's profile, still no version and no place (the snapshot files tolerate exactly this)
        Assert.Equal([$"{ProfileA}|-|-"], await RowsAsync(d, $"""SELECT "TaxProfileId" || '|' || COALESCE("TaxProfileVersionId"::text, '-') || '|' || COALESCE("PlaceOfSupply", '-') FROM "SalesReturnLines" WHERE "Id" = '{Rollback.ReturnLine}'"""));
        // returns of the unmappable lines have nothing to copy
        Assert.Equal(["-"], await RowsAsync(d, $"""SELECT COALESCE("TaxProfileId"::text, '-') FROM "SalesReturnLines" WHERE "Id" = '{OrphanSalesReturnLine}'"""));
        Assert.Equal(["-"], await RowsAsync(d, $"""SELECT COALESCE("TaxProfileId"::text, '-') FROM "PurchaseReturnLines" WHERE "Id" = '{OrphanPurchaseReturnLine}'"""));
        // returns written by the TP-3/TP-4 builds (with a version) are not rewritten
        Assert.Equal(tp4Returns, await FingerprintAsync(d, "SalesReturnLines", "\"TaxProfileVersionId\" IS NOT NULL"));
        Assert.Equal(tp3Returns, await FingerprintAsync(d, "PurchaseReturnLines", "\"TaxProfileVersionId\" IS NOT NULL"));
    }

    [Fact]
    public async Task Backfill_ASecondRunIsANoOp_NoRowIsRewritten_AndTheNoticesSayNothingWasSet()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await BackfillScript.RunAsync(d);
        var snapshot = await XminAsync(d);
        var lines = await FingerprintAsync(d, "SalesInvoiceLines");

        var second = await BackfillScript.RunAsync(d);

        Assert.Equal(snapshot, await XminAsync(d));   // not one row version was written
        Assert.Equal(lines, await FingerprintAsync(d, "SalesInvoiceLines"));
        Assert.All(Done(second.Notices), kv => Assert.Equal((0L, Orphans), (kv.Value.Set, kv.Value.Left)));
        Assert.DoesNotContain(second.Notices, n => Regex.IsMatch(n, @" batch \d+: "));   // a window that sets nothing is not announced
    }

    [Fact]
    public async Task Backfill_RunsThroughTheMigrator_Succeeds_ThenTheJournalSkipsIt_AndRunningTheFileAgainStillChangesNothing()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        var before = await CountsAsync(d);

        var (step, exit) = await BackfillScript.RunViaMigratorAsync(d);

        Assert.Equal((ExitCode.Succeeded, "Succeeded", ScriptKind.Data), (exit, step.Status, step.Kind));
        var after = await CountsAsync(d);
        foreach (var table in LineTables) Assert.Equal(before[table].WithoutProfile - Orphans, after[table].ProfileOnly);
        var snapshot = await XminAsync(d);

        var (again, againExit) = await BackfillScript.RunViaMigratorAsync(d);

        Assert.Equal((ExitCode.Succeeded, "Skipped"), (againExit, again.Status));   // same SHA recorded in the journal
        Assert.Equal(snapshot, await XminAsync(d));
    }

    [Fact]
    public async Task Backfill_TheHeaderOfTheScript_RunsOutsideATransaction_WithALongTimeout_AndIsASingleStatement()
    {
        var text = BackfillScript.Text;

        Assert.Contains("-- sellora:transaction none", text, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"-- sellora:timeout (\d+)"), text);
        Assert.True(int.Parse(Regex.Match(text, @"-- sellora:timeout (\d+)").Groups[1].Value) >= 3600);
        Assert.Contains("SKIP LOCKED", text, StringComparison.Ordinal);
        // the migrator itself rejects a transaction:none script with several statements (25001), so the file is exactly one DO block
        var code = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.Equal(1, Regex.Matches(code, @"\$backfill\$;?").Count / 2);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ validation files before and after

    [Fact]
    public async Task ValidationFiles_HistoryProfileReportsEveryUnbackfilledLine_AfterTheBackfillOnlyTheUnmappableOnes_AndZeroOnceTheyAreFixed()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        var before = await CountsAsync(d);

        var reported = await ViolationsAsync(d, HistoryProfileFile);

        Assert.Equal(LineTables.Sum(t => before[t].WithoutProfile), reported.Count(r => r.StartsWith("line-without-profile|", StringComparison.Ordinal)));
        Assert.Contains($"return-profile-differs-from-original|SalesReturnLines|{Rollback.ReturnLine}", reported);   // rollback-era return: the original has a profile, the return none

        await BackfillScript.RunAsync(d);

        // what is left are exactly the four unmappable lines (BEFORE: one per history line)
        Assert.Equal(
            new[] { $"line-without-profile|PurchaseOrderLines|{OrphanPurchaseLine}", $"line-without-profile|PurchaseReturnLines|{OrphanPurchaseReturnLine}",
                    $"line-without-profile|SalesInvoiceLines|{OrphanSalesLine}", $"line-without-profile|SalesReturnLines|{OrphanSalesReturnLine}" }.Order(StringComparer.Ordinal).ToList(),
            (await ViolationsAsync(d, HistoryProfileFile)).Order(StringComparer.Ordinal).ToList());

        // an operator fixes the data (the orphan lines point at a tax that has a mirror profile) and re-runs the script: it picks the lines up, originals before returns
        await ExecuteAsync(d, $"""UPDATE "SalesInvoiceLines" SET "TaxId" = '{TaxA5}' WHERE "Id" = '{OrphanSalesLine}'; UPDATE "PurchaseOrderLines" SET "TaxId" = '{TaxA5}' WHERE "Id" = '{OrphanPurchaseLine}'""");
        await BackfillScript.RunAsync(d);

        Assert.Empty(await ViolationsAsync(d, HistoryProfileFile));
        var final = await CountsAsync(d);
        Assert.All(LineTables, t => Assert.Equal(0L, final[t].WithoutProfile));
    }

    [Fact]
    public async Task ValidationFiles_TheSnapshotFiles_ReturnZeroRowsBeforeAndAfterTheBackfill_ThoughHistoryNowCarriesProfiles()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        Assert.Empty(await ViolationsAsync(d, Snapshots));   // TP-3/TP-4 documents are valid, history is not looked at

        await BackfillScript.RunAsync(d);

        // the new-line discriminator is the version: a backfilled profile-only line is history, the rollback-era return (profile copied, no version, no place) is the tolerated shape
        Assert.Empty(await ViolationsAsync(d, Snapshots));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesReturnLines" WHERE "Id" = '{Rollback.ReturnLine}' AND "TaxProfileId" IS NOT NULL AND "TaxProfileVersionId" IS NULL"""));
    }

    [Fact]
    public async Task ValidationFiles_TheOtherTaxFiles_StayCleanAfterTheBackfill_OnTheLinesTheyAlreadyCovered()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        // the orphan lines are raw clones without tax rows (unrelated to the backfill): remove them so the row-level files have nothing else to say
        await ExecuteAsync(d, $"""
            DELETE FROM "SalesReturnLines" WHERE "Id" = '{OrphanSalesReturnLine}'; DELETE FROM "PurchaseReturnLines" WHERE "Id" = '{OrphanPurchaseReturnLine}';
            DELETE FROM "SalesInvoiceLines" WHERE "Id" = '{OrphanSalesLine}'; DELETE FROM "PurchaseOrderLines" WHERE "Id" = '{OrphanPurchaseLine}'
            """);
        var files = ValidationFiles.Where(f => f != "tax-line-rows-missing.sql" && f != "tax-line-rows-missing-purchases.sql").ToArray();
        var clean = await ViolationsAsync(d, files);

        await BackfillScript.RunAsync(d);

        Assert.Equal(clean, await ViolationsAsync(d, files));
        Assert.Empty(await ViolationsAsync(d, HistoryProfileFile));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ batching on thousands of lines

    private const int Sales = 3000;

    [Fact]
    public async Task Backfill_WithBatchSize50_OnThousandsOfLines_BackfillsAll_InExactlyTheExpectedNumberOfWindows()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(d, Sales);
        var before = await CountsAsync(d);
        var versioned = await WithVersionAsync(d);
        Assert.True(before["SalesInvoiceLines"].Total > Sales && before["SalesInvoiceLines"].WithVersion > 200, before["SalesInvoiceLines"].ToString());   // every tenth of company A is a TP-4 line
        Assert.True(await ViolationsAsync(d, HistoryProfileFile) is { Count: > 3000 });

        var run = await BackfillScript.RunAsync(d, batchSize: 50);

        var after = await CountsAsync(d);
        var done = Done(run.Notices);
        foreach (var table in LineTables)
        {
            Assert.Equal(before[table].WithoutProfile - Orphans, after[table].ProfileOnly);
            Assert.Equal(Orphans, after[table].WithoutProfile);
            Assert.Equal(before[table].WithVersion, after[table].WithVersion);
            Assert.Equal(before[table].WithoutProfile - Orphans, done[table].Set);
            Assert.Equal(Windows(before[table].Total, 50), done[table].Windows);   // one key window per 50 rows of the table, mappable or not
        }
        Assert.Contains(run.Notices, n => n.Contains("batch size 50", StringComparison.Ordinal));
        Assert.Equal(versioned, await WithVersionAsync(d));   // the TP-4 lines of the bulk are untouched
        // nothing but the unmappable lines is reported, and the mirror / company rules hold for every backfilled line
        Assert.Equal(4, (await ViolationsAsync(d, HistoryProfileFile)).Count);
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesInvoiceLines" l JOIN "SalesInvoices" h ON h."Id" = l."SalesInvoiceId" JOIN "TaxProfiles" p ON p."Id" = l."TaxProfileId" WHERE p."LegacyTaxId" <> l."TaxId" OR p."CompanyId" <> h."CompanyId" """));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" o ON o."Id" = r."SalesInvoiceLineId" WHERE r."TaxProfileId" IS DISTINCT FROM o."TaxProfileId" """));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "PurchaseReturnLines" r JOIN "PurchaseOrderLines" o ON o."Id" = r."PurchaseOrderLineId" WHERE r."TaxProfileId" IS DISTINCT FROM o."TaxProfileId" """));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100000)]
    public async Task Backfill_TheResultDoesNotDependOnTheBatchSize_EvenOneRowPerWindowOrOneWindowForAll(int batch)
    {
        await using var viaDefault = await AtHeadCopyAsync(postgres);
        await using var viaBatch = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(viaDefault, 300);
        await AddBulkAsync(viaBatch, 300);

        await BackfillScript.RunAsync(viaDefault);
        var run = await BackfillScript.RunAsync(viaBatch, batch);

        foreach (var table in LineTables) Assert.Equal(await FingerprintAsync(viaDefault, table), await FingerprintAsync(viaBatch, table));
        Assert.Contains(run.Notices, n => n.Contains($"batch size {batch}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backfill_WithoutTheOption_UsesTheReleasedDefaultOf5000_AndABatchSizeBelowOneIsRefusedBeforeAnyWork()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        var before = await XminAsync(d);

        var run = await BackfillScript.RunAsync(d);
        Assert.Contains(run.Notices, n => n.Contains("batch size 5000", StringComparison.Ordinal));

        await using var other = await AtHeadCopyAsync(postgres);
        var untouched = await XminAsync(other);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => BackfillScript.RunAsync(other, batchSize: 0));
        Assert.Contains("at least 1", ex.MessageText, StringComparison.Ordinal);
        Assert.Equal(untouched, await XminAsync(other));
        Assert.NotEqual(before, await XminAsync(d));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ kill and resume

    [Fact]
    public async Task Backfill_KilledMidRunByStatementTimeout_KeepsEveryFinishedBatch_AndTheNextRunCompletes()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(d, Sales);
        var versioned = await WithVersionAsync(d);
        var total = await TotalAsync(d, "SalesInvoiceLines");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => BackfillScript.RunAsync(d, batchSize: 5, statementTimeoutMs: 400));

        Assert.Equal("57014", ex.SqlState);   // statement timeout: the whole DO statement is cancelled
        var partial = await ProfileOnlyAsync(d);
        Assert.InRange(partial, 1, total - 1);   // earlier batches were COMMITTED and survive the cancellation; the rest is not done
        Assert.Equal(versioned, await WithVersionAsync(d));
        Assert.Equal(0L, await ScalarAsync<long>(d, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND state = 'idle in transaction'"));   // nothing is left open

        var resume = await BackfillScript.RunAsync(d, batchSize: 50);

        var done = Done(resume.Notices);
        Assert.Equal(Orphans, done["SalesInvoiceLines"].Left);
        Assert.Equal(total - Orphans - await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesInvoiceLines" WHERE "TaxProfileVersionId" IS NOT NULL"""), await ProfileOnlyAsync(d));
        Assert.True(done["SalesInvoiceLines"].Set < total - Orphans, "the resumed run must only set what the killed run left");
        Assert.Equal(Orphans, (await CountsAsync(d))["PurchaseReturnLines"].WithoutProfile);
        Assert.Equal(4, (await ViolationsAsync(d, HistoryProfileFile)).Count);
        Assert.Equal(versioned, await WithVersionAsync(d));
    }

    [Fact]
    public async Task Backfill_KilledMidRunThroughTheMigrator_IsRecordedAsFailed_AndTheRetryCompletesIt()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(d, Sales);
        var total = await TotalAsync(d, "SalesInvoiceLines");

        var first = Task.Run(() => BackfillScript.RunViaMigratorAsync(d, batchSize: 5));
        var cancelled = false;
        for (var i = 0; i < 2000 && !cancelled && !first.IsCompleted; i++)
        {
            await Task.Delay(10);
            if (await ProfileOnlyAsync(d) < 20) continue;
            cancelled = await ScalarAsync<long>(d, """SELECT count(*) FROM (SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND backend_type = 'client backend' AND state = 'active') x""") > 0;
        }
        var (step, exit) = await first;

        Assert.True(cancelled, "the script finished before it could be cancelled: raise the number of lines");
        Assert.Equal("Failed", step.Status);
        Assert.NotEqual(ExitCode.Succeeded, exit);
        var partial = await ProfileOnlyAsync(d);
        Assert.InRange(partial, 20, total - 1);   // finished batches stayed committed
        Assert.True(partial < total - Orphans - 1, "cancelled too late");

        var (retry, retryExit) = await BackfillScript.RunViaMigratorAsync(d);   // the release page's "retry": default batch size, finishes the job

        Assert.Equal((ExitCode.Succeeded, "Succeeded"), (retryExit, retry.Status));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "TaxProfileId" IS NULL AND "TaxId" <> '{TaxOrphan}'"""));
        Assert.Equal(4, (await ViolationsAsync(d, HistoryProfileFile)).Count);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ sales and edits while it runs

    [Fact]
    public async Task Backfill_ASaleInsertedAndCommittedWhileItRuns_IsNeverBlocked_AndTheTp4LineIsUntouched()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(d, Sales);
        var run = Task.Run(() => BackfillScript.RunAsync(d, batchSize: 5));

        var started = false;
        for (var i = 0; i < 2000 && !started && !run.IsCompleted; i++) { await Task.Delay(10); started = await ProfileOnlyAsync(d) >= 20; }
        Assert.True(started && !run.IsCompleted, "the script finished before the sale could be written: raise the number of lines");
        var clock = Stopwatch.StartNew();
        // the TP-4 build saving a sale: profile AND version on its lines, a document header with its numbers ...
        await ExecuteAsync(d, ValidSalesSql(SalesDocAt(0xFA00), "SAL-DURING", "SRT-DURING"));
        // ... and the TP-3 build / a rolled-back build saving a line with a TaxId and no profile
        await ExecuteAsync(d, Clone("SalesInvoiceLines", $"\"Id\" = '{Fx(94)}'", ("Id", $"'{G(0xFA20)}'")));
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the sales waited {clock.Elapsed.TotalSeconds:0.0}s for the backfill");
        await run;

        // the new TP-4 line keeps its profile and version
        Assert.Equal([$"{ProfileA}|{VersionA}"], await RowsAsync(d, $"""SELECT "TaxProfileId" || '|' || "TaxProfileVersionId" FROM "SalesInvoiceLines" WHERE "Id" = '{SalesDocAt(0xFA00).Line}'"""));
        // the profile-less line is either picked up (its key window was still ahead) or left for the next run, never a version
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "Id" = '{G(0xFA20)}' AND "TaxProfileVersionId" IS NOT NULL"""));

        await BackfillScript.RunAsync(d);   // R1/P2 tell the operator what is left; a re-run finishes it

        Assert.Equal([$"{ProfileA}"], await RowsAsync(d, $"""SELECT "TaxProfileId" FROM "SalesInvoiceLines" WHERE "Id" = '{G(0xFA20)}'"""));
        Assert.Equal(4, (await ViolationsAsync(d, HistoryProfileFile)).Count);
        Assert.Empty(await ViolationsAsync(d, Snapshots));   // the document of the TP-4 build is still valid
    }

    [Fact]
    public async Task Backfill_ALineBeingEditedRightNow_IsSkippedWithoutWaiting_AndPickedUpByTheNextRun()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(d, 600);
        var locked = G(0xFA30);
        await ExecuteAsync(d, Clone("SalesInvoiceLines", $"\"Id\" = '{Fx(94)}'", ("Id", $"'{locked}'")));
        await using var editor = new NpgsqlConnection(d.ConnectionString);
        await editor.OpenAsync();
        await using var tx = await editor.BeginTransactionAsync();
        await using (var edit = new NpgsqlCommand($"""UPDATE "SalesInvoiceLines" SET "Qty" = "Qty" WHERE "Id" = '{locked}'""", editor, tx)) await edit.ExecuteNonQueryAsync();   // row lock held, uncommitted

        var clock = Stopwatch.StartNew();
        var run = await BackfillScript.RunAsync(d, batchSize: 100);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the script waited {clock.Elapsed.TotalSeconds:0.0}s for a locked row");
        Assert.Equal(["-"], await RowsAsync(d, $"""SELECT COALESCE("TaxProfileId"::text, '-') FROM "SalesInvoiceLines" WHERE "Id" = '{locked}'"""));
        Assert.Equal(Orphans + 1, Done(run.Notices)["SalesInvoiceLines"].Left);   // the notice shows the skipped row next to the unmappable one
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "TaxProfileId" IS NULL AND "TaxId" <> '{TaxOrphan}' AND "TaxProfileVersionId" IS NULL"""));   // only that one is left
        await tx.CommitAsync();

        await BackfillScript.RunAsync(d, batchSize: 100);

        Assert.Equal([$"{ProfileA}"], await RowsAsync(d, $"""SELECT "TaxProfileId" FROM "SalesInvoiceLines" WHERE "Id" = '{locked}'"""));
    }

    [Fact]
    public async Task Backfill_AReturnWhoseOriginalWasSkippedWhileLocked_IsCompletedByTheNextRun()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(d, 600);
        // the original of a bulk return (index 4 of the company A lines) is being edited while the script runs, so its return finds no profile to copy in the same run
        var original = await ScalarAsync<string>(d, """SELECT "SalesInvoiceLineId"::text FROM "SalesReturnLines" WHERE "Id" = md5('bf-srl-1')::uuid""");
        await using var editor = new NpgsqlConnection(d.ConnectionString);
        await editor.OpenAsync();
        await using var tx = await editor.BeginTransactionAsync();
        await using (var edit = new NpgsqlCommand($"""UPDATE "SalesInvoiceLines" SET "Qty" = "Qty" WHERE "Id" = '{original}'""", editor, tx)) await edit.ExecuteNonQueryAsync();

        await BackfillScript.RunAsync(d, batchSize: 100);

        Assert.Equal(["-|-"], await RowsAsync(d, $"""SELECT COALESCE(o."TaxProfileId"::text, '-') || '|' || COALESCE(r."TaxProfileId"::text, '-') FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" o ON o."Id" = r."SalesInvoiceLineId" WHERE r."Id" = md5('bf-srl-1')::uuid"""));
        await tx.CommitAsync();

        await BackfillScript.RunAsync(d, batchSize: 100);

        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "SalesReturnLines" r JOIN "SalesInvoiceLines" o ON o."Id" = r."SalesInvoiceLineId" WHERE r."TaxProfileId" IS DISTINCT FROM o."TaxProfileId" """));
        Assert.Equal(4, (await ViolationsAsync(d, HistoryProfileFile)).Count);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ release notes report and scale

    private static IEnumerable<(string Label, string Sql)> ReleaseNotesStatements()
    {
        var text = File.ReadAllText(BackfillScript.RepoPath("scripts", "sql", "tax-reports-release-notes.sql"));
        var code = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        foreach (var statement in code.Split(";\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(s => s.StartsWith("SELECT", StringComparison.Ordinal)))
            yield return (Regex.Match(statement, @"^SELECT '(\w+)'").Groups[1].Value, statement.TrimEnd(';'));
    }

    [Fact]
    public async Task ReleaseNotes_P2AndR1_ShowTheBackfillProgress_BeforeAndAfter_AndEveryReportRuns()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        var statements = ReleaseNotesStatements().ToDictionary(s => s.Label, s => s.Sql);
        Assert.Equal(["P1", "P2", "R1", "R2"], statements.Keys.Order().ToList());
        var before = await CountsAsync(d);

        var p2Before = (await RowsAsync(d, statements["P2"])).ToDictionary(r => r.Split('|')[1], r => r.Split('|')[2..]);
        foreach (var table in LineTables) Assert.Equal([(before[table].WithoutProfile - Orphans).ToString(), Orphans.ToString()], p2Before[table]);   // backfillable | unmappable

        await BackfillScript.RunAsync(d);

        var p2After = (await RowsAsync(d, statements["P2"])).ToDictionary(r => r.Split('|')[1], r => r.Split('|')[2..]);
        foreach (var table in LineTables) Assert.Equal(["0", Orphans.ToString()], p2After[table]);   // nothing backfillable is left
        var after = await CountsAsync(d);
        var r1 = (await RowsAsync(d, statements["R1"])).ToDictionary(r => r.Split('|')[1], r => r.Split('|')[2..]);
        foreach (var table in LineTables)
            Assert.Equal([after[table].Total.ToString(), after[table].WithVersion.ToString(), after[table].ProfileOnly.ToString(), after[table].WithoutProfile.ToString()], r1[table]);
        await RowsAsync(d, statements["P1"]);   // reads only existing columns
        await RowsAsync(d, statements["R2"]);
    }

    [Fact]
    public async Task Backfill_OnAScaleOfAboutTwoHundredThousandLines_CompletesWithTheDefaultBatchSize_AndTheKeysetPagingStaysLinear()
    {
        await using var d = await AtHeadCopyAsync(postgres);
        await AddBulkAsync(d, 100_000);
        var before = await CountsAsync(d);
        Assert.True(before.Values.Sum(c => c.Total) > 180_000, before.Values.Sum(c => c.Total).ToString());

        var run = await BackfillScript.RunAsync(d);

        var after = await CountsAsync(d);
        foreach (var table in LineTables)
        {
            Assert.Equal(before[table].WithoutProfile - Orphans, after[table].ProfileOnly);
            Assert.Equal(before[table].WithVersion, after[table].WithVersion);
            Assert.Equal(Windows(before[table].Total, 5000), Done(run.Notices)[table].Windows);
        }
        Assert.True(run.Elapsed < TimeSpan.FromMinutes(3), $"{run.Elapsed.TotalSeconds:0}s for {before.Values.Sum(c => c.Total)} lines");
        var second = await BackfillScript.RunAsync(d);
        Assert.All(Done(second.Notices), kv => Assert.Equal(0L, kv.Value.Set));
        Assert.True(second.Elapsed < run.Elapsed + TimeSpan.FromSeconds(5), "a no-op run reads every key window once and must not be slower than the first");
    }
}
