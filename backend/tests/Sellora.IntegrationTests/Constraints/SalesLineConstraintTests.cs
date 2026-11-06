using Npgsql;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Database backstops of the Phase 5 sales snapshots, written with raw SQL so they do not depend on SalesService: the
/// CHECKs on sales lines (Qty &gt; 0, factor &gt; 0, non-empty UnitCode, Qty x factor = BaseQty, override requires a list
/// price, price equals list unless overridden, 0 &lt;= Discount &lt;= gross) and on return lines, the composite
/// (ProductId, ProductUnitId) FKs, the Units references, NOT NULL snapshots and Restrict on the referenced rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SalesLineConstraintTests(PostgresFixture postgres)
{
    private static Guid G(int prefix, int n) => Guid.Parse($"{prefix:x8}-0000-0000-0000-{n:x12}");
    private static readonly Guid UPc = G(1, 1), UBox = G(1, 2), ULitre = G(1, 3);
    private static readonly Guid TaxId = G(2, 1), RoleId = G(2, 2), UserId = G(2, 3), CustomerId = G(2, 4);
    private static readonly Guid PPen = G(3, 1), POil = G(3, 2);
    private static readonly Guid PenPc = G(4, 1), PenBox = G(4, 2), OilBase = G(4, 3);
    private static readonly Guid Invoice = G(5, 1), Line = G(6, 1), Ret = G(7, 1), RetLine = G(8, 1);

    /// <summary>One invoice with a line of 2 Box (20 pc) at 50, no discount, list price not recorded, and a return line of 1 Box.
    /// A second product (Oil) has its own base unit.</summary>
    private async Task<TestDatabase> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await ExecuteAsync(database, $"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES
              ('{UPc}', 'Piece', 'pc-t', 'Count', 0, NULL, true), ('{UBox}', 'Box', 'box-t', 'Packaging', 0, NULL, true), ('{ULitre}', 'Litre', 'l-t', 'Volume', 3, 1, true);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 5% test', 5, true);
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{RoleId}', 'Test role', NULL, true, false);
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{UserId}', now(), 'sales-test@selloramart.in', 'Ravi', 'Krishnan', 'x', '{RoleId}', 'Active', 'sales-test');
            INSERT INTO "Customers" ("Id", "Name", "IsActive", "IsWalkIn") VALUES ('{CustomerId}', 'Walk-in test', true, true);
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{PPen}', 'STN-PEN', 'Ball Pen', NULL, '{UPc}', '{TaxId}', 0, true), ('{POil}', 'OIL', 'Oil', NULL, '{ULitre}', '{TaxId}', 0, true);
            INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit",
              "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive") VALUES
              ('{PenPc}', '{PPen}', '{UPc}', NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true),
              ('{PenBox}', '{PPen}', '{UBox}', '{PenPc}', 10, 10, false, NULL, NULL, true, true, false, false, 1, true),
              ('{OilBase}', '{POil}', '{ULitre}', NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true);
            INSERT INTO "SalesInvoices" ("Id", "InvoiceNo", "CustomerId", "InvoiceDate", "PaymentMethod", "PaidAmount", "ReturnedAmount", "Subtotal", "TaxTotal", "DiscountTotal", "GrandTotal", "Status", "CreatedByUserId", "CreatedAt")
            VALUES ('{Invoice}', 'SAL-T1', '{CustomerId}', DATE '2026-09-02', 'Cash', 0, 0, 0, 0, 0, 0, 'Completed', '{UserId}', now());
            INSERT INTO "SalesInvoiceLines" ("Id", "SalesInvoiceId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "UnitPrice",
              "ListUnitPrice", "PriceOverridden", "TaxId", "TaxRatePercent", "Discount", "TaxableValue", "TaxAmount", "LineTotal", "CostOfGoodsSold")
            VALUES ('{Line}', '{Invoice}', '{PPen}', '{PenBox}', '{UBox}', 'box-t', 2, 10, 20, 50, NULL, false, '{TaxId}', 5, 0, 100, 5, 105, 200);
            INSERT INTO "SalesReturns" ("Id", "ReturnNo", "SalesInvoiceId", "ReturnDate", "Reason", "TaxableTotal", "TaxTotal", "Total", "CreatedByUserId", "CreatedAt")
            VALUES ('{Ret}', 'SRT-T1', '{Invoice}', DATE '2026-09-03', NULL, 50, 2.5, 52.5, '{UserId}', now());
            INSERT INTO "SalesReturnLines" ("Id", "SalesReturnId", "SalesInvoiceLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "ConversionFactorToBase",
              "Qty", "BaseQty", "TaxableValue", "TaxAmount", "LineTotal", "CostRestored")
            VALUES ('{RetLine}', '{Ret}', '{Line}', '{PPen}', '{PenBox}', '{UBox}', 'box-t', 10, 1, 10, 50, 2.5, 52.5, 100);
            """);
        return database;
    }

    private static async Task<PostgresException> AssertViolatesAsync(TestDatabase database, string sql, string sqlState, string constraint)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal((sqlState, constraint), (ex.SqlState, ex.ConstraintName));
        return ex;
    }

    [Theory]
    [InlineData("""SET "Qty" = 0, "BaseQty" = 0""", "CK_SalesInvoiceLines_Qty_Positive")]
    [InlineData("""SET "ConversionFactorToBase" = 0, "BaseQty" = 0""", "CK_SalesInvoiceLines_Factor_Positive")]
    [InlineData("""SET "UnitCode" = '' """, "CK_SalesInvoiceLines_UnitCode_NotEmpty")]
    [InlineData("""SET "BaseQty" = 21""", "CK_SalesInvoiceLines_BaseQty_Arithmetic")]
    [InlineData("""SET "ConversionFactorToBase" = 12""", "CK_SalesInvoiceLines_BaseQty_Arithmetic")]
    [InlineData("""SET "PriceOverridden" = true""", "CK_SalesInvoiceLines_Override_HasList")]
    [InlineData("""SET "ListUnitPrice" = 40""", "CK_SalesInvoiceLines_Price_MatchesList")]
    [InlineData("""SET "ListUnitPrice" = 60, "PriceOverridden" = false""", "CK_SalesInvoiceLines_Price_MatchesList")]
    [InlineData("""SET "Discount" = 100.0001""", "CK_SalesInvoiceLines_Discount_WithinGross")]
    [InlineData("""SET "Discount" = -0.0001""", "CK_SalesInvoiceLines_Discount_WithinGross")]
    public async Task Update_SalesLineViolatingACheck_RejectedByThatConstraint(string set, string constraint)
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "SalesInvoiceLines" {set} WHERE "Id" = '{Line}'""", PostgresErrorCodes.CheckViolation, constraint);

        Assert.Equal("2|10|20|50|0|box-t|false", await ScalarStringAsync(database,
            $"""
            SELECT "Qty"::numeric(18,0)::text || '|' || "ConversionFactorToBase"::numeric(18,0)::text || '|' || "BaseQty"::numeric(18,0)::text || '|'
                   || "UnitPrice"::numeric(18,0)::text || '|' || "Discount"::numeric(18,0)::text || '|' || "UnitCode" || '|' || "PriceOverridden"::text
            FROM "SalesInvoiceLines" WHERE "Id" = '{Line}'
            """));
    }

    /// <summary>The CHECKs reject only what they must: the boundary and the legitimate shapes are accepted.</summary>
    [Theory]
    [InlineData("""SET "Discount" = 100""")]                                              // discount equal to the gross
    [InlineData("""SET "Discount" = 0""")]
    [InlineData("""SET "ListUnitPrice" = 50, "PriceOverridden" = false""")]               // charged = list
    [InlineData("""SET "ListUnitPrice" = 60, "PriceOverridden" = true""")]                // overridden below list
    [InlineData("""SET "ListUnitPrice" = 50, "PriceOverridden" = true""")]                // override flag with an equal price
    [InlineData("""SET "ListUnitPrice" = NULL, "PriceOverridden" = false""")]             // legacy row: not recorded
    [InlineData("""SET "Qty" = 0.001, "BaseQty" = 0.01, "Discount" = 0.05""")]            // smallest quantity at 3 dp
    public async Task Update_SalesLineWithinTheChecks_Accepted(string set)
    {
        await using var database = await SeedAsync();

        await ExecuteAsync(database, $"""UPDATE "SalesInvoiceLines" {set} WHERE "Id" = '{Line}'""");
    }

    [Theory]
    [InlineData("""SET "Qty" = 0, "BaseQty" = 0""", "CK_SalesReturnLines_Qty_Positive")]
    [InlineData("""SET "ConversionFactorToBase" = 0, "BaseQty" = 0""", "CK_SalesReturnLines_Factor_Positive")]
    [InlineData("""SET "UnitCode" = '' """, "CK_SalesReturnLines_UnitCode_NotEmpty")]
    [InlineData("""SET "BaseQty" = 11""", "CK_SalesReturnLines_BaseQty_Arithmetic")]
    [InlineData("""SET "ConversionFactorToBase" = 1""", "CK_SalesReturnLines_BaseQty_Arithmetic")]
    public async Task Update_ReturnLineViolatingACheck_RejectedByThatConstraint(string set, string constraint)
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "SalesReturnLines" {set} WHERE "Id" = '{RetLine}'""", PostgresErrorCodes.CheckViolation, constraint);

        Assert.Equal("box-t|10.0000000000|10.000", await ScalarStringAsync(database,
            $"""SELECT "UnitCode" || '|' || "ConversionFactorToBase"::text || '|' || "BaseQty"::text FROM "SalesReturnLines" WHERE "Id" = '{RetLine}'"""));
    }

    [Fact]
    public async Task Update_ReturnLineToAnotherUnitOfTheSameProduct_Accepted()
    {
        await using var database = await SeedAsync();

        // A return in pieces against a Box line is legal from Phase 5 on: its own unit snapshot.
        await ExecuteAsync(database, $"""UPDATE "SalesReturnLines" SET "ProductUnitId" = '{PenPc}', "UnitId" = '{UPc}', "UnitCode" = 'pc-t', "ConversionFactorToBase" = 1, "Qty" = 10 WHERE "Id" = '{RetLine}'""");
    }

    [Fact]
    public async Task Update_SalesLineToAUnitOfAnotherProduct_RejectedByCompositeForeignKey()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "SalesInvoiceLines" SET "ProductUnitId" = '{OilBase}' WHERE "Id" = '{Line}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_SalesInvoiceLines_ProductUnits_ProductId_ProductUnitId");
    }

    [Fact]
    public async Task Update_SalesLineToAnotherProductKeepingItsUnit_RejectedByCompositeForeignKey()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "SalesInvoiceLines" SET "ProductId" = '{POil}' WHERE "Id" = '{Line}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_SalesInvoiceLines_ProductUnits_ProductId_ProductUnitId");
    }

    [Fact]
    public async Task Update_ReturnLineToAUnitOfAnotherProduct_RejectedByCompositeForeignKey()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "SalesReturnLines" SET "ProductUnitId" = '{OilBase}' WHERE "Id" = '{RetLine}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_SalesReturnLines_ProductUnits_ProductId_ProductUnitId");
    }

    [Fact]
    public async Task Update_SalesAndReturnLineToAnUnknownUnit_RejectedByUnitForeignKey()
    {
        await using var database = await SeedAsync();

        await AssertViolatesAsync(database, $"""UPDATE "SalesInvoiceLines" SET "UnitId" = '{Guid.NewGuid()}' WHERE "Id" = '{Line}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_SalesInvoiceLines_Units_UnitId");
        await AssertViolatesAsync(database, $"""UPDATE "SalesReturnLines" SET "UnitId" = '{Guid.NewGuid()}' WHERE "Id" = '{RetLine}'""",
            PostgresErrorCodes.ForeignKeyViolation, "FK_SalesReturnLines_Units_UnitId");
    }

    [Theory]
    [InlineData("SalesInvoiceLines", "UnitId")]
    [InlineData("SalesInvoiceLines", "UnitCode")]
    [InlineData("SalesInvoiceLines", "PriceOverridden")]
    [InlineData("SalesReturnLines", "ProductUnitId")]
    [InlineData("SalesReturnLines", "UnitId")]
    [InlineData("SalesReturnLines", "UnitCode")]
    [InlineData("SalesReturnLines", "ConversionFactorToBase")]
    public async Task Update_SnapshotColumnToNull_RejectedByNotNull(string table, string column)
    {
        await using var database = await SeedAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""UPDATE "{table}" SET "{column}" = NULL"""));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, ex.SqlState);
    }

    [Fact]
    public async Task Delete_ProductUnitReferencedBySalesAndReturnLines_RefusedNotCascaded()
    {
        await using var database = await SeedAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""DELETE FROM "ProductUnits" WHERE "Id" = '{PenBox}'"""));

        Assert.Contains(ex.SqlState, new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RestrictViolation });
        Assert.Contains("ProductUnits_ProductId_ProductUnitId", ex.ConstraintName);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "SalesInvoiceLines" """));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "SalesReturnLines" """));
    }

    [Fact]
    public async Task Delete_UnitReferencedBySalesLineSnapshots_RefusedByRestrict()
    {
        await using var database = await SeedAsync();
        // Move the product unit off Box would break the FK; instead show the Units row is protected (ProductUnits and both line snapshots point at it).
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""DELETE FROM "Units" WHERE "Id" = '{UBox}'"""));

        Assert.Contains(ex.SqlState, new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RestrictViolation });
        Assert.Contains("Units", ex.ConstraintName);
    }

    [Fact]
    public async Task Delete_SalesLineWithReturnLines_RefusedNotCascaded()
    {
        await using var database = await SeedAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""DELETE FROM "SalesInvoiceLines" WHERE "Id" = '{Line}'"""));

        Assert.Contains(ex.SqlState, new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RestrictViolation });
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "SalesReturnLines" """));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private static Task<string> ScalarStringAsync(TestDatabase database, string sql) => ScalarAsync<string>(database, sql);
}
