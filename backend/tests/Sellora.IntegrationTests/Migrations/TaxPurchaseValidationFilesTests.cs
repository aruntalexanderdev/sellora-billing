using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// The validation SQL of TP-3 (docs/features/tax-profiles.md, "TP-3 design", Validation SQL): <c>V__tax-purchase-snapshot.sql</c> (every check label), <c>V__tax-line-rows-missing-purchases.sql</c> and the two additions to
/// <c>V__tax-line-rows-consistency.sql</c> (the original-amount sum and the legacy-versus-non-taxable-calculator-row distinction). Each file returns zero rows on the migrated legacy world and on hand-built valid TP-3
/// documents (intra-state, inter-state, non-taxable, foreign currency); then one probe per check label writes a violation inside a transaction that is always rolled back and expects exactly the rows it causes.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TaxPurchaseValidationFilesTests(PostgresFixture postgres)
{
    private const string Snapshot = "tax-purchase-snapshot.sql", MissingPurchases = "tax-line-rows-missing-purchases.sql", Consistency = "tax-line-rows-consistency.sql";

    private static readonly Doc Intra = DocAt(0xA00), Inter = DocAt(0xA10), Nil = DocAt(0xA20), Usd = DocAt(0xA30);

    private static async Task<List<string>> AllFilesAsync(Probe p)
    {
        var rows = new List<string>();
        foreach (var file in ValidationFiles) rows.AddRange((await p.ViolationsAsync(file)).Select(r => file + ": " + r));
        return rows;
    }

    private static async Task BuildIntraAsync(Probe p) => await p.ExecAsync(ValidDocumentSql(Intra, "PUR-TP3A", "PRT-TP3A"));

    /// <summary>Intra-state document turned inter-state: IGST 25 (return 5) instead of CGST + SGST.</summary>
    private static async Task BuildInterAsync(Probe p)
    {
        await p.ExecAsync(ValidDocumentSql(Inter, "PUR-TP3B", "PRT-TP3B"));
        await p.ExecAsync($"""
            DELETE FROM "PurchaseOrderLineTaxes" WHERE "Id" IN ('{Inter.Cgst}', '{Inter.Sgst}');
            DELETE FROM "PurchaseReturnLineTaxes" WHERE "Id" IN ('{Inter.ReturnCgst}', '{Inter.ReturnSgst}');
            UPDATE "PurchaseOrders" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = '{Inter.Po}';
            UPDATE "PurchaseOrderLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = '{Inter.Line}';
            UPDATE "PurchaseReturnLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = '{Inter.ReturnLine}';
            {Row("PurchaseOrderLineTaxes", "PurchaseOrderLineId", Inter.Cgst, Inter.Line, "IGST", "Igst", 5m, 500m, 25m)}
            {Row("PurchaseReturnLineTaxes", "PurchaseReturnLineId", Inter.ReturnCgst, Inter.ReturnLine, "IGST", "Igst", 5m, 100m, 5m)}
            """);
    }

    /// <summary>A zero-rated profile: one component-less calculator row with a Treatment and a base that is NOT the line's taxable value (so only the Treatment keeps it from being a legacy row).</summary>
    private static async Task BuildNilAsync(Probe p)
    {
        await p.ExecAsync($"""
            {Clone("PurchaseOrders", $"\"Id\" = '{FxPo}'", ("Id", Lit(Nil.Po)), ("PurchaseNo", "'PUR-TP3N'"), ("Subtotal", "500"), ("TaxTotal", "0"), ("GrandTotal", "500"), ("OriginalGrandTotal", "500"),
                ("TaxableTotal", "500"), ("PlaceOfSupply", "'IntraState'"))}
            {Clone("PurchaseOrderLines", $"\"Id\" = '{FxPoLine}'", ("Id", Lit(Nil.Line)), ("PurchaseOrderId", Lit(Nil.Po)), ("TaxId", Lit(TaxA0)), ("TaxRatePercent", "0"), ("TaxAmount", "0"), ("LineTotal", "500"),
                ("TaxProfileId", Lit(ProfileNil)), ("TaxProfileVersionId", Lit(VersionNil)), ("PlaceOfSupply", "'IntraState'"), ("OriginalRate", "100"), ("OriginalDiscount", "0"), ("OriginalOtherCharges", "0"),
                ("OriginalBillDiscountShare", "0"), ("OriginalBillChargeShare", "0"), ("OriginalTaxableValue", "500"), ("OriginalTaxAmount", "0"), ("OriginalLineTotal", "500"))}
            INSERT INTO "PurchaseOrderLineTaxes" ("Id", "PurchaseOrderLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount", "OriginalBaseAmount", "OriginalTaxAmount", "Treatment")
            VALUES ('{Nil.Cgst}', '{Nil.Line}', NULL, 'NIL', 'Other', 'Percent', 0, NULL, 999, 0, 999, 0, 'NilRated');
            """);
    }

    /// <summary>The intra-state document in US dollars at 83.5: header and line originals in dollars, base values unchanged.</summary>
    private static async Task BuildUsdAsync(Probe p)
    {
        await p.ExecAsync($"""
            INSERT INTO "Currencies" ("Id", "Code", "Name", "Symbol", "DecimalPrecision", "IsActive") VALUES ('{UsdId}', 'USD', 'US Dollar', '$', 2, true);
            """);
        await p.ExecAsync(ValidDocumentSql(Usd, "PUR-TP3U", "PRT-TP3U"));
        await p.ExecAsync($"""
            UPDATE "PurchaseOrders" SET "CurrencyId" = '{UsdId}', "ExchangeRateToBase" = 83.5, "OriginalGrandTotal" = 6.2874 WHERE "Id" = '{Usd.Po}';
            UPDATE "PurchaseOrderLines" SET "OriginalRate" = 1.1976, "OriginalTaxableValue" = 5.9880, "OriginalTaxAmount" = 0.2994, "OriginalLineTotal" = 6.2874 WHERE "Id" = '{Usd.Line}';
            UPDATE "PurchaseOrderLineTaxes" SET "OriginalBaseAmount" = 5.9880, "OriginalTaxAmount" = 0.1497 WHERE "PurchaseOrderLineId" = '{Usd.Line}';
            UPDATE "PurchaseReturnLines" SET "OriginalTaxableValue" = 1.1976, "OriginalTaxAmount" = 0.0600 WHERE "Id" = '{Usd.ReturnLine}';
            UPDATE "PurchaseReturnLineTaxes" SET "OriginalBaseAmount" = 1.1976, "OriginalTaxAmount" = 0.0300 WHERE "PurchaseReturnLineId" = '{Usd.ReturnLine}';
            """);
    }

    private static string R(string label, string table, Guid id) => $"{label}|{table}|{id}";

    /// <summary>Builds the intra-state document, proves every V__ file is clean, applies <paramref name="mutate"/>, and returns the rows of <paramref name="file"/> (sorted).</summary>
    private async Task<List<string>> ProbeAsync(string file, Func<Probe, Doc, Task> mutate)
    {
        var d = await MigratedAsync(postgres);
        var (before, after) = await InRolledBackTransactionAsync(d, async p =>
        {
            await BuildIntraAsync(p);
            var clean = await AllFilesAsync(p);
            await mutate(p, Intra);
            return (clean, (await p.ViolationsAsync(file)).Order(StringComparer.Ordinal).ToList());
        });
        Assert.True(before.Count == 0, "the hand-built valid TP-3 document is not clean: " + string.Join(" | ", before.Take(5)));
        return after;
    }

    private async Task AssertProbeAsync(string file, Func<Probe, Doc, Task> mutate, Func<Doc, string[]> expected)
    {
        var rows = await ProbeAsync(file, mutate);
        Assert.Equal(expected(Intra).Order(StringComparer.Ordinal).ToList(), rows);
    }

    private static Task Exec(Probe p, string sql) => p.ExecAsync(sql);

    // ------------------------------------------------------------------------------------------------------------ clean

    [Theory]
    [InlineData("intra")]
    [InlineData("inter")]
    [InlineData("non-taxable")]
    [InlineData("foreign-currency")]
    public async Task AllFiles_OnAHandBuiltValidTp3Document_ReturnZeroRows(string kind)
    {
        var d = await MigratedAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            switch (kind)
            {
                case "intra": await BuildIntraAsync(p); break;
                case "inter": await BuildInterAsync(p); break;
                case "non-taxable": await BuildNilAsync(p); break;
                default: await BuildUsdAsync(p); break;
            }
            Assert.Equal(1L, await p.ScalarAsync<long>("""SELECT count(*) FROM "PurchaseOrders" WHERE "TaxableTotal" IS NOT NULL"""));   // the document exists and is a TP-3 one
            return await AllFilesAsync(p);
        });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task AllFiles_OnTheMigratedWorldWithEveryKindOfTp3DocumentAtOnce_StayZero_AndTheLegacyHistoryIsNeverChecked()
    {
        var d = await MigratedAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            await BuildIntraAsync(p);
            await BuildInterAsync(p);
            await BuildNilAsync(p);
            await BuildUsdAsync(p);
            return await AllFilesAsync(p);
        });

        Assert.Empty(rows);
    }

    // ------------------------------------------------------------------------------------------------------------ V__tax-purchase-snapshot: version-not-of-profile-or-company

    [Fact]
    public async Task Snapshot_PurchaseLineWhoseVersionBelongsToAnotherProfile_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLines" SET "TaxProfileVersionId" = '{VersionNil}' WHERE "Id" = '{d.Line}'"""),
            d => [R("version-not-of-profile-or-company", "PurchaseOrderLines", d.Line), R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_PurchaseLineWithAProfileAndVersionOfAnotherCompany_IsReported() =>
        // the pair is consistent (the version belongs to the profile): only the company differs; the mirror of that profile is another company's tax too
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLines" SET "TaxProfileId" = '{ProfileB}', "TaxProfileVersionId" = '{VersionB}' WHERE "Id" = '{d.Line}'"""),
            d => [R("version-not-of-profile-or-company", "PurchaseOrderLines", d.Line), R("taxid-not-profile-mirror", "PurchaseOrderLines", d.Line), R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_ReturnLineWhoseVersionBelongsToAnotherProfile_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLines" SET "TaxProfileVersionId" = '{VersionNil}' WHERE "Id" = '{d.ReturnLine}'"""),
            d => [R("version-not-of-profile-or-company", "PurchaseReturnLines", d.ReturnLine), R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_ReturnLineWithAProfileAndVersionOfAnotherCompany_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLines" SET "TaxProfileId" = '{ProfileB}', "TaxProfileVersionId" = '{VersionB}' WHERE "Id" = '{d.ReturnLine}'"""),
            d => [R("version-not-of-profile-or-company", "PurchaseReturnLines", d.ReturnLine), R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    // ------------------------------------------------------------------------------------------------------------ taxid-not-profile-mirror, line-total-not-taxable-plus-tax

    [Fact]
    public async Task Snapshot_PurchaseLineTaxIdThatIsNotTheMirrorOfItsProfile_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLines" SET "TaxId" = '{TaxA18}' WHERE "Id" = '{d.Line}'"""),
            d => [R("taxid-not-profile-mirror", "PurchaseOrderLines", d.Line)]);

    [Fact]
    public async Task Snapshot_PurchaseLineTotalThatIsNotTaxablePlusTax_IsReported_AndSoIsTheHeaderTotalItBreaks() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{d.Line}'"""),
            d => [R("line-total-not-taxable-plus-tax", "PurchaseOrderLines", d.Line), R("header-grand-total-not-sum-of-lines", "PurchaseOrders", d.Po)]);

    [Fact]
    public async Task Snapshot_ReturnLineTotalThatIsNotTaxablePlusTax_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLines" SET "LineTotal" = "LineTotal" - 0.0001 WHERE "Id" = '{d.ReturnLine}'"""),
            d => [R("line-total-not-taxable-plus-tax", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_LineTotalOfALegacyLine_IsNotChecked() =>
        // history has no profile: a legacy line may have any total (the fixture's own PO lines do not add up either)
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLines" SET "LineTotal" = 1 WHERE "Id" = '{FxPoLine}'"""), _ => []);

    // ------------------------------------------------------------------------------------------------------------ row-without-treatment

    [Fact]
    public async Task Snapshot_PurchaseRowWithoutTreatmentOnACalculatorLine_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "Treatment" = NULL WHERE "Id" = '{d.Cgst}'"""),
            d => [R("row-without-treatment", "PurchaseOrderLineTaxes", d.Cgst)]);

    [Fact]
    public async Task Snapshot_ReturnRowWithoutTreatmentOnACalculatorLine_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLineTaxes" SET "Treatment" = NULL WHERE "Id" = '{d.ReturnSgst}'"""),
            d => [R("row-without-treatment", "PurchaseReturnLineTaxes", d.ReturnSgst)]);

    [Fact]
    public async Task Snapshot_ALegacyShapedRow_NoComponentAndNoTreatment_OnACalculatorLine_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "TaxComponentId" = NULL, "Treatment" = NULL WHERE "Id" = '{d.Sgst}'"""),
            d => [R("row-without-treatment", "PurchaseOrderLineTaxes", d.Sgst)]);

    [Fact]
    public async Task Snapshot_ALegacyRowOnALegacyLine_IsNotReported() =>
        // the same shape on a line without a profile is exactly what the catch-up and the TP-2 build write
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "Treatment" = NULL, "TaxComponentId" = NULL WHERE "PurchaseOrderLineId" = '{FxPoLine}'"""), _ => []);

    // ------------------------------------------------------------------------------------------------------------ place-of-supply component rules

    [Fact]
    public async Task Snapshot_IntraStatePurchaseLineWithAnIgstRow_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, Row("PurchaseOrderLineTaxes", "PurchaseOrderLineId", G(0xB01), d.Line, "IGST", "Igst", 5m, 500m, 0m)),
            d => [R("intra-line-has-igst", "PurchaseOrderLines", d.Line)]);

    [Fact]
    public async Task Snapshot_IntraStateReturnLineWithAnIgstRow_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, Row("PurchaseReturnLineTaxes", "PurchaseReturnLineId", G(0xB02), d.ReturnLine, "IGST", "Igst", 5m, 100m, 0m)),
            d => [R("intra-line-has-igst", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_InterStatePurchaseLineWithCgstSgstRows_IsReported() =>
        // the line says inter-state while its rows are CGST + SGST; its return (still intra-state) now differs from the original's place of supply too
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = '{d.Line}'"""),
            d => [R("inter-line-has-cgst-sgst", "PurchaseOrderLines", d.Line), R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_InterStateReturnLineWithCgstSgstRows_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = '{d.ReturnLine}'"""),
            d => [R("inter-line-has-cgst-sgst", "PurchaseReturnLines", d.ReturnLine), R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_NotApplicableLine_HasNoComponentRule() =>
        // VAT countries and non-taxable lines carry NotApplicable: neither the IGST nor the CGST rule applies (the return follows the original)
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""
            UPDATE "PurchaseOrderLines" SET "PlaceOfSupply" = 'NotApplicable' WHERE "Id" = '{d.Line}';
            UPDATE "PurchaseReturnLines" SET "PlaceOfSupply" = 'NotApplicable' WHERE "Id" = '{d.ReturnLine}'
            """), _ => []);

    // ------------------------------------------------------------------------------------------------------------ cgst-not-equal-sgst

    [Fact]
    public async Task Snapshot_PurchaseLineWithCgstDifferentFromSgst_IsReported_EvenByOneTenThousandth() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "TaxAmount" = "TaxAmount" + 0.0001 WHERE "Id" = '{d.Cgst}'"""),
            d => [R("cgst-not-equal-sgst", "PurchaseOrderLines", d.Line)]);

    [Fact]
    public async Task Snapshot_ReturnLineWithCgstDifferentFromSgst_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLineTaxes" SET "TaxAmount" = 2.6 WHERE "Id" = '{d.ReturnSgst}'"""),
            d => [R("cgst-not-equal-sgst", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_AnIgstOnlyLine_HasNoCgstSgstRule() =>
        // inter-state: CGST = SGST = 0 on both sides, nothing to compare
        await AssertProbeAsync(Snapshot, async (p, d) =>
        {
            await BuildInterAsync(p);
        }, _ => []);

    // ------------------------------------------------------------------------------------------------------------ header sums

    [Fact]
    public async Task Snapshot_HeaderTaxableTotalThatIsNotTheSumOfTheLines_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrders" SET "TaxableTotal" = 500.0001 WHERE "Id" = '{d.Po}'"""),
            d => [R("header-taxable-not-sum-of-lines", "PurchaseOrders", d.Po)]);

    [Fact]
    public async Task Snapshot_HeaderTaxTotalMoreThanHalfACentFromTheLines_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrders" SET "TaxTotal" = 25.01 WHERE "Id" = '{d.Po}'"""),
            d => [R("header-tax-not-sum-of-lines", "PurchaseOrders", d.Po)]);

    [Fact]
    public async Task Snapshot_HeaderGrandTotalMoreThanHalfACentFromTheLines_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrders" SET "GrandTotal" = 525.01 WHERE "Id" = '{d.Po}'"""),
            d => [R("header-grand-total-not-sum-of-lines", "PurchaseOrders", d.Po)]);

    [Fact]
    public async Task Snapshot_HeaderMoneyColumnsAreTwoDecimals_SoALineSumWithinHalfACentIsAccepted_AndOneBeyondIsNot()
    {
        // lines are numeric(18,4), the header numeric(18,2): 25.0049 against 25.00 passes, 25.0051 against 25.00 does not
        string Make(string tax) => $"""
            UPDATE "PurchaseOrderLines" SET "TaxAmount" = {tax}, "LineTotal" = 500 + {tax}, "OriginalTaxAmount" = {tax}, "OriginalLineTotal" = 500 + {tax} WHERE "Id" = '{Intra.Line}';
            UPDATE "PurchaseOrders" SET "OriginalGrandTotal" = 500 + {tax} WHERE "Id" = '{Intra.Po}';
            UPDATE "PurchaseOrderLineTaxes" SET "TaxAmount" = {tax}::numeric / 2, "OriginalTaxAmount" = {tax}::numeric / 2 WHERE "PurchaseOrderLineId" = '{Intra.Line}'
            """;

        var within = await ProbeAsync(Snapshot, (p, d) => Exec(p, Make("25.0048")));
        var beyond = await ProbeAsync(Snapshot, (p, d) => Exec(p, Make("25.0052")));

        Assert.Empty(within);
        Assert.Equal(new[] { R("header-grand-total-not-sum-of-lines", "PurchaseOrders", Intra.Po), R("header-tax-not-sum-of-lines", "PurchaseOrders", Intra.Po) }.Order(StringComparer.Ordinal).ToList(), beyond);
    }

    [Fact]
    public async Task Snapshot_HeaderOriginalGrandTotalThatIsNotTheSumOfTheOriginalLineTotals_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrders" SET "OriginalGrandTotal" = 526 WHERE "Id" = '{d.Po}'"""),
            d => [R("header-original-grand-total-not-sum-of-lines", "PurchaseOrders", d.Po)]);

    [Fact]
    public async Task Snapshot_AnOriginalLineTotalThatIsMissingOnATp3Purchase_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLines" SET "OriginalLineTotal" = NULL WHERE "Id" = '{d.Line}'"""),
            d => [R("header-original-grand-total-not-sum-of-lines", "PurchaseOrders", d.Po)]);

    [Fact]
    public async Task Snapshot_AHistoryPurchaseWithoutTaxableTotal_IsNeverCheckedAgainstItsLines() =>
        // TaxableTotal NULL marks a purchase of an older build: its header (all zero in the fixture) disagrees with its lines on purpose
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseOrders" SET "GrandTotal" = 7, "TaxTotal" = 7, "OriginalGrandTotal" = 7 WHERE "Id" = '{FxPo}'"""), _ => []);

    // ------------------------------------------------------------------------------------------------------------ return lines carry the original's snapshot

    [Fact]
    public async Task Snapshot_ReturnLineWithAnotherVersionThanTheLineItReturns_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLines" SET "TaxProfileId" = '{ProfileNil}', "TaxProfileVersionId" = '{VersionNil}' WHERE "Id" = '{d.ReturnLine}'"""),
            d => [R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_ReturnLineWithoutAProfileWhileTheOriginalHasOne_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, """
            UPDATE "PurchaseReturnLines" SET "TaxProfileId" = NULL, "TaxProfileVersionId" = NULL WHERE "PurchaseOrderLineId" IN (SELECT "Id" FROM "PurchaseOrderLines" WHERE "TaxProfileId" IS NOT NULL)
            """), d => [R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    [Fact]
    public async Task Snapshot_ReturnLineOfALegacyPurchaseLine_MayCarryAnyProfile_ButItsLegacyRowIsThenReported() =>
        // the version rule only binds returns of TP-3 lines (the original has a profile); but a line with a profile must have Treatment on its rows, and the legacy row of this one has none
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLines" SET "TaxProfileId" = '{ProfileA}', "TaxProfileVersionId" = '{VersionA}' WHERE "Id" = '{FxReturnLine}'"""),
            _ => [R("row-without-treatment", "PurchaseReturnLineTaxes", TaxSeedData.LegacyLineTaxId(FxReturnLine))]);

    [Fact]
    public async Task Snapshot_ReturnLineWithAnotherPlaceOfSupplyThanTheOriginal_IsReported() =>
        await AssertProbeAsync(Snapshot, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLines" SET "PlaceOfSupply" = 'NotApplicable' WHERE "Id" = '{d.ReturnLine}'"""),
            d => [R("return-line-version-differs-from-original", "PurchaseReturnLines", d.ReturnLine)]);

    // ------------------------------------------------------------------------------------------------------------ V__tax-line-rows-missing-purchases

    [Fact]
    public async Task MissingPurchases_ATp3PurchaseLineAndReturnLineWhoseRowsAreGone_AreReported()
    {
        var rows = await ProbeAsync(MissingPurchases, (p, d) => Exec(p, $"""
            DELETE FROM "PurchaseOrderLineTaxes" WHERE "PurchaseOrderLineId" = '{d.Line}';
            DELETE FROM "PurchaseReturnLineTaxes" WHERE "PurchaseReturnLineId" = '{d.ReturnLine}'
            """));

        Assert.Equal(new[] { $"PurchaseOrderLines|{Intra.Line}", $"PurchaseReturnLines|{Intra.ReturnLine}" }.Order(StringComparer.Ordinal).ToList(), rows);
    }

    [Fact]
    public async Task MissingPurchases_ALineWithOneOfItsTwoRows_IsNotMissing() =>
        await AssertProbeAsync(MissingPurchases, (p, d) => Exec(p, $"""DELETE FROM "PurchaseOrderLineTaxes" WHERE "Id" = '{d.Sgst}'"""), _ => []);

    [Fact]
    public async Task MissingPurchases_SalesGaps_AreNotItsBusiness_TheyStayInTheSalesFile()
    {
        var d = await MigratedAsync(postgres);

        var (purchases, sales) = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""DELETE FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{Fx(90)}'; DELETE FROM "SalesReturnLineTaxes" WHERE "SalesReturnLineId" = '{Fx(101)}'""");
            return (await p.ViolationsAsync(MissingPurchases), await p.ViolationsAsync("tax-line-rows-missing.sql"));
        });

        Assert.Empty(purchases);
        Assert.Equal([$"SalesInvoiceLines|{Fx(90)}", $"SalesReturnLines|{Fx(101)}"], sales.Order(StringComparer.Ordinal).ToList());
    }

    // ------------------------------------------------------------------------------------------------------------ V__tax-line-rows-consistency (TP-3 additions)

    [Fact]
    public async Task Consistency_PurchaseRowsWhoseOriginalTaxDoesNotAddUpToTheLinesOriginalTax_AreReported() =>
        await AssertProbeAsync(Consistency, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "OriginalTaxAmount" = "OriginalTaxAmount" + 0.0001 WHERE "Id" = '{d.Cgst}'"""),
            d => [$"PurchaseOrderLines|{d.Line}"]);

    [Fact]
    public async Task Consistency_ReturnRowsWhoseOriginalTaxDoesNotAddUpToTheLinesOriginalTax_AreReported() =>
        await AssertProbeAsync(Consistency, (p, d) => Exec(p, $"""UPDATE "PurchaseReturnLineTaxes" SET "OriginalTaxAmount" = "OriginalTaxAmount" - 1 WHERE "Id" = '{d.ReturnCgst}'"""),
            d => [$"PurchaseReturnLines|{d.ReturnLine}"]);

    [Fact]
    public async Task Consistency_ALineWithOriginalTaxButRowsWithoutOriginalTax_IsReported() =>
        // the sum of NULLs is NULL: IS DISTINCT FROM keeps this from passing silently
        await AssertProbeAsync(Consistency, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "OriginalTaxAmount" = NULL WHERE "PurchaseOrderLineId" = '{d.Line}'"""),
            d => [$"PurchaseOrderLines|{d.Line}"]);

    [Fact]
    public async Task Consistency_ALegacyLineIsNotCompared_WhateverItsRowsSayOriginally() =>
        await AssertProbeAsync(Consistency, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "OriginalTaxAmount" = 7 WHERE "PurchaseOrderLineId" = '{FxPoLine}'"""), _ => []);

    [Fact]
    public async Task Consistency_ALegacyRowWithABaseDifferentFromTheTaxableValue_IsReported_ButANonTaxableCalculatorRowIsNot()
    {
        var d = await MigratedAsync(postgres);

        var (calculatorRow, legacyRow) = await InRolledBackTransactionAsync(d, async p =>
        {
            await BuildNilAsync(p);   // one row: no component, Treatment NilRated, base 999 against a taxable value of 500
            var asCalculator = await p.ViolationsAsync(Consistency);
            await p.ExecAsync($"""UPDATE "PurchaseOrderLineTaxes" SET "Treatment" = NULL WHERE "Id" = '{Nil.Cgst}'""");   // the same row, now shaped like a legacy one
            return (asCalculator, await p.ViolationsAsync(Consistency));
        });

        Assert.Empty(calculatorRow);
        Assert.Equal([$"PurchaseOrderLines|{Nil.Line}"], legacyRow);
    }

    [Fact]
    public async Task Consistency_SalesLegacyFilterAlsoWaitsForTreatment_ASalesRowWithATreatmentIsNotALegacyRow()
    {
        // the same distinction on the sales tables (their Treatment column exists since TP-3 although sales write it from TP-4)
        var d = await MigratedAsync(postgres);

        var (withTreatment, without) = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.ExecAsync($"""UPDATE "SalesInvoiceLineTaxes" SET "BaseAmount" = "BaseAmount" + 1, "Treatment" = 'NilRated' WHERE "SalesInvoiceLineId" = '{Fx(90)}'""");
            var a = await p.ViolationsAsync(Consistency);
            await p.ExecAsync($"""UPDATE "SalesInvoiceLineTaxes" SET "Treatment" = NULL WHERE "SalesInvoiceLineId" = '{Fx(90)}'""");
            return (a, await p.ViolationsAsync(Consistency));
        });

        Assert.Empty(withTreatment);
        Assert.Equal([$"SalesInvoiceLines|{Fx(90)}"], without);
    }

    [Fact]
    public async Task Consistency_PurchaseRowsThatDoNotAddUpToTheLineTax_AreStillReported_OnATp3Line() =>
        await AssertProbeAsync(Consistency, (p, d) => Exec(p, $"""UPDATE "PurchaseOrderLineTaxes" SET "TaxAmount" = "TaxAmount" + 1 WHERE "Id" = '{d.Sgst}'"""), d => [$"PurchaseOrderLines|{d.Line}"]);

    // ------------------------------------------------------------------------------------------------------------ the files' headers

    [Fact]
    public void NewFiles_DeclareBlock_AndTheMissingPurchasesFileReferencesTheRemedy()
    {
        foreach (var file in new[] { Snapshot, MissingPurchases })
        {
            var text = ValidationScripts.Text(file);
            Assert.Contains("-- sellora:on-violation block", text, StringComparison.Ordinal);
            Assert.Contains("-- sellora:description ", text, StringComparison.Ordinal);
        }
        Assert.Contains("legacy-rows.sql", ValidationScripts.Text(MissingPurchases), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_File_CoversEveryCheckLabelTheProbesAssert()
    {
        var labels = ValidationScripts.Statements(Snapshot).SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s.Sql, @"'([a-z][a-z-]+)'\s+AS\s+chk|SELECT\s+'([a-z][a-z-]+)',\s+'Purchase").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).Distinct().Order().ToList();

        Assert.Equal(new[]
        {
            "cgst-not-equal-sgst", "header-grand-total-not-sum-of-lines", "header-original-grand-total-not-sum-of-lines", "header-tax-not-sum-of-lines", "header-taxable-not-sum-of-lines",
            "inter-line-has-cgst-sgst", "intra-line-has-igst", "line-total-not-taxable-plus-tax", "return-line-version-differs-from-original", "row-without-treatment", "taxid-not-profile-mirror",
            "version-not-of-profile-or-company",
        }.Order().ToList(), labels);
    }
}
