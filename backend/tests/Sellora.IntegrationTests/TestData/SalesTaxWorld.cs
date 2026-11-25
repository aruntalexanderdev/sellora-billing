using System.Globalization;
using Npgsql;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Data builders of the TP-4 database tests (docs/features/tax-profiles.md, "TP-4 design"). Two things:
/// (1) <see cref="Tp3ShapedCopyAsync"/>: the TP-2 shaped world (<see cref="PurchaseTaxWorld.Tp2ShapedCopyAsync"/>) migrated to <see cref="TaxProfileWorld.ValidatePurchase"/> (the schema the TP-3 build runs on)
/// with the sales the TP-4 migration must carry: a named customer in another state, one without a state, a walk-in that carries a state, a branch without a state, a company-B (AE) sale with a return,
/// sales returns, and "gap" lines of the TP-3 build (a sales line and a sales return line without any tax row);
/// (2) <see cref="ValidSalesSql"/> and the variants: hand-built, internally consistent TP-4 sales documents written on the migrated head (exclusive intra, inclusive with discounts and two returns that return
/// a line completely, inter-state, VAT, non-taxable, a return made by the TP-3 build on a TP-4 line). Rows are cloned from the fixture's own documents (<see cref="PurchaseTaxWorld.Clone"/>).
/// </summary>
public static class SalesTaxWorld
{
    // sales extras (the 0xC00 range of TaxProfileWorld.G)
    public static readonly Guid CustOther = G(0xC20), CustNoState = G(0xC21), CustWalkState = G(0xC22), CustB = G(0xC23), BranchNoState = G(0xC24);
    public static readonly Guid SiOther = G(0xC01), SiOtherLine = G(0xC02), SiOtherRow = G(0xC03);
    public static readonly Guid SiNoState = G(0xC04), SiNoStateLine = G(0xC05), SiNoStateRow = G(0xC06);
    public static readonly Guid SiWalk = G(0xC07), SiWalkLine = G(0xC08), SiWalkRow = G(0xC09);
    public static readonly Guid SiNoBranchState = G(0xC0A), SiNoBranchStateLine = G(0xC0B), SiNoBranchStateRow = G(0xC0C);
    public static readonly Guid SiB = G(0xC0D), SiBLine = G(0xC0E), SiBRow = G(0xC0F), SrB = G(0xC10), SrBLine = G(0xC11), SrBRow = G(0xC12);
    public static readonly Guid SrOther = G(0xC13), SrOtherLine = G(0xC14), SrOtherRow = G(0xC15);
    public static readonly Guid GapSalesLine2 = G(0xC16), GapSalesReturnLine2 = G(0xC17);

    public static string Q(Guid id) => $"'{id}'";

    /// <summary>The state of company A (and of the fixture branches): the intra-state place of supply of every TP-4 sample.</summary>
    public static string StateOther => $"""(SELECT s."Id" FROM "States" s JOIN "Countries" c ON c."Id" = s."CountryId" WHERE c."Code" = 'IN' AND s."Code" = 'KA')""";

    public static string CountryIn => """(SELECT "Id" FROM "Countries" WHERE "Code" = 'IN')""";

    /// <summary>Written at <see cref="TaxProfileWorld.ValidatePurchase"/> (no TP-4 column exists yet; every statement is a clone of fixture rows).</summary>
    public static string Tp3ExtrasSql() => $"""
        {Clone("Customers", $"\"Id\" = {Q(Fx(13))}", ("Id", Q(CustOther)), ("Name", "'Other state buyer'"), ("IsWalkIn", "false"), ("StateId", StateOther), ("CountryId", CountryIn))}
        {Clone("Customers", $"\"Id\" = {Q(Fx(13))}", ("Id", Q(CustNoState)), ("Name", "'No state buyer'"), ("IsWalkIn", "false"), ("StateId", "NULL"))}
        {Clone("Customers", $"\"Id\" = {Q(Fx(13))}", ("Id", Q(CustWalkState)), ("Name", "'Walk-in with a state'"), ("IsWalkIn", "true"), ("StateId", StateIn), ("CountryId", CountryIn))}
        {Clone("Customers", $"\"Id\" = {Q(Fx(13))}", ("Id", Q(CustB)), ("Name", "'Buyer B'"), ("CompanyId", Q(CoB)), ("IsWalkIn", "false"), ("StateId", "NULL"))}
        {Clone("Branches", $"\"Id\" = '{DefaultBranch}'", ("Id", Q(BranchNoState)), ("Code", "'NOST'"), ("Name", "'Branch without a state'"), ("InvoicePrefix", "NULL"), ("IsDefault", "false"), ("StateId", "NULL"))}

        -- named customer in another state, from the default branch
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(SiOther)), ("InvoiceNo", "'SAL-T4A'"), ("CustomerId", Q(CustOther)))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(SiOtherLine)), ("SalesInvoiceId", Q(SiOther)))}
        {Clone("SalesInvoiceLineTaxes", $"\"SalesInvoiceLineId\" = {Q(Fx(94))}", ("Id", Q(SiOtherRow)), ("SalesInvoiceLineId", Q(SiOtherLine)))}
        -- named customer without a state
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(SiNoState)), ("InvoiceNo", "'SAL-T4B'"), ("CustomerId", Q(CustNoState)))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(SiNoStateLine)), ("SalesInvoiceId", Q(SiNoState)))}
        {Clone("SalesInvoiceLineTaxes", $"\"SalesInvoiceLineId\" = {Q(Fx(94))}", ("Id", Q(SiNoStateRow)), ("SalesInvoiceLineId", Q(SiNoStateLine)))}
        -- walk-in customer that carries a state
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(SiWalk)), ("InvoiceNo", "'SAL-T4C'"), ("CustomerId", Q(CustWalkState)))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(SiWalkLine)), ("SalesInvoiceId", Q(SiWalk)))}
        {Clone("SalesInvoiceLineTaxes", $"\"SalesInvoiceLineId\" = {Q(Fx(94))}", ("Id", Q(SiWalkRow)), ("SalesInvoiceLineId", Q(SiWalkLine)))}
        -- a branch with neither its own state nor (for the test) a customer state
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(SiNoBranchState)), ("InvoiceNo", "'SAL-T4D'"), ("BranchId", Q(BranchNoState)))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(SiNoBranchStateLine)), ("SalesInvoiceId", Q(SiNoBranchState)))}
        {Clone("SalesInvoiceLineTaxes", $"\"SalesInvoiceLineId\" = {Q(Fx(94))}", ("Id", Q(SiNoBranchStateRow)), ("SalesInvoiceLineId", Q(SiNoBranchStateLine)))}

        -- company B (AE, VAT): a sale and a return of it
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(SiB)), ("InvoiceNo", "'SAL-TB01'"), ("CompanyId", Q(CoB)), ("BranchId", Q(BranchB)), ("StockLocationId", Q(StoreB)), ("CustomerId", Q(CustB)))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(SiBLine)), ("SalesInvoiceId", Q(SiB)), ("ProductId", Q(G(0x211))), ("ProductUnitId", Q(UnitB)), ("UnitId", Q(Fx(1))), ("UnitCode", "'pc'"),
            ("TaxId", Q(TaxB5)), ("Qty", "10"), ("ConversionFactorToBase", "1"), ("BaseQty", "10"), ("UnitPrice", "5"), ("TaxableValue", "50"), ("TaxAmount", "2.5"), ("LineTotal", "52.5"))}
        {Clone("SalesInvoiceLineTaxes", $"\"SalesInvoiceLineId\" = {Q(Fx(94))}", ("Id", Q(SiBRow)), ("SalesInvoiceLineId", Q(SiBLine)))}
        {Clone("SalesReturns", $"\"Id\" = {Q(Fx(100))}", ("Id", Q(SrB)), ("ReturnNo", "'SRT-TB01'"), ("SalesInvoiceId", Q(SiB)), ("CompanyId", Q(CoB)), ("BranchId", Q(BranchB)), ("StockLocationId", Q(StoreB)))}
        {Clone("SalesReturnLines", $"\"Id\" = {Q(Fx(101))}", ("Id", Q(SrBLine)), ("SalesReturnId", Q(SrB)), ("SalesInvoiceLineId", Q(SiBLine)), ("ProductId", Q(G(0x211))), ("ProductUnitId", Q(UnitB)), ("UnitId", Q(Fx(1))))}
        {Clone("SalesReturnLineTaxes", $"\"SalesReturnLineId\" = {Q(Fx(101))}", ("Id", Q(SrBRow)), ("SalesReturnLineId", Q(SrBLine)))}

        -- a return of the other-state sale
        {Clone("SalesReturns", $"\"Id\" = {Q(Fx(100))}", ("Id", Q(SrOther)), ("ReturnNo", "'SRT-T4A'"), ("SalesInvoiceId", Q(SiOther)))}
        {Clone("SalesReturnLines", $"\"Id\" = {Q(Fx(101))}", ("Id", Q(SrOtherLine)), ("SalesReturnId", Q(SrOther)), ("SalesInvoiceLineId", Q(SiOtherLine)))}
        {Clone("SalesReturnLineTaxes", $"\"SalesReturnLineId\" = {Q(Fx(101))}", ("Id", Q(SrOtherRow)), ("SalesReturnLineId", Q(SrOtherLine)))}

        -- gap lines: saved by the TP-3 build after the TP-1 commit and after AddPurchaseTaxSnapshot's catch-up, no tax row
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(GapSalesLine2)), ("SalesInvoiceId", Q(SiOther)))}
        {Clone("SalesReturnLines", $"\"Id\" = {Q(Fx(101))}", ("Id", Q(GapSalesReturnLine2)), ("SalesReturnId", Q(SrOther)), ("SalesInvoiceLineId", Q(SiOtherLine)))}
        """;

    public static readonly Guid[] GapLines = [GapSalesLine2, GapSalesReturnLine2];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TestDatabase? tp3Template;

    /// <summary>A private committed copy of the TP-3 shaped world (at <see cref="ValidatePurchase"/> with <see cref="Tp3ExtrasSql"/>). Dispose to drop.</summary>
    public static async Task<TestDatabase> Tp3ShapedCopyAsync(PostgresFixture postgres)
    {
        await Gate.WaitAsync();
        try
        {
            if (tp3Template is null)
            {
                var d = await Tp2ShapedCopyAsync(postgres);
                await MigrateExactlyAsync(d, ValidatePurchase);
                await ExecuteAsync(d, Tp3ExtrasSql());
                NpgsqlConnection.ClearAllPools();
                tp3Template = d;
            }
        }
        finally { Gate.Release(); }
        return await CloneAsync(postgres, tp3Template);
    }

    private static TestDatabase? head;

    /// <summary>The TP-3 shaped world migrated to the head (<see cref="ValidateSales"/>), shared by read-only probes (they must roll back). Not disposed by tests.</summary>
    public static async Task<TestDatabase> HeadAsync(PostgresFixture postgres)
    {
        var copy = await Tp3ShapedCopyAsync(postgres);   // takes and releases the gate itself
        await Gate.WaitAsync();
        try
        {
            if (head is not null) { await copy.DisposeAsync(); return head; }
            await MigrateExactlyAsync(copy, ValidateSales);
            NpgsqlConnection.ClearAllPools();
            head = copy;
            return copy;
        }
        finally { Gate.Release(); }
    }

    private static async Task<TestDatabase> CloneAsync(PostgresFixture postgres, TestDatabase source)
    {
        var name = "sellora_test_" + Guid.NewGuid().ToString("N");
        postgres.Track(name);
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{name}\" TEMPLATE \"{source.Name}\"";
                await create.ExecuteNonQueryAsync();
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == "55006" && attempt < 100) { await Task.Delay(100); }
        }
        return new TestDatabase(name, PostgresFixture.ConnectionStringFor(postgres.ConnectionString, name), postgres.ConnectionString);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------ valid TP-4 documents

    /// <summary>Ids of one hand-built TP-4 sale, up to two returns of it (the second is used by the inclusive document) and their rows.</summary>
    public sealed record SalesDoc(Guid Si, Guid Line, Guid Cgst, Guid Sgst, Guid Return, Guid ReturnLine, Guid ReturnCgst, Guid ReturnSgst, Guid Return2, Guid Return2Line, Guid Return2Cgst, Guid Return2Sgst);

    public static SalesDoc SalesDocAt(int n) => new(G(n), G(n + 1), G(n + 2), G(n + 3), G(n + 4), G(n + 5), G(n + 6), G(n + 7), G(n + 8), G(n + 9), G(n + 10), G(n + 11));

    private static string D(decimal v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>One calculator row of company A on a sales line (sales rows carry no original-currency columns).</summary>
    public static string SalesRow(string table, string lineColumn, Guid id, Guid line, string code, string kind, decimal rate, decimal baseAmount, decimal tax, string treatment = "Taxable", Guid? company = null, string country = "IN") => $"""
        INSERT INTO "{table}" ("Id", "{lineColumn}", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount", "Treatment")
        VALUES ({Q(id)}, {Q(line)}, '{Component(company ?? CoA, country, code)}', '{code}', '{kind}', 'Percent', {D(rate)}, NULL, {D(baseAmount)}, {D(tax)}, '{treatment}');
        """;

    /// <summary>
    /// Company A, intra-state GST 5 percent, exclusive: 1 Box at 50 (taxable 50, CGST 1.25 + SGST 1.25, total 52.5; 10 base units) and a return of 3 pieces (15 taxable, 0.75 tax, 15.75).
    /// Every TP-4 column set, mirror TaxId = the profile's mirror, header sums equal the line, bill discount and charges zero.
    /// </summary>
    public static string ValidSalesSql(SalesDoc d, string invoiceNo, string returnNo) => $"""
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(d.Si)), ("InvoiceNo", $"'{invoiceNo}'"), ("Subtotal", "50"), ("TaxTotal", "2.5"), ("DiscountTotal", "0"), ("GrandTotal", "52.5"),
            ("TaxableTotal", "50"), ("BillDiscountAmount", "0"), ("BillCharges", "0"), ("PlaceOfSupply", "'IntraState'"), ("PlaceOfSupplyStateId", StateIn), ("SellerStateId", StateIn))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(d.Line)), ("SalesInvoiceId", Q(d.Si)), ("TaxId", Q(TaxA5)), ("TaxRatePercent", "5"), ("Qty", "1"), ("UnitPrice", "50"), ("Discount", "0"),
            ("TaxableValue", "50"), ("TaxAmount", "2.5"), ("LineTotal", "52.5"), ("TaxProfileId", Q(ProfileA)), ("TaxProfileVersionId", Q(VersionA)), ("PlaceOfSupply", "'IntraState'"), ("HsnCode", "'123456'"),
            ("PriceIncludesTax", "false"), ("BillDiscountShare", "0"), ("BillChargeShare", "0"))}
        {SalesRow("SalesInvoiceLineTaxes", "SalesInvoiceLineId", d.Cgst, d.Line, "CGST", "Cgst", 2.5m, 50m, 1.25m)}
        {SalesRow("SalesInvoiceLineTaxes", "SalesInvoiceLineId", d.Sgst, d.Line, "SGST", "Sgst", 2.5m, 50m, 1.25m)}
        {ReturnSql(d.Return, d.ReturnLine, d.ReturnCgst, d.ReturnSgst, d.Si, d.Line, returnNo, 3, 15m, 0.75m)}
        """;

    /// <summary>A return of <paramref name="baseQty"/> base units (pieces) of a TP-4 sales line of company A, GST 5 percent intra-state, carrying the original's profile, version and place.</summary>
    public static string ReturnSql(Guid ret, Guid line, Guid cgst, Guid sgst, Guid si, Guid originalLine, string returnNo, int baseQty, decimal taxable, decimal tax) => $"""
        {Clone("SalesReturns", $"\"Id\" = {Q(Fx(100))}", ("Id", Q(ret)), ("ReturnNo", $"'{returnNo}'"), ("SalesInvoiceId", Q(si)), ("TaxableTotal", D(taxable)), ("TaxTotal", D(tax)), ("Total", D(taxable + tax)))}
        {Clone("SalesReturnLines", $"\"Id\" = {Q(Fx(101))}", ("Id", Q(line)), ("SalesReturnId", Q(ret)), ("SalesInvoiceLineId", Q(originalLine)), ("Qty", D(baseQty)), ("BaseQty", D(baseQty)), ("TaxableValue", D(taxable)),
            ("TaxAmount", D(tax)), ("LineTotal", D(taxable + tax)), ("TaxProfileId", Q(ProfileA)), ("TaxProfileVersionId", Q(VersionA)), ("PlaceOfSupply", "'IntraState'"))}
        {SalesRow("SalesReturnLineTaxes", "SalesReturnLineId", cgst, line, "CGST", "Cgst", 2.5m, taxable, tax / 2)}
        {SalesRow("SalesReturnLineTaxes", "SalesReturnLineId", sgst, line, "SGST", "Sgst", 2.5m, taxable, tax / 2)}
        """;

    /// <summary>
    /// Inclusive: 2 Boxes at a shelf price of 105 (gross 210), line discount 21, bill discount share 10.5: customer pays exactly 210 - 21 - 10.5 = 178.5 = taxable 170 + tax 8.5 (CGST 4.25 + SGST 4.25), 20 base units.
    /// Two returns that return the line completely: 5 base units (taxable 42.5, tax 2.125) and the remaining 15 (127.5, 6.375: the last return takes the remainder).
    /// </summary>
    public static string ValidInclusiveSql(SalesDoc d, string invoiceNo, string returnNo1, string returnNo2) => $"""
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(d.Si)), ("InvoiceNo", $"'{invoiceNo}'"), ("Subtotal", "210"), ("TaxTotal", "8.5"), ("DiscountTotal", "31.5"), ("GrandTotal", "178.5"),
            ("TaxableTotal", "170"), ("BillDiscountAmount", "10.5"), ("BillCharges", "0"), ("PlaceOfSupply", "'IntraState'"), ("PlaceOfSupplyStateId", StateIn), ("SellerStateId", StateIn))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(d.Line)), ("SalesInvoiceId", Q(d.Si)), ("TaxId", Q(TaxA5)), ("TaxRatePercent", "5"), ("Qty", "2"), ("ConversionFactorToBase", "10"), ("BaseQty", "20"),
            ("UnitPrice", "105"), ("Discount", "21"), ("TaxableValue", "170"), ("TaxAmount", "8.5"), ("LineTotal", "178.5"), ("TaxProfileId", Q(ProfileA)), ("TaxProfileVersionId", Q(VersionA)),
            ("PlaceOfSupply", "'IntraState'"), ("HsnCode", "'123456'"), ("PriceIncludesTax", "true"), ("BillDiscountShare", "10.5"), ("BillChargeShare", "0"))}
        {SalesRow("SalesInvoiceLineTaxes", "SalesInvoiceLineId", d.Cgst, d.Line, "CGST", "Cgst", 2.5m, 170m, 4.25m)}
        {SalesRow("SalesInvoiceLineTaxes", "SalesInvoiceLineId", d.Sgst, d.Line, "SGST", "Sgst", 2.5m, 170m, 4.25m)}
        {ReturnSql(d.Return, d.ReturnLine, d.ReturnCgst, d.ReturnSgst, d.Si, d.Line, returnNo1, 5, 42.5m, 2.125m)}
        {ReturnSql(d.Return2, d.Return2Line, d.Return2Cgst, d.Return2Sgst, d.Si, d.Line, returnNo2, 15, 127.5m, 6.375m)}
        """;

    /// <summary>The exclusive document turned inter-state: IGST 5 percent (2.5 on 50; return 0.75 on 15) instead of CGST + SGST, buyer in another state.</summary>
    public static string ToInterSql(SalesDoc d) => $"""
        DELETE FROM "SalesInvoiceLineTaxes" WHERE "Id" IN ({Q(d.Cgst)}, {Q(d.Sgst)});
        DELETE FROM "SalesReturnLineTaxes" WHERE "Id" IN ({Q(d.ReturnCgst)}, {Q(d.ReturnSgst)});
        UPDATE "SalesInvoices" SET "PlaceOfSupply" = 'InterState', "PlaceOfSupplyStateId" = {StateOther} WHERE "Id" = {Q(d.Si)};
        UPDATE "SalesInvoiceLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = {Q(d.Line)};
        UPDATE "SalesReturnLines" SET "PlaceOfSupply" = 'InterState' WHERE "Id" = {Q(d.ReturnLine)};
        {SalesRow("SalesInvoiceLineTaxes", "SalesInvoiceLineId", d.Cgst, d.Line, "IGST", "Igst", 5m, 50m, 2.5m)}
        {SalesRow("SalesReturnLineTaxes", "SalesReturnLineId", d.ReturnCgst, d.ReturnLine, "IGST", "Igst", 5m, 15m, 0.75m)}
        """;

    /// <summary>A zero-rated profile: one component-less calculator row with a Treatment and a base that is NOT the line's taxable value (so only the Treatment keeps it from being a legacy row).</summary>
    public static string ValidNilSql(SalesDoc d, string invoiceNo) => $"""
        {Clone("SalesInvoices", $"\"Id\" = {Q(Fx(82))}", ("Id", Q(d.Si)), ("InvoiceNo", $"'{invoiceNo}'"), ("Subtotal", "50"), ("TaxTotal", "0"), ("DiscountTotal", "0"), ("GrandTotal", "50"),
            ("TaxableTotal", "50"), ("BillDiscountAmount", "0"), ("BillCharges", "0"), ("PlaceOfSupply", "'IntraState'"), ("PlaceOfSupplyStateId", StateIn), ("SellerStateId", StateIn))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(d.Line)), ("SalesInvoiceId", Q(d.Si)), ("TaxId", Q(TaxA0)), ("TaxRatePercent", "0"), ("Qty", "1"), ("UnitPrice", "50"), ("Discount", "0"),
            ("TaxableValue", "50"), ("TaxAmount", "0"), ("LineTotal", "50"), ("TaxProfileId", Q(ProfileNil)), ("TaxProfileVersionId", Q(VersionNil)), ("PlaceOfSupply", "'IntraState'"), ("HsnCode", "NULL"),
            ("PriceIncludesTax", "false"), ("BillDiscountShare", "0"), ("BillChargeShare", "0"))}
        INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount", "Treatment")
        VALUES ({Q(d.Cgst)}, {Q(d.Line)}, NULL, 'NIL', 'Other', 'Percent', 0, NULL, 999, 0, 'NilRated');
        """;

    /// <summary>
    /// Company B (AE, VAT 5 percent on the AE profile, place NotApplicable): 10 pieces at 5 (taxable 50, VAT 2.5, total 52.5) and a return of 4 (taxable 20, VAT 1.0, total 21).
    /// </summary>
    public static string ValidVatSql(SalesDoc d, string invoiceNo, string returnNo) => $"""
        {Clone("SalesInvoices", $"\"Id\" = {Q(SiB)}", ("Id", Q(d.Si)), ("InvoiceNo", $"'{invoiceNo}'"), ("Subtotal", "50"), ("TaxTotal", "2.5"), ("DiscountTotal", "0"), ("GrandTotal", "52.5"),
            ("TaxableTotal", "50"), ("BillDiscountAmount", "0"), ("BillCharges", "0"), ("PlaceOfSupply", "'NotApplicable'"), ("PlaceOfSupplyStateId", "NULL"), ("SellerStateId", "NULL"))}
        {Clone("SalesInvoiceLines", $"\"Id\" = {Q(SiBLine)}", ("Id", Q(d.Line)), ("SalesInvoiceId", Q(d.Si)), ("TaxRatePercent", "5"), ("Qty", "10"), ("UnitPrice", "5"), ("Discount", "0"),
            ("TaxableValue", "50"), ("TaxAmount", "2.5"), ("LineTotal", "52.5"), ("TaxProfileId", Q(ProfileB)), ("TaxProfileVersionId", Q(VersionB)), ("PlaceOfSupply", "'NotApplicable'"), ("HsnCode", "NULL"),
            ("PriceIncludesTax", "false"), ("BillDiscountShare", "0"), ("BillChargeShare", "0"))}
        {SalesRow("SalesInvoiceLineTaxes", "SalesInvoiceLineId", d.Cgst, d.Line, "VAT", "Vat", 5m, 50m, 2.5m, company: CoB, country: "AE")}
        {Clone("SalesReturns", $"\"Id\" = {Q(SrB)}", ("Id", Q(d.Return)), ("ReturnNo", $"'{returnNo}'"), ("SalesInvoiceId", Q(d.Si)), ("TaxableTotal", "20"), ("TaxTotal", "1"), ("Total", "21"))}
        {Clone("SalesReturnLines", $"\"Id\" = {Q(SrBLine)}", ("Id", Q(d.ReturnLine)), ("SalesReturnId", Q(d.Return)), ("SalesInvoiceLineId", Q(d.Line)), ("Qty", "4"), ("BaseQty", "4"), ("TaxableValue", "20"),
            ("TaxAmount", "1"), ("LineTotal", "21"), ("TaxProfileId", Q(ProfileB)), ("TaxProfileVersionId", Q(VersionB)), ("PlaceOfSupply", "'NotApplicable'"))}
        {SalesRow("SalesReturnLineTaxes", "SalesReturnLineId", d.ReturnCgst, d.ReturnLine, "VAT", "Vat", 5m, 20m, 1m, company: CoB, country: "AE")}
        """;

    /// <summary>
    /// The rollback scenario: the exclusive document's return made by the TP-3 build on a TP-4 line (no profile, version or place on the return line, ONE legacy row next to the original's component rows).
    /// </summary>
    public static string ToTp3BuildReturnSql(SalesDoc d) => $"""
        UPDATE "SalesReturnLines" SET "TaxProfileId" = NULL, "TaxProfileVersionId" = NULL, "PlaceOfSupply" = NULL WHERE "Id" = {Q(d.ReturnLine)};
        DELETE FROM "SalesReturnLineTaxes" WHERE "Id" IN ({Q(d.ReturnCgst)}, {Q(d.ReturnSgst)});
        INSERT INTO "SalesReturnLineTaxes" ("Id", "SalesReturnLineId", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount", "Treatment")
        VALUES ({Q(d.ReturnCgst)}, {Q(d.ReturnLine)}, NULL, 'GST', 'Other', 'Percent', 5, NULL, 15, 0.75, NULL);
        """;
}
