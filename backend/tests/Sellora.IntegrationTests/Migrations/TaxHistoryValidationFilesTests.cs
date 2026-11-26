using System.Text.RegularExpressions;
using Npgsql;
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
/// The validation SQL of TP-5a (docs/features/tax-profiles.md, "TP-5 design", "History backfill"): <c>V__tax-history-profile.sql</c> (WARN: every check label has a probe that writes the violation inside a transaction that
/// is always rolled back and expects exactly the rows it causes), and the edit of <c>V__tax-sales-snapshot.sql</c> / <c>V__tax-purchase-snapshot.sql</c>: a "new line" is one with a <c>TaxProfileVersionId</c>, so backfilled
/// history (profile only) is not checked, a corrupted TP-3/TP-4 line still is, and the rollback-era sales return (TP-3 build on a TP-4 line) is tolerated only in its exact shape. All on the TP-4 shaped world after the
/// migrations and the backfill script, without the four unmappable lines (those are covered by <see cref="TaxHistoryBackfillScriptTests"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
public class TaxHistoryValidationFilesTests(PostgresFixture postgres)
{
    private const string History = HistoryProfileFile, SalesSnapshot = "tax-sales-snapshot.sql", PurchaseSnapshot = "tax-purchase-snapshot.sql";

    private static readonly string[] Labels = ["line-without-profile", "taxid-not-profile-mirror", "profile-of-other-company", "return-profile-differs-from-original"];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TestDatabase? shared;

    /// <summary>The TP-4 shaped world at the head, backfilled, minus the unmappable lines: every V__tax-* file is clean on it. Shared, read-only: probes roll back.</summary>
    private async Task<TestDatabase> WorldAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (shared is not null) return shared;
            var d = await AtHeadCopyAsync(postgres);
            await BackfillScript.RunAsync(d);
            await ExecuteAsync(d, $"""
                DELETE FROM "SalesReturnLines" WHERE "Id" = '{OrphanSalesReturnLine}'; DELETE FROM "PurchaseReturnLines" WHERE "Id" = '{OrphanPurchaseReturnLine}';
                DELETE FROM "SalesInvoiceLines" WHERE "Id" = '{OrphanSalesLine}'; DELETE FROM "PurchaseOrderLines" WHERE "Id" = '{OrphanPurchaseLine}'
                """);
            NpgsqlConnection.ClearAllPools();
            shared = d;
            return d;
        }
        finally { Gate.Release(); }
    }

    /// <summary>The rows of <paramref name="file"/> after <paramref name="mutate"/>, sorted; the file must be clean (all rows of all three files) before it.</summary>
    private async Task<List<string>> ProbeAsync(string file, Func<Probe, Task> mutate)
    {
        var d = await WorldAsync();
        var (clean, after) = await InRolledBackTransactionAsync(d, async p =>
        {
            var before = new List<string>();
            foreach (var f in new[] { History, SalesSnapshot, PurchaseSnapshot }) before.AddRange((await p.ViolationsAsync(f)).Select(r => f + ": " + r));
            await mutate(p);
            return (before, (await p.ViolationsAsync(file)).Order(StringComparer.Ordinal).ToList());
        });
        Assert.True(clean.Count == 0, "the backfilled world is not clean: " + string.Join(" | ", clean.Take(5)));
        return after;
    }

    private static string R(string label, string table, Guid id) => $"{label}|{table}|{id}";

    private static List<string> Rows(params string[] rows) => rows.Order(StringComparer.Ordinal).ToList();

    private static string CloneLine(string table, Guid source, Guid id, params (string Column, string Expr)[] set) =>
        PurchaseTaxWorld.Clone(table, $"\"Id\" = '{source}'", [("Id", $"'{id}'"), .. set]);

    private static readonly Guid NewLine = G(0xFB01), NewReturn = G(0xFB02);

    // ------------------------------------------------------------------------------------------------------------------------------ clean

    [Fact]
    public async Task EveryTaxFile_IsCleanOnTheBackfilledWorld_AndTheHistoryFileDeclaresItsFourLabels()
    {
        var d = await WorldAsync();

        foreach (var file in ValidationFiles.Append(History))
            await ValidationScripts.AssertZeroRowsAsync(d, file, minimumStatements: 1, "backfilled TP-4 shaped world");
        var text = ValidationScripts.Text(History);
        var found = Regex.Matches(text, @"SELECT\s+'([\w-]+)'").Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Assert.Equal(Labels.Order().ToList(), found);
        Assert.Contains("-- sellora:on-violation warn", text, StringComparison.Ordinal);   // WARN in 5a, block with 5b
        Assert.Contains("-- sellora:description ", text, StringComparison.Ordinal);
        // the version is deliberately not checked: a TP-3/TP-4 line and a backfilled history line are both clean
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "Id" = '{Intra.Line}' AND "TaxProfileVersionId" IS NOT NULL"""));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesInvoiceLines" WHERE "Id" = '{Fx(94)}' AND "TaxProfileVersionId" IS NULL AND "TaxProfileId" IS NOT NULL"""));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ line-without-profile

    [Theory]
    [InlineData("SalesInvoiceLines")]
    [InlineData("PurchaseOrderLines")]
    public async Task LineWithoutProfile_AnOriginalLine_IsReported(string table)
    {
        var source = table == "SalesInvoiceLines" ? Fx(94) : Fx(50);

        var rows = await ProbeAsync(History, p => p.ExecAsync(CloneLine(table, source, NewLine, ("TaxProfileId", "NULL"), ("TaxProfileVersionId", "NULL"))));

        Assert.Equal(Rows(R("line-without-profile", table, NewLine)), rows);
    }

    [Theory]
    [InlineData("SalesReturnLines")]
    [InlineData("PurchaseReturnLines")]
    public async Task LineWithoutProfile_AReturnLine_IsReported_TogetherWithItsOriginalsProfile(string table)
    {
        var (source, original, originalColumn) = table == "SalesReturnLines" ? (Fx(101), Fx(94), "SalesInvoiceLineId") : (Fx(71), Fx(50), "PurchaseOrderLineId");

        var rows = await ProbeAsync(History, p => p.ExecAsync(CloneLine(table, source, NewReturn, (originalColumn, $"'{original}'"), ("TaxProfileId", "NULL"), ("TaxProfileVersionId", "NULL"))));

        Assert.Equal(Rows(R("line-without-profile", table, NewReturn), R("return-profile-differs-from-original", table, NewReturn)), rows);   // the original has a profile, the return has none
    }

    [Fact]
    public async Task LineWithoutProfile_AReturnOfAnUnprofiledOriginal_IsReportedOnBothLines_ButNotAsDifferent()
    {
        var rows = await ProbeAsync(History, async p =>
        {
            await p.ExecAsync(CloneLine("SalesInvoiceLines", Fx(94), NewLine, ("TaxProfileId", "NULL"), ("TaxProfileVersionId", "NULL")));
            await p.ExecAsync(CloneLine("SalesReturnLines", Fx(101), NewReturn, ("SalesInvoiceLineId", $"'{NewLine}'"), ("TaxProfileId", "NULL"), ("TaxProfileVersionId", "NULL")));
        });

        Assert.Equal(Rows(R("line-without-profile", "SalesInvoiceLines", NewLine), R("line-without-profile", "SalesReturnLines", NewReturn)), rows);   // NULL vs NULL is not "differs"
    }

    // ------------------------------------------------------------------------------------------------------------------------------ taxid-not-profile-mirror

    [Theory]
    [InlineData("SalesInvoiceLines")]
    [InlineData("PurchaseOrderLines")]
    public async Task TaxIdNotProfileMirror_ALineWhoseTaxIdIsNotItsProfilesMirror_IsReported(string table)
    {
        var source = table == "SalesInvoiceLines" ? Fx(94) : Fx(50);

        var rows = await ProbeAsync(History, p => p.ExecAsync(CloneLine(table, source, NewLine, ("TaxId", $"'{TaxA18}'"), ("TaxProfileId", $"'{ProfileA}'"), ("TaxProfileVersionId", "NULL"))));   // the profile of GST 5, the mirror of GST 18

        Assert.Equal(Rows(R("taxid-not-profile-mirror", table, NewLine)), rows);
    }

    [Fact]
    public async Task TaxIdNotProfileMirror_AlsoCatchesAProfileThatChangedAfterTheBackfill_OnAHistoryLine()
    {
        var rows = await ProbeAsync(History, p => p.ExecAsync($"""UPDATE "SalesInvoiceLines" SET "TaxId" = '{TaxA18}' WHERE "Id" = '{Fx(94)}'"""));

        Assert.Equal(Rows(R("taxid-not-profile-mirror", "SalesInvoiceLines", Fx(94))), rows);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ profile-of-other-company

    [Theory]
    [InlineData("SalesInvoiceLines")]
    [InlineData("PurchaseOrderLines")]
    public async Task ProfileOfOtherCompany_ALineOfCompanyAWithCompanyBsProfile_IsReported_AndItsMirrorDiffersToo(string table)
    {
        var source = table == "SalesInvoiceLines" ? Fx(94) : Fx(50);

        var rows = await ProbeAsync(History, p => p.ExecAsync(CloneLine(table, source, NewLine, ("TaxProfileId", $"'{ProfileB}'"), ("TaxProfileVersionId", "NULL"))));

        Assert.Equal(Rows(R("profile-of-other-company", table, NewLine), R("taxid-not-profile-mirror", table, NewLine)), rows);
    }

    [Theory]
    [InlineData("SalesReturnLines")]
    [InlineData("PurchaseReturnLines")]
    public async Task ProfileOfOtherCompany_AReturnLineWithAProfileOfAnotherCompany_IsReported_AndItDiffersFromItsOriginal(string table)
    {
        var (source, original, originalColumn) = table == "SalesReturnLines" ? (Fx(101), Fx(94), "SalesInvoiceLineId") : (Fx(71), Fx(50), "PurchaseOrderLineId");

        var rows = await ProbeAsync(History, p => p.ExecAsync(CloneLine(table, source, NewReturn, (originalColumn, $"'{original}'"), ("TaxProfileId", $"'{ProfileB}'"), ("TaxProfileVersionId", "NULL"))));

        Assert.Equal(Rows(R("profile-of-other-company", table, NewReturn), R("return-profile-differs-from-original", table, NewReturn)), rows);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ return-profile-differs-from-original

    [Theory]
    [InlineData("SalesReturnLines")]
    [InlineData("PurchaseReturnLines")]
    public async Task ReturnProfileDiffers_AReturnWithAnotherProfileOfTheSameCompany_IsReported(string table)
    {
        var (source, original, originalColumn) = table == "SalesReturnLines" ? (Fx(101), Fx(94), "SalesInvoiceLineId") : (Fx(71), Fx(50), "PurchaseOrderLineId");

        var rows = await ProbeAsync(History, p => p.ExecAsync(CloneLine(table, source, NewReturn, (originalColumn, $"'{original}'"), ("TaxProfileId", $"'{ProfileNil}'"), ("TaxProfileVersionId", "NULL"))));

        Assert.Equal(Rows(R("return-profile-differs-from-original", table, NewReturn)), rows);
    }

    [Fact]
    public async Task ReturnProfileDiffers_AFutureTp4ReturnWithTheOriginalsProfileAndAVersion_IsNotReported_TheVersionIsNotChecked()
    {
        var rows = await ProbeAsync(History, p => p.ExecAsync(CloneLine("SalesReturnLines", Intra.ReturnLine, NewReturn)));   // a copy of a valid TP-4 return of the TP-4 line

        Assert.Empty(rows);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the snapshot files after the edit

    [Fact]
    public async Task SnapshotFiles_BackfilledHistoryLinesAreNotChecked_EvenWithCorruptedAmounts_ButTheirMirrorIs()
    {
        // history: amounts that would fail the TP-4 arithmetic checks are old data and stay unreported (the discriminator is the version) ...
        Assert.Empty(await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesInvoiceLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Fx(94)}'""")));
        Assert.Empty(await ProbeAsync(PurchaseSnapshot, p => p.ExecAsync($"""UPDATE "PurchaseOrderLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Fx(50)}'""")));
        Assert.Empty(await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesReturnLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Fx(101)}'""")));
        // ... but the profile of a history line must still be its mirror's: the check runs on every line that has a profile
        Assert.Equal(Rows(R("taxid-not-profile-mirror", "SalesInvoiceLines", Fx(94))), await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesInvoiceLines" SET "TaxId" = '{TaxA18}' WHERE "Id" = '{Fx(94)}'""")));
        Assert.Equal(Rows(R("taxid-not-profile-mirror", "PurchaseOrderLines", Fx(50))), await ProbeAsync(PurchaseSnapshot, p => p.ExecAsync($"""UPDATE "PurchaseOrderLines" SET "TaxId" = '{TaxA18}' WHERE "Id" = '{Fx(50)}'""")));
    }

    [Fact]
    public async Task SnapshotFiles_ACorruptedTp3Tp4Line_IsStillReported_WhateverTheBackfillDid()
    {
        Assert.Contains(R("line-total-not-taxable-plus-tax", "SalesInvoiceLines", Intra.Line),
            await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesInvoiceLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Intra.Line}'""")));
        Assert.Contains(R("line-total-not-taxable-plus-tax", "SalesReturnLines", Intra.ReturnLine),
            await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesReturnLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Intra.ReturnLine}'""")));
        Assert.Contains(R("line-total-not-taxable-plus-tax", "PurchaseOrderLines", Purchase.Line),
            await ProbeAsync(PurchaseSnapshot, p => p.ExecAsync($"""UPDATE "PurchaseOrderLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Purchase.Line}'""")));
        Assert.Contains(R("line-total-not-taxable-plus-tax", "PurchaseReturnLines", Purchase.ReturnLine),
            await ProbeAsync(PurchaseSnapshot, p => p.ExecAsync($"""UPDATE "PurchaseReturnLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Purchase.ReturnLine}'""")));
        // (the corrupted line also unbalances its header: the label of the line is what matters here)
        // a TP-4 line that lost its version is history from now on: the version is the "new line" marker, and the history file is the one that notices a profile that does not belong
        Assert.DoesNotContain(await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesInvoiceLines" SET "TaxProfileVersionId" = NULL, "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Intra.Line}'""")),
            r => r.StartsWith("line-total-not-taxable-plus-tax|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SnapshotFiles_OtherTp4Checks_StillFire_OnTheNewLines()
    {
        // inter-state place on an intra-state document, a version of another profile, and CGST != SGST: three of the original label families, on lines selected by the version
        Assert.Contains(R("version-not-of-profile-or-company", "SalesInvoiceLines", Intra.Line),
            await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesInvoiceLines" SET "TaxProfileVersionId" = '{VersionNil}' WHERE "Id" = '{Intra.Line}'""")));
        Assert.Contains(R("cgst-not-equal-sgst", "SalesInvoiceLines", Intra.Line),
            await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesInvoiceLineTaxes" SET "TaxAmount" = "TaxAmount" + 0.5 WHERE "Id" = '{Intra.Cgst}'""")));
        Assert.Contains(R("row-without-treatment", "SalesInvoiceLineTaxes", Intra.Cgst),
            await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesInvoiceLineTaxes" SET "Treatment" = NULL WHERE "Id" = '{Intra.Cgst}'""")));
        Assert.Contains(R("cgst-not-equal-sgst", "PurchaseOrderLines", Purchase.Line),
            await ProbeAsync(PurchaseSnapshot, p => p.ExecAsync($"""UPDATE "PurchaseOrderLineTaxes" SET "TaxAmount" = "TaxAmount" + 0.5 WHERE "Id" = '{Purchase.Cgst}'""")));
        Assert.Contains(R("row-without-treatment", "PurchaseOrderLineTaxes", Purchase.Cgst),
            await ProbeAsync(PurchaseSnapshot, p => p.ExecAsync($"""UPDATE "PurchaseOrderLineTaxes" SET "Treatment" = NULL WHERE "Id" = '{Purchase.Cgst}'""")));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the rollback-era return tolerance

    [Fact]
    public async Task RollbackEraReturn_AfterTheBackfill_ProfileCopiedNoVersionNoPlaceAndALegacyRow_IsTolerated()
    {
        var d = await WorldAsync();

        Assert.Equal([$"{ProfileA}|-|-"], await QueryAsync(d, $"""SELECT "TaxProfileId" || '|' || COALESCE("TaxProfileVersionId"::text, '-') || '|' || COALESCE("PlaceOfSupply", '-') FROM "SalesReturnLines" WHERE "Id" = '{Rollback.ReturnLine}'""", r => r.GetString(0)));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "SalesReturnLineTaxes" WHERE "SalesReturnLineId" = '{Rollback.ReturnLine}' AND "TaxComponentId" IS NULL AND "Treatment" IS NULL"""));
        Assert.Empty(await ProbeAsync(SalesSnapshot, _ => Task.CompletedTask));
        // before the backfill it was tolerated too (no profile at all): the edit only widened the shape
        await using var before = await AtHeadCopyAsync(postgres);
        Assert.Empty(await InRolledBackTransactionAsync(before, p => p.ViolationsAsync(SalesSnapshot)));
    }

    [Fact]
    public async Task RollbackEraReturn_ToleranceEndsWhereTheShapeEnds()
    {
        string Mutate(string set) => $"""UPDATE "SalesReturnLines" SET {set} WHERE "Id" = '{Rollback.ReturnLine}'""";
        var expected = Rows(R("return-line-version-differs-from-original", "SalesReturnLines", Rollback.ReturnLine));

        // another profile than the original's: not the backfill's doing
        Assert.Equal(expected, await ProbeAsync(SalesSnapshot, p => p.ExecAsync(Mutate($"\"TaxProfileId\" = '{ProfileNil}'"))));
        // a place of supply (a TP-4 build's return would have its version too)
        Assert.Equal(expected, await ProbeAsync(SalesSnapshot, p => p.ExecAsync(Mutate("\"PlaceOfSupply\" = 'IntraState'"))));
        // a version that is not the original's
        Assert.Contains(expected[0], await ProbeAsync(SalesSnapshot, p => p.ExecAsync(Mutate($"\"TaxProfileVersionId\" = '{VersionNil}'"))));
        // no legacy row: the TP-3 build always writes one, so a return without it is not the rollback shape
        Assert.Contains(expected[0], await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""DELETE FROM "SalesReturnLineTaxes" WHERE "SalesReturnLineId" = '{Rollback.ReturnLine}'""")));
        // a legacy row that carries a component or a treatment is a TP-4 row
        Assert.Contains(expected[0], await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesReturnLineTaxes" SET "Treatment" = 'Taxable' WHERE "SalesReturnLineId" = '{Rollback.ReturnLine}'""")));
        // a version-less return of an original that has no version is history (nothing to tolerate, nothing reported)
        Assert.Empty(await ProbeAsync(SalesSnapshot, p => p.ExecAsync(CloneLine("SalesReturnLines", Fx(101), NewReturn, ("SalesInvoiceLineId", $"'{Fx(94)}'"), ("TaxProfileId", "NULL")))));
    }

    [Fact]
    public async Task RollbackEraReturn_ATp4ReturnThatLostItsVersionOrProfile_IsReportedUnlessItIsTheExactShape()
    {
        var expected = Rows(R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

        // the Intra return carries profile, version AND place: dropping only the version keeps the place -> reported
        Assert.Equal(expected, await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesReturnLines" SET "TaxProfileVersionId" = NULL WHERE "Id" = '{Intra.ReturnLine}'""")));
        // the TP-4 return of the Vat document: its original has a version, the return is given another profile -> reported
        Assert.Contains(R("return-line-version-differs-from-original", "SalesReturnLines", Vat.ReturnLine),
            await ProbeAsync(SalesSnapshot, p => p.ExecAsync($"""UPDATE "SalesReturnLines" SET "TaxProfileId" = '{ProfileA}' WHERE "Id" = '{Vat.ReturnLine}'""")));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the three files through the migrator

    [Fact]
    public async Task Migrator_HistoryFileWarns_SnapshotFilesBlock_AndTheOldWorldNeverBlocksOnHistory()
    {
        // before the backfill (history lines without a profile): the history file WARNS, the two block files pass, so the release is never stopped by the 5a check
        await using var before = await AtHeadCopyAsync(postgres);
        var root = Directory.CreateTempSubdirectory("sellora-hist-v-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "validation"));
            foreach (var file in new[] { History, SalesSnapshot, PurchaseSnapshot }) File.WriteAllText(Path.Combine(root, "validation", "V__" + file), ValidationScripts.Text(file));
            var runner = new Sellora.DbMigrator.MigrationRunner(new Sellora.DbMigrator.MigratorOptions(before.ConnectionString, root, new Sellora.DbMigrator.RunInfo("test", null, null, "Deploy", "integration-test", null, null),
                Sellora.DbMigrator.ViolationMode.Block, TimeSpan.FromSeconds(30), EfBundlePath: null, PlanOnly: false), new StringWriter());

            var exit = await runner.RunAsync(CancellationToken.None);

            var status = runner.Outcomes.ToDictionary(o => o.Name[(o.Name.LastIndexOf("V__", StringComparison.Ordinal) + 3)..], o => o.Status);
            Assert.Equal("Warning", status[History]);     // header: on-violation warn, wins over the package default of Block
            Assert.Equal("Passed", status[SalesSnapshot]);
            Assert.Equal("Passed", status[PurchaseSnapshot]);
            Assert.Equal(Sellora.DbMigrator.ExitCode.Succeeded, exit);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
