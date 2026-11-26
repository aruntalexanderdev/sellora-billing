using System.Globalization;
using Npgsql;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Data builders of the TP-3 database tests (docs/features/tax-profiles.md, "TP-3 design"). Two things:
/// (1) <see cref="Tp2ShapedCopyAsync"/>: the legacy tax world migrated only to <see cref="TaxProfileWorld.ValidateFks"/> (the schema the TP-2 build runs on) with purchases in two companies (IN and AE),
/// foreign currency, a purchase return in each company, sales, and "gap" lines of all four kinds that have no tax row (what the TP-2 build writes between the TP-1 commit and the TP-3 swap);
/// (2) <see cref="ValidDocumentSql"/>: a hand-built, internally consistent TP-3 purchase (intra-state GST 5 percent, CGST + SGST rows) and its purchase return, written on the migrated head.
/// Rows are copied from the fixture's own documents (<see cref="Clone"/>) so the builders never repeat the long NOT NULL column lists and stay valid when columns are added.
/// </summary>
public static class PurchaseTaxWorld
{
    public static readonly Guid ProfileA = TaxSeedData.LegacyProfileId(TaxA5), VersionA = TaxSeedData.LegacyVersionId(TaxA5);
    public static readonly Guid ProfileNil = TaxSeedData.LegacyProfileId(TaxA0), VersionNil = TaxSeedData.LegacyVersionId(TaxA0);
    public static readonly Guid ProfileB = TaxSeedData.LegacyProfileId(TaxB5), VersionB = TaxSeedData.LegacyVersionId(TaxB5);

    public static string Component(Guid company, string cc, string code) => TaxSeedData.ComponentId(company, cc, code).ToString();

    // fixture documents (CompanyMigrations.FixtureSql) that are cloned
    public static readonly Guid FxPo = Fx(40), FxPoLine = Fx(50), FxReturn = Fx(70), FxReturnLine = Fx(71), FxSupplier = Fx(14), FxUnit = Fx(30);

    // TP-2 extras (all ids in the 0x9xx range of TaxProfileWorld.G)
    public static readonly Guid SupB = G(0x901), UnitB = G(0x902), PoB = G(0x903), PoLineB = G(0x904), PoLineBRow = G(0x905), PrB = G(0x906), PrLineB = G(0x907), PrLineBRow = G(0x908);
    public static readonly Guid BranchB = G(0x90a), StoreB = G(0x90b);
    public static readonly Guid UsdId = G(0x909), PoUsdA = G(0x920), PoUsdALine = G(0x921), PoUsdARow = G(0x922);
    public static readonly Guid GapPoLine = G(0x911), GapPrLine = G(0x912), GapSalesLine = G(0x913), GapSalesReturnLine = G(0x914);

    private static string Q(Guid id) => $"'{id}'";
    private static int cloneCounter;

    /// <summary>INSERT of a copy of the row(s) matching <paramref name="where"/> with <paramref name="set"/> applied (column to SQL expression). Several statements may be concatenated: each clone has its own temp table.</summary>
    public static string Clone(string table, string where, params (string Column, string Expr)[] set)
    {
        var temp = "_clone_" + Interlocked.Increment(ref cloneCounter).ToString(CultureInfo.InvariantCulture);
        var assignments = string.Join(", ", set.Select(s => $"\"{s.Column}\" = {s.Expr}"));
        return $"""
            CREATE TEMP TABLE {temp} AS SELECT * FROM "{table}" WHERE {where};
            UPDATE {temp} SET {assignments};
            INSERT INTO "{table}" SELECT * FROM {temp};
            DROP TABLE {temp};
            """;
    }

    public static string Lit(Guid id) => $"'{id}'";

    private static string StateOf(string country, string code) => $"""(SELECT s."Id" FROM "States" s JOIN "Countries" c ON c."Id" = s."CountryId" WHERE c."Code" = '{country}' AND s."Code" = '{code}')""";
    public static string StateIn => StateOf("IN", "MH");
    public static string StateAe => StateOf("AE", "DU");

    // ------------------------------------------------------------------------------------------------------------------------------------ TP-2 shaped data

    /// <summary>What the TP-2 build can have written: AE purchase (USD) with a return, an IN purchase in USD, and one gap line of each of the four kinds. Written at <see cref="ValidateFks"/>.</summary>
    public static string Tp2ExtrasSql()
    {
        var bBranch = Q(BranchB);
        var bStore = Q(StoreB);
        return $"""
            -- the world's extra companies were written without a branch: company B gets a main branch and its store (a purchase needs both)
            {Clone("Branches", $"\"Id\" = '{DefaultBranch}'", ("Id", Q(BranchB)), ("CompanyId", Q(CoB)), ("Code", "'BRB'"), ("Name", "'B main'"), ("InvoicePrefix", "NULL"))}
            {Clone("StockLocations", $"\"Id\" = '{DefaultStore}'", ("Id", Q(StoreB)), ("CompanyId", Q(CoB)), ("BranchId", Q(BranchB)), ("Code", "'BRB-S'"), ("Name", "'B store'"))}
            INSERT INTO "Currencies" ("Id", "Code", "Name", "Symbol", "DecimalPrecision", "IsActive") VALUES ({Q(UsdId)}, 'USD', 'US Dollar', '$', 2, true);
            {Clone("Suppliers", $"\"Id\" = {Q(FxSupplier)}", ("Id", Q(SupB)), ("CompanyId", Q(CoB)), ("Name", "'Sup B'"))}
            {Clone("ProductUnits", $"\"Id\" = {Q(FxUnit)}", ("Id", Q(UnitB)), ("ProductId", Q(G(0x211))), ("CompanyId", Q(CoB)))}

            -- AE purchase in USD (company B) with one legacy row, and a return of it with one legacy row
            {Clone("PurchaseOrders", $"\"Id\" = {Q(FxPo)}", ("Id", Q(PoB)), ("PurchaseNo", "'PUR-B001'"), ("CompanyId", Q(CoB)), ("BranchId", bBranch), ("StockLocationId", bStore), ("SupplierId", Q(SupB)),
                ("CurrencyId", Q(UsdId)), ("ExchangeRateToBase", "3.6725"))}
            {Clone("PurchaseOrderLines", $"\"Id\" = {Q(FxPoLine)}", ("Id", Q(PoLineB)), ("PurchaseOrderId", Q(PoB)), ("ProductId", Q(G(0x211))), ("ProductUnitId", Q(UnitB)), ("UnitId", Q(Fx(1))),
                ("UnitCode", "'pc'"), ("TaxId", Q(TaxB5)), ("Qty", "5"), ("ConversionFactorToBase", "1"), ("BaseQty", "5"), ("Rate", "100"), ("TaxableValue", "500"), ("TaxAmount", "25"), ("LineTotal", "525"))}
            {Clone("PurchaseOrderLineTaxes", $"\"PurchaseOrderLineId\" = {Q(FxPoLine)}", ("Id", Q(PoLineBRow)), ("PurchaseOrderLineId", Q(PoLineB)))}
            {Clone("PurchaseReturns", $"\"Id\" = {Q(FxReturn)}", ("Id", Q(PrB)), ("ReturnNo", "'PRT-B001'"), ("PurchaseOrderId", Q(PoB)), ("CompanyId", Q(CoB)), ("BranchId", bBranch), ("StockLocationId", bStore))}
            {Clone("PurchaseReturnLines", $"\"Id\" = {Q(FxReturnLine)}", ("Id", Q(PrLineB)), ("PurchaseReturnId", Q(PrB)), ("PurchaseOrderLineId", Q(PoLineB)), ("ProductId", Q(G(0x211))), ("ProductUnitId", Q(UnitB)), ("UnitId", Q(Fx(1))))}
            {Clone("PurchaseReturnLineTaxes", $"\"PurchaseReturnLineId\" = {Q(FxReturnLine)}", ("Id", Q(PrLineBRow)), ("PurchaseReturnLineId", Q(PrLineB)))}

            -- IN purchase in USD (company A) with one legacy row
            {Clone("PurchaseOrders", $"\"Id\" = {Q(FxPo)}", ("Id", Q(PoUsdA)), ("PurchaseNo", "'PUR-A002'"), ("CurrencyId", Q(UsdId)), ("ExchangeRateToBase", "83.5"))}
            {Clone("PurchaseOrderLines", $"\"Id\" = {Q(FxPoLine)}", ("Id", Q(PoUsdALine)), ("PurchaseOrderId", Q(PoUsdA)))}
            {Clone("PurchaseOrderLineTaxes", $"\"PurchaseOrderLineId\" = {Q(FxPoLine)}", ("Id", Q(PoUsdARow)), ("PurchaseOrderLineId", Q(PoUsdALine)))}

            -- gap lines: saved by the TP-2 build after the TP-1 commit, no tax row
            {Clone("PurchaseOrderLines", $"\"Id\" = {Q(FxPoLine)}", ("Id", Q(GapPoLine)))}
            {Clone("PurchaseReturnLines", $"\"Id\" = {Q(FxReturnLine)}", ("Id", Q(GapPrLine)))}
            {Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(GapSalesLine)))}
            {Clone("SalesReturnLines", $"\"Id\" = {Q(Fx(101))}", ("Id", Q(GapSalesReturnLine)))}
            """;
    }

    public static readonly Guid[] GapLines = [GapPoLine, GapPrLine, GapSalesLine, GapSalesReturnLine];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TestDatabase? tp2Template;

    /// <summary>A private committed copy of the TP-2 shaped world (at <see cref="ValidateFks"/> with <see cref="Tp2ExtrasSql"/>). Dispose to drop.</summary>
    public static async Task<TestDatabase> Tp2ShapedCopyAsync(PostgresFixture postgres)
    {
        var template = await PreviousTemplateAsync(postgres);
        await Gate.WaitAsync();
        try
        {
            if (tp2Template is null)
            {
                var d = await CloneForExtrasAsync(postgres, template);
                await MigrateExactlyAsync(d, ValidateFks);
                await ExecuteAsync(d, Tp2ExtrasSql());
                NpgsqlConnection.ClearAllPools();
                tp2Template = d;
            }
        }
        finally { Gate.Release(); }
        return await CloneForExtrasAsync(postgres, tp2Template);
    }

    private static async Task<TestDatabase> CloneForExtrasAsync(PostgresFixture postgres, TestDatabase source)
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

    // ------------------------------------------------------------------------------------------------------------------------------------ a valid TP-3 document

    /// <summary>Ids of one hand-built TP-3 purchase and its return.</summary>
    public sealed record Doc(Guid Po, Guid Line, Guid Cgst, Guid Sgst, Guid Return, Guid ReturnLine, Guid ReturnCgst, Guid ReturnSgst);

    public static Doc DocAt(int n) => new(G(n), G(n + 1), G(n + 2), G(n + 3), G(n + 4), G(n + 5), G(n + 6), G(n + 7));

    /// <summary>
    /// Company A, intra-state GST 5 percent (the legacy profile of GST 5), one line of 5 Box (500 taxable, 25 tax = CGST 12.5 + SGST 12.5, total 525) and a return of 10 pc
    /// (100 taxable, 5 tax = 2.5 + 2.5, total 105). Every TP-3 column set, mirror TaxId = the profile's mirror, header sums equal the line, original = base currency.
    /// </summary>
    public static string ValidDocumentSql(Doc d, string purchaseNo, string returnNo) => $"""
        {Clone("PurchaseOrders", $"\"Id\" = {Q(FxPo)}", ("Id", Q(d.Po)), ("PurchaseNo", $"'{purchaseNo}'"), ("Subtotal", "500"), ("TaxTotal", "25"), ("GrandTotal", "525"), ("OriginalGrandTotal", "525"),
            ("TaxableTotal", "500"), ("PlaceOfSupply", "'IntraState'"), ("PlaceOfSupplyStateId", StateIn), ("SupplierStateId", StateIn))}
        {Clone("PurchaseOrderLines", $"\"Id\" = {Q(FxPoLine)}", ("Id", Q(d.Line)), ("PurchaseOrderId", Q(d.Po)), ("TaxProfileId", Q(ProfileA)), ("TaxProfileVersionId", Q(VersionA)),
            ("PlaceOfSupply", "'IntraState'"), ("HsnCode", "'123456'"), ("PriceIncludesTax", "false"), ("OriginalRate", "100"), ("OriginalDiscount", "0"), ("OriginalOtherCharges", "0"),
            ("OriginalBillDiscountShare", "0"), ("OriginalBillChargeShare", "0"), ("OriginalTaxableValue", "500"), ("OriginalTaxAmount", "25"), ("OriginalLineTotal", "525"))}
        {Row("PurchaseOrderLineTaxes", "PurchaseOrderLineId", d.Cgst, d.Line, "CGST", "Cgst", 2.5m, 500m, 12.5m)}
        {Row("PurchaseOrderLineTaxes", "PurchaseOrderLineId", d.Sgst, d.Line, "SGST", "Sgst", 2.5m, 500m, 12.5m)}
        {Clone("PurchaseReturns", $"\"Id\" = {Q(FxReturn)}", ("Id", Q(d.Return)), ("ReturnNo", $"'{returnNo}'"), ("PurchaseOrderId", Q(d.Po)), ("TaxableTotal", "100"), ("TaxTotal", "5"), ("Total", "105"), ("OriginalTotal", "105"))}
        {Clone("PurchaseReturnLines", $"\"Id\" = {Q(FxReturnLine)}", ("Id", Q(d.ReturnLine)), ("PurchaseReturnId", Q(d.Return)), ("PurchaseOrderLineId", Q(d.Line)), ("TaxableValue", "100"), ("TaxAmount", "5"), ("LineTotal", "105"),
            ("TaxProfileId", Q(ProfileA)), ("TaxProfileVersionId", Q(VersionA)), ("PlaceOfSupply", "'IntraState'"), ("OriginalTaxableValue", "100"), ("OriginalTaxAmount", "5"))}
        {Row("PurchaseReturnLineTaxes", "PurchaseReturnLineId", d.ReturnCgst, d.ReturnLine, "CGST", "Cgst", 2.5m, 100m, 2.5m)}
        {Row("PurchaseReturnLineTaxes", "PurchaseReturnLineId", d.ReturnSgst, d.ReturnLine, "SGST", "Sgst", 2.5m, 100m, 2.5m)}
        """;

    /// <summary>One calculator row of company A (component of the IN country, Taxable treatment).</summary>
    public static string Row(string table, string lineColumn, Guid id, Guid line, string code, string kind, decimal rate, decimal baseAmount, decimal tax) => $"""
        INSERT INTO "{table}" ("Id", "{lineColumn}", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount", "OriginalBaseAmount", "OriginalTaxAmount", "Treatment")
        VALUES ({Q(id)}, {Q(line)}, '{Component(CoA, "IN", code)}', '{code}', '{kind}', 'Percent', {rate.ToString(CultureInfo.InvariantCulture)}, NULL, {baseAmount.ToString(CultureInfo.InvariantCulture)}, {tax.ToString(CultureInfo.InvariantCulture)},
                {baseAmount.ToString(CultureInfo.InvariantCulture)}, {tax.ToString(CultureInfo.InvariantCulture)}, 'Taxable');
        """;
}
