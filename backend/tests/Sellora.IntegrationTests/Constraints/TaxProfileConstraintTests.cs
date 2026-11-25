using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Constraints;

/// <summary>
/// Database constraints of the tax profile tables (docs/features/tax-profiles.md, TP-1 design "Migration M1" and database design): check constraints (method against rate/amount, scope shape,
/// HSN format, MaxBillDiscountPercent range), the partial unique indexes (one assignment per scope target, one open version per profile), the composite foreign keys (profile country = assignment
/// country, version component method = component method, company ownership) and the four line tax tables. Every INSERT is raw SQL on a freshly migrated database.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TaxProfileConstraintTests(PostgresFixture postgres)
{
    private static readonly Guid Co = Guid.Parse(DefaultCompany);
    private static readonly Guid Other = Guid.Parse("7a000000-0000-4000-8000-0000000000c1");

    // fixed ids of the seeded rows
    private static Guid G(int n) => new($"7a000000-0000-4000-8000-{n:x12}");
    private static readonly Guid Tax1 = G(1), Tax2 = G(2), Tax3 = G(3), TaxOther = G(4);
    private static readonly Guid CompCgst = G(11), CompCessUnit = G(12), CompVatAe = G(13), CompOther = G(14);
    private static readonly Guid ProfIn = G(21), ProfAe = G(22), ProfOther = G(23), ProfIn2 = G(24);
    private static readonly Guid VerIn = G(31), VerAe = G(32), VerOther = G(33);
    private static readonly Guid Category = G(41), Category2 = G(42), CategoryOther = G(43), Sub = G(44), Sub2 = G(45);
    private static readonly Guid ProductA = G(51), ProductB = G(52), ProductOther = G(53);

    private static string Country(string code) => $"""(SELECT "Id" FROM "Countries" WHERE "Code" = '{code}')""";
    private static string Lit(Guid? id) => id is null ? "NULL" : $"'{id}'";

    private static string TaxSql(Guid id, Guid company, string name) =>
        $"""INSERT INTO "Taxes" ("Id", "CompanyId", "Name", "RatePercent", "IsActive") VALUES ('{id}', '{company}', '{name}', 18, true)""";

    private static string ComponentSql(Guid id, Guid company, string cc, string code, string name, string kind = "Cgst", string method = "Percent") =>
        $"""INSERT INTO "TaxComponents" ("Id", "CompanyId", "CountryId", "Code", "Name", "Kind", "Method", "IsActive", "CreatedAt") VALUES ('{id}', '{company}', {Country(cc)}, '{code}', '{name}', '{kind}', '{method}', true, now())""";

    private static string ProfileSql(Guid id, Guid company, string cc, string code, string name, Guid legacyTax, string regime = "Gst", string treatment = "Taxable") =>
        $"""INSERT INTO "TaxProfiles" ("Id", "CompanyId", "CountryId", "Code", "Name", "Regime", "Treatment", "LegacyTaxId", "IsActive", "CreatedAt") VALUES ('{id}', '{company}', {Country(cc)}, '{code}', '{name}', '{regime}', '{treatment}', '{legacyTax}', true, now())""";

    private static string VersionSql(Guid id, Guid company, Guid profile, string from, string? to, string total = "18") =>
        $"""INSERT INTO "TaxProfileVersions" ("Id", "CompanyId", "TaxProfileId", "EffectiveFrom", "EffectiveTo", "TotalPercent", "CreatedAt") VALUES ('{id}', '{company}', '{profile}', '{from}', {(to is null ? "NULL" : $"'{to}'")}, {total}, now())""";

    private static string VcSql(Guid? id, Guid company, Guid version, Guid component, string method, string applicability, string rate, string amount) =>
        $"""INSERT INTO "TaxProfileVersionComponents" ("Id", "CompanyId", "TaxProfileVersionId", "TaxComponentId", "Method", "Applicability", "RatePercent", "AmountPerBaseUnit", "SortOrder") VALUES ({(id is null ? "gen_random_uuid()" : $"'{id}'")}, '{company}', '{version}', '{component}', '{method}', '{applicability}', {rate}, {amount}, 1)""";

    private static string AssignSql(string scope, string cc, Guid? category, Guid? sub, Guid? product, Guid sales, Guid? purchase = null, Guid? company = null) =>
        $"""INSERT INTO "TaxAssignments" ("Id", "CompanyId", "Scope", "CountryId", "CategoryId", "SubcategoryId", "ProductId", "SalesTaxProfileId", "PurchaseTaxProfileId", "CreatedAt") VALUES (gen_random_uuid(), '{company ?? Co}', '{scope}', {Country(cc)}, {Lit(category)}, {Lit(sub)}, {Lit(product)}, '{sales}', {Lit(purchase)}, now())""";

    private static async Task<PostgresException> FailsAsync(TestDatabase d, string sql, string sqlState, string? constraint)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, sql));
        Assert.Equal(sqlState, ex.SqlState);
        if (constraint is not null) Assert.Equal(constraint, ex.ConstraintName);
        return ex;
    }

    /// <summary>A migrated database with a second company and one valid row of every kind (the starting point of each constraint case).</summary>
    private async Task<TestDatabase> SeedAsync()
    {
        var d = await postgres.CreateDatabaseAsync(migrate: true);
        await ExecuteAsync(d, $"""
            INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{Other}', 'TXO', 'Tax other', true, now());
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES ('{G(900)}', 'Tax piece', 'txpc', 'Count', 0, NULL, true);
            {TaxSql(Tax1, Co, "T1")}; {TaxSql(Tax2, Co, "T2")}; {TaxSql(Tax3, Co, "T3")}; {TaxSql(TaxOther, Other, "TO")};
            {ComponentSql(CompCgst, Co, "IN", "CGST", "Central GST")}; {ComponentSql(CompCessUnit, Co, "IN", "CESS-UNIT", "Cess per unit", "Cess", "AmountPerBaseUnit")};
            {ComponentSql(CompVatAe, Co, "AE", "VAT", "Value added tax", "Vat")}; {ComponentSql(CompOther, Other, "IN", "CGST", "Central GST")};
            {ProfileSql(ProfIn, Co, "IN", "GST-X", "GST X", Tax1)}; {ProfileSql(ProfAe, Co, "AE", "VAT-X", "VAT X", Tax2, "Vat")}; {ProfileSql(ProfOther, Other, "IN", "GST-X", "GST X", TaxOther)};
            {ProfileSql(ProfIn2, Co, "IN", "GST-Y", "GST Y", Tax3)};
            {VersionSql(VerIn, Co, ProfIn, "2020-01-01", null)}; {VersionSql(VerAe, Co, ProfAe, "2020-01-01", null, "5")}; {VersionSql(VerOther, Other, ProfOther, "2020-01-01", null)};
            INSERT INTO "ProductCategories" ("Id", "CompanyId", "Code", "Name", "SortOrder", "IsActive", "CreatedAt") VALUES
              ('{Category}', '{Co}', 'CAT1', 'Cat 1', 0, true, now()), ('{Category2}', '{Co}', 'CAT2', 'Cat 2', 0, true, now()), ('{CategoryOther}', '{Other}', 'CAT1', 'Cat 1', 0, true, now());
            INSERT INTO "ProductSubcategories" ("Id", "CompanyId", "CategoryId", "Code", "Name", "SortOrder", "IsActive", "CreatedAt") VALUES
              ('{Sub}', '{Co}', '{Category}', 'SUB1', 'Sub 1', 0, true, now()), ('{Sub2}', '{Co}', '{Category2}', 'SUB2', 'Sub 2', 0, true, now());
            INSERT INTO "Products" ("Id", "CompanyId", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
              ('{ProductA}', '{Co}', 'TXA', 'Tax A', NULL, '{G(900)}', '{Tax1}', 0, true), ('{ProductB}', '{Co}', 'TXB', 'Tax B', NULL, '{G(900)}', '{Tax1}', 0, true),
              ('{ProductOther}', '{Other}', 'TXA', 'Tax A', NULL, '{G(900)}', '{TaxOther}', 0, true);
            """);
        return d;
    }

    // ------------------------------------------------------------------------------------------------------------------ components

    [Fact]
    public async Task TaxComponents_RejectBadCodeKindMethodAndDuplicates()
    {
        await using var d = await SeedAsync();
        string Insert(string code, string name, string kind = "Cgst", string method = "Percent", string cc = "IN", Guid? company = null) =>
            ComponentSql(Guid.NewGuid(), company ?? Co, cc, code, name, kind, method);

        await FailsAsync(d, Insert("cgst-x", "Lower"), "23514", "CK_TaxComponents_Code_Format");
        await FailsAsync(d, Insert("-X", "Leading dash"), "23514", "CK_TaxComponents_Code_Format");
        await FailsAsync(d, Insert("TOO-LONG-CODE-0123456789", "Too long"), "22001", null);   // varchar(20)
        await FailsAsync(d, Insert("OK1", "Bad kind", kind: "Gst"), "23514", "CK_TaxComponents_Kind");
        await FailsAsync(d, Insert("OK2", "Bad method", method: "Flat"), "23514", "CK_TaxComponents_Method");
        await FailsAsync(d, Insert("CGST", "Another name"), "23505", "IX_TaxComponents_CompanyId_CountryId_Code");
        await FailsAsync(d, Insert("OK3", "CENTRAL gst"), "23505", "UX_TaxComponents_Country_Name_Lower");
        await FailsAsync(d, $"""INSERT INTO "TaxComponents" ("Id", "CompanyId", "CountryId", "Code", "Name", "Kind", "Method", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), '{Co}', gen_random_uuid(), 'OK4', 'x', 'Cgst', 'Percent', true, now())""", "23503", "FK_TaxComponents_Countries_CountryId");
        await FailsAsync(d, $"""INSERT INTO "TaxComponents" ("Id", "CompanyId", "CountryId", "Code", "Name", "Kind", "Method", "IsActive", "CreatedAt") VALUES (gen_random_uuid(), gen_random_uuid(), {Country("IN")}, 'OK5', 'x', 'Cgst', 'Percent', true, now())""", "23503", "FK_TaxComponents_Companies_CompanyId");
        await ExecuteAsync(d, Insert("CGST", "Central GST", cc: "AE"));          // the same code and name in another country
        await ExecuteAsync(d, Insert("CGST", "Central GST", company: Other, cc: "AE"));
    }

    // ------------------------------------------------------------------------------------------------------------------ profiles

    [Fact]
    public async Task TaxProfiles_RejectBadCodeRegimeTreatmentDuplicatesAndForeignMirrors()
    {
        await using var d = await SeedAsync();
        var spare = G(61);
        await ExecuteAsync(d, TaxSql(spare, Co, "Spare 1"));
        string Insert(string code, string name, Guid mirror, string regime = "Gst", string treatment = "Taxable", string cc = "IN") =>
            ProfileSql(Guid.NewGuid(), Co, cc, code, name, mirror, regime, treatment);

        await FailsAsync(d, Insert("gst-z", "Lower code", spare), "23514", "CK_TaxProfiles_Code_Format");
        await FailsAsync(d, Insert("GST-Z", "Bad regime", spare, regime: "Sales"), "23514", "CK_TaxProfiles_Regime");
        await FailsAsync(d, Insert("GST-Z", "Bad treatment", spare, treatment: "Free"), "23514", "CK_TaxProfiles_Treatment");
        await FailsAsync(d, Insert("GST-X", "Another name", spare), "23505", "IX_TaxProfiles_CompanyId_CountryId_Code");
        await FailsAsync(d, Insert("GST-Z", "gst x", spare), "23505", "UX_TaxProfiles_Country_Name_Lower");
        await FailsAsync(d, Insert("GST-Z", "Mirror reused", Tax1), "23505", "IX_TaxProfiles_CompanyId_LegacyTaxId");              // one profile per legacy tax
        await FailsAsync(d, Insert("GST-Z", "Foreign mirror", TaxOther), "23503", "FK_TaxProfiles_Taxes_CompanyId_LegacyTaxId");     // the mirror must be the company's own tax
        await FailsAsync(d, Insert("GST-Z", "Missing mirror", Guid.NewGuid()), "23503", "FK_TaxProfiles_Taxes_CompanyId_LegacyTaxId");
        await ExecuteAsync(d, Insert("GST-X", "GST X", spare, cc: "AE", regime: "Vat"));                                              // the same code and name for another country
    }

    // ------------------------------------------------------------------------------------------------------------------ versions

    [Fact]
    public async Task TaxProfileVersions_RejectBadDatesNegativeTotalDuplicateStartSecondOpenVersionAndForeignProfile()
    {
        await using var d = await SeedAsync();

        await FailsAsync(d, VersionSql(Guid.NewGuid(), Co, ProfIn, "2010-01-01", "2009-12-31"), "23514", "CK_TaxProfileVersions_Dates");
        await FailsAsync(d, VersionSql(Guid.NewGuid(), Co, ProfIn, "2010-01-01", "2010-06-30", "-1"), "23514", "CK_TaxProfileVersions_TotalPercent");
        await FailsAsync(d, VersionSql(Guid.NewGuid(), Co, ProfIn, "2020-01-01", "2020-12-31"), "23505", "IX_TaxProfileVersions_CompanyId_TaxProfileId_EffectiveFrom");
        await FailsAsync(d, VersionSql(Guid.NewGuid(), Co, ProfIn, "2021-01-01", null), "23505", "IX_TaxProfileVersions_TaxProfileId_Open");   // one open version per profile
        await FailsAsync(d, VersionSql(Guid.NewGuid(), Co, ProfOther, "2015-01-01", "2015-12-31"), "23503", "FK_TaxProfileVersions_TaxProfiles_CompanyId_TaxProfileId");   // another company's profile
        await ExecuteAsync(d, VersionSql(Guid.NewGuid(), Co, ProfIn, "2010-01-01", "2019-12-31"));   // closed versions in any number are fine
        await ExecuteAsync(d, VersionSql(Guid.NewGuid(), Co, ProfAe, "2010-01-01", "2010-01-01"));   // a one-day version
        await ExecuteAsync(d, VersionSql(Guid.NewGuid(), Co, ProfIn2, "2020-01-01", null));          // another profile may have its own open version
    }

    [Fact]
    public async Task TaxProfileVersionComponents_RejectWrongMethodShapeNegativeValuesAndMethodMismatchWithTheComponent()
    {
        await using var d = await SeedAsync();

        await FailsAsync(d, VcSql(null, Co, VerIn, CompCgst, "Percent", "IntraState", "2.5", "1"), "23514", "CK_TaxProfileVersionComponents_MethodShape");        // percent with an amount
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCgst, "Percent", "IntraState", "NULL", "NULL"), "23514", "CK_TaxProfileVersionComponents_MethodShape");     // neither
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCessUnit, "AmountPerBaseUnit", "Always", "5", "2"), "23514", "CK_TaxProfileVersionComponents_MethodShape"); // amount with a rate
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCessUnit, "AmountPerBaseUnit", "Always", "NULL", "NULL"), "23514", "CK_TaxProfileVersionComponents_MethodShape");
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCgst, "Percent", "IntraState", "-0.5", "NULL"), "23514", "CK_TaxProfileVersionComponents_MethodShape");      // negative rate
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCessUnit, "AmountPerBaseUnit", "Always", "NULL", "-1"), "23514", "CK_TaxProfileVersionComponents_MethodShape");
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCgst, "Flat", "IntraState", "2.5", "NULL"), "23514", "CK_TaxProfileVersionComponents_Method");
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCgst, "Percent", "Sometimes", "2.5", "NULL"), "23514", "CK_TaxProfileVersionComponents_Applicability");
        // the component is Percent; a version component that says AmountPerBaseUnit for it breaks the composite FK (company, component, method)
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCgst, "AmountPerBaseUnit", "Always", "NULL", "2"), "23503", "FK_TaxProfileVersionComponents_TaxComponents");
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCessUnit, "Percent", "Always", "2", "NULL"), "23503", "FK_TaxProfileVersionComponents_TaxComponents");
        await FailsAsync(d, VcSql(null, Co, VerIn, CompOther, "Percent", "IntraState", "2.5", "NULL"), "23503", "FK_TaxProfileVersionComponents_TaxComponents");        // another company's component
        await FailsAsync(d, VcSql(null, Co, VerOther, CompCgst, "Percent", "IntraState", "2.5", "NULL"), "23503", "FK_TaxProfileVersionComponents_TaxProfileVersions");  // another company's version

        await ExecuteAsync(d, VcSql(null, Co, VerIn, CompCgst, "Percent", "IntraState", "9", "NULL"));
        await FailsAsync(d, VcSql(null, Co, VerIn, CompCgst, "Percent", "IntraState", "9", "NULL"), "23505", "IX_TaxProfileVersionComponents_Version_Component_Applicability");
        await ExecuteAsync(d, VcSql(null, Co, VerIn, CompCgst, "Percent", "InterState", "18", "NULL"));      // the same component under another applicability is allowed
        await ExecuteAsync(d, VcSql(null, Co, VerIn, CompCessUnit, "AmountPerBaseUnit", "Always", "NULL", "2.5"));
    }

    // ------------------------------------------------------------------------------------------------------------------ assignments

    [Fact]
    public async Task TaxAssignments_ScopeShapeCheck_RejectsEveryInconsistentCombination()
    {
        await using var d = await SeedAsync();

        await FailsAsync(d, AssignSql("Everything", "IN", null, null, null, ProfIn), "23514", "CK_TaxAssignments_Scope");
        await FailsAsync(d, AssignSql("Company", "IN", Category, null, null, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");        // company scope with a category
        await FailsAsync(d, AssignSql("Company", "IN", null, null, ProductA, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");
        await FailsAsync(d, AssignSql("Category", "IN", null, null, null, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");           // category scope without a category
        await FailsAsync(d, AssignSql("Category", "IN", Category, Sub, null, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");        // ... or with a subcategory
        await FailsAsync(d, AssignSql("Subcategory", "IN", null, Sub, null, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");         // subcategory scope needs its category too
        await FailsAsync(d, AssignSql("Subcategory", "IN", Category, null, null, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");
        await FailsAsync(d, AssignSql("Product", "IN", null, null, null, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");           // product scope without a product
        await FailsAsync(d, AssignSql("Product", "IN", Category, null, ProductA, ProfIn), "23514", "CK_TaxAssignments_Scope_Shape");
        await ExecuteAsync(d, AssignSql("Company", "IN", null, null, null, ProfIn));
        await ExecuteAsync(d, AssignSql("Category", "IN", Category, null, null, ProfIn));
        await ExecuteAsync(d, AssignSql("Subcategory", "IN", Category, Sub, null, ProfIn, ProfIn2));
        await ExecuteAsync(d, AssignSql("Product", "IN", null, null, ProductA, ProfIn));
    }

    [Fact]
    public async Task TaxAssignments_PartialUniqueIndexes_AllowOneAssignmentPerTargetAndCountry()
    {
        await using var d = await SeedAsync();
        await ExecuteAsync(d, AssignSql("Company", "IN", null, null, null, ProfIn));
        await ExecuteAsync(d, AssignSql("Category", "IN", Category, null, null, ProfIn));
        await ExecuteAsync(d, AssignSql("Subcategory", "IN", Category, Sub, null, ProfIn));
        await ExecuteAsync(d, AssignSql("Product", "IN", null, null, ProductA, ProfIn));

        await FailsAsync(d, AssignSql("Company", "IN", null, null, null, ProfIn2), "23505", "IX_TaxAssignments_Scope_Company");
        await FailsAsync(d, AssignSql("Category", "IN", Category, null, null, ProfIn2), "23505", "IX_TaxAssignments_Scope_Category");
        await FailsAsync(d, AssignSql("Subcategory", "IN", Category, Sub, null, ProfIn2), "23505", "IX_TaxAssignments_Scope_Subcategory");
        await FailsAsync(d, AssignSql("Product", "IN", null, null, ProductA, ProfIn2), "23505", "IX_TaxAssignments_Scope_Product");

        // other targets, other countries and other companies are free
        await ExecuteAsync(d, AssignSql("Company", "AE", null, null, null, ProfAe));
        await ExecuteAsync(d, AssignSql("Product", "AE", null, null, ProductA, ProfAe));
        await ExecuteAsync(d, AssignSql("Category", "IN", Category2, null, null, ProfIn));
        await ExecuteAsync(d, AssignSql("Product", "IN", null, null, ProductB, ProfIn));
        await ExecuteAsync(d, AssignSql("Company", "IN", null, null, null, ProfOther, company: Other));
    }

    [Fact]
    public async Task TaxAssignments_CompositeForeignKeys_EnforceCountryOfTheProfileAndOwnershipByTheCompany()
    {
        await using var d = await SeedAsync();

        // profile country = assignment country (sales and purchase side)
        await FailsAsync(d, AssignSql("Company", "IN", null, null, null, ProfAe), "23503", "FK_TaxAssignments_TaxProfiles_Sales");
        await FailsAsync(d, AssignSql("Company", "AE", null, null, null, ProfIn), "23503", "FK_TaxAssignments_TaxProfiles_Sales");
        await FailsAsync(d, AssignSql("Company", "IN", null, null, null, ProfIn, ProfAe), "23503", "FK_TaxAssignments_TaxProfiles_Purchase");
        // another company's profile, product, category
        await FailsAsync(d, AssignSql("Company", "IN", null, null, null, ProfOther), "23503", "FK_TaxAssignments_TaxProfiles_Sales");
        await FailsAsync(d, AssignSql("Product", "IN", null, null, ProductOther, ProfIn), "23503", "FK_TaxAssignments_Products_CompanyId_ProductId");
        await FailsAsync(d, AssignSql("Category", "IN", CategoryOther, null, null, ProfIn), "23503", "FK_TaxAssignments_ProductCategories_CompanyId_CategoryId");
        // a subcategory under another category than the one named
        await FailsAsync(d, AssignSql("Subcategory", "IN", Category, Sub2, null, ProfIn), "23503", "FK_TaxAssignments_ProductSubcategories_Category_Subcategory");
        await FailsAsync(d, AssignSql("Product", "IN", null, null, Guid.NewGuid(), ProfIn), "23503", "FK_TaxAssignments_Products_CompanyId_ProductId");
        // restrict: a profile that is assigned cannot be deleted
        await ExecuteAsync(d, AssignSql("Company", "IN", null, null, null, ProfIn));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, $"""DELETE FROM "TaxProfiles" WHERE "Id" = '{ProfIn}'"""));
        Assert.Equal("23503", ex.SqlState);
    }

    // ------------------------------------------------------------------------------------------------------------------ columns on existing tables

    [Fact]
    public async Task HsnCode_FormatCheck_AcceptsFourSixOrEightDigitsOnly_OnProductsAndSubcategories()
    {
        await using var d = await SeedAsync();

        foreach (var good in new[] { "1234", "123456", "12345678" })
        {
            await ExecuteAsync(d, $"""UPDATE "Products" SET "HsnCode" = '{good}' WHERE "Id" = '{ProductA}'""");
            await ExecuteAsync(d, $"""UPDATE "ProductSubcategories" SET "HsnCode" = '{good}' WHERE "Id" = '{Sub}'""");
        }
        await ExecuteAsync(d, $"""UPDATE "Products" SET "HsnCode" = NULL WHERE "Id" = '{ProductA}'""");
        foreach (var bad in new[] { "123", "12345", "1234567", "12A4", "ABCD", "12 34", "1234.56", "" })
        {
            await FailsAsync(d, $"""UPDATE "Products" SET "HsnCode" = '{bad}' WHERE "Id" = '{ProductA}'""", "23514", "CK_Products_HsnCode_Format");
            await FailsAsync(d, $"""UPDATE "ProductSubcategories" SET "HsnCode" = '{bad}' WHERE "Id" = '{Sub}'""", "23514", "CK_ProductSubcategories_HsnCode_Format");
        }
        var tooLong = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(d, $"""UPDATE "Products" SET "HsnCode" = '123456789' WHERE "Id" = '{ProductA}'"""));
        Assert.Equal("22001", tooLong.SqlState);   // varchar(8)
        await ExecuteAsync(d, $"""UPDATE "Products" SET "PriceIncludesTax" = true WHERE "Id" = '{ProductA}'; UPDATE "Products" SET "PriceIncludesTax" = false WHERE "Id" = '{ProductB}'""");   // tri-state: NULL = company setting
    }

    [Fact]
    public async Task MaxBillDiscountPercent_RangeCheck_AllowsEmptyAndZeroToHundred()
    {
        await using var d = await SeedAsync();
        await ExecuteAsync(d, $"""
            INSERT INTO "BusinessSettings" ("Id", "CompanyId", "BusinessName", "InvoicePrefix", "BaseCurrencyId", "SellingPricesIncludeTax")
            VALUES (gen_random_uuid(), '{Co}', 'Tax settings', 'INV', (SELECT "Id" FROM "Currencies" LIMIT 1), true)
            """);
        string Set(string value) => $"""UPDATE "BusinessSettings" SET "MaxBillDiscountPercent" = {value} WHERE "CompanyId" = '{Co}'""";

        foreach (var good in new[] { "NULL", "0", "0.01", "10", "100", "100.00" }) await ExecuteAsync(d, Set(good));
        foreach (var bad in new[] { "-0.01", "-1", "100.01", "101" }) await FailsAsync(d, Set(bad), "23514", "CK_BusinessSettings_MaxBillDiscountPercent_Range");
    }

    // ------------------------------------------------------------------------------------------------------------------ line tax tables

    public static IEnumerable<object[]> LineTables() =>
    [
        ["SalesInvoiceLineTaxes", "SalesInvoiceLineId", "FK_SalesInvoiceLineTaxes_SalesInvoiceLines", "FK_SalesInvoiceLineTaxes_TaxComponents_TaxComponentId", "CK_SalesInvoiceLineTaxes"],
        ["PurchaseOrderLineTaxes", "PurchaseOrderLineId", "FK_PurchaseOrderLineTaxes_PurchaseOrderLines", "FK_PurchaseOrderLineTaxes_TaxComponents_TaxComponentId", "CK_PurchaseOrderLineTaxes"],
        ["SalesReturnLineTaxes", "SalesReturnLineId", "FK_SalesReturnLineTaxes_SalesReturnLines", "FK_SalesReturnLineTaxes_TaxComponents_TaxComponentId", "CK_SalesReturnLineTaxes"],
        ["PurchaseReturnLineTaxes", "PurchaseReturnLineId", "FK_PurchaseReturnLineTaxes_PurchaseReturnLines", "FK_PurchaseReturnLineTaxes_TaxComponents_TaxComponentId", "CK_PurchaseReturnLineTaxes"],
    ];

    [Theory]
    [MemberData(nameof(LineTables))]
    public async Task LineTaxTables_RejectBadKindMethodShapeUnknownLineAndUnknownComponent(string table, string lineColumn, string lineFk, string componentFk, string ckPrefix)
    {
        await using var d = await SeedAsync();
        string Insert(string line, string component, string kind = "Cgst", string method = "Percent", string rate = "2.5", string amount = "NULL") => $"""
            INSERT INTO "{table}" ("Id", "{lineColumn}", "TaxComponentId", "ComponentCode", "Kind", "Method", "RatePercent", "AmountPerBaseUnit", "BaseAmount", "TaxAmount")
            VALUES (gen_random_uuid(), {line}, {component}, 'CGST', '{kind}', '{method}', {rate}, {amount}, 100, 2.5)
            """;
        var missingLine = "gen_random_uuid()";

        await FailsAsync(d, Insert(missingLine, "NULL", kind: "Bogus"), "23514", ckPrefix + "_Kind");
        await FailsAsync(d, Insert(missingLine, "NULL", rate: "2.5", amount: "1"), "23514", ckPrefix + "_MethodShape");
        await FailsAsync(d, Insert(missingLine, "NULL", rate: "NULL"), "23514", ckPrefix + "_MethodShape");
        await FailsAsync(d, Insert(missingLine, "NULL", rate: "-1"), "23514", ckPrefix + "_MethodShape");
        await FailsAsync(d, Insert(missingLine, "NULL", method: "AmountPerBaseUnit", rate: "NULL", amount: "-2"), "23514", ckPrefix + "_MethodShape");
        await FailsAsync(d, Insert(missingLine, "NULL"), "23503", lineFk);                                  // the line FK is enforced from the first insert
        await FailsAsync(d, Insert(missingLine, $"'{Guid.NewGuid()}'"), "23503", null);                    // an unknown component (either FK may report first)
        _ = componentFk;
    }

    [Fact]
    public async Task LineTaxTables_ComponentForeignKey_IsRestrict_AndLegacyRowsMayHaveNoComponent()
    {
        await using var d = await postgres.CreateDatabaseAsync(migrate: true);

        var rules = await QueryAsync(d, """
            SELECT t.relname || '|' || c.conname || '|' || c.confdeltype::text FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
            WHERE c.contype = 'f' AND (c.conname LIKE 'FK\_%LineTaxes\_TaxComponents\_TaxComponentId' OR c.conname LIKE 'FK\_%LineTaxes\_%Lines') ORDER BY 1
            """, r => r.GetString(0));

        Assert.Equal(8, rules.Count);
        Assert.All(rules.Where(r => r.Contains("_TaxComponents_", StringComparison.Ordinal)), r => Assert.EndsWith("|r", r, StringComparison.Ordinal));   // never cascade a component away from history
        Assert.All(rules.Where(r => !r.Contains("_TaxComponents_", StringComparison.Ordinal)), r => Assert.EndsWith("|c", r, StringComparison.Ordinal));   // the rows follow their line
    }
}
