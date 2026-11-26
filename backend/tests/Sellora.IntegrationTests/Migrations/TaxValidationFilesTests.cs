using Sellora.DbMigrator;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// The ten backend/db/validation/V__tax-*.sql files (six of TP-1, two of TP-3, one of TP-4, the WARN history-profile check of TP-5a (TaxHistoryBackfillScriptTests probes it): see TaxPurchaseValidationFilesTests and TaxSalesValidationFilesTests for their probes) (docs/features/tax-profiles.md, "Validation SQL"): zero rows on a clean database and on the migrated legacy world, then one probe per check
/// (a violation written inside a transaction that is always rolled back) proving the check fires with the expected label, and the block / warn header of each file through the real migrator.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TaxValidationFilesTests(PostgresFixture postgres)
{
    // the legacy GST 5% profile of company A (CGST 2.5 / SGST 2.5 intra, IGST 5 inter) and the zero-rate NilRated one
    private static readonly Guid ProfileA = TaxSeedData.LegacyProfileId(TaxA5), VersionA = TaxSeedData.LegacyVersionId(TaxA5);
    private static readonly Guid ProfileNil = TaxSeedData.LegacyProfileId(TaxA0), VersionNil = TaxSeedData.LegacyVersionId(TaxA0);
    private static string Component(Guid company, string cc, string code) => TaxSeedData.ComponentId(company, cc, code).ToString();

    private const string Integrity = "tax-profiles-integrity.sql", GstRules = "tax-gst-rules.sql", Resolve = "tax-products-resolve.sql",
        Consistency = "tax-line-rows-consistency.sql", Missing = "tax-line-rows-missing.sql", Drift = "tax-mirror-drift.sql",
        MissingPurchases = "tax-line-rows-missing-purchases.sql", PurchaseSnapshotFile = "tax-purchase-snapshot.sql", SalesSnapshotFile = "tax-sales-snapshot.sql",
        HistoryProfile = HistoryProfileFile;

    /// <summary>Applies <paramref name="mutate"/> in a rolled-back transaction, then runs the file; the rows must be empty before and contain the expected text after.</summary>
    private async Task<List<string>> ProbeAsync(string file, Func<Probe, Task> mutate, params string[] expected)
    {
        var d = await MigratedAsync(postgres);
        var (before, after) = await InRolledBackTransactionAsync(d, async p =>
        {
            var clean = await p.ViolationsAsync(file);
            await mutate(p);
            return (clean, await p.ViolationsAsync(file));
        });
        Assert.True(before.Count == 0, $"{file} is not clean on the migrated world: {string.Join(" | ", before.Take(5))}");
        foreach (var text in expected)
            Assert.True(after.Any(r => r.Contains(text, StringComparison.Ordinal)), $"{file}: no row contains '{text}'. Rows: {string.Join(" | ", after.Take(10))}");
        return after;
    }

    // ------------------------------------------------------------------------------------------------------------ clean

    [Fact]
    public async Task AllTaxValidationFiles_OnAFreshlyMigratedEmptyDatabase_ReturnZeroRows()
    {
        await using var d = await postgres.CreateDatabaseAsync(migrate: true);

        foreach (var file in ValidationFiles.Append(HistoryProfileFile)) await ValidationScripts.AssertZeroRowsAsync(d, file, minimumStatements: 1, "clean migrated database");
    }

    [Fact]
    public async Task AllTaxValidationFiles_OnTheMigratedLegacyWorld_ReturnZeroRows()
    {
        var d = await MigratedAsync(postgres);

        foreach (var file in ValidationFiles) await ValidationScripts.AssertZeroRowsAsync(d, file, minimumStatements: 1, "migrated legacy world (13 taxes, 16 products, 8 companies, history lines of four kinds)");
    }

    [Fact]
    public async Task AllTaxValidationFiles_StayZeroAfterTheSqlResourcesAreRunAgain()
    {
        var d = await MigratedAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.RunResourceAsync(SeedResource);
            await p.RunResourceAsync(BackfillResource);
            await p.RunResourceAsync(LegacyRowsResource);
            var all = new List<string>();
            foreach (var file in ValidationFiles) all.AddRange((await p.ViolationsAsync(file)).Select(r => file + ": " + r));
            return all;
        });

        Assert.Empty(rows);
    }

    // ------------------------------------------------------------------------------------------------------------ V__tax-profiles-integrity

    [Fact]
    public async Task Integrity_VersionOverlap_IsReported() =>
        await ProbeAsync(Integrity, p => p.ExecAsync($"""
            INSERT INTO "TaxProfileVersions" ("Id", "CompanyId", "TaxProfileId", "EffectiveFrom", "EffectiveTo", "TotalPercent", "CreatedAt")
            VALUES (gen_random_uuid(), '{CoA}', '{ProfileA}', '2010-01-01', '2010-12-31', 5, now())
            """), "version overlap|" + ProfileA);

    [Fact]
    public async Task Integrity_ProfileWithoutOpenVersion_IsReported() =>
        await ProbeAsync(Integrity, p => p.ExecAsync($"""UPDATE "TaxProfileVersions" SET "EffectiveTo" = '2099-01-01' WHERE "Id" = '{VersionNil}'"""),
            "profile without open version|" + ProfileNil);

    [Fact]
    public async Task Integrity_TaxableVersionWithoutComponents_IsReported() =>
        await ProbeAsync(Integrity, p => p.ExecAsync($"""DELETE FROM "TaxProfileVersionComponents" WHERE "TaxProfileVersionId" = '{VersionA}'"""),
            "taxable version without components|" + ProfileA + "|" + VersionA);

    [Fact]
    public async Task Integrity_NonTaxableVersionWithComponents_IsReported() =>
        await ProbeAsync(Integrity, p => p.ExecAsync($"""
            INSERT INTO "TaxProfileVersionComponents" ("Id", "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Method", "Applicability", "RatePercent", "AmountPerBaseUnit", "SortOrder")
            VALUES (gen_random_uuid(), '{CoA}', '{VersionNil}', '{Component(CoA, "IN", "CGST")}', 'Percent', 'IntraState', 0, NULL, 1)
            """), "non-taxable version with components|" + ProfileNil + "|" + VersionNil);

    [Fact]
    public async Task Integrity_ComponentOfAnotherCountryThanItsProfile_IsReported() =>
        await ProbeAsync(Integrity, p => p.ExecAsync($"""
            INSERT INTO "TaxComponents" ("Id", "CompanyId", "CountryId", "Code", "Name", "Kind", "Method", "IsActive", "CreatedAt")
            VALUES ('{G(0xA01)}', '{CoA}', (SELECT "Id" FROM "Countries" WHERE "Code" = 'AE'), 'VAT', 'Value added tax', 'Vat', 'Percent', true, now());
            INSERT INTO "TaxProfileVersionComponents" ("Id", "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Method", "Applicability", "RatePercent", "AmountPerBaseUnit", "SortOrder")
            VALUES ('{G(0xA02)}', '{CoA}', '{VersionA}', '{G(0xA01)}', 'Percent', 'Always', 1, NULL, 9)
            """), "component country differs from profile country|" + ProfileA + "|" + G(0xA02));

    [Fact]
    public async Task Integrity_ProfileWithoutMirrorTax_IsReported() =>
        // the composite FK would normally make this impossible: drop it inside the probe to prove the safety net itself
        await ProbeAsync(Integrity, p => p.ExecAsync($"""
            ALTER TABLE "TaxProfiles" DROP CONSTRAINT "FK_TaxProfiles_Taxes_CompanyId_LegacyTaxId";
            DELETE FROM "Taxes" WHERE "Id" = '{TaxSeedData.MirrorTaxId(CoB, "AE", "VAT-5")}'
            """), "profile without mirror tax|" + TaxSeedData.ProfileId(CoB, "AE", "VAT-5"));

    // ------------------------------------------------------------------------------------------------------------ V__tax-gst-rules

    [Fact]
    public async Task GstRules_MissingSgst_IsReported() =>
        await ProbeAsync(GstRules, p => p.ExecAsync($"""DELETE FROM "TaxProfileVersionComponents" WHERE "TaxProfileVersionId" = '{VersionA}' AND "TaxComponentId" = '{Component(CoA, "IN", "SGST")}'"""),
            "GST taxable version missing CGST, SGST/UTGST or IGST|" + ProfileA + "|" + VersionA);

    [Fact]
    public async Task GstRules_MissingIgst_IsReported() =>
        await ProbeAsync(GstRules, p => p.ExecAsync($"""DELETE FROM "TaxProfileVersionComponents" WHERE "TaxProfileVersionId" = '{VersionA}' AND "TaxComponentId" = '{Component(CoA, "IN", "IGST")}'"""),
            "GST taxable version missing CGST, SGST/UTGST or IGST|" + ProfileA);

    [Fact]
    public async Task GstRules_CgstDifferentFromSgst_IsReported() =>
        await ProbeAsync(GstRules, p => p.ExecAsync($"""UPDATE "TaxProfileVersionComponents" SET "RatePercent" = 3 WHERE "TaxProfileVersionId" = '{VersionA}' AND "TaxComponentId" = '{Component(CoA, "IN", "SGST")}'"""),
            "CGST differs from SGST/UTGST|" + ProfileA);

    [Fact]
    public async Task GstRules_IgstDifferentFromCgstPlusSgst_IsReported() =>
        await ProbeAsync(GstRules, p => p.ExecAsync($"""UPDATE "TaxProfileVersionComponents" SET "RatePercent" = 6 WHERE "TaxProfileVersionId" = '{VersionA}' AND "TaxComponentId" = '{Component(CoA, "IN", "IGST")}'"""),
            "IGST differs from CGST + SGST/UTGST|" + ProfileA);

    [Fact]
    public async Task GstRules_VatComponentInAGstProfile_IsReported() =>
        await ProbeAsync(GstRules, p => p.ExecAsync($"""
            INSERT INTO "TaxComponents" ("Id", "CompanyId", "CountryId", "Code", "Name", "Kind", "Method", "IsActive", "CreatedAt")
            VALUES ('{G(0xA11)}', '{CoA}', (SELECT "Id" FROM "Countries" WHERE "Code" = 'IN'), 'VATX', 'Wrong kind', 'Vat', 'Percent', true, now());
            INSERT INTO "TaxProfileVersionComponents" ("Id", "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Method", "Applicability", "RatePercent", "AmountPerBaseUnit", "SortOrder")
            VALUES (gen_random_uuid(), '{CoA}', '{VersionA}', '{G(0xA11)}', 'Percent', 'Always', 0, NULL, 9)
            """), "VAT component in a GST profile|" + ProfileA);

    [Fact]
    public async Task GstRules_TotalPercentDifferentFromTheComponents_IsReported() =>
        await ProbeAsync(GstRules, p => p.ExecAsync($"""UPDATE "TaxProfileVersions" SET "TotalPercent" = 7 WHERE "Id" = '{VersionA}'"""),
            "TotalPercent differs from the percent components|" + ProfileA);

    [Fact]
    public async Task GstRules_APerUnitCessComponent_NeverCountsTowardTheRate()
    {
        // BR3: a per-base-unit cess is not a percentage: adding one to a valid GST version keeps the file clean
        var d = await MigratedAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""
                INSERT INTO "TaxProfileVersionComponents" ("Id", "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Method", "Applicability", "RatePercent", "AmountPerBaseUnit", "SortOrder")
                VALUES (gen_random_uuid(), '{CoA}', '{VersionA}', '{Component(CoA, "IN", "CESS-UNIT")}', 'AmountPerBaseUnit', 'Always', NULL, 2.5, 9)
                """);
            return await p.ViolationsAsync(GstRules);
        });

        Assert.Empty(rows);
    }

    // ------------------------------------------------------------------------------------------------------------ V__tax-products-resolve

    [Fact]
    public async Task ProductsResolve_ProductWithNoAssignmentAndNoCompanyDefault_IsReported() =>
        await ProbeAsync(Resolve, p => p.ExecAsync($"""
            DELETE FROM "TaxAssignments" WHERE "CompanyId" = '{CoA}' AND ("Scope" = 'Company' OR "ProductId" = '{Fx(20)}')
            """), "does not resolve", Fx(20).ToString());

    [Fact]
    public async Task ProductsResolve_ProductTaxIdDifferentFromTheMirrorOfItsProfile_IsReported() =>
        await ProbeAsync(Resolve, p => p.ExecAsync($"""UPDATE "Products" SET "TaxId" = '{TaxA18}' WHERE "Id" = '{Fx(20)}'"""),
            Fx(20) + "|TaxId differs from the mirror of the resolved profile");

    [Fact]
    public async Task ProductsResolve_ProductWithoutItsOwnAssignment_FallsBackThroughTheCompanyDefault()
    {
        // BR8: the order is product, subcategory, category, company default; with only the default left, a product whose tax is not the default's mirror is reported
        var d = await MigratedAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""DELETE FROM "TaxAssignments" WHERE "CompanyId" = '{CoA}' AND "ProductId" = '{Fx(20)}'""");
            return await p.ViolationsAsync(Resolve);
        });

        Assert.Equal([Fx(20) + "|TaxId differs from the mirror of the resolved profile"], rows);   // default = GST 18%, the product still points at GST 5%
    }

    // ------------------------------------------------------------------------------------------------------------ V__tax-line-rows-consistency

    public static IEnumerable<object[]> LineKindRows() => LineKinds.Select(k => new object[] { k.Lines, k.TaxRows, k.IdColumn });

    [Theory]
    [MemberData(nameof(LineKindRows))]
    public async Task LineRowsConsistency_RowsThatDoNotAddUpToTheLineTaxAmount_AreReported(string lines, string taxRows, string idColumn) =>
        await ProbeAsync(Consistency, p => p.ExecAsync($"""
            UPDATE "{taxRows}" SET "TaxAmount" = "TaxAmount" + 1 WHERE "{idColumn}" = (SELECT "Id" FROM "{lines}" ORDER BY "Id" LIMIT 1)
            """), lines + "|");

    [Theory]
    [MemberData(nameof(LineKindRows))]
    public async Task LineRowsConsistency_ALegacyRowWhoseBaseDiffersFromTheLinesTaxableValue_IsReported(string lines, string taxRows, string idColumn) =>
        await ProbeAsync(Consistency, p => p.ExecAsync($"""
            UPDATE "{taxRows}" SET "BaseAmount" = "BaseAmount" + 1 WHERE "{idColumn}" = (SELECT "Id" FROM "{lines}" ORDER BY "Id" LIMIT 1)
            """), lines + "|");

    [Fact]
    public async Task LineRowsConsistency_ComponentRowsThatAddUpToTheLineTax_AreAccepted_EvenWithADifferentBase()
    {
        // from TP-3 a line carries component rows (CGST + SGST): the sum is what counts, and only legacy rows (no component) are compared with TaxableValue
        var d = await MigratedAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""
                DELETE FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{Fx(90)}';
                INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
                SELECT gen_random_uuid(), l."Id", '{Component(CoA, "IN", "CGST")}', 'CGST', 'Cgst', 'Percent', 2.5, NULL, 999, l."TaxAmount" / 2 FROM "SalesInvoiceLines" l WHERE l."Id" = '{Fx(90)}';
                INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
                SELECT gen_random_uuid(), l."Id", '{Component(CoA, "IN", "SGST")}', 'SGST', 'Sgst', 'Percent', 2.5, NULL, 999, l."TaxAmount" / 2 FROM "SalesInvoiceLines" l WHERE l."Id" = '{Fx(90)}';
                """);
            return await p.ViolationsAsync(Consistency);
        });

        Assert.Empty(rows);
    }

    // ------------------------------------------------------------------------------------------------------------ V__tax-line-rows-missing

    public static IEnumerable<object[]> SalesLineKindRows() => LineKinds.Where(k => k.Lines.StartsWith("Sales", StringComparison.Ordinal)).Select(k => new object[] { k.Lines, k.TaxRows, k.IdColumn });

    public static IEnumerable<object[]> PurchaseLineKindRows() => LineKinds.Where(k => k.Lines.StartsWith("Purchase", StringComparison.Ordinal)).Select(k => new object[] { k.Lines, k.TaxRows, k.IdColumn });

    [Theory]
    [MemberData(nameof(SalesLineKindRows))]
    public async Task LineRowsMissing_ASalesLineWithoutAnyTaxRow_IsReported_AndCaughtUpByTheLegacyRowsResource(string lines, string taxRows, string idColumn)
    {
        var d = await MigratedAsync(postgres);

        var (reported, afterCatchUp) = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""DELETE FROM "{taxRows}" WHERE "{idColumn}" = (SELECT "Id" FROM "{lines}" ORDER BY "Id" LIMIT 1)""");
            var rows = await p.ViolationsAsync(Missing);
            await p.RunResourceAsync(LegacyRowsResource);
            return (rows, await p.ViolationsAsync(Missing));
        });

        Assert.Single(reported);
        Assert.StartsWith(lines + "|", reported[0], StringComparison.Ordinal);
        Assert.Empty(afterCatchUp);   // the documented remedy: re-run the idempotent legacy-rows file
    }

    [Theory]
    [MemberData(nameof(PurchaseLineKindRows))]
    public async Task LineRowsMissingPurchases_APurchaseLineWithoutAnyTaxRow_IsReported_AndCaughtUpByTheLegacyRowsResource(string lines, string taxRows, string idColumn)
    {
        var d = await MigratedAsync(postgres);

        var (reported, afterCatchUp, salesFileRows) = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""DELETE FROM "{taxRows}" WHERE "{idColumn}" = (SELECT "Id" FROM "{lines}" ORDER BY "Id" LIMIT 1)""");
            var rows = await p.ViolationsAsync(MissingPurchases);
            var sales = await p.ViolationsAsync(Missing);   // the sales-only (blocking since TP-4) file does not see purchase gaps
            await p.RunResourceAsync(LegacyRowsResource);
            return (rows, await p.ViolationsAsync(MissingPurchases), sales);
        });

        Assert.Single(reported);
        Assert.StartsWith(lines + "|", reported[0], StringComparison.Ordinal);
        Assert.Empty(salesFileRows);
        Assert.Empty(afterCatchUp);
    }

    // ------------------------------------------------------------------------------------------------------------ V__tax-mirror-drift

    [Fact]
    public async Task MirrorDrift_MirrorRateDifferentFromTheCurrentVersion_IsReported() =>
        await ProbeAsync(Drift, p => p.ExecAsync($"""UPDATE "Taxes" SET "RatePercent" = 99 WHERE "Id" = '{TaxA5}'"""), ProfileA + "|mirror rate differs from the current version");

    [Fact]
    public async Task MirrorDrift_MirrorActiveFlagDifferentFromTheProfile_IsReported() =>
        await ProbeAsync(Drift, p => p.ExecAsync($"""UPDATE "Taxes" SET "IsActive" = false WHERE "Id" = '{TaxA5}'"""), ProfileA + "|mirror IsActive differs from the profile");

    [Fact]
    public async Task MirrorDrift_ProfileWithNoVersionValidToday_IsReported() =>
        // a future-dated only version: the mirror lags until its date
        await ProbeAsync(Drift, p => p.ExecAsync($"""UPDATE "TaxProfileVersions" SET "EffectiveFrom" = '2999-01-01' WHERE "Id" = '{VersionA}'"""), ProfileA + "|no version valid today");

    // ------------------------------------------------------------------------------------------------------------ headers through the migrator

    private static string RepoPath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"{string.Join('/', parts)} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>Runs ONE real V__ file through <see cref="MigrationRunner"/> with the package default Warn, so Failed can only come from the file's own block header.</summary>
    private static async Task<string> RunFileAsync(TestDatabase d, string file)
    {
        var root = Directory.CreateTempSubdirectory("sellora-tax-v-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "validation"));
            File.WriteAllText(Path.Combine(root, "validation", "V__" + file), ValidationScripts.Text(file));
            var runner = new MigrationRunner(new MigratorOptions(d.ConnectionString, root, new RunInfo("test", null, null, "Deploy", "integration-test", null, null),
                ViolationMode.Warn, TimeSpan.FromSeconds(30), EfBundlePath: null, PlanOnly: false), new StringWriter());
            await runner.RunAsync(CancellationToken.None);
            return runner.Outcomes.Single(o => o.Name.EndsWith("V__" + file, StringComparison.Ordinal)).Status;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Migrator_AllTaxFiles_PassOnTheMigratedWorld_AndAreDiscoveredInTheRealDbFolder()
    {
        await using var d = await MigratedCopyAsync(postgres);

        foreach (var file in ValidationFiles) Assert.Equal("Passed", await RunFileAsync(d, file));
        // TP-5a: the history check WARNS until the backfill script has run (the world's history lines have no profile yet) ...
        Assert.Equal("Warning", await RunFileAsync(d, HistoryProfile));
        // the whole backend/db folder as a release runs it. The legacy fixture's own raw rows legitimately trip the unrelated stock and unit checks, so those are not asserted here:
        // what must hold is that the tax files are discovered, run and pass.
        var log = new StringWriter();
        var runner = new MigrationRunner(new MigratorOptions(d.ConnectionString, RepoPath("backend", "db"), new RunInfo("test", null, null, "Deploy", "integration-test", null, null),
            ViolationMode.Warn, TimeSpan.FromSeconds(60), EfBundlePath: null, PlanOnly: false), log);
        await runner.RunAsync(CancellationToken.None);
        var tax = runner.Outcomes.Where(o => o.Name.Contains("V__tax-", StringComparison.Ordinal)).ToList();
        Assert.True(tax.Count == ValidationFiles.Length + 1, $"expected {ValidationFiles.Length + 1} tax files, got {tax.Count}\n{log}");
        // ... and the same run applies the data scripts of the real folder first (the backfill script is one of them): afterwards every tax file passes, the history check included
        Assert.Contains(runner.Outcomes, o => o.Kind == ScriptKind.Data && o.Name.Contains("tax-history-profile-backfill", StringComparison.Ordinal) && o.Status == "Succeeded");
        Assert.All(tax, o => Assert.Equal("Passed", o.Status));
    }

    [Theory]
    [InlineData(Integrity, "block")]
    [InlineData(GstRules, "block")]
    [InlineData(Resolve, "block")]
    [InlineData(Consistency, "block")]
    [InlineData(Missing, "block")]
    [InlineData(Drift, "warn")]
    [InlineData(MissingPurchases, "block")]
    [InlineData(PurchaseSnapshotFile, "block")]
    [InlineData(SalesSnapshotFile, "block")]
    [InlineData(HistoryProfile, "warn")]
    public async Task Migrator_ABlockFileFailsAndAWarnFileWarns_WhenItsCheckFindsARow(string file, string mode)
    {
        await using var d = await MigratedCopyAsync(postgres);
        await ExecuteAsync(d, file switch
        {
            Integrity => $"""UPDATE "TaxProfileVersions" SET "EffectiveTo" = '2099-01-01' WHERE "Id" = '{VersionNil}'""",
            GstRules => $"""UPDATE "TaxProfileVersions" SET "TotalPercent" = 7 WHERE "Id" = '{VersionA}'""",
            Resolve => $"""UPDATE "Products" SET "TaxId" = '{TaxA18}' WHERE "Id" = '{Fx(20)}'""",
            Consistency => """UPDATE "SalesInvoiceLineTaxes" SET "TaxAmount" = "TaxAmount" + 1 WHERE "Id" = (SELECT "Id" FROM "SalesInvoiceLineTaxes" LIMIT 1)""",
            HistoryProfile => $"""UPDATE "SalesInvoiceLines" SET "TaxProfileId" = '{TaxSeedData.LegacyProfileId(TaxB5)}' WHERE "Id" = '{Fx(90)}'""",   // a profile of another company whose mirror is not the line's TaxId
            Missing => """DELETE FROM "SalesInvoiceLineTaxes" WHERE "Id" = (SELECT "Id" FROM "SalesInvoiceLineTaxes" LIMIT 1)""",
            SalesSnapshotFile => $"""UPDATE "SalesInvoiceLines" SET "TaxProfileId" = '{ProfileA}', "TaxProfileVersionId" = '{VersionA}', "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Fx(90)}'""",
            MissingPurchases => """DELETE FROM "PurchaseOrderLineTaxes" WHERE "Id" = (SELECT "Id" FROM "PurchaseOrderLineTaxes" LIMIT 1)""",
            PurchaseSnapshotFile => """INSERT INTO "PurchaseOrderLineTaxes" ("Id", "PurchaseOrderLineId", "ComponentCode", "Kind", "Method", "RatePercent", "BaseAmount", "TaxAmount", "TaxComponentId") SELECT gen_random_uuid(), "Id", 'X', 'Other', 'Percent', 1, 1, 0, (SELECT "Id" FROM "TaxComponents" LIMIT 1) FROM "PurchaseOrderLines" LIMIT 1""",
            _ => $"""UPDATE "Taxes" SET "RatePercent" = 99 WHERE "Id" = '{TaxA5}'""",
        });

        Assert.Equal(mode == "block" ? "Failed" : "Warning", await RunFileAsync(d, file));
    }

    [Fact]
    public void TaxValidationFiles_DeclareTheirDescriptionAndViolationMode()
    {
        var expected = new Dictionary<string, string>
        {
            [Integrity] = "block", [GstRules] = "block", [Resolve] = "block", [Consistency] = "block", [Missing] = "block", [Drift] = "warn",
            [MissingPurchases] = "block", [PurchaseSnapshotFile] = "block", [SalesSnapshotFile] = "block", [HistoryProfile] = "warn",
        };

        foreach (var (file, mode) in expected)
        {
            var text = ValidationScripts.Text(file);
            Assert.Contains("-- sellora:on-violation " + mode, text, StringComparison.Ordinal);
            Assert.Contains("-- sellora:description ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("personal", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
