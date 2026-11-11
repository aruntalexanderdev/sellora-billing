using System.Text;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Multi-company Phase 8a <c>AddStockLocationDimension</c> (B9, owner O5) on the Phase 7 head (B8) with two companies: the legacy pen/rice dataset, a second
/// product (Soap) whose documents (purchases incl. a cancelled one, sale, cancelled sale, sales return, purchase return, adjustment) belong to a NON-default
/// branch (BLR), and company B with its own product, purchase, adjustment and sale. First execution of the migration on PostgreSQL: Up on an empty database and on
/// the dataset (everything lands in each company's default store, header BranchId untouched), the V-block statements exactly as shipped in the migration header,
/// guards G1 / G2 / G3 raise and roll back, new constraints, the Down guard, Down restores the B8 schema, Down then Up, idempotent script twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddStockLocationDimensionMigrationTests(PostgresFixture postgres)
{
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";

    private static readonly string BlrBranch = G(700), BlrStore = G(701), Soap = G(710), SoapUnit = G(711);
    private static readonly string Pen = G(20), Rice = G(21), Tea = G(750);
    private static readonly string BStore = $"(SELECT \"Id\" FROM \"StockLocations\" WHERE \"CompanyId\" = '{OtherCompany}' AND \"LocationType\" = 'BranchStore')";
    private static readonly string BMain = $"(SELECT \"Id\" FROM \"Branches\" WHERE \"CompanyId\" = '{OtherCompany}' AND \"IsDefault\")";

    // Column lists shared with the legacy fixture (CompanyMigrations.FixtureSql), plus the multi-company / branch columns.
    private const string PoCols = "\"Id\", \"CreatedAt\", \"CreatedByUserId\", \"DiscountTotal\", \"GrandTotal\", \"OtherChargesTotal\", \"PaidAmount\", \"PaymentMethod\", \"PurchaseDate\", \"PurchaseNo\", \"Status\", \"Subtotal\", \"SupplierId\", \"TaxTotal\", \"CompanyId\", \"BranchId\"";
    private const string PoLineCols = "\"Id\", \"PurchaseOrderId\", \"ProductId\", \"ProductUnitId\", \"UnitId\", \"UnitCode\", \"Qty\", \"ConversionFactorToBase\", \"BaseQty\", \"Rate\", \"Discount\", \"OtherCharges\", \"TaxId\", \"TaxRatePercent\", \"TaxableValue\", \"TaxAmount\", \"LineTotal\", \"UnitCost\"";
    private const string BatchCols = "\"Id\", \"CreatedAt\", \"ProductId\", \"PurchaseOrderLineId\", \"PurchasedBaseQty\", \"ReceivedDate\", \"RemainingBaseQty\", \"StockAdjustmentId\", \"UnitCost\", \"CompanyId\"";
    private const string SaleCols = "\"Id\", \"InvoiceNo\", \"CustomerId\", \"InvoiceDate\", \"PaymentMethod\", \"PaidAmount\", \"Subtotal\", \"TaxTotal\", \"DiscountTotal\", \"GrandTotal\", \"Status\", \"CreatedByUserId\", \"CreatedAt\", \"CompanyId\", \"BranchId\"";
    private const string SaleLineCols = "\"Id\", \"SalesInvoiceId\", \"ProductId\", \"ProductUnitId\", \"UnitId\", \"UnitCode\", \"Qty\", \"ConversionFactorToBase\", \"BaseQty\", \"UnitPrice\", \"TaxId\", \"TaxRatePercent\", \"Discount\", \"TaxableValue\", \"TaxAmount\", \"LineTotal\", \"CostOfGoodsSold\"";
    private const string MoveCols = "\"Id\", \"ProductId\", \"Type\", \"QuantityBaseUnits\", \"PurchaseBatchId\", \"ReferenceType\", \"ReferenceId\", \"ReferenceNo\", \"UserId\", \"OccurredAt\", \"Notes\", \"ReasonCode\", \"CompanyId\"";
    private const string AdjCols = "\"Id\", \"AdjustmentNo\", \"AfterQty\", \"BeforeQty\", \"ConversionFactorToBase\", \"CreatedAt\", \"Direction\", \"EnteredQuantity\", \"ProductId\", \"ProductUnitId\", \"QuantityBaseUnits\", \"Type\", \"UnitCode\", \"UnitId\", \"UserId\", \"CompanyId\", \"BranchId\"";

    private static string Move(int id, string product, string type, decimal qty, string? batch, string refType, string refId, string refNo, string company = DefaultCompany) =>
        $"""('{G(id)}', '{product}', '{type}', {qty}, {(batch is null ? "NULL" : $"'{batch}'")}, '{refType}', '{refId}', '{refNo}', '{G(12)}', now(), NULL, {(type == "Adjustment" ? "'Damage'" : "NULL")}, '{company}')""";

    /// <summary>The dataset written at the Phase 7 schema, on top of the legacy fixture + second company (pen 6 and rice 25 at the default company, all reconciled).</summary>
    private static readonly string DatasetSql = $"""
        CREATE FUNCTION pg_temp.g(n int) RETURNS uuid LANGUAGE sql AS $$ SELECT ('00000000-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid $$;

        -- default company: non-default branch BLR with its own store, product Soap
        INSERT INTO "Branches" ("Id", "CompanyId", "Code", "Name", "IsDefault", "IsActive", "CreatedAt") VALUES ('{BlrBranch}', '{DefaultCompany}', 'BLR', 'Bengaluru', false, true, now());
        INSERT INTO "StockLocations" ("Id", "CompanyId", "BranchId", "LocationType", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{BlrStore}', '{DefaultCompany}', '{BlrBranch}', 'BranchStore', 'BLR', 'Bengaluru', true, now());
        INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive", "CompanyId") VALUES ('{Soap}', 'SOAP', 'Soap', NULL, pg_temp.g(1), pg_temp.g(10), 0, true, '{DefaultCompany}');
        INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit", "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive", "CompanyId")
        VALUES ('{SoapUnit}', '{Soap}', pg_temp.g(1), NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true, '{DefaultCompany}');

        INSERT INTO "PurchaseOrders" ({PoCols}) VALUES
          (pg_temp.g(720), now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-10-01', 'PUR-0100', 'Completed', 0, pg_temp.g(14), 0, '{DefaultCompany}', '{BlrBranch}'),
          (pg_temp.g(723), now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-10-02', 'PUR-0101', 'Cancelled', 0, pg_temp.g(14), 0, '{DefaultCompany}', '{BlrBranch}');
        INSERT INTO "PurchaseOrderLines" ({PoLineCols}) VALUES
          (pg_temp.g(721), pg_temp.g(720), '{Soap}', '{SoapUnit}', pg_temp.g(1), 'pc', 100, 1, 100, 10, 0, 0, pg_temp.g(10), 5, 1000, 50, 1050, 10),
          (pg_temp.g(722), pg_temp.g(720), '{Soap}', '{SoapUnit}', pg_temp.g(1), 'pc', 50, 1, 50, 10, 0, 0, pg_temp.g(10), 5, 500, 25, 525, 10),
          (pg_temp.g(724), pg_temp.g(723), '{Soap}', '{SoapUnit}', pg_temp.g(1), 'pc', 20, 1, 20, 10, 0, 0, pg_temp.g(10), 5, 200, 10, 210, 10);
        INSERT INTO "PurchaseBatches" ({BatchCols}) VALUES
          (pg_temp.g(740), now() - interval '3 days', '{Soap}', pg_temp.g(721), 100, DATE '2026-10-01', 63, NULL, 10, '{DefaultCompany}'),
          (pg_temp.g(741), now() - interval '2 days', '{Soap}', pg_temp.g(722), 50, DATE '2026-10-01', 45, NULL, 10, '{DefaultCompany}'),
          (pg_temp.g(742), now() - interval '1 day',  '{Soap}', pg_temp.g(724), 20, DATE '2026-10-02', 0, NULL, 10, '{DefaultCompany}');
        INSERT INTO "SalesInvoices" ({SaleCols}) VALUES
          (pg_temp.g(730), 'SAL-0100', pg_temp.g(13), DATE '2026-10-03', 'Cash', 0, 0, 0, 0, 0, 'Completed', pg_temp.g(12), now(), '{DefaultCompany}', '{BlrBranch}'),
          (pg_temp.g(733), 'SAL-0101', pg_temp.g(13), DATE '2026-10-04', 'Cash', 0, 0, 0, 0, 0, 'Cancelled', pg_temp.g(12), now(), '{DefaultCompany}', '{BlrBranch}');
        INSERT INTO "SalesInvoiceLines" ({SaleLineCols}) VALUES
          (pg_temp.g(731), pg_temp.g(730), '{Soap}', '{SoapUnit}', pg_temp.g(1), 'pc', 30, 1, 30, 12, pg_temp.g(10), 5, 0, 360, 18, 378, 300),
          (pg_temp.g(734), pg_temp.g(733), '{Soap}', '{SoapUnit}', pg_temp.g(1), 'pc', 4, 1, 4, 12, pg_temp.g(10), 5, 0, 48, 2.4, 50.4, 40);
        INSERT INTO "SalesInvoiceLineBatchConsumptions" ("Id", "PurchaseBatchId", "QtyBaseUnits", "ReturnedQtyBaseUnits", "SalesInvoiceLineId", "UnitCost") VALUES
          (pg_temp.g(732), pg_temp.g(740), 30, 3, pg_temp.g(731), 10), (pg_temp.g(735), pg_temp.g(741), 4, 0, pg_temp.g(734), 10);
        INSERT INTO "SalesReturns" ("Id", "ReturnNo", "SalesInvoiceId", "ReturnDate", "TaxableTotal", "TaxTotal", "Total", "CreatedByUserId", "CreatedAt", "CompanyId", "BranchId")
        VALUES (pg_temp.g(736), 'SRT-0100', pg_temp.g(730), DATE '2026-10-05', 0, 0, 0, pg_temp.g(12), now(), '{DefaultCompany}', '{BlrBranch}');
        INSERT INTO "SalesReturnLines" ("Id", "SalesReturnId", "SalesInvoiceLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "TaxableValue", "TaxAmount", "LineTotal", "CostRestored")
        VALUES (pg_temp.g(737), pg_temp.g(736), pg_temp.g(731), '{Soap}', '{SoapUnit}', pg_temp.g(1), 'pc', 3, 1, 3, 0, 0, 0, 30);
        INSERT INTO "PurchaseReturns" ("Id", "CreatedAt", "CreatedByUserId", "OriginalTotal", "PurchaseOrderId", "ReturnDate", "ReturnNo", "TaxTotal", "TaxableTotal", "Total", "CompanyId", "BranchId")
        VALUES (pg_temp.g(738), now(), pg_temp.g(12), 0, pg_temp.g(720), DATE '2026-10-06', 'PRT-0100', 0, 0, 0, '{DefaultCompany}', '{BlrBranch}');
        INSERT INTO "PurchaseReturnLines" ("Id", "PurchaseReturnId", "PurchaseOrderLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "CostRemoved", "TaxableValue", "TaxAmount", "LineTotal")
        VALUES (pg_temp.g(739), pg_temp.g(738), pg_temp.g(721), '{Soap}', '{SoapUnit}', pg_temp.g(1), 'pc', 10, 1, 10, 100, 0, 0, 0);
        INSERT INTO "StockAdjustments" ({AdjCols}) VALUES
          (pg_temp.g(745), 'ADJ-0100', 50, 55, 1, now(), 'Decrease', 5, '{Soap}', '{SoapUnit}', 5, 'Damage', 'pc', pg_temp.g(1), pg_temp.g(12), '{DefaultCompany}', '{BlrBranch}');
        INSERT INTO "StockItems" ("ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ('{Soap}', 108, 10, '{DefaultCompany}');
        INSERT INTO "StockMovements" ({MoveCols}) VALUES
          {Move(800, Soap, "Purchase", 100, G(740), "PurchaseOrder", G(720), "PUR-0100")},
          {Move(801, Soap, "Purchase", 50, G(741), "PurchaseOrder", G(720), "PUR-0100")},
          {Move(802, Soap, "Purchase", 20, G(742), "PurchaseOrder", G(723), "PUR-0101")},
          {Move(803, Soap, "PurchaseCancellation", -20, G(742), "PurchaseOrder", G(723), "PUR-0101")},
          {Move(804, Soap, "Sale", -30, null, "SalesInvoice", G(730), "SAL-0100")},
          {Move(805, Soap, "Sale", -4, null, "SalesInvoice", G(733), "SAL-0101")},
          {Move(806, Soap, "SaleCancellation", 4, null, "SalesInvoice", G(733), "SAL-0101")},
          {Move(807, Soap, "SaleReturn", 3, null, "SalesReturn", G(736), "SRT-0100")},
          {Move(808, Soap, "PurchaseReturn", -10, G(740), "PurchaseReturn", G(738), "PRT-0100")},
          {Move(809, Soap, "Adjustment", -5, G(741), "StockAdjustment", G(745), "ADJ-0100")};

        -- company B: tax, customer, product Tea; purchase 40, adjustment -4, sale -6 (30 left) at its Main branch
        INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive", "CompanyId") VALUES (pg_temp.g(772), 'GST B', 5, true, '{OtherCompany}');
        INSERT INTO "Customers" ("Id", "Name", "IsActive", "IsWalkIn", "CompanyId") VALUES (pg_temp.g(770), 'Walk-in B', true, true, '{OtherCompany}');
        INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive", "CompanyId") VALUES ('{Tea}', 'TEA', 'Tea', NULL, pg_temp.g(1), pg_temp.g(772), 0, true, '{OtherCompany}');
        INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit", "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive", "CompanyId")
        VALUES (pg_temp.g(751), '{Tea}', pg_temp.g(1), NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true, '{OtherCompany}');
        INSERT INTO "PurchaseOrders" ({PoCols}) VALUES (pg_temp.g(760), now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-10-10', 'PUR-0200', 'Completed', 0, '00000000-0000-0000-0000-0000000b0014', 0, '{OtherCompany}', {BMain});
        INSERT INTO "PurchaseOrderLines" ({PoLineCols}) VALUES (pg_temp.g(761), pg_temp.g(760), '{Tea}', pg_temp.g(751), pg_temp.g(1), 'pc', 40, 1, 40, 12, 0, 0, pg_temp.g(772), 5, 480, 24, 504, 12);
        INSERT INTO "PurchaseBatches" ({BatchCols}) VALUES (pg_temp.g(762), now(), '{Tea}', pg_temp.g(761), 40, DATE '2026-10-10', 30, NULL, 12, '{OtherCompany}');
        INSERT INTO "SalesInvoices" ({SaleCols}) VALUES (pg_temp.g(771), 'INV-0200', pg_temp.g(770), DATE '2026-10-11', 'Cash', 0, 0, 0, 0, 0, 'Completed', pg_temp.g(12), now(), '{OtherCompany}', {BMain});
        INSERT INTO "SalesInvoiceLines" ({SaleLineCols}) VALUES (pg_temp.g(773), pg_temp.g(771), '{Tea}', pg_temp.g(751), pg_temp.g(1), 'pc', 6, 1, 6, 15, pg_temp.g(772), 5, 0, 90, 4.5, 94.5, 72);
        INSERT INTO "SalesInvoiceLineBatchConsumptions" ("Id", "PurchaseBatchId", "QtyBaseUnits", "ReturnedQtyBaseUnits", "SalesInvoiceLineId", "UnitCost") VALUES (pg_temp.g(774), pg_temp.g(762), 6, 0, pg_temp.g(773), 12);
        INSERT INTO "StockAdjustments" ({AdjCols}) VALUES (pg_temp.g(763), 'ADJ-0200', 36, 40, 1, now(), 'Decrease', 4, '{Tea}', pg_temp.g(751), 4, 'Loss', 'pc', pg_temp.g(1), pg_temp.g(12), '{OtherCompany}', {BMain});
        INSERT INTO "StockItems" ("ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ('{Tea}', 30, 12, '{OtherCompany}');
        INSERT INTO "StockMovements" ({MoveCols}) VALUES
          {Move(820, Tea, "Purchase", 40, G(762), "PurchaseOrder", G(760), "PUR-0200", OtherCompany)},
          {Move(821, Tea, "Adjustment", -4, G(762), "StockAdjustment", G(763), "ADJ-0200", OtherCompany)},
          {Move(822, Tea, "Sale", -6, null, "SalesInvoice", G(771), "INV-0200", OtherCompany)};
        """;

    private async Task<TestDatabase> CreateAtB8Async(bool withDataset = true)
    {
        var database = await CreateWithSecondCompanyAtAsync(postgres, B8);
        if (withDataset) await ExecuteAsync(database, DatasetSql);
        return database;
    }

    private static readonly string[] Touched =
        ["StockItems", "StockMovements", "PurchaseBatches", "PurchaseOrders", "PurchaseReturns", "SalesInvoices", "SalesReturns", "StockAdjustments", "SalesInvoiceLineBatchConsumptions"];

    private const string DefaultStoreMapSql = """
        SELECT b."CompanyId", l."Id" AS "StockLocationId" FROM "Branches" b
        JOIN "StockLocations" l ON l."CompanyId" = b."CompanyId" AND l."BranchId" = b."Id" AND l."LocationType" = 'BranchStore' WHERE b."IsDefault"
        """;

    private static string Str(Npgsql.NpgsqlDataReader r, int i = 0) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;

    // ---- the V-statements exactly as shipped in the migration header ---------------------------------------------------------------

    private static string MigrationSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Sellora.Infrastructure"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Sellora.Infrastructure", "Data", "Migrations", B9 + ".cs"));
    }

    private static List<(string Label, string Sql)> VStatements()
    {
        var source = MigrationSource();
        var start = source.IndexOf("V-block: AFTER applying", StringComparison.Ordinal);
        var end = source.IndexOf("Down (guard first", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "V-block markers not found in the migration header");
        var result = new List<(string, string)>();
        string label = "";
        var buffer = new StringBuilder();
        foreach (var raw in source[start..end].Split('\n'))
        {
            var line = raw.TrimStart(' ', '\t', '\r');
            if (!line.StartsWith('*')) continue;
            line = line[1..].Trim();
            if (line.StartsWith("-- V"))
            {
                if (buffer.Length > 0) { result.Add((label, buffer.ToString())); buffer.Clear(); }
                label = line;
                continue;
            }
            if (line.Length == 0 || line.StartsWith("--")) continue;
            buffer.Append(line).Append(' ');
            if (line.EndsWith(';')) { result.Add((label, buffer.ToString())); buffer.Clear(); }
        }
        return result;
    }

    private static async Task AssertVBlockAsync(TestDatabase database)
    {
        var statements = VStatements();
        Assert.Equal(8, statements.Select(s => s.Label.Split(' ')[1]).Distinct().Count()); // V1-V8 all shipped
        foreach (var (label, sql) in statements)
        {
            var rows = await QueryAsync(database, sql.Replace("@MAP@", DefaultStoreMapSql).TrimEnd().TrimEnd(';'), r => Str(r));
            Assert.True(rows.Count == 0, $"{label} returned {rows.Count} row(s): {string.Join(", ", rows.Take(5))}");
        }
    }

    private static async Task AssertGuardRolledBackAsync(TestDatabase database, HashSet<string> schema, List<string> history, List<string> counts)
    {
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.DoesNotContain(B9, history);
        Assert.Equal(counts, await RowCountsAsync(database, CountedTables));
    }

    private async Task AssertGuardRaisesAsync(string breakSql, string expected)
    {
        await using var database = await CreateAtB8Async();
        await ExecuteAsync(database, breakSql);
        var (schema, history, counts) = (await SchemaSignatureAsync(database), await AppliedAsync(database), await RowCountsAsync(database, CountedTables));

        await AssertRaisesAsync(() => MigrateToAsync(database, B9), expected);

        await AssertGuardRolledBackAsync(database, schema, history, counts);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'BatchLocationBalances'"));
    }

    private static string Sig(string table, string column) => $"col {table}.{column} uuid null=NO default=-";

    // ---- Up -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Up_EmptyDatabase_AppliesAndVBlockClean()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B8);

        await MigrateToAsync(database, B9);

        await AssertVBlockAsync(database);
        Assert.Equal(B9, (await AppliedAsync(database)).Last());
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "BatchLocationBalances" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockItems" """));
    }

    [Fact]
    public async Task Up_Phase7DatasetWithTwoCompaniesAndNonDefaultBranchDocuments_BackfillsEverythingToTheDefaultStores()
    {
        await using var database = await CreateAtB8Async();
        var before = await RowCountsAsync(database, Touched);
        var stockBefore = await QueryAsync(database, """SELECT "ProductId"::text || ':' || "QuantityOnHand" || ':' || "AverageCost" FROM "StockItems" ORDER BY 1""", r => r.GetString(0));
        var branchesBefore = await QueryAsync(database, """SELECT 'PO:' || "Id" || ':' || "BranchId" FROM "PurchaseOrders" UNION ALL SELECT 'SI:' || "Id" || ':' || "BranchId" FROM "SalesInvoices" ORDER BY 1""", r => r.GetString(0));

        await MigrateToAsync(database, B9);

        await AssertVBlockAsync(database);
        Assert.Equal(B9, (await AppliedAsync(database)).Last());
        Assert.Equal(before, await RowCountsAsync(database, Touched));
        // Stock figures are untouched; header BranchId is untouched (D2).
        Assert.Equal(stockBefore, await QueryAsync(database, """SELECT "ProductId"::text || ':' || "QuantityOnHand" || ':' || "AverageCost" FROM "StockItems" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(branchesBefore, await QueryAsync(database, """SELECT 'PO:' || "Id" || ':' || "BranchId" FROM "PurchaseOrders" UNION ALL SELECT 'SI:' || "Id" || ':' || "BranchId" FROM "SalesInvoices" ORDER BY 1""", r => r.GetString(0)));
        // The default company's rows sit at its Main store, company B's at B's own store.
        Assert.Equal(0L, await ScalarAsync<long>(database, $"""
            SELECT COUNT(*) FROM (
              SELECT "CompanyId", "StockLocationId" AS loc FROM "StockItems" UNION ALL SELECT "CompanyId", "StockLocationId" FROM "StockMovements"
              UNION ALL SELECT "CompanyId", "OriginStockLocationId" FROM "PurchaseBatches" UNION ALL SELECT "CompanyId", "StockLocationId" FROM "PurchaseOrders"
              UNION ALL SELECT "CompanyId", "StockLocationId" FROM "PurchaseReturns" UNION ALL SELECT "CompanyId", "StockLocationId" FROM "SalesInvoices"
              UNION ALL SELECT "CompanyId", "StockLocationId" FROM "SalesReturns" UNION ALL SELECT "CompanyId", "StockLocationId" FROM "StockAdjustments") x
            WHERE x.loc <> CASE WHEN x."CompanyId" = '{DefaultCompany}' THEN '{DefaultStore}'::uuid ELSE {BStore} END
            """));
        // Non-default-branch documents (BLR) stay on BLR but post at the default store.
        Assert.Equal(7L, await ScalarAsync<long>(database, $"""
            SELECT (SELECT COUNT(*) FROM "PurchaseOrders" WHERE "BranchId" = '{BlrBranch}' AND "StockLocationId" = '{DefaultStore}')
                 + (SELECT COUNT(*) FROM "SalesInvoices" WHERE "BranchId" = '{BlrBranch}' AND "StockLocationId" = '{DefaultStore}')
                 + (SELECT COUNT(*) FROM "SalesReturns" WHERE "BranchId" = '{BlrBranch}' AND "StockLocationId" = '{DefaultStore}')
                 + (SELECT COUNT(*) FROM "PurchaseReturns" WHERE "BranchId" = '{BlrBranch}' AND "StockLocationId" = '{DefaultStore}')
                 + (SELECT COUNT(*) FROM "StockAdjustments" WHERE "BranchId" = '{BlrBranch}' AND "StockLocationId" = '{DefaultStore}')
            """));
        Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockItems" WHERE "StockLocationId" = '{BlrStore}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockMovements" WHERE "StockLocationId" = '{BlrStore}'"""));
    }

    [Fact]
    public async Task Up_Dataset_BalancesMirrorBatchesAndEveryPerLocationInvariantHolds()
    {
        await using var database = await CreateAtB8Async();

        await MigrateToAsync(database, B9);

        // Batches with stock: pen 60, rice 63, soap 740/741, tea 762 = 5 positive batches (61, 62, 742 are empty and get no row).
        Assert.Equal(5L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "BatchLocationBalances" """));
        Assert.Equal(5L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PurchaseBatches" WHERE "RemainingBaseQty" > 0"""));
        Assert.Equal(63m, await ScalarAsync<decimal>(database, $"""SELECT "RemainingBaseQty" FROM "BatchLocationBalances" WHERE "PurchaseBatchId" = '{G(740)}' AND "StockLocationId" = '{DefaultStore}'"""));
        Assert.Equal(30m, await ScalarAsync<decimal>(database, $"""SELECT "RemainingBaseQty" FROM "BatchLocationBalances" WHERE "PurchaseBatchId" = '{G(762)}'"""));
        Assert.Equal(108m, await ScalarAsync<decimal>(database, $"""SELECT "QuantityOnHand" FROM "StockItems" WHERE "ProductId" = '{Soap}' AND "StockLocationId" = '{DefaultStore}'"""));
        // Per (location, product): quantity = Σ movements = Σ balances; per batch: Σ balances = remaining; legacy per-product invariants still hold.
        Assert.Empty(await QueryAsync(database, """
            SELECT s."StockLocationId"::text FROM "StockItems" s
            LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1, 2) m USING ("StockLocationId", "ProductId")
            LEFT JOIN (SELECT "StockLocationId", "ProductId", SUM("RemainingBaseQty") q FROM "BatchLocationBalances" GROUP BY 1, 2) b USING ("StockLocationId", "ProductId")
            WHERE s."QuantityOnHand" <> COALESCE(m.q, 0) OR s."QuantityOnHand" <> COALESCE(b.q, 0)
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, TestData.StockInvariants.QohVsLedgerSql, r => r.GetGuid(0).ToString()));
        Assert.Empty(await QueryAsync(database, TestData.StockInvariants.QohVsBatchesSql, r => r.GetGuid(0).ToString()));
    }

    [Fact]
    public async Task Up_NewSchema_HasKeysForeignKeysChecksAndNotNullColumns()
    {
        await using var database = await CreateAtB8Async();
        await MigrateToAsync(database, B9);
        var schema = await SchemaSignatureAsync(database);

        foreach (var (t, c) in new[]
        {
            ("StockItems", "StockLocationId"), ("StockMovements", "StockLocationId"), ("PurchaseOrders", "StockLocationId"), ("PurchaseReturns", "StockLocationId"),
            ("SalesInvoices", "StockLocationId"), ("SalesReturns", "StockLocationId"), ("StockAdjustments", "StockLocationId"), ("PurchaseBatches", "OriginStockLocationId"),
        })
        {
            Assert.Contains(Sig(t, c), schema);
        }
        var constraints = await QueryAsync(database, """
            SELECT c.conname || ' ' || pg_get_constraintdef(c.oid) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname IN ('StockItems', 'BatchLocationBalances', 'PurchaseBatches')
            """, r => r.GetString(0));
        Assert.Contains(constraints, c => c.StartsWith("PK_StockItems ") && c.Contains("(\"StockLocationId\", \"ProductId\")"));
        Assert.Contains(constraints, c => c.StartsWith("PK_BatchLocationBalances ") && c.Contains("(\"PurchaseBatchId\", \"StockLocationId\")"));
        Assert.Contains(constraints, c => c.StartsWith("CK_BatchLocationBalances_Remaining_NonNegative "));
        foreach (var name in new[] { "FK_BatchLocationBalances_PurchaseBatches", "FK_BatchLocationBalances_StockLocations", "FK_BatchLocationBalances_StockItems", "FK_PurchaseBatches_StockLocations_Origin" })
        {
            Assert.Contains(constraints, c => c.StartsWith(name + " "));
        }
        Assert.All(await QueryAsync(database, "SELECT conname FROM pg_constraint WHERE connamespace = 'public'::regnamespace", r => r.GetString(0)), n => Assert.True(n.Length <= 63, n));
    }

    [Fact]
    public async Task Up_NewConstraints_RejectCrossCompanyLocationNegativeBalanceAndOrphanBalance()
    {
        await using var database = await CreateAtB8Async();
        await MigrateToAsync(database, B9);

        // StockItems of the default company cannot sit at company B's store (composite FK to StockLocations (CompanyId, Id)).
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database,
            $"""INSERT INTO "StockItems" ("StockLocationId", "ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ({BStore}, '{Soap}', 0, 0, '{DefaultCompany}')"""));
        // A balance cannot exist without its (location, product) StockItems row.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database,
            $"""INSERT INTO "BatchLocationBalances" ("PurchaseBatchId", "StockLocationId", "CompanyId", "ProductId", "RemainingBaseQty") VALUES ('{G(740)}', '{BlrStore}', '{DefaultCompany}', '{Soap}', 1)"""));
        // Remaining can never go negative.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database,
            $"""UPDATE "BatchLocationBalances" SET "RemainingBaseQty" = -1 WHERE "PurchaseBatchId" = '{G(740)}'"""));
        // The same product at a second location is allowed by the new key (the one-row-per-product rule is only a backfill fact, V8).
        await ExecuteAsync(database, $"""INSERT INTO "StockItems" ("StockLocationId", "ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ('{BlrStore}', '{Soap}', 0, 0, '{DefaultCompany}')""");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database,
            $"""INSERT INTO "StockItems" ("StockLocationId", "ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ('{BlrStore}', '{Soap}', 0, 0, '{DefaultCompany}')"""));
        // A location row still referenced by stock cannot be deleted (Restrict).
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""DELETE FROM "StockLocations" WHERE "Id" = '{DefaultStore}'"""));
    }

    // ---- guards G1 / G2 / G3 (raise and roll back) -------------------------------------------------------------------------------

    [Fact]
    public async Task Up_StockLocationIdColumnAlreadyExists_GuardG1RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync("""ALTER TABLE "StockItems" ADD COLUMN "StockLocationId" uuid NULL""", "(G1)");

    [Fact]
    public async Task Up_ModelNotAtAddScopedRolesHead_GuardG1RaisesAndRollsBack()
    {
        await using var database = await CreateAtB8Async();
        var script = await ScriptAsync(database, B8, B9);
        await ExecuteAsync(database, $"""DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '{B8}'""");
        var (schema, history, counts) = (await SchemaSignatureAsync(database), await AppliedAsync(database), await RowCountsAsync(database, CountedTables));

        await AssertRaisesAsync(() => ExecuteAsync(database, script), "not at the AddScopedRoles head");

        await AssertGuardRolledBackAsync(database, schema, history, counts);
    }

    [Fact]
    public async Task Up_CompanyWithStockButNoDefaultBranchStore_GuardG2RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync($"""DELETE FROM "StockLocations" WHERE "CompanyId" = '{OtherCompany}' AND "LocationType" = 'BranchStore'""", "exactly one default branch");

    [Fact]
    public async Task Up_CompanyWithStockButNoDefaultBranch_GuardG2RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync($"""UPDATE "Branches" SET "IsDefault" = false WHERE "CompanyId" = '{OtherCompany}'""", "exactly one default branch");

    [Fact]
    public async Task Up_QuantityOnHandDiffersFromLedger_GuardG3RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync($"""UPDATE "StockItems" SET "QuantityOnHand" = "QuantityOnHand" + 1 WHERE "ProductId" = '{Pen}'""", "differs from the movement ledger");

    [Fact]
    public async Task Up_QuantityOnHandDiffersFromBatches_GuardG3RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync($"""UPDATE "PurchaseBatches" SET "RemainingBaseQty" = "RemainingBaseQty" + 1 WHERE "Id" = '{G(63)}'""", "differs from the batch remainders");

    [Fact]
    public async Task Up_MovementsWithoutAStockItemsRow_GuardG3RaisesAndRollsBack() =>
        await AssertGuardRaisesAsync($"""DELETE FROM "StockItems" WHERE "ProductId" = '{Rice}'""", "G3");

    [Fact]
    public async Task Up_AfterTheDriftIsFixed_AppliesCleanly()
    {
        await using var database = await CreateAtB8Async();
        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = "QuantityOnHand" + 1 WHERE "ProductId" = '{Pen}'""");
        await AssertRaisesAsync(() => MigrateToAsync(database, B9), "G3");

        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = "QuantityOnHand" - 1 WHERE "ProductId" = '{Pen}'""");
        await MigrateToAsync(database, B9);

        await AssertVBlockAsync(database);
    }

    // ---- Down ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Down_EverythingStillAtTheDefaultStore_RestoresTheB8SchemaAndKeepsTheData()
    {
        await using var database = await CreateAtB8Async();
        var counts = await RowCountsAsync(database, CountedTables);
        var stock = await QueryAsync(database, """SELECT "ProductId"::text || ':' || "QuantityOnHand" || ':' || "AverageCost" FROM "StockItems" ORDER BY 1""", r => r.GetString(0));
        await MigrateToAsync(database, B9);

        await MigrateToAsync(database, B8);

        Assert.DoesNotContain(B9, await AppliedAsync(database));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'BatchLocationBalances'"));
        var expected = await ReferenceSchemaAsync(postgres, B8);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expected.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expected).Order());
        Assert.Equal(counts, await RowCountsAsync(database, CountedTables));
        Assert.Equal(stock, await QueryAsync(database, """SELECT "ProductId"::text || ':' || "QuantityOnHand" || ':' || "AverageCost" FROM "StockItems" ORDER BY 1""", r => r.GetString(0)));
        Assert.Empty(await QueryAsync(database, TestData.StockInvariants.QohVsLedgerSql, r => r.GetGuid(0).ToString()));
        Assert.Empty(await QueryAsync(database, TestData.StockInvariants.QohVsBatchesSql, r => r.GetGuid(0).ToString()));
    }

    private async Task AssertDownRefusedAsync(string breakSql, string expected)
    {
        await using var database = await CreateAtB8Async();
        await MigrateToAsync(database, B9);
        await ExecuteAsync(database, breakSql);
        var schema = await SchemaSignatureAsync(database);
        var counts = await RowCountsAsync(database, Touched);

        await AssertRaisesAsync(() => MigrateToAsync(database, B8), expected);

        Assert.Equal(B9, (await AppliedAsync(database)).Last());
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(counts, await RowCountsAsync(database, Touched));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'BatchLocationBalances'"));
    }

    [Fact]
    public async Task Down_ProductHasStockRowsAtTwoLocations_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync($"""INSERT INTO "StockItems" ("StockLocationId", "ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ('{BlrStore}', '{Pen}', 0, 0, '{DefaultCompany}')""", "more than one location");

    [Fact]
    public async Task Down_StockRowOutsideTheDefaultStore_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync($"""
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive", "CompanyId") VALUES ('{G(900)}', 'NEW', 'New', NULL, '{G(1)}', '{G(10)}', 0, true, '{DefaultCompany}');
            INSERT INTO "StockItems" ("StockLocationId", "ProductId", "QuantityOnHand", "AverageCost", "CompanyId") VALUES ('{BlrStore}', '{G(900)}', 0, 0, '{DefaultCompany}')
            """, "outside the default store");

    [Fact]
    public async Task Down_MovementOutsideTheDefaultStore_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync($"""UPDATE "StockMovements" SET "StockLocationId" = '{BlrStore}' WHERE "Id" = '{G(804)}'""", "outside the default store");

    [Fact]
    public async Task Down_DocumentOutsideTheDefaultStore_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync($"""UPDATE "SalesInvoices" SET "StockLocationId" = '{BlrStore}' WHERE "Id" = '{G(730)}'""", "outside the default store");

    [Fact]
    public async Task Down_BatchOriginOutsideTheDefaultStore_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync($"""UPDATE "PurchaseBatches" SET "OriginStockLocationId" = '{BlrStore}' WHERE "Id" = '{G(740)}'""", "outside the default store");

    [Fact]
    public async Task Down_BalanceSumDiffersFromBatchTotal_GuardRefusesAndRollsBack() =>
        await AssertDownRefusedAsync($"""UPDATE "BatchLocationBalances" SET "RemainingBaseQty" = "RemainingBaseQty" - 1 WHERE "PurchaseBatchId" = '{G(740)}'""", "differs from the sum of balances");

    [Fact]
    public async Task Up_AfterDown_RebuildsTheSameBalancesAndStockLocations()
    {
        await using var database = await CreateAtB8Async();
        await MigrateToAsync(database, B9);
        const string Q = """
            SELECT "PurchaseBatchId"::text || ':' || "StockLocationId" || ':' || "RemainingBaseQty" FROM "BatchLocationBalances"
            UNION ALL SELECT 'SI:' || "ProductId" || ':' || "StockLocationId" || ':' || "QuantityOnHand" FROM "StockItems" ORDER BY 1
            """;
        var first = await QueryAsync(database, Q, r => r.GetString(0));

        await MigrateToAsync(database, B8);
        await MigrateToAsync(database, B9);

        Assert.Equal(first, await QueryAsync(database, Q, r => r.GetString(0)));
        await AssertVBlockAsync(database);
    }

    // ---- idempotent script -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdempotentScript_RunTwice_SecondRunChangesNothing()
    {
        await using var database = await CreateAtB8Async();
        var script = await ScriptAsync(database, B8, B9);
        Assert.Contains(B9, script);

        await ExecuteAsync(database, script);

        await AssertVBlockAsync(database);
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);
        var counts = await RowCountsAsync(database, Touched.Append("BatchLocationBalances").ToArray());
        // Operator (8b-style) activity between the runs must survive a re-run.
        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "AverageCost" = 11 WHERE "ProductId" = '{Pen}'""");

        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(counts, await RowCountsAsync(database, Touched.Append("BatchLocationBalances").ToArray()));
        Assert.Equal(11m, await ScalarAsync<decimal>(database, $"""SELECT "AverageCost" FROM "StockItems" WHERE "ProductId" = '{Pen}'"""));
        Assert.Equal(B9, history.Last());
    }

    [Fact]
    public async Task IdempotentScript_EmptyDatabase_AppliesAndSecondRunChangesNothing()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(database, B8);
        var script = await ScriptAsync(database, B8, B9);

        await ExecuteAsync(database, script);
        var schema = await SchemaSignatureAsync(database);
        await ExecuteAsync(database, script);

        Assert.Equal(schema, await SchemaSignatureAsync(database));
        await AssertVBlockAsync(database);
    }
}
