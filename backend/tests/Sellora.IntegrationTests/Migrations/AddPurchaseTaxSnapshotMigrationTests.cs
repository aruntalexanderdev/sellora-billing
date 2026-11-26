using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Data.Migrations;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.PurchaseTaxWorld;
using static Sellora.IntegrationTests.TestData.TaxProfileWorld;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// <c>AddPurchaseTaxSnapshot</c> + <c>ValidatePurchaseTaxConstraints</c> (docs/features/tax-profiles.md, "TP-3 design", Migration): purely additive, no table rewrite, the D2 catch-up of lines the TP-2 build saved without a tax row,
/// every constraint NOT VALID first and validated by the second migration, the old build still inserting, lock_timeout fail-fast, Down/Up and the idempotent script. All on <see cref="PurchaseTaxWorld.Tp2ShapedCopyAsync"/>:
/// the legacy tax world at the TP-1 head (the schema the TP-2 build runs on) with IN and AE purchases (foreign currency, returns), sales and one gap line of each of the four kinds.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddPurchaseTaxSnapshotMigrationTests(PostgresFixture postgres)
{
    private static readonly string[] LineTaxTables = ["PurchaseOrderLineTaxes", "PurchaseReturnLineTaxes", "SalesReturnLineTaxes", "SalesInvoiceLineTaxes"];
    private static readonly string[] RewriteWatch = ["PurchaseOrders", "PurchaseOrderLines", "PurchaseReturnLines", .. LineTaxTables];

    private static readonly string[] OrderColumns =
        ["RatesIncludeTax", "BillDiscountPercent", "BillDiscountAmount", "BillCharges", "OriginalBillDiscountAmount", "OriginalBillCharges", "TaxableTotal", "PlaceOfSupply", "PlaceOfSupplyStateId", "SupplierStateId", "PartyTaxStatus"];
    private static readonly string[] LineColumns =
        ["TaxProfileId", "TaxProfileVersionId", "PlaceOfSupply", "HsnCode", "PriceIncludesTax", "BillDiscountShare", "BillChargeShare", "OriginalRate", "OriginalDiscount", "OriginalOtherCharges",
         "OriginalBillDiscountShare", "OriginalBillChargeShare", "OriginalTaxableValue", "OriginalTaxAmount", "OriginalLineTotal"];
    private static readonly string[] ReturnLineColumns = ["TaxProfileId", "TaxProfileVersionId", "PlaceOfSupply", "OriginalTaxableValue", "OriginalTaxAmount"];

    /// <summary>Constraint to table, in the order the migration adds them.</summary>
    private static readonly (string Table, string Name)[] Constraints =
    [
        ("PurchaseReturnLines", "CK_PurchaseReturnLines_PlaceOfSupply"), ("PurchaseReturnLines", "CK_PurchaseReturnLines_TaxProfile_Pair"),
        ("PurchaseReturnLines", "FK_PurchaseReturnLines_TaxProfiles_TaxProfileId"), ("PurchaseReturnLines", "FK_PurchaseReturnLines_TaxProfileVersions_TaxProfileVersionId"),
        ("PurchaseOrderLines", "CK_PurchaseOrderLines_PlaceOfSupply"), ("PurchaseOrderLines", "CK_PurchaseOrderLines_TaxProfile_Pair"), ("PurchaseOrderLines", "CK_PurchaseOrderLines_HsnCode_Format"),
        ("PurchaseOrderLines", "CK_PurchaseOrderLines_BillShares_NonNegative"), ("PurchaseOrderLines", "FK_PurchaseOrderLines_TaxProfiles_TaxProfileId"),
        ("PurchaseOrderLines", "FK_PurchaseOrderLines_TaxProfileVersions_TaxProfileVersionId"),
        ("PurchaseOrders", "CK_PurchaseOrders_PlaceOfSupply"), ("PurchaseOrders", "CK_PurchaseOrders_PartyTaxStatus"), ("PurchaseOrders", "CK_PurchaseOrders_BillDiscountPercent_Range"),
        ("PurchaseOrders", "CK_PurchaseOrders_BillAmounts_NonNegative"), ("PurchaseOrders", "FK_PurchaseOrders_States_PlaceOfSupplyStateId"), ("PurchaseOrders", "FK_PurchaseOrders_States_SupplierStateId"),
        ("PurchaseOrderLineTaxes", "CK_PurchaseOrderLineTaxes_Treatment"), ("PurchaseReturnLineTaxes", "CK_PurchaseReturnLineTaxes_Treatment"),
        ("SalesReturnLineTaxes", "CK_SalesReturnLineTaxes_Treatment"), ("SalesInvoiceLineTaxes", "CK_SalesInvoiceLineTaxes_Treatment"),
    ];

    private static readonly string[] Indexes =
    [
        "IX_PurchaseReturnLines_TaxProfileId", "IX_PurchaseReturnLines_TaxProfileVersionId", "IX_PurchaseOrderLines_TaxProfileId", "IX_PurchaseOrderLines_TaxProfileVersionId",
        "IX_PurchaseOrders_PlaceOfSupplyStateId", "IX_PurchaseOrders_SupplierStateId",
    ];

    private static Exception Root(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is PostgresException || e.InnerException is null) return e;
        return ex;
    }

    private static async Task<List<string>> RowsAsync(TestDatabase d, string sql) =>
        await QueryAsync(d, sql, r => string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "-" : Format(r.GetValue(i)))));

    private static string InList(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    private static Task<List<string>> FilenodesAsync(TestDatabase d) =>
        RowsAsync(d, $"""SELECT relname || '=' || relfilenode::text FROM pg_class WHERE relkind = 'r' AND relnamespace = 'public'::regnamespace AND relname IN ({InList(RewriteWatch)}) ORDER BY 1""");

    private static async Task<Dictionary<string, bool>> ConstraintStateAsync(TestDatabase d) =>
        (await QueryAsync(d, $"""SELECT conname, convalidated FROM pg_constraint WHERE conname IN ({InList(Constraints.Select(c => c.Name))})""", r => (r.GetString(0), r.GetBoolean(1)))).ToDictionary(x => x.Item1, x => x.Item2);

    private static Task<List<string>> NewColumnsAsync(TestDatabase d) => QueryAsync(d, $"""
        SELECT table_name || '.' || column_name FROM information_schema.columns
        WHERE table_schema = 'public' AND (
          (table_name = 'PurchaseOrders' AND column_name IN ({InList(OrderColumns)})) OR (table_name = 'PurchaseOrderLines' AND column_name IN ({InList(LineColumns)}))
          OR (table_name = 'PurchaseReturnLines' AND column_name IN ({InList(ReturnLineColumns)})) OR (table_name IN ({InList(LineTaxTables)}) AND column_name = 'Treatment'))
        ORDER BY 1
        """, r => r.GetString(0));

    private static Task<List<string>> LineTaxCountsAsync(TestDatabase d) => RowsAsync(d,
        string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}=' || count(*) FROM "{t}" """)) + " ORDER BY 1");

    /// <summary>Gap line to its tax rows: line id, table of the rows, count.</summary>
    private static readonly (Guid Line, string Lines, string Rows, string Column)[] Gaps =
    [
        (GapPoLine, "PurchaseOrderLines", "PurchaseOrderLineTaxes", "PurchaseOrderLineId"), (GapPrLine, "PurchaseReturnLines", "PurchaseReturnLineTaxes", "PurchaseReturnLineId"),
        (GapSalesLine, "SalesInvoiceLines", "SalesInvoiceLineTaxes", "SalesInvoiceLineId"), (GapSalesReturnLine, "SalesReturnLines", "SalesReturnLineTaxes", "SalesReturnLineId"),
    ];

    // ------------------------------------------------------------------------------------------------------------------------------ the starting point

    [Fact]
    public async Task Tp2ShapedWorld_HasTheDocumentsTheMigrationTestsRelyOn()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);

        Assert.DoesNotContain(PurchaseSnapshot, await AppliedAsync(d));
        Assert.Contains(ValidateFks, await AppliedAsync(d));
        Assert.Equal(0L, (await NewColumnsAsync(d)).Count);
        Assert.Equal(new[] { "IN|PUR-A002|USD", "AE|PUR-B001|USD" }.Order().ToList(), (await RowsAsync(d, $"""
            SELECT c."Code" || '|' || o."PurchaseNo" || '|' || cur."Code" FROM "PurchaseOrders" o JOIN "Currencies" cur ON cur."Id" = o."CurrencyId" JOIN "BusinessSettings" b ON b."CompanyId" = o."CompanyId"
            JOIN "Countries" c ON c."Id" = b."CountryId" WHERE cur."Code" = 'USD'
            """)).Order().ToList());
        foreach (var (line, lines, rows, column) in Gaps)
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{rows}" WHERE "{column}" = '{line}'"""));   // the gap
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "PurchaseReturns" WHERE "Id" = '{PrB}'"""));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ defaults and no rewrite

    [Fact]
    public async Task Up_HistoryKeepsItsValues_DefaultsAreConstant_AndEveryNullableColumnIsNull()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        var before = await UnchangedSnapshotAsync(d);
        var orders = await ScalarAsync<long>(d, """SELECT count(*) FROM "PurchaseOrders" """);
        var lines = await ScalarAsync<long>(d, """SELECT count(*) FROM "PurchaseOrderLines" """);
        var returnLines = await ScalarAsync<long>(d, """SELECT count(*) FROM "PurchaseReturnLines" """);
        Assert.True(orders >= 6 && lines >= 5 && returnLines >= 3, $"{orders}/{lines}/{returnLines}");

        await MigrateExactlyAsync(d, ValidatePurchase);

        Assert.Equal(before, await UnchangedSnapshotAsync(d));   // money columns, mirrors and TaxIds of history are untouched
        Assert.Equal(orders, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "PurchaseOrders" WHERE NOT "RatesIncludeTax" AND "BillDiscountAmount" = 0 AND "BillCharges" = 0 AND "OriginalBillDiscountAmount" = 0 AND "OriginalBillCharges" = 0
              AND "PartyTaxStatus" = 'Unspecified' AND "BillDiscountPercent" IS NULL AND "TaxableTotal" IS NULL AND "PlaceOfSupply" IS NULL AND "PlaceOfSupplyStateId" IS NULL AND "SupplierStateId" IS NULL
            """));
        Assert.Equal(lines, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "PurchaseOrderLines" WHERE NOT "PriceIncludesTax" AND "BillDiscountShare" = 0 AND "BillChargeShare" = 0 AND "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NULL
              AND "PlaceOfSupply" IS NULL AND "HsnCode" IS NULL AND "OriginalRate" IS NULL AND "OriginalDiscount" IS NULL AND "OriginalOtherCharges" IS NULL AND "OriginalBillDiscountShare" IS NULL
              AND "OriginalBillChargeShare" IS NULL AND "OriginalTaxableValue" IS NULL AND "OriginalTaxAmount" IS NULL AND "OriginalLineTotal" IS NULL
            """));
        Assert.Equal(returnLines, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "PurchaseReturnLines" WHERE "TaxProfileId" IS NULL AND "TaxProfileVersionId" IS NULL AND "PlaceOfSupply" IS NULL AND "OriginalTaxableValue" IS NULL AND "OriginalTaxAmount" IS NULL
            """));
        foreach (var table in LineTaxTables)
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{table}" WHERE "Treatment" IS NOT NULL"""));
        Assert.Equal(OrderColumns.Length + LineColumns.Length + ReturnLineColumns.Length + 4, (await NewColumnsAsync(d)).Count);
    }

    [Fact]
    public async Task Up_RewritesNoTable_NeitherTheFirstMigrationNorTheSecond()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        var before = await FilenodesAsync(d);
        Assert.Equal(RewriteWatch.Length, before.Count);

        await MigrateExactlyAsync(d, PurchaseSnapshot);
        var afterFirst = await FilenodesAsync(d);
        await MigrateExactlyAsync(d, ValidatePurchase);
        var afterSecond = await FilenodesAsync(d);

        Assert.Equal(before, afterFirst);    // every column is nullable or has a constant default: ADD COLUMN is metadata only; the catch-up only INSERTs
        Assert.Equal(before, afterSecond);   // VALIDATE CONSTRAINT and CREATE INDEX do not rewrite the table
    }

    // ------------------------------------------------------------------------------------------------------------------------------ catch-up (D2)

    [Fact]
    public async Task Up_CatchUp_InsertsExactlyOneLegacyRowPerGapLine_OfAllFourKinds_AndNothingElse()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        var before = await LineTaxCountsAsync(d);
        var existing = await RowsAsync(d, string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)));

        await MigrateExactlyAsync(d, PurchaseSnapshot);

        var after = await LineTaxCountsAsync(d);
        Assert.Equal(before.Select(b => b.Split('=')[0] + "=" + (long.Parse(b.Split('=')[1]) + 1)), after);   // exactly +1 in each table: one gap line each
        foreach (var (line, lines, rows, column) in Gaps)
        {
            var row = await RowsAsync(d, $"""
                SELECT x."Id" || '|' || COALESCE(x."TaxComponentId"::text, '-') || '|' || x."Kind" || '|' || x."Method" || '|' || x."RatePercent" || '|' || x."BaseAmount" || '|' || x."TaxAmount" || '|' || COALESCE(x."Treatment", '-') || '|' || x."ComponentCode"
                FROM "{rows}" x WHERE x."{column}" = '{line}'
                """);
            Assert.Single(row);
            var f = row[0].Split('|');
            Assert.Equal(TaxSeedData.LegacyLineTaxId(line).ToString(), f[0]);     // the id the write paths use: an idempotent catch-up can never duplicate
            Assert.Equal(["-", "Other", "Percent"], f[1..4]);                    // legacy row: no component, Kind Other
            Assert.Equal("-", f[7]);                                            // no Treatment: the catch-up runs before the column exists and the column is NULL for legacy rows
        }
        // the lines themselves are untouched and the rows that existed are the same
        var nowRows = await RowsAsync(d, string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)));
        Assert.Empty(existing.Except(nowRows));
        Assert.Equal(4, nowRows.Except(existing).Count());
    }

    [Fact]
    public async Task Up_CatchUp_ALegacyRowCarriesTheLineValues_AndAReturnLineTheOriginalsTax()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(d, PurchaseSnapshot);

        Assert.Equal(["5|500|25|GST"], await RowsAsync(d, $"""SELECT x."RatePercent", x."BaseAmount", x."TaxAmount", x."ComponentCode" FROM "PurchaseOrderLineTaxes" x WHERE x."PurchaseOrderLineId" = '{GapPoLine}'"""));
        Assert.Equal(["5|0|0|GST"], await RowsAsync(d, $"""SELECT x."RatePercent", x."BaseAmount", x."TaxAmount", x."ComponentCode" FROM "PurchaseReturnLineTaxes" x WHERE x."PurchaseReturnLineId" = '{GapPrLine}'"""));
    }

    [Fact]
    public async Task Up_RerunningTheLegacyRowsFile_IsANoOp_AndItsShaIsUnchanged()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, ValidatePurchase);
        var counts = await LineTaxCountsAsync(d);
        var ids = await RowsAsync(d, string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)));

        await InRolledBackTransactionAsync(d, async p =>
        {
            await p.RunResourceAsync(LegacyRowsResource);
            Assert.Equal(counts, await p.RowsAsync(string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}=' || count(*) FROM "{t}" """)) + " ORDER BY 1"));
            Assert.Equal(ids.Order().ToList(), (await p.RowsAsync(string.Join(" UNION ALL ", LineTaxTables.Select(t => $"""SELECT '{t}|' || "Id" FROM "{t}" """)))).Order().ToList());
        });
        Assert.Equal(counts, await LineTaxCountsAsync(d));

        // the frozen resource is unchanged (same pin as backend/tests/Sellora.Tests/TaxProfiles/TaxMigrationSqlTests) and the migration runs exactly that text
        const string pin = "284ec03227940bea0c54bb137d0f4a85e21c2c2118233ed3a5a9643967068afe";
        var embedded = MigrationSql.Read(LegacyRowsResource);
        Assert.Equal(pin, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(embedded))).ToLowerInvariant());
    }

    [Fact]
    public async Task Up_CatchUp_RunsBeforeTheTreatmentColumnExists_SoItWorksOnTheTp2Schema()
    {
        // AddPurchaseTaxSnapshot starts with the frozen file: a database where Treatment cannot exist yet. Prove the file runs on the TP-2 schema as is.
        await using var d = await Tp2ShapedCopyAsync(postgres);
        Assert.Equal(0L, await ScalarAsync<long>(d, """SELECT count(*) FROM information_schema.columns WHERE column_name = 'Treatment' AND table_name LIKE '%LineTaxes'"""));

        var gapRowIds = string.Join(", ", Gaps.Select(g => $"'{TaxSeedData.LegacyLineTaxId(g.Line)}'"));
        var inserted = await InRolledBackTransactionAsync(d, async p =>
        {
            await p.RunResourceAsync(LegacyRowsResource);
            long total = 0;
            foreach (var table in LineTaxTables) total += await p.ScalarAsync<long>($"""SELECT count(*) FROM "{table}" WHERE "Id" IN ({gapRowIds})""");
            return total;
        });
        Assert.Equal(4L, inserted);
    }

    // ------------------------------------------------------------------------------------------------------------------------------ NOT VALID then VALIDATE

    [Fact]
    public async Task Constraints_AreNotValidAfterTheFirstMigration_AndValidatedAfterTheSecond_WithTheSixForeignKeyIndexes()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(d, PurchaseSnapshot);

        var first = await ConstraintStateAsync(d);
        Assert.Equal(Constraints.Length, first.Count);
        Assert.All(first, kv => Assert.False(kv.Value, kv.Key + " must be NOT VALID after AddPurchaseTaxSnapshot"));
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(Indexes)})"""));
        // not validated does not mean not enforced: a new row violating one is refused
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, $"""UPDATE "PurchaseOrders" SET "PlaceOfSupply" = 'Nowhere' WHERE "Id" = '{PoB}'"""));
        Assert.Equal(("23514", "CK_PurchaseOrders_PlaceOfSupply"), (ex.SqlState, ex.ConstraintName));

        await MigrateExactlyAsync(d, ValidatePurchase);

        var second = await ConstraintStateAsync(d);
        Assert.Equal(Constraints.Length, second.Count);
        Assert.All(second, kv => Assert.True(kv.Value, kv.Key + " must be validated after ValidatePurchaseTaxConstraints"));
        Assert.Equal(Indexes.Order().ToList(), (await RowsAsync(d, $"""SELECT indexname FROM pg_indexes WHERE indexname IN ({InList(Indexes)}) ORDER BY 1""")).ToList());
        foreach (var (table, name) in Constraints)
            Assert.Equal(1L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE c.conname = '{name}' AND t.relname = '{table}'"""));
    }

    [Fact]
    public async Task Constraints_Foreign_AreRestrict_AndEveryCheckTargetsTheDesignedValues()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, ValidatePurchase);

        var fks = await RowsAsync(d, """
            SELECT conname || '|' || confdeltype::text FROM pg_constraint WHERE conname LIKE 'FK\_PurchaseOrders\_States\_%' OR conname LIKE 'FK\_PurchaseOrderLines\_TaxProfile%' OR conname LIKE 'FK\_PurchaseReturnLines\_TaxProfile%' ORDER BY 1
            """);

        Assert.Equal(6, fks.Count);
        Assert.All(fks, f => Assert.EndsWith("|r", f, StringComparison.Ordinal));   // history never loses its profile, version or state
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the old build keeps working

    private static readonly (string Table, string Where, (string Column, string Expr)[] Set)[] OldInserts =
    [
        ("PurchaseOrders", $"\"Id\" = '{PoB}'", [("Id", "gen_random_uuid()"), ("PurchaseNo", "'PUR-OLD1'")]),
        ("PurchaseOrderLines", $"\"Id\" = '{PoLineB}'", [("Id", "gen_random_uuid()")]),
        ("PurchaseReturnLines", $"\"Id\" = '{PrLineB}'", [("Id", "gen_random_uuid()")]),
        ("PurchaseOrderLineTaxes", $"\"PurchaseOrderLineId\" = '{PoLineB}'", [("Id", "gen_random_uuid()")]),
        ("PurchaseReturnLineTaxes", $"\"PurchaseReturnLineId\" = '{PrLineB}'", [("Id", "gen_random_uuid()")]),
        ("SalesInvoiceLineTaxes", $"\"SalesInvoiceLineId\" = '{Fx(90)}'", [("Id", "gen_random_uuid()")]),
        ("SalesReturnLineTaxes", $"\"SalesReturnLineId\" = '{Fx(101)}'", [("Id", "gen_random_uuid()")]),
    ];

    private static async Task<Dictionary<string, string[]>> Tp2ColumnsAsync(TestDatabase d)
    {
        var map = new Dictionary<string, string[]>();
        foreach (var (table, _, _) in OldInserts)
            map[table] = (await QueryAsync(d, $"""SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' ORDER BY ordinal_position""", r => r.GetString(0))).ToArray();
        return map;
    }

    /// <summary>The TP-2 build's INSERT: only the columns that exist on the TP-2 schema (no new column named), values copied from an existing row.</summary>
    private static string OldBuildInsert(string table, string[] oldColumns, string where, (string Column, string Expr)[] set, string temp)
    {
        var cols = string.Join(", ", oldColumns.Select(c => $"\"{c}\""));
        var assignments = string.Join(", ", set.Select(s => $"\"{s.Column}\" = {s.Expr}"));
        return $"""
            CREATE TEMP TABLE {temp} AS SELECT {cols} FROM "{table}" WHERE {where};
            UPDATE {temp} SET {assignments};
            INSERT INTO "{table}" ({cols}) SELECT {cols} FROM {temp};
            DROP TABLE {temp};
            """;
    }

    [Theory]
    [InlineData(PurchaseSnapshot)]
    [InlineData(ValidatePurchase)]
    public async Task OldBuild_InsertingWithoutTheNewColumns_StillSucceeds_AndGetsTheDefaults(string migrateTo)
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        var columns = await Tp2ColumnsAsync(d);   // read BEFORE the migration: exactly the columns the TP-2 build writes
        await MigrateExactlyAsync(d, migrateTo);
        var counts = new Dictionary<string, long>();
        foreach (var (table, _, _) in OldInserts) counts[table] = await ScalarAsync<long>(d, $"SELECT count(*) FROM \"{table}\"");

        var n = 0;
        foreach (var (table, where, set) in OldInserts) await ExecuteAsync(d, OldBuildInsert(table, columns[table], where, set, "_old_" + ++n));

        foreach (var (table, _, _) in OldInserts) Assert.Equal(counts[table] + 1, await ScalarAsync<long>(d, $"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(1L, await ScalarAsync<long>(d, """
            SELECT count(*) FROM "PurchaseOrders" WHERE "PurchaseNo" = 'PUR-OLD1' AND NOT "RatesIncludeTax" AND "BillDiscountAmount" = 0 AND "BillCharges" = 0 AND "OriginalBillDiscountAmount" = 0 AND "OriginalBillCharges" = 0
              AND "PartyTaxStatus" = 'Unspecified' AND "TaxableTotal" IS NULL AND "PlaceOfSupply" IS NULL AND "BillDiscountPercent" IS NULL
            """));
        Assert.Equal(1L, await ScalarAsync<long>(d, $"""
            SELECT count(*) FROM "PurchaseOrderLines" WHERE "PurchaseOrderId" = '{PoB}' AND "Id" <> '{PoLineB}' AND NOT "PriceIncludesTax" AND "BillDiscountShare" = 0 AND "BillChargeShare" = 0
              AND "TaxProfileId" IS NULL AND "OriginalTaxAmount" IS NULL
            """));
        foreach (var table in LineTaxTables)
            Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM "{table}" WHERE "Treatment" IS NOT NULL"""));   // an old row is a legacy row
    }

    // ------------------------------------------------------------------------------------------------------------------------------ lock_timeout

    [Theory]
    [InlineData("PurchaseReturnLines")]
    [InlineData("PurchaseOrderLines")]
    [InlineData("PurchaseOrders")]
    [InlineData("PurchaseOrderLineTaxes")]
    [InlineData("SalesInvoiceLineTaxes")]
    public async Task Up_FirstMigration_WhenATouchedTableIsLocked_FailsFastWith55P03_LeavesNoPartialState_AndRetrySucceeds(string lockedTable)
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        var counts = await LineTaxCountsAsync(d);
        var before = await UnchangedSnapshotAsync(d);
        await using var blocker = new NpgsqlConnection(d.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{lockedTable}\" IN ACCESS EXCLUSIVE MODE", blocker, tx)) await hold.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, PurchaseSnapshot));
        clock.Stop();

        var pg = Assert.IsType<PostgresException>(Root(ex));
        Assert.Equal("55P03", pg.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s instead of failing after about 5s");
        await tx.RollbackAsync();
        Assert.Empty(await NewColumnsAsync(d));                                            // one transaction: no column, no constraint, no catch-up row
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_constraint WHERE conname IN ({InList(Constraints.Select(c => c.Name))})"""));
        Assert.Equal(counts, await LineTaxCountsAsync(d));
        Assert.DoesNotContain(PurchaseSnapshot, await AppliedAsync(d));

        await MigrateExactlyAsync(d, ValidatePurchase);   // after the blocker released, a retry succeeds

        Assert.Contains(PurchaseSnapshot, await AppliedAsync(d));
        Assert.Contains(ValidatePurchase, await AppliedAsync(d));
        Assert.Equal(before, await UnchangedSnapshotAsync(d));
    }

    [Fact]
    public async Task Up_SecondMigration_WhenAPurchaseTableIsLocked_FailsFastWith55P03_AndRetrySucceeds()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, PurchaseSnapshot);
        await using var blocker = new NpgsqlConnection(d.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("""LOCK TABLE "PurchaseOrderLines" IN ACCESS EXCLUSIVE MODE""", blocker, tx)) await hold.ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => MigrateExactlyAsync(d, ValidatePurchase));

        Assert.Equal("55P03", Assert.IsType<PostgresException>(Root(ex)).SqlState);
        await tx.RollbackAsync();
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.False(v));   // nothing validated halfway
        Assert.Equal(0L, await ScalarAsync<long>(d, $"""SELECT count(*) FROM pg_indexes WHERE indexname IN ({InList(Indexes)})"""));

        await MigrateExactlyAsync(d, ValidatePurchase);

        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Fact]
    public async Task Up_SecondMigration_BlocksOnlyPurchaseWrites_ASalesInsertInFlightDoesNotStopIt()
    {
        // VALIDATE CONSTRAINT takes SHARE UPDATE EXCLUSIVE (compatible with row writes); the only plain CREATE INDEX statements are on purchase tables.
        await using var d = await Tp2ShapedCopyAsync(postgres);
        await MigrateExactlyAsync(d, PurchaseSnapshot);
        await using var writer = new NpgsqlConnection(d.ConnectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "SalesInvoiceLineTaxes" ("Id", "SalesInvoiceLineId", "ComponentCode", "Kind", "Method", "RatePercent", "BaseAmount", "TaxAmount")
            VALUES (gen_random_uuid(), '{Fx(94)}', 'X', 'Other', 'Percent', 1, 1, 0)
            """, writer, tx)) await insert.ExecuteNonQueryAsync();

        var clock = Stopwatch.StartNew();
        await MigrateExactlyAsync(d, ValidatePurchase);   // does not wait for the open sales write
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the migration waited {clock.Elapsed.TotalSeconds:0.0}s for an open sales insert");
        await tx.RollbackAsync();
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    // ------------------------------------------------------------------------------------------------------------------------------ Down / Up and the script

    [Fact]
    public async Task DownThenUp_RoundTrip_RestoresTheTp2Schema_KeepsTheCatchUpRows_AndComesBackIdentical()
    {
        await using var d = await Tp2ShapedCopyAsync(postgres);
        var tp2Schema = await SchemaSignatureAsync(d);
        var tp2Snapshot = await UnchangedSnapshotAsync(d);

        await MigrateExactlyAsync(d, ValidatePurchase);
        var headSchema = await SchemaSignatureAsync(d);
        var headData = await DataFingerprintAsync(d);
        Assert.NotEqual(tp2Schema, headSchema);

        await MigrateExactlyAsync(d, ValidateFks);   // Down: ValidatePurchaseTaxConstraints (indexes) then AddPurchaseTaxSnapshot (columns, constraints)

        Assert.DoesNotContain(PurchaseSnapshot, await AppliedAsync(d));
        Assert.DoesNotContain(ValidatePurchase, await AppliedAsync(d));
        Assert.Empty(await NewColumnsAsync(d));
        Assert.Equal(tp2Schema, await SchemaSignatureAsync(d));
        Assert.Equal(tp2Snapshot, await UnchangedSnapshotAsync(d));
        Assert.Equal(headData, await DataFingerprintAsync(d));   // the catch-up rows stay: they are the rows the TP-1 build would have written

        await MigrateExactlyAsync(d, ValidatePurchase);   // Up again

        Assert.Equal(headSchema, await SchemaSignatureAsync(d));
        Assert.Equal(headData, await DataFingerprintAsync(d));
        Assert.All((await ConstraintStateAsync(d)).Values, v => Assert.True(v));
    }

    [Fact]
    public async Task Up_IdempotentScriptFromTheTp1Head_AppliesThroughNpgsql_MatchesTheEfPath_AndRerunsAsANoOp()
    {
        await using var viaScript = await Tp2ShapedCopyAsync(postgres);
        await using var viaEf = await Tp2ShapedCopyAsync(postgres);
        var script = await ScriptAsync(viaScript, ValidateFks, ValidatePurchase);

        await ExecuteAsync(viaScript, script);   // as an operator runs it (EF wrapper around the migration's raw SQL, the SHA-pinned file included)
        await MigrateExactlyAsync(viaEf, ValidatePurchase);

        Assert.Contains(PurchaseSnapshot, await AppliedAsync(viaScript));
        Assert.Contains(ValidatePurchase, await AppliedAsync(viaScript));
        Assert.Equal(await SchemaSignatureAsync(viaEf), await SchemaSignatureAsync(viaScript));
        Assert.Equal(await DataFingerprintAsync(viaEf), await DataFingerprintAsync(viaScript));
        Assert.Equal(await ConstraintStateAsync(viaEf), await ConstraintStateAsync(viaScript));
        var data = await DataFingerprintAsync(viaScript);
        var schema = await SchemaSignatureAsync(viaScript);

        await ExecuteAsync(viaScript, script);   // a second run is a no-op

        Assert.Equal(data, await DataFingerprintAsync(viaScript));
        Assert.Equal(schema, await SchemaSignatureAsync(viaScript));
        Assert.Equal(await LineTaxCountsAsync(viaEf), await LineTaxCountsAsync(viaScript));
    }

    [Fact]
    public async Task Up_FromAnEmptyTp1Database_AndFromTheWholeHistory_BothReachTheSameSchema()
    {
        // an environment without any purchase (nothing to catch up) migrates cleanly, and the head schema equals the one of the data-bearing world
        await using var empty = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(empty, ValidateFks);
        await using var tp2 = await Tp2ShapedCopyAsync(postgres);

        await MigrateExactlyAsync(empty, ValidatePurchase);
        await MigrateExactlyAsync(tp2, ValidatePurchase);

        var emptySchema = await SchemaSignatureAsync(empty);
        var tp2Schema = await SchemaSignatureAsync(tp2);
        Assert.Empty(emptySchema.Except(tp2Schema));
        Assert.Empty(tp2Schema.Except(emptySchema));
    }
}
