using System.Diagnostics;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Data.Migrations;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// <c>AddTaxProfiles</c> + <c>ValidateTaxLineForeignKeys</c> (docs/features/tax-profiles.md, TP-1 design "Migration M1"): AC16, AC17, the standard seeds and company default (T12, T21), T18, the preflight,
/// idempotency of the frozen SQL resources, drift against <see cref="TaxSeedData"/>, NOT VALID then VALIDATE, lock_timeout fail-fast and the Down/Up round trip. All of it runs on the legacy world of
/// <see cref="TaxProfileWorld"/> (the fixture lines plus eight more companies) written into the schema before the migration.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddTaxProfilesMigrationTests(PostgresFixture postgres)
{
    private static readonly string[] TaxTables =
        ["TaxComponents", "TaxProfiles", "TaxProfileVersions", "TaxProfileVersionComponents", "TaxAssignments",
         "SalesInvoiceLineTaxes", "PurchaseOrderLineTaxes", "SalesReturnLineTaxes", "PurchaseReturnLineTaxes"];

    private static Exception Root(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is PostgresException || e.InnerException is null) return e;
        return ex;
    }

    private static string D(Guid id) => id.ToString("D");
    private static string Dec(decimal? value) => value?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<List<string>> RowsAsync(TestDatabase d, string sql) =>
        await QueryAsync(d, sql, r => string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "-" : Format(r.GetValue(i)))));

    private static async Task<bool> TableExistsAsync(TestDatabase d, string table) =>
        await ScalarAsync<long>(d, $"SELECT count(*) FROM pg_class WHERE relname = '{table}' AND relkind = 'r'") == 1;

    private static void AssertSame(IEnumerable<string> expected, IEnumerable<string> actual, string what)
    {
        var e = expected.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var a = actual.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (e.SequenceEqual(a)) return;
        Assert.Fail($"{what}: expected {e.Count} row(s), got {a.Count}.\nmissing: {string.Join(" ;; ", e.Except(a).Take(6))}\nunexpected: {string.Join(" ;; ", a.Except(e).Take(6))}");
    }

    private static async Task RunResourceInTransactionAsync(TestDatabase d, params string[] resources)
    {
        // exactly as the migration: one transaction (the frozen files create ON COMMIT DROP temp tables)
        await using var connection = new NpgsqlConnection(d.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        foreach (var resource in resources)
        {
            await using var command = new NpgsqlCommand(MigrationSql.Read(resource), connection, tx);
            await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    // ------------------------------------------------------------------------------------------------------------------------- AC16

    [Fact]
    public async Task Up_HistoricLinesAndBothReturnTypes_GetExactlyOneLegacyRowEach_AndNothingElseChanges()
    {
        await using var d = await AtPreviousAsync(postgres);
        var before = await UnchangedSnapshotAsync(d);
        var lineIds = new Dictionary<string, List<string>>();
        foreach (var (lines, _, _, _) in LineKinds) lineIds[lines] = await QueryAsync(d, $"""SELECT "Id"::text FROM "{lines}" """, r => r.GetString(0));
        Assert.All(lineIds, kv => Assert.NotEmpty(kv.Value));   // the fixture has historic sales and purchase lines and both return types
        Assert.False(await TableExistsAsync(d, "SalesInvoiceLineTaxes"));

        await MigrateExactlyAsync(d, TaxM1);

        foreach (var (lines, taxRows, idColumn, _) in LineKinds)
        {
            var ids = lineIds[lines];
            Assert.Equal(ids.Count, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{lines}" """));
            Assert.Equal(ids.Count, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{taxRows}" """));
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""
                SELECT count(*) FROM "{lines}" l WHERE (SELECT count(*) FROM "{taxRows}" x WHERE x."{idColumn}" = l."Id") <> 1
                """));
            // the single row is the legacy one: no component, Kind Other, Method Percent, the line's rate, base = TaxableValue, amount = TaxAmount, name of the old tax
            var returned = lines.EndsWith("ReturnLines", StringComparison.Ordinal);
            var rate = returned
                ? lines == "SalesReturnLines" ? "(SELECT o.\"TaxRatePercent\" FROM \"SalesInvoiceLines\" o WHERE o.\"Id\" = l.\"SalesInvoiceLineId\")" : "(SELECT o.\"TaxRatePercent\" FROM \"PurchaseOrderLines\" o WHERE o.\"Id\" = l.\"PurchaseOrderLineId\")"
                : "l.\"TaxRatePercent\"";
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""
                SELECT count(*) FROM "{lines}" l JOIN "{taxRows}" x ON x."{idColumn}" = l."Id"
                WHERE x."TaxComponentId" IS NOT NULL OR x."ComponentCode" <> 'GST' OR x."Kind" <> 'Other' OR x."Method" <> 'Percent' OR x."AmountPerBaseUnit" IS NOT NULL
                   OR x."RatePercent" IS DISTINCT FROM {rate} OR x."BaseAmount" <> l."TaxableValue" OR x."TaxAmount" <> l."TaxAmount"
                   OR x."Id" <> md5('sellora:linetax:legacy:' || l."Id"::text)::uuid
                """));
            // drift: the C# id formula used by the write paths is the one in legacy-rows.sql
            var expectedIds = ids.Select(id => D(TaxSeedData.LegacyLineTaxId(Guid.Parse(id))));
            AssertSame(expectedIds, await RowsAsync(d, $"""SELECT "Id" FROM "{taxRows}" """), $"{taxRows} ids");
        }

        // TaxableValue, TaxAmount, LineTotal, header totals and every TaxId are exactly as they were; the legacy Taxes rows are untouched (the seeded mirror taxes are ADDED)
        var after = await UnchangedSnapshotAsync(d);
        Assert.Equal(before.Where(r => !r.StartsWith("TAX|", StringComparison.Ordinal)), after.Where(r => !r.StartsWith("TAX|", StringComparison.Ordinal)));
        Assert.Empty(before.Where(r => r.StartsWith("TAX|", StringComparison.Ordinal)).Except(after));
    }

    // ------------------------------------------------------------------------------------------------------------------------- AC17

    [Fact]
    public async Task Up_EachLegacyTax_BecomesOneProfileAndVersionFrom2000_WithTheComponentsOfItsCountry()
    {
        var d = await MigratedAsync(postgres);

        var expectedProfiles = new List<string>();
        var expectedVersions = new List<string>();
        var expectedComponents = new List<string>();
        foreach (var t in Taxes)
        {
            var regime = t.Country == "IN" ? "Gst" : "Vat";
            var treatment = t.Taxable ? "Taxable" : t.Country == "IN" ? "NilRated" : "ZeroRated";
            expectedProfiles.Add($"{TaxSeedData.LegacyProfileId(t.Id):D}|{t.Company:D}|{t.Country}|{TaxSeedData.LegacyCode(t.Id)}|{t.ProfileName}|{regime}|{treatment}|{t.Id:D}|{(t.Active ? "true" : "false")}");
            expectedVersions.Add($"{TaxSeedData.LegacyVersionId(t.Id):D}|{TaxSeedData.LegacyProfileId(t.Id):D}|2000-01-01|-|{Dec(t.Rate)}");
            if (!t.Taxable) continue;
            var version = TaxSeedData.LegacyVersionId(t.Id);
            if (t.Country == "IN")
            {
                foreach (var (code, app, rate, sort) in new[] { ("CGST", TaxApplicability.IntraState, t.Rate / 2, 1), ("SGST", TaxApplicability.IntraState, t.Rate / 2, 2), ("IGST", TaxApplicability.InterState, t.Rate, 3) })
                    expectedComponents.Add($"{TaxSeedData.VersionComponentId(version, code, app):D}|{version:D}|{TaxSeedData.ComponentId(t.Company, "IN", code):D}|Percent|{app}|{Dec(rate)}|-|{sort}");
            }
            else
            {
                expectedComponents.Add($"{TaxSeedData.VersionComponentId(version, "VAT", TaxApplicability.Always):D}|{version:D}|{TaxSeedData.ComponentId(t.Company, t.Country, "VAT"):D}|Percent|Always|{Dec(t.Rate)}|-|1");
            }
        }

        AssertSame(expectedProfiles, await RowsAsync(d, """
            SELECT p."Id", p."CompanyId", c."Code", p."Code", p."Name", p."Regime", p."Treatment", p."LegacyTaxId", p."IsActive"
            FROM "TaxProfiles" p JOIN "Countries" c ON c."Id" = p."CountryId" WHERE p."Code" LIKE 'LEGACY-%'
            """), "legacy profiles (one per Taxes row; Dup/DUP/dup get an id-derived suffix)");
        AssertSame(expectedVersions, await RowsAsync(d, """
            SELECT v."Id", v."TaxProfileId", v."EffectiveFrom", v."EffectiveTo", v."TotalPercent" FROM "TaxProfileVersions" v JOIN "TaxProfiles" p ON p."Id" = v."TaxProfileId" WHERE p."Code" LIKE 'LEGACY-%'
            """), "legacy versions (single open version from 2000-01-01, TotalPercent = the tax rate)");
        AssertSame(expectedComponents, await RowsAsync(d, """
            SELECT c."Id", c."TaxProfileVersionId", c."TaxComponentId", c."Method", c."Applicability", c."RatePercent", c."AmountPerBaseUnit", c."SortOrder"
            FROM "TaxProfileVersionComponents" c JOIN "TaxProfileVersions" v ON v."Id" = c."TaxProfileVersionId" JOIN "TaxProfiles" p ON p."Id" = v."TaxProfileId" WHERE p."Code" LIKE 'LEGACY-%'
            """), "legacy version components (IN: CGST/SGST halves IntraState + IGST InterState; others: VAT Always; 0% none)");

        // every legacy tax: the old Taxes row is untouched, and exactly one profile maps to it
        Assert.Equal(Taxes.Length, await ScalarAsync<long>(d, """SELECT count(*) FROM "Taxes" t WHERE EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."LegacyTaxId" = t."Id" AND p."Code" LIKE 'LEGACY-%')"""));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM (SELECT "LegacyTaxId" FROM "TaxProfiles" GROUP BY 1 HAVING count(*) > 1) x"""));
    }

    [Fact]
    public async Task Up_EveryProductGetsAProductLevelAssignmentToTheProfileOfItsTax_ForItsCompanysCountry()
    {
        var d = await MigratedAsync(postgres);
        var country = Countries.ToDictionary(c => c.Company, c => c.Country);

        var expected = WorldProducts.Select(p =>
            $"{TaxSeedData.ProductAssignmentId(p.Company, country[p.Company], p.Product):D}|{p.Company:D}|Product|{country[p.Company]}|{p.Product:D}|{TaxSeedData.LegacyProfileId(p.Tax):D}|-");
        var actual = await RowsAsync(d, """
            SELECT a."Id", a."CompanyId", a."Scope", c."Code", a."ProductId", a."SalesTaxProfileId", a."PurchaseTaxProfileId"
            FROM "TaxAssignments" a JOIN "Countries" c ON c."Id" = a."CountryId" WHERE a."Scope" = 'Product'
            """);

        AssertSame(expected, actual, "product assignments (purchase profile empty = same as sales)");
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "Products" pr WHERE NOT EXISTS (SELECT 1 FROM "TaxAssignments" a WHERE a."Scope" = 'Product' AND a."ProductId" = pr."Id")"""));
        // no category or subcategory assignment is invented
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "TaxAssignments" WHERE "Scope" IN ('Category', 'Subcategory')"""));
    }

    [Fact]
    public async Task Up_DuplicateTaxNamesDifferingOnlyByCase_GetIdDerivedSuffixes_AndKeepOneProfileEach()
    {
        var d = await MigratedAsync(postgres);

        var names = await RowsAsync(d, $"""SELECT p."Name" FROM "TaxProfiles" p WHERE p."CompanyId" = '{CoI}' AND p."Code" LIKE 'LEGACY-%' ORDER BY p."LegacyTaxId" """);

        Assert.Equal(["Dup", "DUP (legacy 00000132)", "dup (legacy 00000133)"], names);
        Assert.Equal(3L, await ScalarAsync<long>(d, $"""SELECT count(DISTINCT lower("Name")) FROM "TaxProfiles" WHERE "CompanyId" = '{CoI}' AND "Code" LIKE 'LEGACY-%'"""));
    }

    [Fact]
    public async Task Up_ATaxAlreadyNamedLikeTheSuffixedDuplicate_StillMigrates_WithDistinctProfileNames()
    {
        // "Dup", "DUP" and a real tax called "dup (2)": the suffixed name of the second collides with the third, which the unique name index must never see
        await using var d = await AtPreviousAsync(postgres);
        await ExecuteAsync(d, $"""
            INSERT INTO "Taxes" ("Id", "CompanyId", "Name", "RatePercent", "IsActive") VALUES ('{G(0x140)}', '{CoI}', 'dup (2)', 5, true)
            """);

        await MigrateExactlyAsync(d, ValidateFks);

        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "TaxProfiles" WHERE "LegacyTaxId" = '{G(0x140)}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM (SELECT lower("Name") FROM "TaxProfiles" WHERE "CompanyId" = '{CoI}' GROUP BY 1 HAVING count(*) > 1) x"""));
    }

    // ------------------------------------------------------------------------------------------------------------------------- T12 / T21 / T18

    private static async Task<List<string>> SeededCodesAsync(TestDatabase d, Guid company) =>
        (await QueryAsync(d, $"""SELECT "Code" FROM "TaxProfiles" WHERE "CompanyId" = '{company}' AND "Code" NOT LIKE 'LEGACY-%'""", r => r.GetString(0))).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static Task<string> DefaultProfileAsync(TestDatabase d, Guid company) =>
        ScalarAsync<string>(d, $"""SELECT a."SalesTaxProfileId"::text FROM "TaxAssignments" a WHERE a."CompanyId" = '{company}' AND a."Scope" = 'Company'""");

    [Fact]
    public async Task Seed_ExistingGstTaxWithAStandardName_SkipsThatSeed_AndTheCompanyDefaultIsTheExistingSameNameProfile()
    {
        var d = await MigratedAsync(postgres);

        // company A has a tax named "GST 18%" (the standard 18% name): GST-18 is skipped, the other four standard profiles are created
        Assert.Equal(["GST-40", "GST-5", "GST-EXEMPT", "GST-NIL"], await SeededCodesAsync(d, CoA));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "TaxProfiles" WHERE "Id" = '{TaxSeedData.ProfileId(CoA, "IN", "GST-18")}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "Taxes" WHERE "Id" = '{TaxSeedData.MirrorTaxId(CoA, "IN", "GST-18")}'"""));
        // T21: the default is the existing same-name profile, not a most-used fallback (the products mostly use "GST" 5%)
        Assert.Equal(D(TaxSeedData.LegacyProfileId(TaxA18)), await DefaultProfileAsync(d, CoA));
        // the legacy tax named "GST" (not a standard name) does not stop GST-5 "GST 5%"
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "TaxProfiles" WHERE "CompanyId" = '{CoA}' AND "Code" = 'GST-5'"""));
    }

    [Fact]
    public async Task Seed_CompanyWithoutStandardNames_GetsAllStandardProfiles_AndTheStandardRateIsTheDefault()
    {
        var d = await MigratedAsync(postgres);

        Assert.Equal(["VAT-5", "VAT-EXEMPT", "VAT-ZERO"], await SeededCodesAsync(d, CoB));
        Assert.Equal(D(TaxSeedData.ProfileId(CoB, "AE", "VAT-5")), await DefaultProfileAsync(d, CoB));   // legacy "Local VAT" 5% exists but the standard rate wins (T21)
        // the seeded profile has its mirror tax (D3): same id formula, rate of the version
        Assert.Equal(["VAT 5%|5|true"], await RowsAsync(d, $"""SELECT "Name", "RatePercent", "IsActive" FROM "Taxes" WHERE "Id" = '{TaxSeedData.MirrorTaxId(CoB, "AE", "VAT-5")}'"""));
        Assert.Equal(D(TaxSeedData.MirrorTaxId(CoB, "AE", "VAT-5")), await ScalarAsync<string>(d, $"""SELECT "LegacyTaxId"::text FROM "TaxProfiles" WHERE "Id" = '{TaxSeedData.ProfileId(CoB, "AE", "VAT-5")}'"""));
    }

    [Fact]
    public async Task Seed_NewIndiaCompanyWithoutTaxes_GetsTheFiveProfilesAndGst18AsDefault()
    {
        var d = await MigratedAsync(postgres);

        Assert.Equal(["GST-18", "GST-40", "GST-5", "GST-EXEMPT", "GST-NIL"], await SeededCodesAsync(d, CoD));
        Assert.Equal(D(TaxSeedData.ProfileId(CoD, "IN", "GST-18")), await DefaultProfileAsync(d, CoD));
        Assert.Equal(["CESS", "CESS-UNIT", "CGST", "IGST", "SGST", "UTGST"], (await QueryAsync(d, $"""SELECT "Code" FROM "TaxComponents" WHERE "CompanyId" = '{CoD}'""", r => r.GetString(0))).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "TaxProfiles" WHERE "CompanyId" = '{CoD}' AND "Code" LIKE 'LEGACY-%'"""));
    }

    [Fact]
    public async Task Seed_CountryWithoutStandardSeeds_CreatesOnlyAVatComponent_AndTheDefaultFallsBackToTheMostUsedActiveTaxableLegacyProfile()
    {
        var d = await MigratedAsync(postgres);

        Assert.Empty(await SeededCodesAsync(d, CoC));
        Assert.Equal(["VAT|Vat|Percent|Value added tax"], await RowsAsync(d, $"""SELECT "Code", "Kind", "Method", "Name" FROM "TaxComponents" WHERE "CompanyId" = '{CoC}'"""));
        // Q10 (2 products) beats Q3 (1); the inactive QX (3 products) and the zero-rate Q0 never qualify
        Assert.Equal(D(TaxSeedData.LegacyProfileId(TaxC10)), await DefaultProfileAsync(d, CoC));
    }

    [Fact]
    public async Task Seed_CompanyWithoutCountry_GetsNothing_AndTheGccCountriesGetTheirStandardRateAsDefault()
    {
        var d = await MigratedAsync(postgres);

        foreach (var table in new[] { "TaxComponents", "TaxProfiles", "TaxAssignments" })
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{table}" WHERE "CompanyId" = '{CoE}'"""));
        Assert.Equal(D(TaxSeedData.ProfileId(CoF, "SA", "VAT-15")), await DefaultProfileAsync(d, CoF));
        Assert.Equal(D(TaxSeedData.ProfileId(CoG, "BH", "VAT-10")), await DefaultProfileAsync(d, CoG));
        Assert.Equal(D(TaxSeedData.ProfileId(CoH, "OM", "VAT-5")), await DefaultProfileAsync(d, CoH));
        Assert.Equal(Countries.Length, await ScalarAsync<long>(d, """SELECT count(*) FROM "TaxAssignments" WHERE "Scope" = 'Company'"""));   // one default per company that has a country, none for E
    }

    [Fact]
    public async Task Up_ExistingCompanies_KeepPricesExclusiveOfTax_AndTheColumnHasNoDefaultAnymore()
    {
        var d = await MigratedAsync(postgres);

        // T18: every existing company stays false (stored prices keep their meaning)
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "BusinessSettings" WHERE "SellingPricesIncludeTax" IS DISTINCT FROM false"""));
        Assert.Equal(Countries.Length, await ScalarAsync<long>(d, """SELECT count(*) FROM "BusinessSettings" """));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "BusinessSettings" WHERE "MaxBillDiscountPercent" IS NOT NULL"""));
        // no column default is left: the model has none, so the next writer must state it
        Assert.Equal(["NO|-"], await RowsAsync(d, """
            SELECT is_nullable, column_default FROM information_schema.columns WHERE table_name = 'BusinessSettings' AND column_name = 'SellingPricesIncludeTax'
            """));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, $"""
            INSERT INTO "BusinessSettings" ("Id", "CompanyId", "BusinessName", "InvoicePrefix", "BaseCurrencyId")
            VALUES (gen_random_uuid(), '{CoE}', 'No default', 'INV', (SELECT "Id" FROM "Currencies" LIMIT 1))
            """));
        Assert.Equal("23502", ex.SqlState);
        Assert.Equal("SellingPricesIncludeTax", ex.ColumnName);
    }

    [Fact]
    public async Task Up_Permissions_AreGrantedToTheHoldersOfTheirPrerequisitePermissions_Only()
    {
        var d = await MigratedAsync(postgres);

        var granted = await RowsAsync(d, """
            SELECT r."Name", p."Code" FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" JOIN "Roles" r ON r."Id" = rp."RoleId"
            WHERE p."Code" IN ('Tax.View', 'Tax.Manage', 'Sales.BillDiscount') ORDER BY 1, 2
            """);

        // R1 Masters.View, R2 Inventory.Create -> Tax.View; R3 Masters.Edit -> Tax.Manage; R4 Sales.PriceOverride -> Sales.BillDiscount; R5 (Sales.View only) -> nothing
        Assert.Equal(["TaxR1|Tax.View", "TaxR2|Tax.View", "TaxR3|Tax.Manage", "TaxR4|Sales.BillDiscount"], granted);
        Assert.Equal(3L, await ScalarAsync<long>(d, """SELECT count(*) FROM "Permissions" WHERE "Code" IN ('Tax.View', 'Tax.Manage', 'Sales.BillDiscount')"""));
    }

    // ------------------------------------------------------------------------------------------------------------------------- preflight

    [Fact]
    public async Task Up_CompanyWithTaxesAndNoCountry_FailsWithTheCompanyIds_LeavesNothingBehind_AndSucceedsOnceFixed()
    {
        await using var d = await AtPreviousAsync(postgres);
        // company B: country removed; company C: no business settings at all; company A and the rest stay valid
        await ExecuteAsync(d, $"""UPDATE "BusinessSettings" SET "CountryId" = NULL WHERE "CompanyId" = '{CoB}'""");
        await ExecuteAsync(d, $"""DELETE FROM "BusinessSettings" WHERE "CompanyId" = '{CoC}'""");
        var taxesBefore = await ScalarAsync<long>(d, """SELECT count(*) FROM "Taxes" """);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, TaxM1));

        var pg = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("P0001", pg.SqlState);
        Assert.Contains("preflight failed", pg.MessageText);
        Assert.Contains(D(CoB), pg.MessageText);
        Assert.Contains(D(CoC), pg.MessageText);
        Assert.DoesNotContain(D(CoA), pg.MessageText);
        Assert.DoesNotContain(D(CoI), pg.MessageText);
        foreach (var table in TaxTables) Assert.False(await TableExistsAsync(d, table), table + " must not exist after the failed release");
        Assert.DoesNotContain(TaxM1, await AppliedAsync(d));
        Assert.Equal(taxesBefore, await ScalarAsync<long>(d, """SELECT count(*) FROM "Taxes" """));   // the failed release seeded no mirror tax

        await ExecuteAsync(d, $"""UPDATE "BusinessSettings" SET "CountryId" = (SELECT "Id" FROM "Countries" WHERE "Code" = 'AE') WHERE "CompanyId" = '{CoB}'""");
        await ExecuteAsync(d, $"""
            INSERT INTO "BusinessSettings" ("Id", "CompanyId", "BusinessName", "InvoicePrefix", "BaseCurrencyId", "CountryId")
            VALUES (gen_random_uuid(), '{CoC}', 'Tax QA', 'INV', (SELECT "Id" FROM "Currencies" WHERE "Code" = 'INR'), (SELECT "Id" FROM "Countries" WHERE "Code" = 'QA'))
            """);
        await MigrateExactlyAsync(d, TaxM1);
        Assert.Contains(TaxM1, await AppliedAsync(d));
    }

    [Fact]
    public async Task Up_DefaultCompanyWithTaxesButNoBusinessSettings_Fails_NamingTheDefaultCompany()
    {
        // the plain legacy fixture: the default company has the tax "GST" and no business settings row
        await using var d = await CreateAtAsync(postgres, Previous);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, TaxM1));

        var pg = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("P0001", pg.SqlState);
        Assert.Contains(DefaultCompany, pg.MessageText);
        Assert.False(await TableExistsAsync(d, "TaxProfiles"));
    }

    // ------------------------------------------------------------------------------------------------------------------------- idempotency

    [Fact]
    public async Task Resources_RunAgain_AreANoOp()
    {
        await using var d = await MigratedCopyAsync(postgres);
        var before = await DataFingerprintAsync(d);
        var profilesBefore = await ScalarAsync<long>(d, """SELECT count(*) FROM "TaxProfiles" """);

        await RunResourceInTransactionAsync(d, LegacyRowsResource);
        await RunResourceInTransactionAsync(d, SeedResource);
        await RunResourceInTransactionAsync(d, BackfillResource);
        await RunResourceInTransactionAsync(d, SeedResource, BackfillResource, LegacyRowsResource);   // the migration's own order

        Assert.Equal(before, await DataFingerprintAsync(d));
        Assert.Equal(profilesBefore, await ScalarAsync<long>(d, """SELECT count(*) FROM "TaxProfiles" """));
    }

    [Fact]
    public async Task LegacyRows_ANewlySavedLine_IsCaughtUpByExactlyOneRow_AndAnotherRunAddsNothing()
    {
        var d = await MigratedAsync(postgres);

        var (afterFirst, afterSecond, rowId) = await InRolledBackTransactionAsync(d, async p =>
        {
            // a line written by the previous build between the TaxM1 commit and the app swap (copy of an existing line, new id)
            await p.ExecAsync($"""
                CREATE TEMP TABLE _copy ON COMMIT DROP AS SELECT * FROM "SalesInvoiceLines" WHERE "Id" = '{Fx(90)}';
                UPDATE _copy SET "Id" = '{G(0x900)}';
                INSERT INTO "SalesInvoiceLines" SELECT * FROM _copy;
                """);
            Assert.Equal(["0"], await p.RowsAsync($"""SELECT count(*) FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{G(0x900)}'"""));
            await p.RunResourceAsync(LegacyRowsResource);
            var first = await p.ScalarAsync<long>($"""SELECT count(*) FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{G(0x900)}'""");
            await p.RunResourceAsync(LegacyRowsResource);
            var second = await p.ScalarAsync<long>($"""SELECT count(*) FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{G(0x900)}'""");
            var id = await p.ScalarAsync<string>($"""SELECT "Id"::text FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{G(0x900)}'""");
            return (first, second, id);
        });

        Assert.Equal(1L, afterFirst);
        Assert.Equal(1L, afterSecond);
        Assert.Equal(D(TaxSeedData.LegacyLineTaxId(G(0x900))), rowId);
    }

    [Fact]
    public async Task LegacyRows_ALineThatAlreadyHasATaxRow_IsLeftAlone()
    {
        var d = await MigratedAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            // the app (TP-3+) already wrote a component row for this line: the catch-up must not add the legacy one beside it
            await p.ExecAsync($"""DELETE FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{Fx(90)}'""");
            await p.ExecAsync($"""
                INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
                VALUES (gen_random_uuid(), '{Fx(90)}', NULL, 'CGST', 'Cgst', 'Percent', 2.5, NULL, 50, 1.25)
                """);
            await p.RunResourceAsync(LegacyRowsResource);
            return await p.RowsAsync($"""SELECT "ComponentCode" FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{Fx(90)}'""");
        });

        Assert.Equal(["CGST"], rows);
    }

    [Fact]
    public async Task Seed_ExistingProfileWithTheSameCodeOrName_IsSkipped_NotDuplicated()
    {
        var d = await MigratedAsync(postgres);
        var sa = TaxSeedData.ProfileId(CoF, "SA", "VAT-15");
        var mirror = TaxSeedData.MirrorTaxId(CoF, "SA", "VAT-15");

        var (afterCode, afterName, afterProfileNameOnly) = await InRolledBackTransactionAsync(d, async p =>
        {
            async Task<long> CountAsync() => await p.ScalarAsync<long>($"""SELECT count(*) FROM "TaxProfiles" WHERE "CompanyId" = '{CoF}'""");
            // by code: the company renamed the profile and its mirror, the code VAT-15 stays
            await p.ExecAsync($"""UPDATE "TaxProfiles" SET "Name" = 'Custom 15' WHERE "Id" = '{sa}'; UPDATE "Taxes" SET "Name" = 'Custom 15' WHERE "Id" = '{mirror}';""");
            await p.RunResourceAsync(SeedResource);
            var byCode = await CountAsync();
            // by name: the code was changed, the name VAT 15% is back
            await p.ExecAsync($"""UPDATE "TaxProfiles" SET "Name" = 'VAT 15%', "Code" = 'MYVAT15' WHERE "Id" = '{sa}'; UPDATE "Taxes" SET "Name" = 'VAT 15%' WHERE "Id" = '{mirror}';""");
            await p.RunResourceAsync(SeedResource);
            var byName = await CountAsync();
            // by the PROFILE name alone (the mirror tax renamed, so only the profile can make the seed skip)
            await p.ExecAsync($"""UPDATE "Taxes" SET "Name" = 'Renamed mirror' WHERE "Id" = '{mirror}'""");
            await p.RunResourceAsync(SeedResource);
            return (byCode, byName, await CountAsync());
        });

        var expected = await ScalarAsync<long>(d, $"""SELECT count(*) FROM "TaxProfiles" WHERE "CompanyId" = '{CoF}'""");
        Assert.Equal(expected, afterCode);
        Assert.Equal(expected, afterName);
        Assert.Equal(expected, afterProfileNameOnly);
    }

    // ------------------------------------------------------------------------------------------------------------------------- drift vs TaxSeedData

    [Fact]
    public async Task Seed_SqlResult_EqualsTaxSeedData_ForInAeSaBhOm_AndTheQatarFallback()
    {
        var d = await MigratedAsync(postgres);
        // (company, country, standard profile codes the world's legacy taxes make the seed skip)
        var seeded = new (Guid Company, string Country, string[] Skipped)[]
        {
            (CoA, "IN", ["GST-18"]), (CoB, "AE", []), (CoD, "IN", []), (CoF, "SA", []), (CoG, "BH", []), (CoH, "OM", []), (CoI, "IN", []),
        };
        var expComponents = new List<string>(); var expProfiles = new List<string>(); var expVersions = new List<string>(); var expVc = new List<string>();
        var expMirrors = new List<string>(); var expDefaults = new List<string>();
        foreach (var (company, cc, skipped) in seeded)
        {
            foreach (var c in TaxSeedData.ComponentsFor(cc))
                expComponents.Add($"{TaxSeedData.ComponentId(company, cc, c.Code):D}|{company:D}|{cc}|{c.Code}|{c.Name}|{c.Kind}|{c.Method}|true");
            foreach (var pr in TaxSeedData.ProfilesFor(cc).Where(x => !skipped.Contains(x.Code)))
            {
                var profile = TaxSeedData.ProfileId(company, cc, pr.Code);
                var version = TaxSeedData.VersionId(company, cc, pr.Code, pr.EffectiveFrom);
                expProfiles.Add($"{profile:D}|{company:D}|{cc}|{pr.Code}|{pr.Name}|{pr.Regime}|{pr.Treatment}|{TaxSeedData.MirrorTaxId(company, cc, pr.Code):D}|true");
                expVersions.Add($"{version:D}|{profile:D}|{Day(pr.EffectiveFrom)}|-|{Dec(pr.TotalPercent)}");
                expMirrors.Add($"{TaxSeedData.MirrorTaxId(company, cc, pr.Code):D}|{company:D}|{pr.Name}|{Dec(pr.TotalPercent)}|true");
                foreach (var vc in pr.Components)
                    expVc.Add($"{TaxSeedData.VersionComponentId(version, vc.ComponentCode, vc.Applicability):D}|{version:D}|{TaxSeedData.ComponentId(company, cc, vc.ComponentCode):D}|" +
                              $"{TaxSeedData.ComponentsFor(cc).Single(c => c.Code == vc.ComponentCode).Method}|{vc.Applicability}|{Dec(vc.RatePercent)}|{Dec(vc.AmountPerBaseUnit)}|{vc.SortOrder}");
                if (pr.IsCompanyDefault && company != CoA)   // company A's default is its existing "GST 18%" profile (T21)
                    expDefaults.Add($"{TaxSeedData.CompanyDefaultAssignmentId(company, cc):D}|{company:D}|Company|{cc}|{profile:D}|-");
            }
        }
        // Qatar has legacy taxes and no standard seed: exactly one generic VAT component, no profile of the seed kind, the default is the fallback legacy profile
        var qa = TaxSeedData.GenericVatComponent("QA");
        expComponents.Add($"{TaxSeedData.ComponentId(CoC, "QA", qa.Code):D}|{CoC:D}|QA|{qa.Code}|{qa.Name}|{qa.Kind}|{qa.Method}|true");
        expDefaults.Add($"{TaxSeedData.CompanyDefaultAssignmentId(CoC, "QA"):D}|{CoC:D}|Company|QA|{TaxSeedData.LegacyProfileId(TaxC10):D}|-");
        expDefaults.Add($"{TaxSeedData.CompanyDefaultAssignmentId(CoA, "IN"):D}|{CoA:D}|Company|IN|{TaxSeedData.LegacyProfileId(TaxA18):D}|-");

        AssertSame(expComponents, await RowsAsync(d, """
            SELECT k."Id", k."CompanyId", c."Code", k."Code", k."Name", k."Kind", k."Method", k."IsActive" FROM "TaxComponents" k JOIN "Countries" c ON c."Id" = k."CountryId"
            """), "components");
        AssertSame(expProfiles, await RowsAsync(d, """
            SELECT p."Id", p."CompanyId", c."Code", p."Code", p."Name", p."Regime", p."Treatment", p."LegacyTaxId", p."IsActive"
            FROM "TaxProfiles" p JOIN "Countries" c ON c."Id" = p."CountryId" WHERE p."Code" NOT LIKE 'LEGACY-%'
            """), "seeded profiles");
        AssertSame(expVersions, await RowsAsync(d, """
            SELECT v."Id", v."TaxProfileId", v."EffectiveFrom", v."EffectiveTo", v."TotalPercent" FROM "TaxProfileVersions" v JOIN "TaxProfiles" p ON p."Id" = v."TaxProfileId" WHERE p."Code" NOT LIKE 'LEGACY-%'
            """), "seeded versions");
        AssertSame(expVc, await RowsAsync(d, """
            SELECT c."Id", c."TaxProfileVersionId", c."TaxComponentId", c."Method", c."Applicability", c."RatePercent", c."AmountPerBaseUnit", c."SortOrder"
            FROM "TaxProfileVersionComponents" c JOIN "TaxProfileVersions" v ON v."Id" = c."TaxProfileVersionId" JOIN "TaxProfiles" p ON p."Id" = v."TaxProfileId" WHERE p."Code" NOT LIKE 'LEGACY-%'
            """), "seeded version components");
        AssertSame(expMirrors, await RowsAsync(d, """
            SELECT t."Id", t."CompanyId", t."Name", t."RatePercent", t."IsActive" FROM "Taxes" t JOIN "TaxProfiles" p ON p."LegacyTaxId" = t."Id" WHERE p."Code" NOT LIKE 'LEGACY-%'
            """), "mirror taxes of the seeded profiles");
        AssertSame(expDefaults, await RowsAsync(d, """
            SELECT a."Id", a."CompanyId", a."Scope", c."Code", a."SalesTaxProfileId", a."PurchaseTaxProfileId" FROM "TaxAssignments" a JOIN "Countries" c ON c."Id" = a."CountryId" WHERE a."Scope" = 'Company'
            """), "company default assignments");
    }

    [Fact]
    public async Task TaxSeedData_StandardRates_MatchTheDecisionsT12()
    {
        // pure data check next to the SQL drift check: the rates and the single default per country of the seed table
        Assert.Equal(["IN", "AE", "SA", "BH", "OM"], TaxSeedData.SeededCountryCodes);
        Assert.Equal(["GST-18", "VAT-5", "VAT-15", "VAT-10", "VAT-5"], TaxSeedData.SeededCountryCodes.Select(c => TaxSeedData.DefaultFor(c)!.Code));
        Assert.Equal([0m, 0m, 5m, 18m, 40m], TaxSeedData.ProfilesFor("IN").Select(p => p.TotalPercent));
        Assert.Equal(["GST-NIL", "GST-EXEMPT", "GST-5", "GST-18", "GST-40"], TaxSeedData.ProfilesFor("IN").Select(p => p.Code));
        Assert.Equal(["VAT-5", "VAT-ZERO", "VAT-EXEMPT"], TaxSeedData.ProfilesFor("AE").Select(p => p.Code));
        Assert.Empty(TaxSeedData.ProfilesFor("QA"));
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------------------------------- script path, NOT VALID, Down/Up, lock timeout

    [Fact]
    public async Task Up_IdempotentScriptPath_SurvivesTheEfWrapper_AndGivesTheSameRowsAsTheEfPath()
    {
        await using var viaScript = await AtPreviousAsync(postgres);
        await using var viaEf = await AtPreviousAsync(postgres);
        var script = await ScriptAsync(viaScript, Previous, ValidateFks);

        await ExecuteAsync(viaScript, script);   // the EF idempotent script as an operator runs it (DO $EF$ wrapper around the p-tagged resources)
        await MigrateExactlyAsync(viaEf, ValidateFks);

        Assert.Contains(TaxM1, await AppliedAsync(viaScript));
        Assert.Equal(await DataFingerprintAsync(viaEf), await DataFingerprintAsync(viaScript));
        Assert.Equal(await SchemaSignatureAsync(viaEf), await SchemaSignatureAsync(viaScript));
        await ExecuteAsync(viaScript, script);   // a second run is a no-op
        Assert.Equal(await DataFingerprintAsync(viaEf), await DataFingerprintAsync(viaScript));
    }

    private static readonly string[] LineFks =
    [
        "SalesInvoiceLineTaxes|FK_SalesInvoiceLineTaxes_SalesInvoiceLines", "PurchaseOrderLineTaxes|FK_PurchaseOrderLineTaxes_PurchaseOrderLines",
        "SalesReturnLineTaxes|FK_SalesReturnLineTaxes_SalesReturnLines", "PurchaseReturnLineTaxes|FK_PurchaseReturnLineTaxes_PurchaseReturnLines",
    ];

    private static Task<List<string>> LineFkStateAsync(TestDatabase d) => RowsAsync(d, """
        SELECT t.relname || '|' || c.conname || '|' || c.convalidated::text || '|' || c.confdeltype::text FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
        WHERE c.conname IN ('FK_SalesInvoiceLineTaxes_SalesInvoiceLines', 'FK_PurchaseOrderLineTaxes_PurchaseOrderLines', 'FK_SalesReturnLineTaxes_SalesReturnLines', 'FK_PurchaseReturnLineTaxes_PurchaseReturnLines')
        ORDER BY 1
        """);

    [Fact]
    public async Task LineForeignKeys_AreAddedNotValid_StillEnforcedForNewRows_AndValidatedBySecondMigration()
    {
        await using var d = await AtPreviousAsync(postgres);

        await MigrateExactlyAsync(d, TaxM1);

        Assert.Equal(LineFks.Select(f => $"{f.Split('|')[0]}|{f.Split('|')[1]}|false|c").OrderBy(x => x, StringComparer.Ordinal), await LineFkStateAsync(d));   // NOT VALID, ON DELETE CASCADE
        // not validated does not mean not enforced: a new row for a missing line is refused
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, $"""
            INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "ComponentCode", "Kind", "Method", "RatePercent", "BaseAmount", "TaxAmount")
            VALUES (gen_random_uuid(), gen_random_uuid(), 'X', 'Other', 'Percent', 1, 1, 1)
            """));
        Assert.Equal("23503", ex.SqlState);
        Assert.Equal("FK_SalesInvoiceLineTaxes_SalesInvoiceLines", ex.ConstraintName);

        await MigrateExactlyAsync(d, ValidateFks);

        Assert.Equal(LineFks.Select(f => $"{f.Split('|')[0]}|{f.Split('|')[1]}|true|c").OrderBy(x => x, StringComparer.Ordinal), await LineFkStateAsync(d));
    }

    [Fact]
    public async Task LineForeignKeys_OnTheMigratedWorld_CascadeWhenALineIsRemoved()
    {
        var d = await MigratedAsync(postgres);

        var remaining = await InRolledBackTransactionAsync(d, async p =>
        {
            // a line without return rows or batches of its own: remove the dependants of the fixture's cancelled invoice line, then the line
            await p.ExecAsync($"""
                DELETE FROM "SalesInvoiceLineBatchConsumptions" WHERE "SalesInvoiceLineId" = '{Fx(92)}';
                DELETE FROM "SalesInvoiceLines" WHERE "Id" = '{Fx(92)}';
                """);
            return await p.ScalarAsync<long>($"""SELECT count(*) FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{Fx(92)}'""");
        });

        Assert.Equal(0L, remaining);
    }

    [Fact]
    public async Task DownThenUp_RoundTrip_RestoresThePreviousStateAndComesBackIdentical()
    {
        await using var d = await AtPreviousAsync(postgres);
        var previousSchema = await SchemaSignatureAsync(d);
        var previousData = await DataFingerprintAsync(d, includeTaxTables: false);
        var previousSnapshot = await UnchangedSnapshotAsync(d);

        await MigrateExactlyAsync(d, ValidateFks);
        var headSchema = await SchemaSignatureAsync(d);
        var headData = await DataFingerprintAsync(d);
        Assert.NotEqual(previousSchema, headSchema);

        await MigrateExactlyAsync(d, Previous);   // Down: ValidateTaxLineForeignKeys (no-op) then TaxM1

        foreach (var table in TaxTables) Assert.False(await TableExistsAsync(d, table), table);
        Assert.DoesNotContain(TaxM1, await AppliedAsync(d));
        Assert.Equal(previousSchema, await SchemaSignatureAsync(d));
        Assert.Equal(previousSnapshot, await UnchangedSnapshotAsync(d));   // legacy Taxes (the seeded mirrors are removed), products and lines are untouched
        Assert.Equal(previousData, await DataFingerprintAsync(d, includeTaxTables: false));
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM "Permissions" WHERE "Code" IN ('Tax.View', 'Tax.Manage', 'Sales.BillDiscount')"""));

        await MigrateExactlyAsync(d, ValidateFks);   // Up again: seeded again (the mirrors were removed, so they are not mistaken for legacy taxes)

        Assert.Equal(headSchema, await SchemaSignatureAsync(d));
        Assert.Equal(headData, await DataFingerprintAsync(d));
    }

    [Theory]
    [InlineData("Taxes")]
    [InlineData("Products")]
    [InlineData("SalesInvoiceLines")]
    [InlineData("Companies")]
    public async Task Up_EfPath_WhenATouchedTableIsLocked_FailsFastWith55P03_LeavesNoPartialState_AndRetrySucceeds(string lockedTable)
    {
        await using var d = await AtPreviousAsync(postgres);
        await using var blocker = new NpgsqlConnection(d.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{lockedTable}\" IN ACCESS EXCLUSIVE MODE", blocker, tx)) await hold.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, TaxM1));
        clock.Stop();

        var pg = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("55P03", pg.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s instead of failing after about 5s");
        await tx.RollbackAsync();
        foreach (var table in TaxTables) Assert.False(await TableExistsAsync(d, table), table);
        Assert.Equal(0L, await ScalarAsync<long>(d, """
            SELECT count(*) FROM information_schema.columns
            WHERE (table_name = 'Products' AND column_name IN ('HsnCode', 'PriceIncludesTax')) OR (table_name = 'BusinessSettings' AND column_name IN ('SellingPricesIncludeTax', 'MaxBillDiscountPercent'))
            """));
        Assert.DoesNotContain(TaxM1, await AppliedAsync(d));

        await MigrateExactlyAsync(d, ValidateFks);   // after the blocker released, a retry succeeds
        Assert.Contains(TaxM1, await AppliedAsync(d));
        Assert.Contains(ValidateFks, await AppliedAsync(d));
    }

    // ------------------------------------------------------------------------------------------------------------------------- release notes

    [Fact]
    public async Task ReleaseNotesScript_RunsOnTheMigratedWorld_AndReportsTheSkippedSeedAndTheDefaults()
    {
        var d = await MigratedAsync(postgres);
        var text = ReleaseNotesScript();

        var report = await InRolledBackTransactionAsync(d, async p =>
        {
            var rows = new List<string>();
            foreach (var statement in SplitStatements(text)) rows.AddRange((await p.RowsAsync(statement)).Select(r => r));
            return rows;
        });

        AssertHas(report, r => r.StartsWith($"R2|{CoA}|IN|GST-18|GST 18%|GST 18%", StringComparison.Ordinal));   // the skipped seed and its existing match
        AssertHas(report, r => r.StartsWith($"R3|{CoC}|QA", StringComparison.Ordinal) && r.EndsWith("|fallback", StringComparison.Ordinal));
        AssertHas(report, r => r.StartsWith($"R3|{CoA}|IN|", StringComparison.Ordinal) && r.EndsWith("|existing match", StringComparison.Ordinal));
        AssertHas(report, r => r.StartsWith($"R3|{CoB}|AE|VAT-5", StringComparison.Ordinal) && r.EndsWith("|seeded", StringComparison.Ordinal));
        AssertHas(report, r => r.StartsWith($"R1|{CoA}|IN|Zero GST|NilRated", StringComparison.Ordinal));
        AssertHas(report, r => r.StartsWith($"R1|{CoC}|QA|Q0|ZeroRated", StringComparison.Ordinal));
        AssertHas(report, r => r.StartsWith($"R4|{CoE}|", StringComparison.Ordinal));
        // R5 rows 10-13 read lines * 1000000 + tax rows: every line has exactly one row
        var pairs = report.Where(r => r.StartsWith("R5|", StringComparison.Ordinal) && r.Contains("lines", StringComparison.OrdinalIgnoreCase) || r.StartsWith("R5|", StringComparison.Ordinal) && r.Contains("tax rows", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, pairs.Count);
        Assert.All(pairs, r => { var n = long.Parse(r[(r.LastIndexOf('|') + 1)..]); Assert.True(n / 1_000_000 > 0 && n / 1_000_000 == n % 1_000_000, r); });
        AssertHas(report, r => r.StartsWith($"R6|{CoI}|DUP|DUP (legacy 00000132)", StringComparison.Ordinal));   // the renamed duplicate-name profiles are listed
    }

    private static void AssertHas(List<string> report, Func<string, bool> predicate, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(predicate))] string? what = null) =>
        Assert.True(report.Any(predicate), $"no report row matches {what}. Report:\n" + string.Join("\n", report));

    private static string ReleaseNotesScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "sql", "tax-profiles-release-notes.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("scripts/sql/tax-profiles-release-notes.sql not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Statements of a read-only report script: comment lines dropped, split after each line ending in ';'.</summary>
    private static List<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            if (line.TrimStart().StartsWith("--", StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(line) && current.Count == 0) continue;
            current.Add(line);
            if (!line.TrimEnd().EndsWith(';')) continue;
            statements.Add(string.Join('\n', current));
            current.Clear();
        }
        Assert.Empty(current);
        return statements;
    }
}
