using System.Net;
using System.Net.Http.Json;
using Sellora.Api.Contracts;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.Phase6Support;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Platform Console data reset through the real pipeline on PostgreSQL: off by default (403 and no change), platform administrators
/// only, a preview that changes nothing, a typed database name that must match, and a reset that runs the embedded
/// scripts/sql/dev-reset-fresh-setup.sql: business data of the whole database is removed, customers and suppliers follow the options,
/// the walk-in customer and one Main Cash account survive, document numbers restart and one audit row names the actor.
/// This is the first execution of the reset script's wipe statements on PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlatformDataResetApiTests(PostgresFixture postgres)
{
    private const string Base = "/api/v1/platform/data-reset";
    private static readonly Dictionary<string, string> ResetOn = new() { ["Platform:DataResetEnabled"] = "true" };

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ProblemCodeAsync());
    }

    private static async Task<(ApiHost Host, Guid Ops)> StartWithOperatorAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null)
    {
        var host = await ApiHost.StartAsync(postgres, settings);
        var ops = await AddUserAsync(host, "ops.alice", administrator: true, company: null);
        await GrantPlatformAdminAsync(host, ops);
        return (host, ops);
    }

    private static Task<long> CountAsync(ApiHost host, string table, string where = "true") =>
        ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "{table}" WHERE {where}""");

    private static Task<string> DatabaseNameAsync(ApiHost host) => ScalarAsync<string>(host.Database, "SELECT current_database()");

    private static Task<long> ResetAuditRowsAsync(ApiHost host) => CountAsync(host, "PlatformAuditEvents", """ "Action" = 'ops.dev_reset_fresh_setup' """);

    [Fact]
    public async Task SwitchOff_ByDefault_StatusIsDisabled_PreviewAndResetAre403_AndNothingChanges()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        var units = await CountAsync(host, "Units");
        Assert.True(units > 0);

        var status = await client.GetFromJsonAsync<DataResetStatusDto>($"{Base}/status", ApiHost.Json);

        Assert.False(status!.Enabled);
        await AssertCodeAsync(await client.GetAsync($"{Base}/preview"), HttpStatusCode.Forbidden, "data_reset_disabled");
        await AssertCodeAsync(await client.PostAsJsonAsync(Base, new DataResetRequest(database), ApiHost.Json), HttpStatusCode.Forbidden, "data_reset_disabled");
        Assert.Equal(units, await CountAsync(host, "Units"));
        Assert.Equal(0L, await ResetAuditRowsAsync(host));
    }

    [Fact]
    public async Task CompanyAdministrator_IsNotAPlatformSession_AndIsRefused()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var company = await host.LoginClientAsync();
        using var anonymous = host.NewClient();
        var database = await DatabaseNameAsync(host);
        var units = await CountAsync(host, "Units");

        Assert.Equal(HttpStatusCode.Unauthorized, (await company.GetAsync($"{Base}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await company.GetAsync($"{Base}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await company.PostAsJsonAsync(Base, new DataResetRequest(database), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Base, new DataResetRequest(database), ApiHost.Json)).StatusCode);
        Assert.Equal(units, await CountAsync(host, "Units"));
    }

    [Fact]
    public async Task Preview_ListsTheTablesWithTheirRowCounts_FollowsTheOptions_AndChangesNothing()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        var units = await CountAsync(host, "Units");
        var products = await CountAsync(host, "Products");

        var status = await client.GetFromJsonAsync<DataResetStatusDto>($"{Base}/status", ApiHost.Json);
        var report = await client.GetFromJsonAsync<DataResetReportDto>($"{Base}/preview", ApiHost.Json);
        var everything = await client.GetFromJsonAsync<DataResetReportDto>($"{Base}/preview?wipeCustomers=true&wipeSuppliers=true", ApiHost.Json);
        var keeping = await client.GetFromJsonAsync<DataResetReportDto>($"{Base}/preview?keepTaxes=true&keepCategories=true&keepExpenseCategories=true&keepAccounts=true", ApiHost.Json);

        Assert.True(status!.Enabled);
        Assert.False(report!.Applied);
        Assert.Equal(database, report.Database);
        Assert.Equal(["customers", "suppliers"], report.KeptMasters);
        Assert.Equal(51, report.Tables.Count);   // 42 + the nine tax profile tables
        Assert.DoesNotContain(report.Tables, t => t.Table is "Customers" or "Suppliers");
        Assert.Equal(units, report.Tables.Single(t => t.Table == "Units").RowsBefore);
        Assert.Equal(products, report.Tables.Single(t => t.Table == "Products").RowsBefore);
        Assert.All(report.Tables, t => Assert.Null(t.RowsAfter));
        Assert.Equal(53, everything!.Tables.Count);   // 44 + the nine tax profile tables
        Assert.Empty(everything.KeptMasters);
        Assert.Equal(42, keeping!.Tables.Count);   // 53 listed tables minus customers, suppliers, the five tax masters, two category tables, expense categories, accounts
        Assert.DoesNotContain(keeping.Tables, t => t.Table is "Taxes" or "TaxComponents" or "TaxProfiles" or "TaxProfileVersions" or "TaxProfileVersionComponents"
            or "ProductCategories" or "ProductSubcategories" or "ExpenseCategories" or "FinancialAccounts");
        // T23: TaxAssignments is always listed, and reported as a partial action when taxes are kept.
        Assert.StartsWith("partial", keeping.Tables.Single(t => t.Table == "TaxAssignments").Action);
        Assert.StartsWith("remove all", report.Tables.Single(t => t.Table == "TaxAssignments").Action);

        Assert.Equal(units, await CountAsync(host, "Units"));
        Assert.Equal(products, await CountAsync(host, "Products"));
        Assert.Equal(0L, await ResetAuditRowsAsync(host));
    }

    [Fact]
    public async Task Reset_WithAnotherDatabaseName_Is400OnTheField_AndChangesNothing()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var units = await CountAsync(host, "Units");

        var response = await client.PostAsJsonAsync(Base, new DataResetRequest("not-this-database"), ApiHost.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("confirmDatabase", out _));
        Assert.Equal(units, await CountAsync(host, "Units"));
        Assert.Equal(0L, await ResetAuditRowsAsync(host));
    }

    [Fact]
    public async Task Reset_RemovesBusinessData_KeepsSetupCustomersAndSuppliers_RestoresWalkInAndMainCash_AndAuditsTheActor()
    {
        var (host, ops) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        // A used document series, so the restart at 1 is visible.
        await ExecuteAsync(host.Database, """UPDATE "DocumentSequences" SET "NextValue" = 7""");
        var customers = await CountAsync(host, "Customers");
        var suppliers = await CountAsync(host, "Suppliers");
        var users = await CountAsync(host, "Users");
        var companies = await CountAsync(host, "Companies");
        var oldAccount = await ScalarAsync<Guid>(host.Database, """SELECT "Id" FROM "FinancialAccounts" ORDER BY "CreatedAt" LIMIT 1""");
        foreach (var table in new[] { "Units", "Taxes", "Products", "ProductUnits", "ProductCategories", "ExpenseCategories" })
        {
            Assert.True(await CountAsync(host, table) > 0, $"{table} is seeded before the reset");
        }

        var response = await client.PostAsJsonAsync(Base, new DataResetRequest(database), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<DataResetReportDto>(ApiHost.Json))!;
        Assert.True(report.Applied);
        Assert.Equal(database, report.Database);
        Assert.Equal(51, report.Tables.Count);   // 42 + the nine tax profile tables
        Assert.All(report.Tables, t => Assert.NotNull(t.RowsBefore));
        Assert.All(report.Tables.Where(t => t.Table != "FinancialAccounts"), t => Assert.Equal(0L, t.RowsAfter));
        Assert.Equal(companies, report.Tables.Single(t => t.Table == "FinancialAccounts").RowsAfter);

        foreach (var table in new[] { "Units", "Products", "ProductUnits", "SellingPrices", "ProductCategories", "ProductSubcategories", "StockItems",
                     "StockMovements", "PurchaseOrders", "SalesInvoices", "FinancialTransactions", "Expenses", "ExpenseCategories", "IdempotencyKeys" })
        {
            Assert.Equal(0L, await CountAsync(host, table));
        }
        // taxes are not kept: the old ones are gone and only the standard profiles (with their mirror taxes) are back (T23)
        Assert.Equal(0L, await CountAsync(host, "TaxProfiles", "\"Code\" LIKE 'LEGACY-%'"));
        Assert.Equal(await CountAsync(host, "TaxProfiles"), await CountAsync(host, "Taxes"));
        Assert.Equal(0L, await MirrorMismatchesAsync(host));
        // kept by default
        Assert.Equal(customers, await CountAsync(host, "Customers"));
        Assert.Equal(suppliers, await CountAsync(host, "Suppliers"));
        Assert.Equal(1L, await CountAsync(host, "Customers", """ "IsWalkIn" """));
        // one fresh, empty Main Cash per company
        Assert.Equal(companies, await CountAsync(host, "FinancialAccounts"));
        Assert.Equal(companies, await CountAsync(host, "FinancialAccounts", """ "Name" = 'Main Cash' AND "Kind" = 'Cash' AND "IsActive" """));
        Assert.Equal(0L, await CountAsync(host, "FinancialAccounts", $""" "Id" = '{oldAccount}' """));
        // document numbers restart
        Assert.Equal(0L, await CountAsync(host, "DocumentSequences", """ "NextValue" <> 1 """));
        // setup and identity untouched
        Assert.Equal(users, await CountAsync(host, "Users"));
        Assert.Equal(companies, await CountAsync(host, "Companies"));
        Assert.True(await CountAsync(host, "Permissions") > 0);
        Assert.True(await CountAsync(host, "Branches") > 0);
        Assert.True(await CountAsync(host, "StockLocations") > 0);
        // one audit row, with the platform administrator and the request id (not the SQL-tool default)
        Assert.Equal(1L, await ResetAuditRowsAsync(host));
        Assert.Equal(1L, await CountAsync(host, "PlatformAuditEvents", $""" "Action" = 'ops.dev_reset_fresh_setup' AND "ActorUserId" = '{ops}' AND "CorrelationId" <> 'ops-script' """));
        // nothing of the script's session result sheet stays behind
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, "SELECT COUNT(*) FROM pg_class WHERE relname = 'sellora_reset_report'"));

        // "run whenever needed": a second reset on the now empty database works and is audited again
        var again = await client.PostAsJsonAsync(Base, new DataResetRequest(database), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2L, await ResetAuditRowsAsync(host));
        Assert.Equal(companies, await CountAsync(host, "FinancialAccounts"));

        // the company still signs in afterwards
        using var company = await host.LoginClientAsync();
    }

    [Fact]
    public async Task Reset_WithOptions_AlsoRemovesCustomersAndSuppliersExceptWalkIn_AndKeepsTheChosenMasters()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        var taxes = await CountAsync(host, "Taxes");
        var account = await ScalarAsync<Guid>(host.Database, """SELECT "Id" FROM "FinancialAccounts" ORDER BY "CreatedAt" LIMIT 1""");
        Assert.True(await CountAsync(host, "Customers", """ NOT "IsWalkIn" """) > 0);
        Assert.True(await CountAsync(host, "Suppliers") > 0);
        Assert.True(taxes > 0);

        var response = await client.PostAsJsonAsync(Base,
            new DataResetRequest(database, WipeCustomers: true, WipeSuppliers: true, KeepTaxes: true, KeepAccounts: true), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<DataResetReportDto>(ApiHost.Json))!;
        Assert.Equal(["accounts", "taxes"], report.KeptMasters);
        Assert.Equal(0L, await CountAsync(host, "Suppliers"));
        Assert.Equal(1L, await CountAsync(host, "Customers"));
        Assert.Equal(1L, await CountAsync(host, "Customers", """ "IsWalkIn" """));
        Assert.Equal(taxes, await CountAsync(host, "Taxes"));
        Assert.Equal(1L, await CountAsync(host, "FinancialAccounts", $""" "Id" = '{account}' """));
        Assert.Equal(0L, await CountAsync(host, "Units"));
        Assert.Equal(0L, await CountAsync(host, "Products"));
    }

    // ---- T23: tax tables ----------------------------------------------------------------------------------------------------------

    private const string DefaultCompany = "0c0a1f00-0000-4000-8000-000000000001";

    private static async Task<HttpResponseMessage> CreateProductAfterResetAsync(ApiHost host)
    {
        var unit = Guid.NewGuid();
        await ExecuteAsync(host.Database, $"""INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES ('{unit}', 'Piece', 'pc', 'Count', 0, NULL, true)""");
        using var company = await host.LoginClientAsync();
        return await company.PostAsJsonAsync("/api/v1/products",
            new UpsertProductRequest("AFTER-RESET", "After reset", null, null, unit, 0m, [new UpsertProductUnitRequest(null, unit, null, 1m, "Each", null, true, true, true, true, true)]), ApiHost.Json);
    }

    private static Task<long> MirrorMismatchesAsync(ApiHost host) => ScalarAsync<long>(host.Database, """
        SELECT (SELECT COUNT(*) FROM "Taxes" t WHERE NOT EXISTS (SELECT 1 FROM "TaxProfiles" p WHERE p."LegacyTaxId" = t."Id"))
             + (SELECT COUNT(*) FROM "TaxProfiles" p WHERE NOT EXISTS (SELECT 1 FROM "Taxes" t WHERE t."Id" = p."LegacyTaxId"))
        """);

    [Fact]
    public async Task Reset_KeepTaxes_KeepsTheProfileMastersAndOnlyTheCompanyDefault_AndAProductCanBeCreatedAfterwards()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        var masters = new[] { "Taxes", "TaxComponents", "TaxProfiles", "TaxProfileVersions", "TaxProfileVersionComponents" };
        var before = new List<long>();
        foreach (var table in masters) before.Add(await CountAsync(host, table));
        var companyDefaults = await CountAsync(host, "TaxAssignments", """ "Scope" = 'Company' """);
        Assert.True(await CountAsync(host, "TaxAssignments", """ "Scope" = 'Product' """) > 0, "the demo products carry assignments before the reset");
        Assert.True(companyDefaults > 0);

        var response = await client.PostAsJsonAsync(Base, new DataResetRequest(database, KeepTaxes: true), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<DataResetReportDto>(ApiHost.Json))!;
        for (var i = 0; i < masters.Length; i++) Assert.Equal(before[i], await CountAsync(host, masters[i]));
        Assert.Equal(companyDefaults, await CountAsync(host, "TaxAssignments"));
        Assert.Equal(companyDefaults, await CountAsync(host, "TaxAssignments", """ "Scope" = 'Company' """));
        Assert.Equal(0L, await MirrorMismatchesAsync(host));
        var row = report.Tables.Single(t => t.Table == "TaxAssignments");
        Assert.StartsWith("partial", row.Action);
        Assert.Equal(companyDefaults, row.RowsAfter);
        Assert.Contains("Tax setup", report.Message);

        var created = await CreateProductAfterResetAsync(host);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Reset_KeepTaxes_RestoresAMissingCompanyDefault_PerT21()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        await ExecuteAsync(host.Database, $"""DELETE FROM "TaxAssignments" WHERE "Scope" = 'Company' AND "CompanyId" = '{DefaultCompany}'""");

        var response = await client.PostAsJsonAsync(Base, new DataResetRequest(database, KeepTaxes: true), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<DataResetReportDto>(ApiHost.Json))!;
        Assert.Equal(1L, await CountAsync(host, "TaxAssignments", $""" "Scope" = 'Company' AND "CompanyId" = '{DefaultCompany}' """));
        Assert.Contains("company default restored", report.Message);
        Assert.Equal(HttpStatusCode.Created, (await CreateProductAfterResetAsync(host)).StatusCode);
    }

    [Fact]
    public async Task Reset_KeepTaxesAndCategories_KeepsCategoryAndSubcategoryAssignmentsOfTheKeptCategories()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        await ExecuteAsync(host.Database, $"""
            INSERT INTO "TaxAssignments" ("Id", "CompanyId", "Scope", "CountryId", "CategoryId", "SubcategoryId", "ProductId", "SalesTaxProfileId", "PurchaseTaxProfileId", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), c."CompanyId", 'Category', d."CountryId", c."Id", NULL, NULL, d."SalesTaxProfileId", NULL, now(), NULL
            FROM (SELECT "Id", "CompanyId" FROM "ProductCategories" WHERE "CompanyId" = '{DefaultCompany}' ORDER BY "Name" LIMIT 1) c
            JOIN "TaxAssignments" d ON d."CompanyId" = c."CompanyId" AND d."Scope" = 'Company'
            """);
        var categoryRows = await CountAsync(host, "TaxAssignments", """ "Scope" = 'Category' """);
        var categories = await CountAsync(host, "ProductCategories");
        Assert.True(categoryRows > 0);

        var response = await client.PostAsJsonAsync(Base, new DataResetRequest(database, KeepTaxes: true, KeepCategories: true), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(categories, await CountAsync(host, "ProductCategories"));
        Assert.Equal(categoryRows, await CountAsync(host, "TaxAssignments", """ "Scope" = 'Category' """));
        Assert.Equal(0L, await CountAsync(host, "TaxAssignments", """ "Scope" = 'Product' """));
        Assert.Equal(0L, await CountAsync(host, "TaxAssignments", """ "Scope" IN ('Category', 'Subcategory') AND NOT EXISTS (SELECT 1 FROM "ProductCategories" c WHERE c."Id" = "TaxAssignments"."CategoryId") """));
    }

    [Fact]
    public async Task Reset_WithoutKeepTaxes_ReSeedsTheStandardProfilesAndTheDefault_AndAProductCanBeCreatedAfterwards()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        // India (the demo company): the standard set is five profiles; any extra profile or tax made before is gone.
        await ExecuteAsync(host.Database, $"""UPDATE "BusinessSettings" SET "CountryId" = (SELECT "Id" FROM "Countries" WHERE "Code" = 'IN') WHERE "CompanyId" = '{DefaultCompany}'""");

        var response = await client.PostAsJsonAsync(Base, new DataResetRequest(database), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<DataResetReportDto>(ApiHost.Json))!;
        Assert.Contains("re-seeded", report.Message);
        var codes = await ScalarAsync<string>(host.Database, $"""SELECT string_agg("Code", ',' ORDER BY "Code") FROM "TaxProfiles" WHERE "CompanyId" = '{DefaultCompany}'""");
        Assert.Equal("GST-18,GST-40,GST-5,GST-EXEMPT,GST-NIL", codes);
        Assert.Equal(5L, await CountAsync(host, "Taxes", $""" "CompanyId" = '{DefaultCompany}' """));
        Assert.Equal(0L, await MirrorMismatchesAsync(host));
        Assert.Equal(1L, await CountAsync(host, "TaxAssignments", $""" "CompanyId" = '{DefaultCompany}' AND "Scope" = 'Company' """));
        Assert.Equal(0L, await CountAsync(host, "TaxAssignments", """ "Scope" <> 'Company' """));
        Assert.StartsWith("remove all", report.Tables.Single(t => t.Table == "TaxAssignments").Action);
        Assert.Equal(HttpStatusCode.Created, (await CreateProductAfterResetAsync(host)).StatusCode);
    }

    [Fact]
    public async Task Reset_WithoutKeepTaxes_ForACountryWithoutAStandardSeed_SaysSo()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, ResetOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var database = await DatabaseNameAsync(host);
        await ExecuteAsync(host.Database, $"""UPDATE "BusinessSettings" SET "CountryId" = (SELECT "Id" FROM "Countries" WHERE "Code" = 'QA'), "StateId" = NULL WHERE "CompanyId" = '{DefaultCompany}'""");

        var response = await client.PostAsJsonAsync(Base, new DataResetRequest(database), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<DataResetReportDto>(ApiHost.Json))!;
        Assert.Contains("no standard tax seed exists for QA", report.Message);
        Assert.Equal(0L, await CountAsync(host, "TaxProfiles", $""" "CompanyId" = '{DefaultCompany}' """));
    }
}
