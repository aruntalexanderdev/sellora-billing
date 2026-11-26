using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.SalesTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// The validation SQL of TP-4 (docs/features/tax-profiles.md, "TP-4 design", Validation SQL): <c>V__tax-sales-snapshot.sql</c> (every check label) and <c>V__tax-line-rows-missing.sql</c> (blocking since TP-4).
/// Each file returns zero rows on the migrated legacy world and on hand-built valid TP-4 sales (exclusive, inclusive with discounts and two returns that return a line completely, inter-state, VAT, non-taxable,
/// and a return made by the TP-3 build on a TP-4 line = the rollback scenario); then one probe per check label writes a violation inside a transaction that is always rolled back and expects exactly the rows it causes.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TaxSalesValidationFilesTests(PostgresFixture postgres)
{
    private const string Snapshot = "tax-sales-snapshot.sql", Missing = "tax-line-rows-missing.sql";

    private static readonly SalesDoc Intra = SalesDocAt(0xE00), Incl = SalesDocAt(0xE10), Inter = SalesDocAt(0xE20), Nil = SalesDocAt(0xE30), Vat = SalesDocAt(0xE40);

    private static async Task<List<string>> AllFilesAsync(Probe p)
    {
        var rows = new List<string>();
        foreach (var file in ValidationFiles) rows.AddRange((await p.ViolationsAsync(file)).Select(r => file + ": " + r));
        return rows;
    }

    private static Task BuildIntraAsync(Probe p) => p.ExecAsync(ValidSalesSql(Intra, "SAL-TP4A", "SRT-TP4A"));
    private static Task BuildInclAsync(Probe p) => p.ExecAsync(ValidInclusiveSql(Incl, "SAL-TP4B", "SRT-TP4B1", "SRT-TP4B2"));
    private static async Task BuildInterAsync(Probe p) { await p.ExecAsync(ValidSalesSql(Inter, "SAL-TP4C", "SRT-TP4C")); await p.ExecAsync(ToInterSql(Inter)); }
    private static Task BuildNilAsync(Probe p) => p.ExecAsync(ValidNilSql(Nil, "SAL-TP4D"));
    private static Task BuildVatAsync(Probe p) => p.ExecAsync(ValidVatSql(Vat, "SAL-TPB1", "SRT-TPB1"));
    private static async Task BuildTp3ReturnAsync(Probe p) { await BuildIntraAsync(p); await p.ExecAsync(ToTp3BuildReturnSql(Intra)); }

    private static string R(string label, string table, Guid id) => $"{label}|{table}|{id}";
    private static Task Exec(Probe p, string sql) => p.ExecAsync(sql);

    /// <summary>Builds with <paramref name="build"/>, proves every V__ file is clean, applies <paramref name="mutate"/>, and returns the rows of <paramref name="file"/> (sorted).</summary>
    private async Task<List<string>> ProbeAsync(string file, Func<Probe, Task> build, Func<Probe, Task> mutate)
    {
        var d = await HeadAsync(postgres);
        var (before, after) = await InRolledBackTransactionAsync(d, async p =>
        {
            await build(p);
            var clean = await AllFilesAsync(p);
            await mutate(p);
            return (clean, (await p.ViolationsAsync(file)).Order(StringComparer.Ordinal).ToList());
        });
        Assert.True(before.Count == 0, "the hand-built valid TP-4 document is not clean: " + string.Join(" | ", before.Take(5)));
        return after;
    }

    private async Task AssertProbeAsync(Func<Probe, Task> mutate, params string[] expected) => await AssertProbeAsync(BuildIntraAsync, mutate, expected);

    private async Task AssertProbeAsync(Func<Probe, Task> build, Func<Probe, Task> mutate, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToList(), await ProbeAsync(Snapshot, build, mutate));

    // ------------------------------------------------------------------------------------------------------------ clean

    [Theory]
    [InlineData("exclusive")]
    [InlineData("inclusive")]
    [InlineData("inter")]
    [InlineData("vat")]
    [InlineData("non-taxable")]
    [InlineData("tp3-build-return")]
    public async Task AllFiles_OnAHandBuiltValidTp4Sale_ReturnZeroRows(string kind)
    {
        var d = await HeadAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            switch (kind)
            {
                case "exclusive": await BuildIntraAsync(p); break;
                case "inclusive": await BuildInclAsync(p); break;
                case "inter": await BuildInterAsync(p); break;
                case "vat": await BuildVatAsync(p); break;
                case "non-taxable": await BuildNilAsync(p); break;
                default: await BuildTp3ReturnAsync(p); break;
            }
            Assert.Equal(1L, await p.ScalarAsync<long>("""SELECT count(*) FROM "SalesInvoices" WHERE "TaxableTotal" IS NOT NULL"""));   // the sale exists and is a TP-4 one
            return await AllFilesAsync(p);
        });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task AllFiles_OnTheMigratedWorldWithEveryKindOfTp4SaleAtOnce_StayZero_AndTheLegacyHistoryIsNeverChecked()
    {
        var d = await HeadAsync(postgres);

        var rows = await InRolledBackTransactionAsync(d, async p =>
        {
            await BuildIntraAsync(p);
            await BuildInclAsync(p);
            await BuildInterAsync(p);
            await BuildNilAsync(p);
            await BuildVatAsync(p);
            return await AllFilesAsync(p);
        });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Inclusive_TheHandBuiltSale_IsTheExactPriceTheCustomerPays()
    {
        var d = await HeadAsync(postgres);

        var total = await InRolledBackTransactionAsync(d, async p =>
        {
            await BuildInclAsync(p);
            return await p.ScalarAsync<decimal>($"""SELECT "Qty" * "UnitPrice" - "Discount" - "BillDiscountShare" FROM "SalesInvoiceLines" WHERE "Id" = '{Incl.Line}'""");
        });

        Assert.Equal(178.5m, total);   // shelf 105 x 2 - 21 - 10.5 = LineTotal 178.5
    }

    // ------------------------------------------------------------------------------------------------------------ version-not-of-profile-or-company

    [Fact]
    public async Task Snapshot_SalesLineWhoseVersionBelongsToAnotherProfile_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "TaxProfileVersionId" = '{VersionNil}' WHERE "Id" = '{Intra.Line}'"""),
            R("version-not-of-profile-or-company", "SalesInvoiceLines", Intra.Line), R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_SalesLineWithAProfileAndVersionOfAnotherCompany_IsReported() =>
        // the pair is consistent (the version belongs to the profile): only the company differs; the mirror of that profile is another company's tax too
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "TaxProfileId" = '{ProfileB}', "TaxProfileVersionId" = '{VersionB}' WHERE "Id" = '{Intra.Line}'"""),
            R("version-not-of-profile-or-company", "SalesInvoiceLines", Intra.Line), R("taxid-not-profile-mirror", "SalesInvoiceLines", Intra.Line), R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_ReturnLineWhoseVersionBelongsToAnotherProfile_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxProfileVersionId" = '{VersionNil}' WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("version-not-of-profile-or-company", "SalesReturnLines", Intra.ReturnLine), R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_ReturnLineWithAProfileAndVersionOfAnotherCompany_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxProfileId" = '{ProfileB}', "TaxProfileVersionId" = '{VersionB}' WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("version-not-of-profile-or-company", "SalesReturnLines", Intra.ReturnLine), R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    // ------------------------------------------------------------------------------------------------------------ taxid-not-profile-mirror, line-total-not-taxable-plus-tax

    [Fact]
    public async Task Snapshot_SalesLineTaxIdThatIsNotTheMirrorOfItsProfile_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "TaxId" = '{TaxA18}' WHERE "Id" = '{Intra.Line}'"""), R("taxid-not-profile-mirror", "SalesInvoiceLines", Intra.Line));

    [Fact]
    public async Task Snapshot_SalesLineTotalThatIsNotTaxablePlusTax_IsReported_AndSoIsTheHeaderTotalItBreaks() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "LineTotal" = "LineTotal" + 1 WHERE "Id" = '{Intra.Line}'"""),
            R("line-total-not-taxable-plus-tax", "SalesInvoiceLines", Intra.Line), R("header-grand-total-not-sum-of-lines", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_ReturnLineTotalThatIsNotTaxablePlusTax_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "LineTotal" = "LineTotal" - 0.0001 WHERE "Id" = '{Intra.ReturnLine}'"""), R("line-total-not-taxable-plus-tax", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_LineTotalOfALegacyLine_IsNotChecked() =>
        // history has no profile: a legacy line may have any total
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "LineTotal" = 1 WHERE "Id" = '{Fx(94)}'"""));

    // ------------------------------------------------------------------------------------------------------------ price basis (BR16, BR17, T9)

    [Fact]
    public async Task Snapshot_InclusiveLineWhoseTotalIsNotTheExactPrice_IsReported() =>
        await AssertProbeAsync(BuildInclAsync, p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "LineTotal" = 178.4, "TaxableValue" = 169.9 WHERE "Id" = '{Incl.Line}'"""),
            R("inclusive-line-total-not-exact-price", "SalesInvoiceLines", Incl.Line), R("header-grand-total-not-sum-of-lines", "SalesInvoices", Incl.Si), R("header-taxable-not-sum-of-lines", "SalesInvoices", Incl.Si),
            R("returned-line-amounts-exceed-original", "SalesInvoiceLines", Incl.Line), R("fully-returned-line-not-exact", "SalesInvoiceLines", Incl.Line));

    [Fact]
    public async Task Snapshot_InclusiveLineWithABillChargeShare_IsExemptFromTheExactPriceRule()
    {
        // the exact-price promise holds only without a bill-charge share (the charge is added on top of the shelf price)
        var rows = await ProbeAsync(Snapshot, BuildInclAsync, p => Exec(p, $"""
            UPDATE "SalesInvoiceLines" SET "LineTotal" = 183.5, "BillChargeShare" = 5, "TaxableValue" = 174.7619, "TaxAmount" = 8.7381 WHERE "Id" = '{Incl.Line}'
            """));

        Assert.DoesNotContain(rows, r => r.StartsWith("inclusive-line-total-not-exact-price", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_ExclusiveLineWhoseTaxableIsNotGrossMinusDiscounts_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "Discount" = 1 WHERE "Id" = '{Intra.Line}'"""),
            R("exclusive-line-taxable-mismatch", "SalesInvoiceLines", Intra.Line), R("header-discount-not-sum-of-lines", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_ExclusiveLineTaxableAcceptsAnyOfTheThreeCurrencyPrecisions() =>
        // gross = Qty x UnitPrice rounded to 0, 2 or 3 decimals (INR/AED, 3-decimal GCC currencies): 2 x 12.3456 = 24.6912 -> 25, 24.69 or 24.691
        await AssertProbeAsync(BuildIntraAsync, p => Exec(p, $"""
            UPDATE "SalesInvoiceLines" SET "Qty" = 2, "UnitPrice" = 12.3456, "ConversionFactorToBase" = 10, "BaseQty" = 20, "TaxableValue" = 24.69, "TaxAmount" = 1.2345, "LineTotal" = 25.9245 WHERE "Id" = '{Intra.Line}';
            UPDATE "SalesInvoices" SET "Subtotal" = 24.69, "TaxTotal" = 1.23, "GrandTotal" = 25.92, "TaxableTotal" = 24.69 WHERE "Id" = '{Intra.Si}';
            DELETE FROM "SalesReturnLines" WHERE "Id" = '{Intra.ReturnLine}'
            """));

    // ------------------------------------------------------------------------------------------------------------ row-without-treatment

    [Fact]
    public async Task Snapshot_SalesRowWithoutTreatmentOnACalculatorLine_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLineTaxes" SET "Treatment" = NULL WHERE "Id" = '{Intra.Cgst}'"""), R("row-without-treatment", "SalesInvoiceLineTaxes", Intra.Cgst));

    [Fact]
    public async Task Snapshot_ReturnRowWithoutTreatmentOnACalculatorLine_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLineTaxes" SET "Treatment" = NULL WHERE "Id" = '{Intra.ReturnSgst}'"""), R("row-without-treatment", "SalesReturnLineTaxes", Intra.ReturnSgst));

    [Fact]
    public async Task Snapshot_ALegacyShapedRow_NoComponentAndNoTreatment_OnACalculatorLine_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLineTaxes" SET "TaxComponentId" = NULL, "Treatment" = NULL WHERE "Id" = '{Intra.Sgst}'"""), R("row-without-treatment", "SalesInvoiceLineTaxes", Intra.Sgst));

    [Fact]
    public async Task Snapshot_ALegacyRowOnALegacyLine_IsNotReported() =>
        // the same shape on a line without a profile is exactly what the catch-up, the TP-1 and the TP-3 build write
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLineTaxes" SET "Treatment" = NULL, "TaxComponentId" = NULL WHERE "SalesInvoiceLineId" = '{Fx(94)}'"""));

    // ------------------------------------------------------------------------------------------------------------ place-of-supply component rules

    [Fact]
    public async Task Snapshot_IntraStateSalesLineWithAnIgstRow_IsReported() =>
        await AssertProbeAsync(p => Exec(p, SalesRow("SalesInvoiceLineTaxes", "SalesInvoiceLineId", G(0xF01), Intra.Line, "IGST", "Igst", 5m, 50m, 0m)), R("intra-line-has-igst", "SalesInvoiceLines", Intra.Line));

    [Fact]
    public async Task Snapshot_IntraStateReturnLineWithAnIgstRow_IsReported() =>
        await AssertProbeAsync(p => Exec(p, SalesRow("SalesReturnLineTaxes", "SalesReturnLineId", G(0xF02), Intra.ReturnLine, "IGST", "Igst", 5m, 15m, 0m)), R("intra-line-has-igst", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_InterStateSalesLineWithCgstSgstRows_IsReported() =>
        // the line says inter-state while its rows are CGST + SGST; its return (still intra-state) now differs from the original's place of supply too
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = '{Intra.Line}'"""),
            R("inter-line-has-cgst-sgst", "SalesInvoiceLines", Intra.Line), R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_InterStateReturnLineWithCgstSgstRows_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("inter-line-has-cgst-sgst", "SalesReturnLines", Intra.ReturnLine), R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_VatLineWithAGstRow_IsReported_OnTheSaleAndOnItsReturn() =>
        // a NotApplicable (VAT or non-taxable) line has no GST row at all: unlike the purchase file this one checks it
        await AssertProbeAsync(p => Exec(p, $"""
            UPDATE "SalesInvoiceLines" SET "PlaceOfSupply" = 'NotApplicable' WHERE "Id" = '{Intra.Line}';
            UPDATE "SalesReturnLines" SET "PlaceOfSupply" = 'NotApplicable' WHERE "Id" = '{Intra.ReturnLine}'
            """), R("vat-line-has-gst-row", "SalesInvoiceLines", Intra.Line), R("vat-line-has-gst-row", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_AnIgstOnlyLine_HasNoCgstSgstRule() =>
        // inter-state: CGST = SGST = 0 on both sides, nothing to compare
        await AssertProbeAsync(BuildInterAsync, _ => Task.CompletedTask);

    // ------------------------------------------------------------------------------------------------------------ cgst-not-equal-sgst

    [Fact]
    public async Task Snapshot_SalesLineWithCgstDifferentFromSgst_IsReported_EvenByOneTenThousandth() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoiceLineTaxes" SET "TaxAmount" = "TaxAmount" + 0.0001 WHERE "Id" = '{Intra.Cgst}'"""), R("cgst-not-equal-sgst", "SalesInvoiceLines", Intra.Line));

    [Fact]
    public async Task Snapshot_ReturnLineWithCgstDifferentFromSgst_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLineTaxes" SET "TaxAmount" = 0.4 WHERE "Id" = '{Intra.ReturnSgst}'"""), R("cgst-not-equal-sgst", "SalesReturnLines", Intra.ReturnLine));

    // ------------------------------------------------------------------------------------------------------------ header sums

    [Fact]
    public async Task Snapshot_HeaderTaxableTotalThatIsNotTheSumOfTheLines_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "TaxableTotal" = 50.0001 WHERE "Id" = '{Intra.Si}'"""), R("header-taxable-not-sum-of-lines", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_HeaderTaxTotalMoreThanHalfACentFromTheLines_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "TaxTotal" = 2.51 WHERE "Id" = '{Intra.Si}'"""), R("header-tax-not-sum-of-lines", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_HeaderGrandTotalMoreThanHalfACentFromTheLines_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "GrandTotal" = 52.51 WHERE "Id" = '{Intra.Si}'"""), R("header-grand-total-not-sum-of-lines", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_HeaderDiscountTotalThatIsNotLineDiscountsPlusBillDiscountShares_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "DiscountTotal" = 0.01 WHERE "Id" = '{Intra.Si}'"""), R("header-discount-not-sum-of-lines", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_HeaderSubtotalThatIsNotTheSumOfTheGrossLines_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "Subtotal" = 50.03 WHERE "Id" = '{Intra.Si}'"""), R("header-subtotal-not-sum-of-lines", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_HeaderBillDiscountThatIsNotTheSumOfTheShares_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "BillDiscountAmount" = 1 WHERE "Id" = '{Intra.Si}'"""), R("header-bill-discount-not-sum-of-shares", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_HeaderBillChargesThatAreNotTheSumOfTheShares_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "BillCharges" = 1 WHERE "Id" = '{Intra.Si}'"""), R("header-bill-charges-not-sum-of-shares", "SalesInvoices", Intra.Si));

    [Fact]
    public async Task Snapshot_HeaderMoneyColumnsAreTwoDecimals_SoALineSumWithinHalfACentIsAccepted_AndOneBeyondIsNot()
    {
        // lines are numeric(18,4), the header numeric(18,2): 2.5048 against 2.50 passes, 2.5052 against 2.50 does not
        string Make(string tax) => $"""
            UPDATE "SalesInvoiceLines" SET "TaxAmount" = {tax}, "LineTotal" = 50 + {tax} WHERE "Id" = '{Intra.Line}';
            UPDATE "SalesInvoiceLineTaxes" SET "TaxAmount" = {tax}::numeric / 2 WHERE "SalesInvoiceLineId" = '{Intra.Line}'
            """;

        var within = await ProbeAsync(Snapshot, BuildIntraAsync, p => Exec(p, Make("2.5048")));
        var beyond = await ProbeAsync(Snapshot, BuildIntraAsync, p => Exec(p, Make("2.5052")));

        Assert.Empty(within);
        Assert.Equal(new[] { R("header-grand-total-not-sum-of-lines", "SalesInvoices", Intra.Si), R("header-tax-not-sum-of-lines", "SalesInvoices", Intra.Si) }.Order(StringComparer.Ordinal).ToList(), beyond);
    }

    [Fact]
    public async Task Snapshot_AHistoryInvoiceWithoutTaxableTotal_IsNeverCheckedAgainstItsLines() =>
        // TaxableTotal NULL marks an invoice of an older build: its header (all zero in the fixture) disagrees with its lines on purpose
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesInvoices" SET "GrandTotal" = 7, "TaxTotal" = 7, "Subtotal" = 7, "DiscountTotal" = 7 WHERE "Id" = '{Fx(82)}'"""));

    // ------------------------------------------------------------------------------------------------------------ return lines carry the original's snapshot

    [Fact]
    public async Task Snapshot_ReturnLineWithAnotherVersionThanTheLineItReturns_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxProfileId" = '{ProfileNil}', "TaxProfileVersionId" = '{VersionNil}' WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_ReturnLineWithoutAProfileWhileTheOriginalHasOne_IsReported_WhenItHasComponentRows() =>
        // only a return that wrote ONE legacy row (the TP-3 build) is tolerated; the return line and rows of the TP-4 build always carry the snapshot
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxProfileId" = NULL, "TaxProfileVersionId" = NULL, "PlaceOfSupply" = NULL WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_ReturnLineWithAnotherPlaceOfSupplyThanTheOriginal_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "PlaceOfSupply" = 'NotApplicable' WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("return-line-version-differs-from-original", "SalesReturnLines", Intra.ReturnLine), R("vat-line-has-gst-row", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_ReturnLineOfALegacyLine_MayCarryAnyProfile_ButItsLegacyRowIsThenReported() =>
        // the version rule only binds returns of TP-4 lines (the original has a profile); but a line with a profile must have Treatment on its rows, and the legacy row of this one has none
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxProfileId" = '{ProfileA}', "TaxProfileVersionId" = '{VersionA}' WHERE "Id" = '{Fx(101)}'"""),
            R("row-without-treatment", "SalesReturnLineTaxes", TaxSeedData.LegacyLineTaxId(Fx(101))));

    [Fact]
    public async Task Snapshot_ATp3BuildReturnOnATp4Line_IsTolerated_ButStillBoundByTheOriginalsAmounts() =>
        // the rollback scenario: no profile, version or place and ONE legacy row next to the original's component rows; the amounts rule still applies
        await AssertProbeAsync(BuildTp3ReturnAsync, p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxableValue" = 51, "LineTotal" = 51.75 WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("returned-line-amounts-exceed-original", "SalesInvoiceLines", Intra.Line));

    // ------------------------------------------------------------------------------------------------------------ returned amounts never exceed the original

    [Fact]
    public async Task Snapshot_ReturnedTaxableValueAboveTheOriginal_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxableValue" = 50.0001, "LineTotal" = 50.7501 WHERE "Id" = '{Intra.ReturnLine}'"""), R("returned-line-amounts-exceed-original", "SalesInvoiceLines", Intra.Line));

    [Fact]
    public async Task Snapshot_ReturnedTaxAboveTheOriginal_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxAmount" = 2.6, "LineTotal" = 17.6 WHERE "Id" = '{Intra.ReturnLine}'"""), R("returned-line-amounts-exceed-original", "SalesInvoiceLines", Intra.Line));

    [Fact]
    public async Task Snapshot_AFullyReturnedLine_ThatDoesNotSumExactly_IsReported_OnTheLineAndOnItsComponents() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLines" SET "Qty" = 10, "BaseQty" = 10, "TaxableValue" = 49, "TaxAmount" = 2.45, "LineTotal" = 51.45 WHERE "Id" = '{Intra.ReturnLine}'"""),
            R("fully-returned-line-not-exact", "SalesInvoiceLines", Intra.Line), R("fully-returned-component-not-exact", "SalesInvoiceLineTaxes", Intra.Cgst), R("fully-returned-component-not-exact", "SalesInvoiceLineTaxes", Intra.Sgst));

    [Fact]
    public async Task Snapshot_TheLastOfTwoReturnsThatIsOneTenThousandthShort_IsReported() =>
        await AssertProbeAsync(BuildInclAsync, p => Exec(p, $"""UPDATE "SalesReturnLines" SET "TaxableValue" = 127.4999, "LineTotal" = 133.8749 WHERE "Id" = '{Incl.Return2Line}'"""),
            R("fully-returned-line-not-exact", "SalesInvoiceLines", Incl.Line));

    [Fact]
    public async Task Snapshot_AComponentReturnedMoreThanSold_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLineTaxes" SET "BaseAmount" = 60 WHERE "Id" = '{Intra.ReturnCgst}'"""), R("returned-component-exceeds-original", "SalesInvoiceLineTaxes", Intra.Cgst));

    [Fact]
    public async Task Snapshot_AComponentTaxReturnedMoreThanSold_IsReported() =>
        await AssertProbeAsync(p => Exec(p, $"""UPDATE "SalesReturnLineTaxes" SET "TaxAmount" = 1.26 WHERE "Id" = '{Intra.ReturnSgst}'"""), R("returned-component-exceeds-original", "SalesInvoiceLineTaxes", Intra.Sgst), R("cgst-not-equal-sgst", "SalesReturnLines", Intra.ReturnLine));

    [Fact]
    public async Task Snapshot_ATp3BuildReturnNextToAComponentReturn_SkipsThePerComponentRules_ButNotTheLineRule()
    {
        // two returns of the same line: the first by the TP-3 build (one legacy row), the second by the TP-4 build (component rows) that together exceed the original.
        // The per-component split is approximate by design (skipped); the line totals still catch it.
        var rows = await ProbeAsync(Snapshot, BuildTp3ReturnAsync, p => Exec(p, ReturnSql(Intra.Return2, Intra.Return2Line, Intra.Return2Cgst, Intra.Return2Sgst, Intra.Si, Intra.Line, "SRT-TP4X", 8, 40m, 2m)));

        Assert.Equal([R("returned-line-amounts-exceed-original", "SalesInvoiceLines", Intra.Line)], rows);
    }

    [Fact]
    public async Task Snapshot_TwoComponentReturnsWithinTheOriginal_AreClean_AndAFullReturnSummingExactlyIsClean() =>
        await AssertProbeAsync(BuildInclAsync, _ => Task.CompletedTask);

    // ------------------------------------------------------------------------------------------------------------ V__tax-line-rows-missing (block since TP-4)

    [Fact]
    public async Task Missing_ATp4SalesLineAndReturnLineWhoseRowsAreGone_AreReported_AndPurchasesAreNotItsBusiness()
    {
        var d = await HeadAsync(postgres);

        var (rows, afterCatchUp) = await InRolledBackTransactionAsync(d, async p =>
        {
            await BuildIntraAsync(p);
            await p.ExecAsync($"""
                DELETE FROM "SalesInvoiceLineTaxes" WHERE "SalesInvoiceLineId" = '{Intra.Line}';
                DELETE FROM "SalesReturnLineTaxes" WHERE "SalesReturnLineId" = '{Intra.ReturnLine}';
                DELETE FROM "PurchaseOrderLineTaxes" WHERE "PurchaseOrderLineId" = '{Fx(50)}'
                """);
            var reported = await p.ViolationsAsync(Missing);
            await p.RunResourceAsync(LegacyRowsResource);
            return (reported, await p.ViolationsAsync(Missing));
        });

        Assert.Equal(new[] { $"SalesInvoiceLines|{Intra.Line}", $"SalesReturnLines|{Intra.ReturnLine}" }.Order(StringComparer.Ordinal).ToList(), rows.Order(StringComparer.Ordinal).ToList());
        Assert.Empty(afterCatchUp);   // the documented remedy: re-run the idempotent legacy-rows file
    }

    [Fact]
    public async Task Missing_ALineWithOneOfItsTwoRows_IsNotMissing() =>
        Assert.Empty(await ProbeAsync(Missing, BuildIntraAsync, p => Exec(p, $"""DELETE FROM "SalesInvoiceLineTaxes" WHERE "Id" = '{Intra.Sgst}'""")));

    [Fact]
    public void Missing_FileIsBlocking_AndPointsAtTheRemedy()
    {
        var text = ValidationScripts.Text(Missing);

        Assert.Contains("-- sellora:on-violation block", text, StringComparison.Ordinal);
        Assert.DoesNotContain("on-violation warn", text, StringComparison.Ordinal);
        Assert.Contains("legacy-rows.sql", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------ the file itself

    [Fact]
    public void NewFile_DeclaresBlock_AndADescription()
    {
        var text = ValidationScripts.Text(Snapshot);

        Assert.Contains("-- sellora:on-violation block", text, StringComparison.Ordinal);
        Assert.Contains("-- sellora:description ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("personal", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_File_CoversEveryCheckLabelTheProbesAssert()
    {
        var labels = ValidationScripts.Statements(Snapshot).SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s.Sql, @"'([a-z][a-z-]+)'\s+AS\s+chk|SELECT\s+'([a-z][a-z-]+)',\s+'Sales").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).Distinct().Order().ToList();

        Assert.Equal(new[]
        {
            "cgst-not-equal-sgst", "exclusive-line-taxable-mismatch", "fully-returned-component-not-exact", "fully-returned-line-not-exact", "header-bill-charges-not-sum-of-shares",
            "header-bill-discount-not-sum-of-shares", "header-discount-not-sum-of-lines", "header-grand-total-not-sum-of-lines", "header-subtotal-not-sum-of-lines", "header-tax-not-sum-of-lines",
            "header-taxable-not-sum-of-lines", "inclusive-line-total-not-exact-price", "inter-line-has-cgst-sgst", "intra-line-has-igst", "line-total-not-taxable-plus-tax",
            "return-line-version-differs-from-original", "returned-component-exceeds-original", "returned-line-amounts-exceed-original", "row-without-treatment", "taxid-not-profile-mirror",
            "version-not-of-profile-or-company", "vat-line-has-gst-row",
        }.Order().ToList(), labels);
    }
}
