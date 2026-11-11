using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// The ProductUnits CHECKs, partial unique indexes and foreign keys (design §3) are the last line of defence behind
/// <c>ProductService</c>: raw writes that bypass the service must still be rejected, and history-bearing rows must
/// never be deleted by a cascade.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProductUnitConstraintTests(PostgresFixture postgres)
{
    private sealed record Seed(Guid Kg, Guid Gram, Guid Box, Guid Bag, Guid Crate, Guid ProductA, Guid ProductB, Guid BaseA, Guid BaseB, Guid UserId);

    /// <summary>Two kg-based products, each with only its base row.</summary>
    private static async Task<(TestDatabase Database, Seed Seed)> SeedAsync(PostgresFixture postgres)
    {
        var database = await postgres.CreateDatabaseAsync();
        await Sellora.IntegrationTests.TestData.CompanyMigrations.DefaultRawInsertsToDefaultCompanyAsync(database); // raw fixture rows below name no company
        var s = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tax = Guid.NewGuid();
        var role = Guid.NewGuid();
        await ExecuteAsync(database, $"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES
              ('{s.Kg}', 'Kilogram', 'kg', 'Weight', 3, 1, true),
              ('{s.Gram}', 'Gram', 'g', 'Weight', 0, 0.001, true),
              ('{s.Box}', 'Box', 'box', 'Packaging', 0, NULL, true),
              ('{s.Bag}', 'Bag', 'bag', 'Packaging', 0, NULL, true),
              ('{s.Crate}', 'Crate', 'crate', 'Packaging', 0, NULL, true);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{tax}', 'GST 0%', 0, true);
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{s.ProductA}', 'A', 'Product A', NULL, '{s.Kg}', '{tax}', 0, true),
              ('{s.ProductB}', 'B', 'Product B', NULL, '{s.Kg}', '{tax}', 0, true);
            INSERT INTO "Roles" ("Id", "CompanyId", "Name", "Description", "IsActive", "IsSystem", "CreatedAt") VALUES ('{role}', '0c0a1f00-0000-4000-8000-000000000001', 'Administrator', NULL, true, true, now());
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{s.UserId}', now(), 'admin@selloramart.in', 'Ravi', 'Krishnan', 'x', '{role}', 'Active', 'admin');
            """);
        await InsertAsync(database, s.BaseA, s.ProductA, s.Kg, inner: null, isBase: true);
        await InsertAsync(database, s.BaseB, s.ProductB, s.Kg, inner: null, isBase: true);
        return (database, s);
    }

    /// <summary>Raw insert; every column explicit so a test changes exactly one thing.</summary>
    private static Task InsertAsync(TestDatabase database, Guid id, Guid productId, Guid unitId, Guid? inner, bool isBase = false,
        decimal? innerQuantity = null, decimal? factor = null, bool isActive = true, bool purchasable = true, bool sellable = true,
        bool defaultPurchase = false, bool defaultSales = false, string? barcode = null)
    {
        var q = innerQuantity ?? (isBase ? 1m : 10m);
        var f = factor ?? q;
        return ExecuteAsync(database, $"""
            INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase",
              "IsBaseUnit", "Label", "Barcode", "IsActive", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder")
            VALUES ('{id}', '{productId}', '{unitId}', {(inner is null ? "NULL" : $"'{inner}'")}, {Num(q)}, {Num(f)},
              {B(isBase)}, NULL, {(barcode is null ? "NULL" : $"'{barcode}'")}, {B(isActive)}, {B(purchasable)}, {B(sellable)},
              {B(defaultPurchase)}, {B(defaultSales)}, 0);
            """);
    }

    private static string B(bool b) => b ? "true" : "false";
    private static string Num(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public enum Violation
    {
        BaseWithInner, NonBaseWithoutInner, InnerQuantityZero, InnerQuantityNegative, FactorZero, FactorNegative,
        BaseInnerQuantityNotOne, BaseFactorNotOne, BaseInactive, InnerIsSelf,
        DefaultPurchaseNotPurchasable, DefaultPurchaseInactive, DefaultSalesNotSellable, DefaultSalesInactive,
    }

    [Theory]
    [InlineData(Violation.BaseWithInner, "CK_ProductUnits_Base_NoInner")]
    [InlineData(Violation.NonBaseWithoutInner, "CK_ProductUnits_Base_NoInner")]
    [InlineData(Violation.InnerQuantityZero, "CK_ProductUnits_InnerQuantity_Positive")]
    [InlineData(Violation.InnerQuantityNegative, "CK_ProductUnits_InnerQuantity_Positive")]
    [InlineData(Violation.FactorZero, "CK_ProductUnits_Factor_Positive")]
    [InlineData(Violation.FactorNegative, "CK_ProductUnits_Factor_Positive")]
    [InlineData(Violation.BaseInnerQuantityNotOne, "CK_ProductUnits_Base_Identity")]
    [InlineData(Violation.BaseFactorNotOne, "CK_ProductUnits_Base_Identity")]
    [InlineData(Violation.BaseInactive, "CK_ProductUnits_Base_Active")]
    [InlineData(Violation.InnerIsSelf, "CK_ProductUnits_Inner_NotSelf")]
    [InlineData(Violation.DefaultPurchaseNotPurchasable, "CK_ProductUnits_DefaultPurchase")]
    [InlineData(Violation.DefaultPurchaseInactive, "CK_ProductUnits_DefaultPurchase")]
    [InlineData(Violation.DefaultSalesNotSellable, "CK_ProductUnits_DefaultSales")]
    [InlineData(Violation.DefaultSalesInactive, "CK_ProductUnits_DefaultSales")]
    public async Task InsertProductUnit_ViolatingCheck_RejectedWithConstraintName(Violation violation, string constraint)
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        // Product B has a base row only, so base-row cases use a fresh product (no partial-unique clash on the base).
        var productC = Guid.NewGuid();
        await ExecuteAsync(database, $"""
            INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive")
            SELECT '{productC}', 'C', 'Product C', NULL, "BaseUnitId", "TaxId", 0, true FROM "Products" WHERE "Id" = '{s.ProductA}';
            """);
        var id = Guid.NewGuid();

        Task Act() => violation switch
        {
            Violation.BaseWithInner => InsertAsync(database, id, s.ProductA, s.Box, inner: s.BaseA, isBase: true, innerQuantity: 1m, factor: 1m),
            Violation.NonBaseWithoutInner => InsertAsync(database, id, s.ProductA, s.Box, inner: null),
            Violation.InnerQuantityZero => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, innerQuantity: 0m, factor: 10m),
            Violation.InnerQuantityNegative => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, innerQuantity: -10m, factor: 10m),
            Violation.FactorZero => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, innerQuantity: 10m, factor: 0m),
            Violation.FactorNegative => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, innerQuantity: 10m, factor: -10m),
            Violation.BaseInnerQuantityNotOne => InsertAsync(database, id, productC, s.Kg, null, isBase: true, innerQuantity: 2m, factor: 1m),
            Violation.BaseFactorNotOne => InsertAsync(database, id, productC, s.Kg, null, isBase: true, innerQuantity: 1m, factor: 2m),
            Violation.BaseInactive => InsertAsync(database, id, productC, s.Kg, null, isBase: true, isActive: false),
            Violation.InnerIsSelf => InsertAsync(database, id, s.ProductA, s.Box, inner: id),
            Violation.DefaultPurchaseNotPurchasable => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, purchasable: false, defaultPurchase: true),
            Violation.DefaultPurchaseInactive => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, isActive: false, defaultPurchase: true),
            Violation.DefaultSalesNotSellable => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, sellable: false, defaultSales: true),
            Violation.DefaultSalesInactive => InsertAsync(database, id, s.ProductA, s.Box, s.BaseA, isActive: false, defaultSales: true),
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };

        var ex = await Assert.ThrowsAsync<PostgresException>(Act);

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ProductUnits" """));
    }

    [Fact]
    public async Task InsertProductUnit_ValidChainAndDefaults_Accepted()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        var bag = Guid.NewGuid();
        var crate = Guid.NewGuid();

        await InsertAsync(database, bag, s.ProductA, s.Bag, s.BaseA, innerQuantity: 25m, defaultPurchase: true, defaultSales: true);
        await InsertAsync(database, crate, s.ProductA, s.Crate, bag, innerQuantity: 4m, factor: 100m);
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Gram, s.BaseA, innerQuantity: 0.001m); // fraction of the base
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, s.BaseA, isActive: false, purchasable: false, sellable: false);

        Assert.Equal(6L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ProductUnits" """));
    }

    [Fact]
    public async Task InsertProductUnit_SameUnitTwiceInactive_AcceptedButSecondActiveRejected()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;

        // Deactivate-and-replace: Box(10) and Box(12) retired, Box(20) active.
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, s.BaseA, innerQuantity: 10m, isActive: false);
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, s.BaseA, innerQuantity: 12m, isActive: false);
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, s.BaseA, innerQuantity: 20m);
        await InsertAsync(database, Guid.NewGuid(), s.ProductB, s.Box, s.BaseB, innerQuantity: 20m); // other product: independent

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, s.BaseA, innerQuantity: 24m));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("IX_ProductUnits_ProductId_UnitId", ex.ConstraintName);
    }

    [Fact]
    public async Task InsertProductUnit_SecondBaseRow_RejectedByPartialUnique()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;

        // Different unit, so only the one-base-per-product index can object.
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, null, isBase: true));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("IX_ProductUnits_ProductId_Base", ex.ConstraintName);
    }

    [Theory]
    [InlineData(true, "IX_ProductUnits_ProductId_DefaultPurchase")]
    [InlineData(false, "IX_ProductUnits_ProductId_DefaultSales")]
    public async Task InsertProductUnit_SecondDefaultUnit_RejectedByPartialUnique(bool purchase, string index)
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, s.BaseA, defaultPurchase: purchase, defaultSales: !purchase);
        await InsertAsync(database, Guid.NewGuid(), s.ProductB, s.Box, s.BaseB, defaultPurchase: purchase, defaultSales: !purchase); // other product OK

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Bag, s.BaseA, innerQuantity: 25m, defaultPurchase: purchase, defaultSales: !purchase));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal(index, ex.ConstraintName);
    }

    [Fact]
    public async Task InsertProductUnit_DuplicateActiveBarcodeAcrossProducts_Rejected()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Box, s.BaseA, barcode: "8901234567890");
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Bag, s.BaseA, innerQuantity: 25m, isActive: false, barcode: "8901234567891");
        await InsertAsync(database, Guid.NewGuid(), s.ProductB, s.Bag, s.BaseB, innerQuantity: 25m, barcode: "8901234567891"); // inactive holder: reusable
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Crate, s.BaseA, innerQuantity: 50m);                         // NULL barcodes never clash
        await InsertAsync(database, Guid.NewGuid(), s.ProductB, s.Crate, s.BaseB, innerQuantity: 50m);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(database, Guid.NewGuid(), s.ProductB, s.Box, s.BaseB, barcode: "8901234567890"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("IX_ProductUnits_CompanyId_Barcode", ex.ConstraintName);
    }

    [Fact]
    public async Task InsertProductUnit_InnerFromAnotherProduct_RejectedByCompositeForeignKey()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(database, Guid.NewGuid(), s.ProductB, s.Box, inner: s.BaseA));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_ProductUnits_ProductUnits_ProductId_InnerProductUnitId", ex.ConstraintName);
    }

    [Fact]
    public async Task UpdateProductUnit_RepointInnerToAnotherProduct_RejectedByCompositeForeignKey()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        var box = Guid.NewGuid();
        await InsertAsync(database, box, s.ProductB, s.Box, s.BaseB);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database, $"""UPDATE "ProductUnits" SET "InnerProductUnitId" = '{s.BaseA}' WHERE "Id" = '{box}' """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_ProductUnits_ProductUnits_ProductId_InnerProductUnitId", ex.ConstraintName);
    }

    [Fact]
    public async Task Migrate_ProductUnitForeignKeys_ExistWithRestrictDeleteRules()
    {
        await using var database = await postgres.CreateDatabaseAsync();

        var fks = await QueryAsync(database, """
            SELECT c.conname, c.confdeltype::text, array_length(c.conkey, 1), pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
            WHERE c.contype = 'f' AND (t.relname = 'ProductUnits' OR c.confrelid = '"ProductUnits"'::regclass)
            """, r => (Name: r.GetString(0), OnDelete: r.GetString(1), Columns: r.GetInt32(2), Definition: r.GetString(3)));
        var byName = fks.ToDictionary(f => f.Name);

        // 'r' = RESTRICT, 'a' = NO ACTION; both refuse the delete. Cascade ('c') would silently drop history.
        foreach (var name in new[]
        {
            "FK_ProductUnits_Products_ProductId", "FK_ProductUnits_Units_UnitId",
            "FK_ProductUnits_ProductUnits_ProductId_InnerProductUnitId", "FK_SellingPrices_ProductUnits_ProductUnitId",
            "FK_PurchaseOrderLines_ProductUnits_ProductId_ProductUnitId", "FK_PurchaseReturnLines_ProductUnits_ProductId_ProductUnitId",
            "FK_SalesInvoiceLines_ProductUnits_ProductId_ProductUnitId", "FK_SalesReturnLines_ProductUnits_ProductId_ProductUnitId",
        })
        {
            Assert.True(byName.TryGetValue(name, out var fk), $"Foreign key {name} is missing. Found: {string.Join(", ", byName.Keys)}");
            Assert.True(fk.OnDelete is "r" or "a", $"{name} delete rule is '{fk.OnDelete}': {fk.Definition}");
        }
        Assert.All(fks, fk => Assert.NotEqual("c", fk.OnDelete));

        var composite = byName["FK_ProductUnits_ProductUnits_ProductId_InnerProductUnitId"];
        Assert.Equal(2, composite.Columns);
        Assert.Contains("(\"ProductId\", \"InnerProductUnitId\")", composite.Definition);
        Assert.Contains("\"ProductUnits\"(\"ProductId\", \"Id\")", composite.Definition);

        // Phases 4-5: purchase, sales and both return-line tables reference a unit of the SAME product (composite), not by Id alone.
        foreach (var name in new[]
        {
            "FK_PurchaseOrderLines_ProductUnits_ProductId_ProductUnitId", "FK_PurchaseReturnLines_ProductUnits_ProductId_ProductUnitId",
            "FK_SalesInvoiceLines_ProductUnits_ProductId_ProductUnitId", "FK_SalesReturnLines_ProductUnits_ProductId_ProductUnitId",
        })
        {
            Assert.Equal(2, byName[name].Columns);
            Assert.Contains("(\"ProductId\", \"ProductUnitId\")", byName[name].Definition);
            Assert.Contains("\"ProductUnits\"(\"ProductId\", \"Id\")", byName[name].Definition);
        }
        Assert.DoesNotContain("FK_PurchaseOrderLines_ProductUnits_ProductUnitId", byName.Keys);
        Assert.DoesNotContain("FK_SalesInvoiceLines_ProductUnits_ProductUnitId", byName.Keys);
    }

    [Fact]
    public async Task DeleteProduct_WithProductUnits_RejectedNotCascaded()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database, $"""DELETE FROM "Products" WHERE "Id" = '{s.ProductA}' """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_ProductUnits_Products_ProductId", ex.ConstraintName);
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ProductUnits" """));
    }

    [Fact]
    public async Task DeleteProductUnit_WithSellingPrice_RejectedNotCascaded()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        await ExecuteAsync(database, $"""
            INSERT INTO "SellingPrices" ("Id", "CreatedAt", "CreatedByUserId", "EffectiveFrom", "EffectiveTo", "Price", "ProductUnitId")
            VALUES (gen_random_uuid(), now(), '{s.UserId}', now(), NULL, 65, '{s.BaseA}');
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database, $"""DELETE FROM "ProductUnits" WHERE "Id" = '{s.BaseA}' """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_SellingPrices_ProductUnits_ProductUnitId", ex.ConstraintName);
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "SellingPrices" """));
    }

    [Fact]
    public async Task DeleteProductUnit_UsedAsInner_Rejected()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        var bag = Guid.NewGuid();
        await InsertAsync(database, bag, s.ProductA, s.Bag, s.BaseA, innerQuantity: 25m);
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Crate, bag, innerQuantity: 4m, factor: 100m);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database, $"""DELETE FROM "ProductUnits" WHERE "Id" = '{bag}' """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_ProductUnits_ProductUnits_ProductId_InnerProductUnitId", ex.ConstraintName);
    }

    [Fact]
    public async Task DeleteUnit_UsedByProductUnit_Rejected()
    {
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        await InsertAsync(database, Guid.NewGuid(), s.ProductA, s.Bag, s.BaseA, innerQuantity: 25m);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database, $"""DELETE FROM "Units" WHERE "Id" = '{s.Bag}' """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("FK_ProductUnits_Units_UnitId", ex.ConstraintName);
    }

    [Fact]
    public async Task SaveChangesAsync_NewHierarchyInOneSave_InsertsInDependencyOrder()
    {
        // EF must order inserts by the overlapping composite FK (ProductId, InnerProductUnitId) -> (ProductId, Id).
        var (database, s) = await SeedAsync(postgres);
        await using var _ = database;
        var tax = await ScalarAsync<Guid>(database, """SELECT "Id" FROM "Taxes" """);
        var productId = Guid.NewGuid();
        Guid pc, box, hctn, fctn;
        await using (var db = database.CreateDbContext())
        {
            var piece = new Unit { Id = Guid.NewGuid(), Name = "Piece", ShortCode = "pc", MeasurementCategory = MeasurementCategory.Count };
            var half = new Unit { Id = Guid.NewGuid(), Name = "Half Carton", ShortCode = "hctn", MeasurementCategory = MeasurementCategory.Packaging };
            var full = new Unit { Id = Guid.NewGuid(), Name = "Full Carton", ShortCode = "fctn", MeasurementCategory = MeasurementCategory.Packaging };
            db.Units.AddRange(piece, half, full);
            var product = new Product { Id = productId, Sku = "STN-PEN", Name = "Pen", BaseUnitId = piece.Id, TaxId = tax };
            // Added outermost first so EF, not insertion order, must sort them.
            var basePc = new ProductUnit { Id = Guid.NewGuid(), ProductId = productId, UnitId = piece.Id, IsBaseUnit = true, ConversionFactorToBase = 1m };
            var boxRow = new ProductUnit { Id = Guid.NewGuid(), ProductId = productId, UnitId = s.Box, InnerProductUnitId = basePc.Id, InnerQuantity = 10m, ConversionFactorToBase = 10m };
            var hctnRow = new ProductUnit { Id = Guid.NewGuid(), ProductId = productId, UnitId = half.Id, InnerProductUnitId = boxRow.Id, InnerQuantity = 5m, ConversionFactorToBase = 50m };
            var fctnRow = new ProductUnit { Id = Guid.NewGuid(), ProductId = productId, UnitId = full.Id, InnerProductUnitId = hctnRow.Id, InnerQuantity = 2m, ConversionFactorToBase = 100m };
            (pc, box, hctn, fctn) = (basePc.Id, boxRow.Id, hctnRow.Id, fctnRow.Id);
            db.Products.Add(product);
            db.ProductUnits.AddRange(fctnRow, hctnRow, boxRow, basePc);
            await db.SaveChangesAsync();
        }

        await using var check = database.CreateDbContext();
        var rows = await check.ProductUnits.AsNoTracking().Where(u => u.ProductId == productId).ToDictionaryAsync(u => u.Id);
        Assert.Equal((Guid?)null, rows[pc].InnerProductUnitId);
        Assert.Equal((pc, 10m), (rows[box].InnerProductUnitId!.Value, rows[box].ConversionFactorToBase));
        Assert.Equal((box, 50m), (rows[hctn].InnerProductUnitId!.Value, rows[hctn].ConversionFactorToBase));
        Assert.Equal((hctn, 100m), (rows[fctn].InnerProductUnitId!.Value, rows[fctn].ConversionFactorToBase));
    }

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
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<T>> QueryAsync<T>(TestDatabase database, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }
}
