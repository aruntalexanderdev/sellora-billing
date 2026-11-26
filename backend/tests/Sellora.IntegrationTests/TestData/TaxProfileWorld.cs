using System.Globalization;
using Npgsql;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Data.Migrations;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// The legacy dataset of the Tax Profiles M1 tests (docs/features/tax-profiles.md, TP-1 design "Migration M1"): the standard legacy fixture (<see cref="CompanyMigrations.FixtureSql"/>: lines of all four
/// kinds on the default company) plus eight more companies covering every branch of seed.sql / backfill.sql. It is written into the schema BEFORE <c>TaxM1</c>; the world is built once per run
/// (<see cref="PreviousTemplateAsync"/>), every test works on a file-level copy of it, and read-only tests share one migrated copy (<see cref="MigratedAsync"/>) and roll their probes back.
/// </summary>
public static class TaxProfileWorld
{
    public const string Previous = "20261119103652_AddCountryStateMasters";
    public const string TaxM1 = "20261124120429_AddTaxProfiles";
    public const string ValidateFks = "20261124120719_ValidateTaxLineForeignKeys";
    /// <summary>TP-3: the additive purchase tax snapshot (constraints NOT VALID) and the migration that validates them and creates the FK indexes. The latest migration of the tax programme.</summary>
    public const string PurchaseSnapshot = "20261125062951_AddPurchaseTaxSnapshot";
    public const string ValidatePurchase = "20261125063043_ValidatePurchaseTaxConstraints";
    /// <summary>TP-4: the additive sales tax snapshot (one short ACCESS EXCLUSIVE window, constraints NOT VALID) and the migration that validates them and creates the indexes. The latest migration of the tax programme.</summary>
    public const string SalesSnapshot = "20261125092815_AddSalesTaxSnapshot";
    public const string ValidateSales = "20261125092833_ValidateSalesTaxConstraints";
    /// <summary>TP-5a (M3a, three migrations, T35): AddReportSettings (Reports.Tax + grants, Units.Uqc + CK + UQC seed, BusinessSettings.HsnRequiredFrom; no line table); PrepareHistory (ONLY the lock on the four line tables and the pair to "version needs profile" swap, NOT VALID); ValidateHistory (validates it). The latest migrations of the tax programme.</summary>
    public const string AddReportSettings = "20261125113000_AddTaxReportSettings";
    public const string PrepareHistory = "20261125113341_PrepareTaxHistoryBackfill";
    public const string ValidateHistory = "20261125113403_ValidateTaxHistoryBackfill";
    public const string SeedResource = "20261124120429_AddTaxProfiles.seed.sql";
    public const string BackfillResource = "20261124120429_AddTaxProfiles.backfill.sql";
    public const string LegacyRowsResource = "20261124120429_AddTaxProfiles.legacy-rows.sql";

    /// <summary>TP-5a: the history profile check (WARN in 5a, block in 5b). Not in <see cref="ValidationFiles"/>: it reports every history line until the backfill script has run, so each test chooses when it applies.</summary>
    public const string HistoryProfileFile = "tax-history-profile.sql";

    /// <summary>The nine V__ files of the tax profiles design (six of TP-1, two added by TP-3, one by TP-4) that are zero on a migrated world before the backfill (name without the V__ prefix). Add <see cref="HistoryProfileFile"/> after the backfill.</summary>
    public static readonly string[] ValidationFiles =
    [
        "tax-profiles-integrity.sql", "tax-gst-rules.sql", "tax-products-resolve.sql", "tax-line-rows-consistency.sql", "tax-line-rows-missing.sql", "tax-mirror-drift.sql",
        "tax-line-rows-missing-purchases.sql", "tax-purchase-snapshot.sql", "tax-sales-snapshot.sql",
    ];

    /// <summary>Deterministic ids of the extra rows (the fixture's own rows use <see cref="Fx"/>).</summary>
    public static Guid G(int n) => new($"7a000000-0000-4000-8000-{n:x12}");

    /// <summary>The fixture ids written by <c>pg_temp.g(n)</c> in <see cref="CompanyMigrations.FixtureSql"/>.</summary>
    public static Guid Fx(int n) => new($"00000000-0000-0000-0000-{n:x12}");

    public static readonly Guid CoA = Guid.Parse(DefaultCompany);          // IN, the fixture company (taxes GST 5%, GST 18%, Zero GST, Old 12)
    public static readonly Guid CoB = G(0xB1);                             // AE: taxes Local VAT 5% and Zero 0%
    public static readonly Guid CoC = G(0xB2);                             // QA (no standard seed): Q10, Q3, Q0 and the inactive QX
    public static readonly Guid CoD = G(0xB3);                             // IN, no taxes (the "new India company" case)
    public static readonly Guid CoE = G(0xB4);                             // no business settings at all
    public static readonly Guid CoF = G(0xB5);                             // SA, no taxes
    public static readonly Guid CoG = G(0xB6);                             // BH, no taxes
    public static readonly Guid CoH = G(0xB7);                             // OM, no taxes
    public static readonly Guid CoI = G(0xB8);                             // IN, taxes Dup / DUP / dup (case-insensitive duplicates)

    /// <summary>Company, country code, one entry per company that has business settings.</summary>
    public static readonly (Guid Company, string Country)[] Countries =
        [(CoA, "IN"), (CoB, "AE"), (CoC, "QA"), (CoD, "IN"), (CoF, "SA"), (CoG, "BH"), (CoH, "OM"), (CoI, "IN")];

    public static readonly Guid TaxA5 = Fx(10), TaxA18 = G(0x101), TaxA0 = G(0x102), TaxAOld = G(0x103);
    public static readonly Guid TaxB5 = G(0x111), TaxB0 = G(0x112);
    public static readonly Guid TaxC10 = G(0x121), TaxC3 = G(0x122), TaxC0 = G(0x123), TaxCX = G(0x124);
    public static readonly Guid TaxIDup1 = G(0x131), TaxIDup2 = G(0x132), TaxIDup3 = G(0x133);

    /// <summary>A legacy tax of the world: its profile is expected to carry <see cref="ProfileName"/> (duplicates get ' (legacy <last 8 hex of the tax id>)').</summary>
    public sealed record WorldTax(Guid Id, Guid Company, string Country, string Name, decimal Rate, bool Active, string ProfileName)
    {
        public bool Taxable => Rate > 0;
    }

    public static readonly WorldTax[] Taxes =
    [
        new(TaxA5, CoA, "IN", "GST", 5m, true, "GST"),
        new(TaxA18, CoA, "IN", "GST 18%", 18m, true, "GST 18%"),
        new(TaxA0, CoA, "IN", "Zero GST", 0m, true, "Zero GST"),
        new(TaxAOld, CoA, "IN", "Old 12", 12m, false, "Old 12"),
        new(TaxB5, CoB, "AE", "Local VAT", 5m, true, "Local VAT"),
        new(TaxB0, CoB, "AE", "Zero", 0m, true, "Zero"),
        new(TaxC10, CoC, "QA", "Q10", 10m, true, "Q10"),
        new(TaxC3, CoC, "QA", "Q3", 3m, true, "Q3"),
        new(TaxC0, CoC, "QA", "Q0", 0m, true, "Q0"),
        new(TaxCX, CoC, "QA", "QX", 8m, false, "QX"),
        new(TaxIDup1, CoI, "IN", "Dup", 5m, true, "Dup"),
        new(TaxIDup2, CoI, "IN", "DUP", 5m, true, "DUP (legacy 00000132)"),
        new(TaxIDup3, CoI, "IN", "dup", 5m, true, "dup (legacy 00000133)"),
    ];

    /// <summary>Product id to the tax it uses (the two fixture products use GST 5%).</summary>
    public static readonly (Guid Product, Guid Company, Guid Tax)[] WorldProducts =
    [
        (Fx(20), CoA, TaxA5), (Fx(21), CoA, TaxA5), (G(0x201), CoA, TaxA18), (G(0x202), CoA, TaxA0), (G(0x203), CoA, TaxAOld),
        (G(0x211), CoB, TaxB5), (G(0x212), CoB, TaxB5), (G(0x213), CoB, TaxB0),
        (G(0x221), CoC, TaxC10), (G(0x222), CoC, TaxC10), (G(0x223), CoC, TaxC3), (G(0x224), CoC, TaxC0), (G(0x225), CoC, TaxCX), (G(0x226), CoC, TaxCX), (G(0x227), CoC, TaxCX),
        (G(0x231), CoI, TaxIDup1),
    ];

    /// <summary>The fixture lines (written by <see cref="CompanyMigrations.FixtureSql"/>): table, line table, id column, tax-row table, ids.</summary>
    public static readonly (string Lines, string TaxRows, string IdColumn, int[] Ids)[] LineKinds =
    [
        ("SalesInvoiceLines", "SalesInvoiceLineTaxes", "SalesInvoiceLineId", [90, 91, 92, 93, 94, 95]),
        ("PurchaseOrderLines", "PurchaseOrderLineTaxes", "PurchaseOrderLineId", [50, 51]),
        ("SalesReturnLines", "SalesReturnLineTaxes", "SalesReturnLineId", [101]),
        ("PurchaseReturnLines", "PurchaseReturnLineTaxes", "PurchaseReturnLineId", [71]),
    ];

    private static string Q(Guid id) => $"'{id}'";

    private static string WorldSql()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("""
            INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description") VALUES
              (md5('perm:Masters.View')::uuid, 'Masters.View', 'Masters', 'View', 'x'), (md5('perm:Inventory.Create')::uuid, 'Inventory.Create', 'Inventory', 'Create', 'x'),
              (md5('perm:Inventory.Edit')::uuid, 'Inventory.Edit', 'Inventory', 'Edit', 'x'), (md5('perm:Masters.Edit')::uuid, 'Masters.Edit', 'Masters', 'Edit', 'x'),
              (md5('perm:Sales.PriceOverride')::uuid, 'Sales.PriceOverride', 'Sales', 'PriceOverride', 'x'), (md5('perm:Sales.View')::uuid, 'Sales.View', 'Sales', 'View', 'x')
            ON CONFLICT ("Code") DO NOTHING;
            """);
        // Roles holding the permissions the grants key on (Role names are unique): R1 Masters.View, R2 Inventory.Create, R3 Masters.Edit, R4 Sales.PriceOverride, R5 Sales.View only (no grant).
        var roles = new[] { (1, "TaxR1", "Masters.View"), (2, "TaxR2", "Inventory.Create"), (3, "TaxR3", "Masters.Edit"), (4, "TaxR4", "Sales.PriceOverride"), (5, "TaxR5", "Sales.View") };
        foreach (var (n, name, permission) in roles)
        {
            sb.AppendLine($"""
                INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "IsActive", "IsSystem", "CreatedAt") VALUES ({Q(G(0x700 + n))}, '{DefaultCompany}', '{name}', NULL, true, false, now());
                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt") SELECT {Q(G(0x700 + n))}, "Id", now() FROM "Permissions" WHERE "Code" = '{permission}';
                """);
        }

        sb.AppendLine($"""
            INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES
              ({Q(CoB)}, 'TAXB', 'Tax B', true, now()), ({Q(CoC)}, 'TAXC', 'Tax C', true, now()), ({Q(CoD)}, 'TAXD', 'Tax D', true, now()), ({Q(CoE)}, 'TAXE', 'Tax E', true, now()),
              ({Q(CoF)}, 'TAXF', 'Tax F', true, now()), ({Q(CoG)}, 'TAXG', 'Tax G', true, now()), ({Q(CoH)}, 'TAXH', 'Tax H', true, now()), ({Q(CoI)}, 'TAXI', 'Tax I', true, now());
            """);
        for (var i = 0; i < Countries.Length; i++)
        {
            var (company, cc) = Countries[i];
            if (company == CoA)
            {
                // the fixture already holds the default company's settings row
                sb.AppendLine($"""UPDATE "BusinessSettings" SET "CountryId" = (SELECT "Id" FROM "Countries" WHERE "Code" = 'IN') WHERE "CompanyId" = {Q(CoA)};""");
                continue;
            }
            sb.AppendLine($"""
                INSERT INTO "BusinessSettings" ("Id", "CompanyId", "BusinessName", "InvoicePrefix", "BaseCurrencyId", "CountryId")
                VALUES ({Q(G(0x300 + i))}, {Q(company)}, 'Tax {cc}', 'INV', (SELECT "Id" FROM "Currencies" WHERE "Code" = 'INR'), (SELECT "Id" FROM "Countries" WHERE "Code" = '{cc}'));
                """);
        }

        // The fixture already holds GST 5% (TaxA5) on the default company; every other tax is written here with an explicit company.
        foreach (var t in Taxes.Where(t => t.Id != TaxA5))
            sb.AppendLine($"""INSERT INTO "Taxes" ("Id", "CompanyId", "Name", "RatePercent", "IsActive") VALUES ({Q(t.Id)}, {Q(t.Company)}, '{t.Name}', {t.Rate.ToString(CultureInfo.InvariantCulture)}, {(t.Active ? "true" : "false")});""");
        var sku = 0;
        foreach (var p in WorldProducts.Where(p => p.Product != Fx(20) && p.Product != Fx(21)))
            sb.AppendLine($"""
                INSERT INTO "Products" ("Id", "CompanyId", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive")
                VALUES ({Q(p.Product)}, {Q(p.Company)}, 'TX{++sku}', 'Tax product {sku}', NULL, {Q(Fx(1))}, {Q(p.Tax)}, 0, true);
                """);
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------------------------- database copies

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TestDatabase? previousTemplate;
    private static TestDatabase? migrated;

    /// <summary>The legacy world at <see cref="Previous"/>, built once. Never connect to it: it only serves as the source of <c>CREATE DATABASE … TEMPLATE</c>.</summary>
    public static async Task<TestDatabase> PreviousTemplateAsync(PostgresFixture postgres)
    {
        await Gate.WaitAsync();
        try
        {
            if (previousTemplate is not null) return previousTemplate;
            var d = await CreateAtAsync(postgres, Previous);
            await ExecuteAsync(d, WorldSql());
            NpgsqlConnection.ClearAllPools();
            previousTemplate = d;
            return d;
        }
        finally { Gate.Release(); }
    }

    /// <summary>A private copy of the legacy world still at <see cref="Previous"/> (the caller migrates it). Dispose to drop.</summary>
    public static async Task<TestDatabase> AtPreviousAsync(PostgresFixture postgres)
    {
        var template = await PreviousTemplateAsync(postgres);
        return await CloneAsync(postgres, template);
    }

    /// <summary>The legacy world migrated up to <see cref="ValidateHistory"/> (the head of the tax programme, history NOT backfilled), shared by read-only tests (probes must roll back). Not disposed by tests.</summary>
    public static async Task<TestDatabase> MigratedAsync(PostgresFixture postgres)
    {
        var template = await PreviousTemplateAsync(postgres);
        await Gate.WaitAsync();
        try
        {
            if (migrated is not null) return migrated;
            var d = await CloneAsync(postgres, template);
            await MigrateExactlyAsync(d, ValidateHistory);
            migrated = d;
            return d;
        }
        finally { Gate.Release(); }
    }

    /// <summary>A private, committed copy of the migrated world (for tests that must commit a probe).</summary>
    public static async Task<TestDatabase> MigratedCopyAsync(PostgresFixture postgres)
    {
        var shared = await MigratedAsync(postgres);
        return await CloneAsync(postgres, shared);
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
            catch (PostgresException ex) when (ex.SqlState == "55006" && attempt < 100)
            {
                // the source still lists a closing session
                await Task.Delay(100);
            }
        }
        return new TestDatabase(name, PostgresFixture.ConnectionStringFor(postgres.ConnectionString, name), postgres.ConnectionString);
    }

    // ------------------------------------------------------------------------------------------------------------------------------- probe sessions

    /// <summary>One connection with one open transaction that is ALWAYS rolled back: probes mutate freely and never leak into the shared database.</summary>
    public sealed class Probe(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        public async Task ExecAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>Every row as its values joined with '|' (NULL as "-", decimals invariant).</summary>
        public async Task<List<string>> RowsAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "-" : Format(reader.GetValue(i)))));
            return rows;
        }

        /// <summary>Runs a frozen embedded resource as the migration does (one command, one transaction: the probe's).</summary>
        public async Task RunResourceAsync(string resource)
        {
            // the files create ON COMMIT DROP temp tables: within one (probe) transaction a second run needs them gone
            await ExecAsync("DROP TABLE IF EXISTS _taxseed_company, _taxseed_component, _taxseed_profile, _taxseed_vc, _taxseed_todo, _taxfill_legacy, _taxfill_standard");
            await ExecAsync(MigrationSql.Read(resource));
        }

        /// <summary>Runs a V__ file statement by statement and returns every output row.</summary>
        public async Task<List<string>> ViolationsAsync(string file)
        {
            var rows = new List<string>();
            foreach (var (_, sql) in ValidationScripts.Statements(file)) rows.AddRange(await RowsAsync(sql));
            return rows;
        }
    }

    public static string Format(object value) => value switch
    {
        decimal m => m.ToString("0.######", CultureInfo.InvariantCulture),
        DateTime t => t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-",
    };

    public static async Task<T> InRolledBackTransactionAsync<T>(TestDatabase database, Func<Probe, Task<T>> body)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try { return await body(new Probe(connection, transaction)); }
        finally { await transaction.RollbackAsync(); }
    }

    public static async Task InRolledBackTransactionAsync(TestDatabase database, Func<Probe, Task> body) =>
        await InRolledBackTransactionAsync<int>(database, async p => { await body(p); return 0; });

    // ------------------------------------------------------------------------------------------------------------------------------- snapshots

    /// <summary>Everything AC16 says must not change: line values, header totals, Taxes, and every TaxId reference.</summary>
    public static readonly string[] UnchangedSql =
    [
        """SELECT 'SIL|' || "Id" || '|' || "TaxId" || '|' || "TaxRatePercent" || '|' || "TaxableValue" || '|' || "TaxAmount" || '|' || "LineTotal" FROM "SalesInvoiceLines" ORDER BY 1""",
        """SELECT 'POL|' || "Id" || '|' || "TaxId" || '|' || "TaxRatePercent" || '|' || "TaxableValue" || '|' || "TaxAmount" || '|' || "LineTotal" FROM "PurchaseOrderLines" ORDER BY 1""",
        """SELECT 'SRL|' || "Id" || '|' || "TaxableValue" || '|' || "TaxAmount" || '|' || "LineTotal" FROM "SalesReturnLines" ORDER BY 1""",
        """SELECT 'PRL|' || "Id" || '|' || "TaxableValue" || '|' || "TaxAmount" || '|' || "LineTotal" FROM "PurchaseReturnLines" ORDER BY 1""",
        """SELECT 'SI|' || "Id" || '|' || "Subtotal" || '|' || "TaxTotal" || '|' || "DiscountTotal" || '|' || "GrandTotal" FROM "SalesInvoices" ORDER BY 1""",
        """SELECT 'PO|' || "Id" || '|' || "Subtotal" || '|' || "TaxTotal" || '|' || "DiscountTotal" || '|' || "GrandTotal" FROM "PurchaseOrders" ORDER BY 1""",
        """SELECT 'SR|' || "Id" || '|' || "TaxableTotal" || '|' || "TaxTotal" || '|' || "Total" FROM "SalesReturns" ORDER BY 1""",
        """SELECT 'PR|' || "Id" || '|' || "TaxableTotal" || '|' || "TaxTotal" || '|' || "Total" FROM "PurchaseReturns" ORDER BY 1""",
        """SELECT 'TAX|' || "Id" || '|' || "CompanyId" || '|' || "Name" || '|' || "RatePercent" || '|' || "IsActive" FROM "Taxes" ORDER BY 1""",
        """SELECT 'PRD|' || "Id" || '|' || "TaxId" FROM "Products" ORDER BY 1""",
    ];

    public static async Task<List<string>> UnchangedSnapshotAsync(TestDatabase database)
    {
        var all = new List<string>();
        foreach (var sql in UnchangedSql) all.AddRange(await QueryAsync(database, sql, r => r.GetString(0)));
        return all;
    }

    /// <summary>Id-only fingerprint (timestamps differ between runs) of the tables TaxM1 fills, used for Down/Up and script-path equality.
    /// <paramref name="includeTaxTables"/> false (the schema before TaxM1) fingerprints only what exists there: the Taxes rows.</summary>
    public static async Task<List<string>> DataFingerprintAsync(TestDatabase database, bool includeTaxTables = true)
    {
        var rows = new List<string>();
        var tables = includeTaxTables
            ? new[]
            {
                "TaxComponents", "TaxProfiles", "TaxProfileVersions", "TaxProfileVersionComponents", "TaxAssignments",
                "SalesInvoiceLineTaxes", "PurchaseOrderLineTaxes", "SalesReturnLineTaxes", "PurchaseReturnLineTaxes", "Taxes",
            }
            : ["Taxes"];
        foreach (var table in tables)
        {
            rows.Add(table + "=" + await ScalarAsync<string>(database, $"""SELECT count(*) || ':' || COALESCE(md5(string_agg("Id"::text, ',' ORDER BY "Id"::text)), '-') FROM "{table}" """));
        }
        if (!includeTaxTables) return rows;
        rows.Add("settings=" + await ScalarAsync<string>(database, """SELECT COALESCE(string_agg("CompanyId"::text || ':' || "SellingPricesIncludeTax" || ':' || COALESCE("MaxBillDiscountPercent"::text, '-'), ',' ORDER BY "CompanyId"), '-') FROM "BusinessSettings" """));
        rows.Add("permissions=" + await ScalarAsync<string>(database, """SELECT COALESCE(string_agg(p."Code" || '>' || r."Name", ',' ORDER BY p."Code", r."Name"), '-') FROM "RolePermissions" rp JOIN "Permissions" p ON p."Id" = rp."PermissionId" JOIN "Roles" r ON r."Id" = rp."RoleId" WHERE p."Code" IN ('Tax.View', 'Tax.Manage', 'Sales.BillDiscount')"""));
        return rows;
    }
}
