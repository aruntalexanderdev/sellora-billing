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
        Assert.Equal(42, report.Tables.Count);
        Assert.DoesNotContain(report.Tables, t => t.Table is "Customers" or "Suppliers");
        Assert.Equal(units, report.Tables.Single(t => t.Table == "Units").RowsBefore);
        Assert.Equal(products, report.Tables.Single(t => t.Table == "Products").RowsBefore);
        Assert.All(report.Tables, t => Assert.Null(t.RowsAfter));
        Assert.Equal(44, everything!.Tables.Count);
        Assert.Empty(everything.KeptMasters);
        Assert.Equal(37, keeping!.Tables.Count);
        Assert.DoesNotContain(keeping.Tables, t => t.Table is "Taxes" or "ProductCategories" or "ProductSubcategories" or "ExpenseCategories" or "FinancialAccounts");

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
        Assert.Equal(42, report.Tables.Count);
        Assert.All(report.Tables, t => Assert.NotNull(t.RowsBefore));
        Assert.All(report.Tables.Where(t => t.Table != "FinancialAccounts"), t => Assert.Equal(0L, t.RowsAfter));
        Assert.Equal(companies, report.Tables.Single(t => t.Table == "FinancialAccounts").RowsAfter);

        foreach (var table in new[] { "Units", "Taxes", "Products", "ProductUnits", "SellingPrices", "ProductCategories", "ProductSubcategories", "StockItems",
                     "StockMovements", "PurchaseOrders", "SalesInvoices", "FinancialTransactions", "Expenses", "ExpenseCategories", "IdempotencyKeys" })
        {
            Assert.Equal(0L, await CountAsync(host, table));
        }
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
}
