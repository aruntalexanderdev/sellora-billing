using Microsoft.EntityFrameworkCore;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Functional-improvements Phase 08 <c>AddProductCategoryMasters</c> (FI-D16 / FI-D17) on the B10 head: legacy free-text categories in two companies are grouped
/// case-insensitively per company (most-used spelling, generated codes with collision suffixes), products survive unchanged apart from the new links, nothing links
/// across companies, and the database refuses a subcategory of another category, a category of another company and a subcategory without a category.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddProductCategoryMastersMigrationTests(PostgresFixture postgres)
{
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";
    private static readonly string Pc = G(1), Tax = G(10), OtherTax = G(1010);

    private const string LongA = "Extra Long Category Name X", LongB = "Extra Long Category Name Y";

    private static string ProductSql(int id, string company, string tax, string sku, string? category) =>
        $"""INSERT INTO "Products" ("Id", "CompanyId", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES ('{G(id)}', '{company}', '{sku}', 'P {sku}', {(category is null ? "NULL" : "'" + category.Replace("'", "''") + "'")}, '{Pc}', '{tax}', 0, true);""";

    private static string LegacySql =>
        $"""INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive", "CompanyId") VALUES ('{OtherTax}', 'GST B', 5, true, '{OtherCompany}');"""
        // default company: Rice x2 spellings (most-used wins), Oil, blanks, collisions (Rice!), truncation collision (two long names)
        + ProductSql(1101, DefaultCompany, Tax, "A1", "Rice") + ProductSql(1102, DefaultCompany, Tax, "A2", " rice") + ProductSql(1103, DefaultCompany, Tax, "A3", "RICE ")
        + ProductSql(1104, DefaultCompany, Tax, "A4", "Rice") + ProductSql(1105, DefaultCompany, Tax, "A5", "Oil") + ProductSql(1106, DefaultCompany, Tax, "A6", null)
        + ProductSql(1107, DefaultCompany, Tax, "A7", "") + ProductSql(1108, DefaultCompany, Tax, "A8", "   ") + ProductSql(1109, DefaultCompany, Tax, "A9", "Rice!")
        + ProductSql(1110, DefaultCompany, Tax, "A10", LongA) + ProductSql(1111, DefaultCompany, Tax, "A11", LongB) + ProductSql(1112, DefaultCompany, Tax, "A12", "!!!")
        // other company: RICE most used, Oil, a symbol name
        + ProductSql(1201, OtherCompany, OtherTax, "B1", "rice") + ProductSql(1202, OtherCompany, OtherTax, "B2", "RICE") + ProductSql(1203, OtherCompany, OtherTax, "B3", "RICE")
        + ProductSql(1204, OtherCompany, OtherTax, "B4", "oil") + ProductSql(1205, OtherCompany, OtherTax, "B5", "Oils & Fats") + ProductSql(1206, OtherCompany, OtherTax, "B6", null);

    private const string Snapshot = """SELECT "Id"::text || '|' || "CompanyId" || '|' || "Sku" || '|' || "Name" || '|' || COALESCE("Category", '<null>') || '|' || "BaseUnitId" || '|' || "TaxId" || '|' || "ReorderLevel" || '|' || "IsActive" FROM "Products" ORDER BY "Id" """;

    private async Task<TestDatabase> CreateLegacyAsync()
    {
        var database = await CreateWithSecondCompanyAtAsync(postgres, B10);
        await ExecuteAsync(database, LegacySql);
        return database;
    }

    private static async Task<List<string>> CategoriesAsync(TestDatabase d, string company) =>
        await QueryAsync(d, $"""SELECT "Code" || '|' || "Name" || '|' || "SortOrder" || '|' || "IsActive" FROM "ProductCategories" WHERE "CompanyId" = '{company}' ORDER BY "SortOrder" """, r => r.GetString(0));

    private static async Task AssertValidationCleanAsync(TestDatabase d)
    {
        Assert.Empty(await QueryAsync(d, """SELECT "Id"::text FROM "Products" WHERE "CategoryId" IS NULL AND btrim("Category", E' \t\r\n') <> '' """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(d, """
            SELECT COALESCE(c."CompanyId", p."CompanyId")::text FROM (SELECT "CompanyId", COUNT(*) n FROM "ProductCategories" GROUP BY 1) c
            FULL JOIN (SELECT "CompanyId", COUNT(DISTINCT lower(btrim("Category", E' \t\r\n'))) n FROM "Products" WHERE btrim("Category", E' \t\r\n') <> '' GROUP BY 1) p USING ("CompanyId")
            WHERE COALESCE(c.n, 0) <> COALESCE(p.n, 0)
            """, r => r.GetString(0)));
        Assert.Empty(await QueryAsync(d, """
            SELECT p."Id"::text FROM "Products" p JOIN "ProductCategories" c ON c."Id" = p."CategoryId"
            WHERE c."CompanyId" <> p."CompanyId" OR lower(c."Name") <> lower(btrim(p."Category", E' \t\r\n'))
            """, r => r.GetString(0)));
    }

    [Fact]
    public async Task Up_LegacyProductsInTwoCompanies_GroupsPerCompany_GeneratesCodes_AndKeepsProductsUnchanged()
    {
        await using var database = await CreateLegacyAsync();
        var before = await QueryAsync(database, Snapshot, r => r.GetString(0));
        var counts = await RowCountsAsync(database, CountedTables);
        Assert.DoesNotContain(await AppliedAsync(database), m => m.EndsWith("_AddProductCategoryMasters"));

        await MigrateToAsync(database);

        Assert.EndsWith("_AddProductCategoryMasters", (await AppliedAsync(database)).Last());
        Assert.Equal(before, await QueryAsync(database, Snapshot, r => r.GetString(0))); // legacy string untouched, nothing else changed
        Assert.Equal(counts, await RowCountsAsync(database, CountedTables));
        // Default company: alphabetical by lower(name) in C collation ("!!!" sorts first, then extra..., oil, rice, rice!). "!!!" -> empty code -> CAT.
        Assert.Equal(
        [
            "CAT|!!!|1|true", "EXTRA-LONG-CATEGORY|Extra Long Category Name X|2|true", "EXTRA-LONG-CATEGOR-2|Extra Long Category Name Y|3|true",
            "OIL|Oil|4|true", "RICE|Rice|5|true", "RICE-2|Rice!|6|true",
        ], await CategoriesAsync(database, DefaultCompany));
        // Other company: most-used spelling "RICE" (2 of 3), symbols collapse to '-'.
        Assert.Equal(["OIL|oil|1|true", "OILS-FATS|Oils & Fats|2|true", "RICE|RICE|3|true"], await CategoriesAsync(database, OtherCompany));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "ProductSubcategories" """));

        // Products: variants of one string share the category; blank / null get none; no link crosses companies.
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""SELECT COUNT(DISTINCT "CategoryId") FROM "Products" WHERE "Id" IN ('{G(1101)}', '{G(1102)}', '{G(1103)}', '{G(1104)}')"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Products" WHERE "CategoryId" IS NOT NULL AND btrim("Category", E' \t\r\n') = '' """));
        Assert.Equal(6L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Products" WHERE "CategoryId" IS NULL""")); // fixture PEN + RICE, A6 (null), A7 + A8 (blank), B6 (null)
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Products" p JOIN "ProductCategories" c ON c."Id" = p."CategoryId" WHERE c."CompanyId" <> p."CompanyId" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Products" WHERE "SubcategoryId" IS NOT NULL"""));
        await AssertValidationCleanAsync(database);
    }

    [Fact]
    public async Task Up_NewConstraints_RefuseMismatchedSubcategory_ForeignCompanyCategory_AndOrphanSubcategory()
    {
        await using var database = await CreateLegacyAsync();
        await MigrateToAsync(database);
        var riceA = await ScalarAsync<Guid>(database, $"""SELECT "Id" FROM "ProductCategories" WHERE "CompanyId" = '{DefaultCompany}' AND "Code" = 'RICE'""");
        var oilA = await ScalarAsync<Guid>(database, $"""SELECT "Id" FROM "ProductCategories" WHERE "CompanyId" = '{DefaultCompany}' AND "Code" = 'OIL'""");
        var riceB = await ScalarAsync<Guid>(database, $"""SELECT "Id" FROM "ProductCategories" WHERE "CompanyId" = '{OtherCompany}' AND "Code" = 'RICE'""");
        string Sub(int id, string company, Guid category, string code, string name) =>
            $"""INSERT INTO "ProductSubcategories" ("Id", "CompanyId", "CategoryId", "Code", "Name", "SortOrder", "IsActive", "CreatedAt") VALUES ('{G(id)}', '{company}', '{category}', '{code}', '{name}', 1, true, now());""";
        await ExecuteAsync(database, Sub(2001, DefaultCompany, riceA, "BASMATI", "Basmati"));

        // A matching product link is accepted.
        await ExecuteAsync(database, $"""UPDATE "Products" SET "CategoryId" = '{riceA}', "SubcategoryId" = '{G(2001)}' WHERE "Id" = '{G(1101)}'""");
        // Subcategory of another category / category of another company / subcategory without a category are refused.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""UPDATE "Products" SET "CategoryId" = '{oilA}', "SubcategoryId" = '{G(2001)}' WHERE "Id" = '{G(1105)}'"""));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""UPDATE "Products" SET "CategoryId" = '{riceB}' WHERE "Id" = '{G(1105)}'"""));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""UPDATE "Products" SET "CategoryId" = NULL, "SubcategoryId" = '{G(2001)}' WHERE "Id" = '{G(1101)}'"""));
        // A subcategory under another company's category, duplicate codes / case-insensitive names, bad code format.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, Sub(2002, DefaultCompany, riceB, "X1", "X1")));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, Sub(2003, DefaultCompany, riceA, "BASMATI", "Other")));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, Sub(2004, DefaultCompany, riceA, "OTHER", "BASMATI".ToLowerInvariant())));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, Sub(2005, DefaultCompany, riceA, "bad code", "Bad")));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""INSERT INTO "ProductCategories" ("Id", "CompanyId", "Code", "Name", "SortOrder", "IsActive", "CreatedAt") VALUES ('{G(2006)}', '{DefaultCompany}', 'NEW', 'rICE', 1, true, now())"""));
        // Same name / code in another category or company is fine.
        await ExecuteAsync(database, Sub(2007, DefaultCompany, oilA, "BASMATI", "Basmati") + Sub(2008, OtherCompany, riceB, "BASMATI", "Basmati"));
        // Restrict: a referenced category / subcategory cannot be deleted.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""DELETE FROM "ProductSubcategories" WHERE "Id" = '{G(2001)}'"""));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(database, $"""DELETE FROM "ProductCategories" WHERE "Id" = '{riceA}'"""));
        Assert.All(await QueryAsync(database, "SELECT conname FROM pg_constraint WHERE connamespace = 'public'::regnamespace", r => r.GetString(0)), n => Assert.True(n.Length <= 63, n));
    }

    [Fact]
    public async Task Down_RestoresTheB10Schema_AndKeepsTheProducts_ThenUpAgainRebuildsTheSameCategories()
    {
        await using var database = await CreateLegacyAsync();
        var schemaB10 = await SchemaSignatureAsync(database);
        await MigrateToAsync(database);
        var schemaHead = await SchemaSignatureAsync(database);
        var categories = await CategoriesAsync(database, DefaultCompany);
        var products = await QueryAsync(database, Snapshot, r => r.GetString(0));

        await MigrateToAsync(database, B10);

        Assert.Equal(schemaB10, await SchemaSignatureAsync(database));
        Assert.Equal(products, await QueryAsync(database, Snapshot, r => r.GetString(0)));
        await MigrateToAsync(database);
        Assert.Equal(schemaHead, await SchemaSignatureAsync(database));
        Assert.Equal(categories, await CategoriesAsync(database, DefaultCompany));
        await AssertValidationCleanAsync(database);
    }

    [Fact]
    public async Task IdempotentScript_OnTheLegacyDataset_AppliesAndBackfills()
    {
        await using var database = await CreateLegacyAsync();
        var script = await ScriptAsync(database, B10, (await ScriptTargetAsync(database)));
        await ExecuteAsync(database, script);
        await AssertValidationCleanAsync(database);
        await ExecuteAsync(database, script); // second run changes nothing
        Assert.Equal(6L, await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "ProductCategories" WHERE "CompanyId" = '{DefaultCompany}'"""));
    }

    private static async Task<string> ScriptTargetAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return db.Database.GetMigrations().Last();
    }
}
