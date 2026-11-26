using System.Globalization;
using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.SalesTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Data builders of the TP-5a history backfill tests (docs/features/tax-profiles.md, "TP-5 design", "History backfill (M3a)").
/// <see cref="Tp4ShapedCopyAsync"/> is the schema the TP-4 build runs on (<see cref="TaxProfileWorld.ValidateSales"/>, BEFORE AddTaxReportSettings, PrepareTaxHistoryBackfill and ValidateTaxHistoryBackfill) holding every kind of line the backfill meets: the legacy fixture and
/// company A (IN) / company B (AE) history lines with a TaxId and no profile, lines the TP-3 and TP-4 builds wrote (profile AND version), returns of both kinds (history returns, TP-4 returns, the rollback-era return the
/// TP-3 build makes on a TP-4 line), four "orphan" lines whose TaxId has no mirror profile (they can never be backfilled), standard units to carry a UQC and roles holding Reports.Sales.
/// <see cref="AddBulkAsync"/> adds thousands of history lines (random-looking ids, so keyset paging is exercised) for the batching, kill-and-resume and concurrency tests.
/// </summary>
public static class HistoryBackfillWorld
{
    public static readonly Guid TaxOrphan = G(0xF01);
    public static readonly Guid OrphanSalesLine = G(0xF02), OrphanSalesReturnLine = G(0xF03), OrphanPurchaseLine = G(0xF04), OrphanPurchaseReturnLine = G(0xF05);

    /// <summary>TP-4 sales documents (profile AND version on lines and returns), the purchase of the TP-3 build, and the rollback-era sale whose return the TP-3 build made.</summary>
    public static readonly SalesDoc Intra = SalesDocAt(0xF10), Incl = SalesDocAt(0xF20), Vat = SalesDocAt(0xF30), Rollback = SalesDocAt(0xF40);
    public static readonly Doc Purchase = DocAt(0xF50);

    /// <summary>Roles holding Reports.Sales (granted Reports.Tax by the migration) and one that does not.</summary>
    public static readonly Guid RoleSalesA = G(0xF71), RoleSalesB = G(0xF72), RoleNoSales = G(0xF73);

    /// <summary>Units added before the migration (code, expected UQC or null): standard codes in several spellings and two without a mapping.</summary>
    public static readonly (string Code, string? Uqc)[] UnitCodes =
    [
        ("PCS", "PCS"), ("Nos", "NOS"), ("KGS", "KGS"), ("Gm", "GMS"), ("LTR", "LTR"), ("L", "LTR"), ("Liter", "LTR"), ("Mtr", "MTR"),
        ("Dozen", "DOZ"), ("Pkt", "PAC"), ("Pack", "PAC"), ("CTN", "CTN"), ("Piece", "PCS"), ("weird", null), ("Quintal", null),
    ];

    private static Guid UnitId(int n) => G(0xF80 + n);
    public static Guid UnitOf(string code) => UnitId(Array.FindIndex(UnitCodes, u => u.Code == code));

    private static string Q(Guid id) => $"'{id}'";

    public static string ExtrasSql()
    {
        var sb = new System.Text.StringBuilder();
        // an unmappable tax: no mirror profile (written raw, as a database that skipped TP-1 handling would hold)
        sb.AppendLine($"""INSERT INTO "Taxes" ("Id", "CompanyId", "Name", "RatePercent", "IsActive") VALUES ({Q(TaxOrphan)}, '{DefaultCompany}', 'Orphan', 7, true);""");
        sb.AppendLine(Clone("SalesInvoiceLines", $"\"Id\" = {Q(Fx(94))}", ("Id", Q(OrphanSalesLine)), ("TaxId", Q(TaxOrphan))));
        sb.AppendLine(Clone("SalesReturnLines", $"\"Id\" = {Q(Fx(101))}", ("Id", Q(OrphanSalesReturnLine)), ("SalesInvoiceLineId", Q(OrphanSalesLine))));
        sb.AppendLine(Clone("PurchaseOrderLines", $"\"Id\" = {Q(FxPoLine)}", ("Id", Q(OrphanPurchaseLine)), ("TaxId", Q(TaxOrphan))));
        sb.AppendLine(Clone("PurchaseReturnLines", $"\"Id\" = {Q(FxReturnLine)}", ("Id", Q(OrphanPurchaseReturnLine)), ("PurchaseOrderLineId", Q(OrphanPurchaseLine))));

        // documents of the TP-3 / TP-4 builds
        sb.AppendLine(ValidSalesSql(Intra, "SAL-BF1", "SRT-BF1"));
        sb.AppendLine(ValidInclusiveSql(Incl, "SAL-BF2", "SRT-BF2A", "SRT-BF2B"));
        sb.AppendLine(ValidVatSql(Vat, "SAL-BFB1", "SRT-BFB1"));
        sb.AppendLine(ValidSalesSql(Rollback, "SAL-BF3", "SRT-BF3"));
        sb.AppendLine(ToTp3BuildReturnSql(Rollback));
        sb.AppendLine(ValidDocumentSql(Purchase, "PO-BF1", "PRT-BF1"));

        // Reports.Sales holders
        sb.AppendLine("""
            INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES
              (md5('perm:Reports.Sales')::uuid, 'Reports.Sales', 'Reports', 'View', 'x'), (md5('perm:Sales.View')::uuid, 'Sales.View', 'Sales', 'View', 'x')
            ON CONFLICT ("Code") DO NOTHING;
            """);
        foreach (var (role, company, name, permission) in new[] { (RoleSalesA, DefaultCompany, "BfSalesA", "Reports.Sales"), (RoleSalesB, CoB.ToString(), "BfSalesB", "Reports.Sales"), (RoleNoSales, DefaultCompany, "BfNoSales", "Sales.View") })
        {
            sb.AppendLine($"""
                INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "IsActive", "IsSystem", "CreatedAt") VALUES ({Q(role)}, '{company}', '{name}', NULL, true, false, now());
                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt") SELECT {Q(role)}, "Id", now() FROM "Permissions" WHERE "Code" = '{permission}';
                """);
        }

        for (var i = 0; i < UnitCodes.Length; i++)
            sb.AppendLine(Clone("Units", $"\"Id\" = {Q(Fx(1))}", ("Id", Q(UnitId(i))), ("ShortCode", $"'{UnitCodes[i].Code}'"), ("Name", $"'Unit {UnitCodes[i].Code}'")));
        return sb.ToString();
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TestDatabase? template;

    /// <summary>A private committed copy of the TP-4 shaped world (see the class summary). Dispose to drop.</summary>
    public static async Task<TestDatabase> Tp4ShapedCopyAsync(PostgresFixture postgres)
    {
        await Gate.WaitAsync();
        try
        {
            if (template is null)
            {
                var d = await Tp3ShapedCopyAsync(postgres);
                await MigrateExactlyAsync(d, ValidateSales);
                await ExecuteAsync(d, ExtrasSql());
                NpgsqlConnection.ClearAllPools();
                template = d;
            }
        }
        finally { Gate.Release(); }
        return await SalesTaxWorld.CloneAsync(postgres, template);
    }

    /// <summary>The TP-4 shaped world migrated by <see cref="AddReportSettings"/>, <see cref="PrepareHistory"/> and <see cref="ValidateHistory"/>: the starting point of the data script tests.</summary>
    public static async Task<TestDatabase> AtHeadCopyAsync(PostgresFixture postgres)
    {
        var d = await Tp4ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, ValidateHistory);
        return d;
    }

    // ------------------------------------------------------------------------------------------------------------------------------ bulk history

    private static string Bulk(string table, Guid source, string idPrefix, int count, params (string Column, string Expr)[] set)
    {
        var assignments = string.Join(", ", new (string Column, string Expr)[] { ("Id", $"md5('{idPrefix}' || _i)::uuid") }.Concat(set).Select(s => $"\"{s.Column}\" = {s.Expr}"));
        return $"""
            CREATE TEMP TABLE _bulk AS SELECT t.*, g.i AS _i FROM "{table}" t CROSS JOIN generate_series(1, {count.ToString(CultureInfo.InvariantCulture)}) g(i) WHERE t."Id" = {Q(source)};
            UPDATE _bulk SET {assignments};
            ALTER TABLE _bulk DROP COLUMN _i;
            INSERT INTO "{table}" SELECT * FROM _bulk;
            DROP TABLE _bulk;
            """;
    }

    /// <summary>
    /// History written like the TP-1..TP-3 builds did, in volume: <paramref name="sales"/> sales lines (four fifths company A with four of its taxes, one fifth company B), about a quarter of that many sales return lines,
    /// half as many purchase lines and an eighth as many purchase return lines. Every tenth company A sales line is a TP-4 line (profile and version of GST 5 percent): the backfill must not touch it. No other line has a profile.
    /// Ids are md5 based (bf-sil-n, bf-silb-n, bf-srl-n, bf-pol-n, bf-prl-n): the key order is unrelated to the insert order.
    /// </summary>
    public static async Task AddBulkAsync(TestDatabase d, int sales)
    {
        var a = $"(CASE _i % 4 WHEN 0 THEN {Q(TaxA5)} WHEN 1 THEN {Q(TaxA18)} WHEN 2 THEN {Q(TaxA0)} ELSE {Q(TaxAOld)} END)::uuid";
        var nA = sales - sales / 5;
        var nB = sales / 5;
        var returns = Math.Max(1, nA / 4 - 1);
        var purchases = sales / 2;
        var purchaseReturns = Math.Max(1, purchases / 4 - 1);
        var sql = new System.Text.StringBuilder();
        sql.AppendLine(Bulk("SalesInvoiceLines", Fx(94), "bf-sil-", nA, ("TaxId", $"(CASE WHEN _i % 10 = 0 THEN {Q(TaxA5)}::uuid ELSE {a} END)"),
            ("TaxProfileId", $"(CASE WHEN _i % 10 = 0 THEN {Q(ProfileA)}::uuid END)"), ("TaxProfileVersionId", $"(CASE WHEN _i % 10 = 0 THEN {Q(VersionA)}::uuid END)")));
        sql.AppendLine(Bulk("SalesInvoiceLines", SiBLine, "bf-silb-", nB, ("SalesInvoiceId", Q(SiB))));
        // returns of company A lines that are not TP-4 lines (index 4i or 4i+1, never a multiple of 10)
        sql.AppendLine(Bulk("SalesReturnLines", Fx(101), "bf-srl-", returns, ("SalesInvoiceLineId", "md5('bf-sil-' || (CASE WHEN (_i * 4) % 10 = 0 THEN _i * 4 + 1 ELSE _i * 4 END))::uuid")));
        sql.AppendLine(Bulk("PurchaseOrderLines", FxPoLine, "bf-pol-", purchases, ("TaxId", a)));
        sql.AppendLine(Bulk("PurchaseReturnLines", FxReturnLine, "bf-prl-", purchaseReturns, ("PurchaseOrderLineId", "md5('bf-pol-' || (_i * 4))::uuid")));
        await ExecuteAsync(d, sql.ToString());
    }

    public static readonly string[] LineTables = ["SalesInvoiceLines", "PurchaseOrderLines", "SalesReturnLines", "PurchaseReturnLines"];

    /// <summary>Total lines, lines with a version (written by the TP-3/TP-4 builds), history lines with a profile only, and lines without a profile.</summary>
    public sealed record Counts(long Total, long WithVersion, long ProfileOnly, long WithoutProfile);

    public static async Task<Dictionary<string, Counts>> CountsAsync(TestDatabase d)
    {
        var result = new Dictionary<string, Counts>();
        foreach (var table in LineTables)
        {
            var row = (await QueryAsync(d, $"""
                SELECT count(*), count(*) FILTER (WHERE "TaxProfileVersionId" IS NOT NULL), count(*) FILTER (WHERE "TaxProfileId" IS NOT NULL AND "TaxProfileVersionId" IS NULL), count(*) FILTER (WHERE "TaxProfileId" IS NULL)
                FROM "{table}"
                """, r => new Counts(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)))).Single();
            result[table] = row;
        }
        return result;
    }

    /// <summary>Id, profile and version of every line of one table (optionally filtered), as one fingerprint.</summary>
    public static Task<string> FingerprintAsync(TestDatabase d, string table, string? where = null) => ScalarAsync<string>(d, $"""
        SELECT count(*) || ':' || COALESCE(md5(string_agg("Id"::text || '|' || COALESCE("TaxProfileId"::text, '-') || '|' || COALESCE("TaxProfileVersionId"::text, '-'), ',' ORDER BY "Id"::text)), '-')
        FROM "{table}" {(where is null ? "" : "WHERE " + where)}
        """);
}
