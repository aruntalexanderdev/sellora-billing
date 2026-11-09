using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// T6 — HTTP isolation. Companies A (default) and B hold identical business keys (SKU, barcode, tax / category / account names,
/// document numbers). Through the real pipeline: every company-owned GET shows the caller's company only (A never sees B's ids and
/// vice versa; the other company's detail answers 404); every write naming the other company's ids is refused (400/404) and changes
/// nothing; scoped uniques let both companies create the same name; and every controller action must be classified.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CompanyIsolationApiTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IsolationWorld? _world;

    private async Task<IsolationWorld> WorldAsync()
    {
        await Gate.WaitAsync();
        try { return _world ??= await IsolationWorld.BuildAsync(postgres); }
        finally { Gate.Release(); }
    }

    private static string Own(Ids ids) => ids.Token;

    [Fact]
    public async Task Setup_BothCompaniesHoldTheSameBusinessKeysAndDocumentNumbers()
    {
        var w = await WorldAsync();
        var db = w.Host.Database;

        async Task<List<string>> Keys(string sql) => await QueryAsync(db, sql, r => r.GetString(0));
        Assert.Equal(2L, await ScalarAsync<long>(db, """SELECT COUNT(*) FROM "Products" WHERE "Sku" = 'STN-PEN'"""));
        Assert.Equal(2L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "ProductUnits" WHERE "Barcode" = '{IsolationCases.SharedBarcode}' AND "IsActive" """));
        Assert.Equal(["PUR-0001", "PUR-0001"], await Keys("""SELECT "PurchaseNo" FROM "PurchaseOrders" ORDER BY "CompanyId" """));
        Assert.Single(await Keys("""SELECT "InvoiceNo" FROM "SalesInvoices" GROUP BY "InvoiceNo" HAVING COUNT(DISTINCT "CompanyId") = 2"""));
        Assert.Equal(["EXP-0001", "EXP-0001"], await Keys("""SELECT "ExpenseNo" FROM "Expenses" ORDER BY "CompanyId" """));
        Assert.NotEmpty(w.RowIdsOfA);
        Assert.NotEmpty(w.RowIdsOfB);
        Assert.Empty(w.RowIdsOfA.Intersect(w.RowIdsOfB));
    }

    [Fact]
    public async Task Reads_AsEachCompany_ShowOnlyThatCompanysRowsAndOtherCompanysDetailsAre404()
    {
        var w = await WorldAsync();
        var failures = new List<string>();

        foreach (var (self, other, foreignIds, ownIds, label) in new[]
        {
            (w.A, w.B, w.RowIdsOfB, w.RowIdsOfA, "A"), (w.B, w.A, w.RowIdsOfA, w.RowIdsOfB, "B"),
        })
        {
            using var client = w.Host.NewClient(Own(self));
            foreach (var read in IsolationCases.Reads)
            {
                var response = await client.GetAsync(read.Route(self));
                var body = (await response.Content.ReadAsStringAsync()).ToLowerInvariant();
                if (response.StatusCode != HttpStatusCode.OK) { failures.Add($"{label} {read.Action}: own request returned {(int)response.StatusCode} {body[..Math.Min(150, body.Length)]}"); continue; }
                var leaked = foreignIds.FirstOrDefault(body.Contains);
                if (leaked is not null) failures.Add($"{label} {read.Action}: response contains id {leaked} of the other company");
                if (read.ShowsRowIds && !ownIds.Any(body.Contains)) failures.Add($"{label} {read.Action}: response shows none of the caller's rows (vacuous): {body[..Math.Min(200, body.Length)]}");

                if (!read.Detail) continue;
                var foreign = await client.GetAsync(read.Route(other));
                var foreignBody = (await foreign.Content.ReadAsStringAsync()).Trim();
                var emptyOk = read.EmptyIsNotFound && foreign.StatusCode == HttpStatusCode.OK && foreignBody == "[]";
                if (foreign.StatusCode != HttpStatusCode.NotFound && !emptyOk) failures.Add($"{label} {read.Action}: other company's id answered {(int)foreign.StatusCode}, expected 404");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Defect probe: <c>GET financial-transactions</c> reports <c>total</c> &gt; 0 but returns no <c>items</c> — the list projection
    /// walks required navigations to company-filtered entities (PurchasePayment → PurchaseOrder, Expense → Category, …) from optional ones,
    /// which EF turns into inner joins once the "Company" filter applies.</summary>
    [Fact]
    public async Task FinancialTransactionsList_ItemsMatchTheReportedTotal()
    {
        var w = await WorldAsync();
        using var a = w.Host.NewClient(Own(w.A));

        var body = await a.GetFromJsonAsync<JsonElement>("/api/v1/financial-transactions?pageSize=100", ApiHost.Json);

        var total = body.GetProperty("total").GetInt32();
        Assert.True(total > 0, "Fixture should have financial transactions (purchase payment, expense).");
        Assert.Equal(total, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Writes_NamingTheOtherCompanysIds_AreRefusedAndChangeNothing()
    {
        var w = await WorldAsync();
        var failures = new List<string>();
        var before = (A: await SnapshotAsync(w, w.A.Company), B: await SnapshotAsync(w, w.B.Company));

        foreach (var (self, other) in new[] { (w.A, w.B), (w.B, w.A) })
        {
            using var client = w.Host.NewClient(Own(self));
            foreach (var write in IsolationCases.Writes)
            {
                var (route, body) = write.Build(self, other);
                using var request = new HttpRequestMessage(write.Method, route);
                if (body is not null) request.Content = JsonContent.Create(body, options: ApiHost.Json);
                var response = await client.SendAsync(request);
                if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    failures.Add($"{(self == w.A ? "A" : "B")} {write.Action} [{write.Scenario}]: {(int)response.StatusCode} {text[..Math.Min(150, text.Length)]}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(before.A, await SnapshotAsync(w, w.A.Company));
        Assert.Equal(before.B, await SnapshotAsync(w, w.B.Company));
    }

    [Fact]
    public async Task Create_SameNameInBothCompanies_SucceedsButDuplicateWithinACompanyIsRefused()
    {
        var w = await WorldAsync();
        using var a = w.Host.NewClient(Own(w.A));
        using var b = w.Host.NewClient(Own(w.B));
        var taxName = "Scoped " + Guid.NewGuid().ToString("N")[..6];

        var createA = await a.PostAsJsonAsync("/api/v1/taxes", new UpsertTaxRequest(taxName, 7m), ApiHost.Json);
        var createB = await b.PostAsJsonAsync("/api/v1/taxes", new UpsertTaxRequest(taxName, 9m), ApiHost.Json);
        var duplicateA = await a.PostAsJsonAsync("/api/v1/taxes", new UpsertTaxRequest(taxName, 11m), ApiHost.Json);
        var categoryA = await a.PostAsJsonAsync("/api/v1/expense-categories", new UpsertExpenseCategoryRequest(taxName), ApiHost.Json);
        var categoryB = await b.PostAsJsonAsync("/api/v1/expense-categories", new UpsertExpenseCategoryRequest(taxName), ApiHost.Json);

        Assert.True(createA.IsSuccessStatusCode, await createA.Content.ReadAsStringAsync());
        Assert.True(createB.IsSuccessStatusCode, await createB.Content.ReadAsStringAsync());
        Assert.True(categoryA.IsSuccessStatusCode && categoryB.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateA.StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "Taxes" WHERE "Name" = '{taxName}' AND "CompanyId" = '{w.A.Company}' AND "RatePercent" = 7"""));
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "Taxes" WHERE "Name" = '{taxName}' AND "CompanyId" = '{w.B.Company}' AND "RatePercent" = 9"""));
        // The new rows are stamped with the caller's company, never the other one.
        var names = await b.GetStringAsync("/api/v1/taxes");
        Assert.Contains("\"ratePercent\":9", names.Replace(" ", ""));
        Assert.DoesNotContain("\"ratePercent\":7", names.Replace(" ", ""));
    }

    [Fact]
    public async Task Settings_UpdateUnderA_LeavesBsProfileUntouched_AndEachCompanyReadsItsOwn()
    {
        var w = await WorldAsync();
        using var a = w.Host.NewClient(Own(w.A));
        using var b = w.Host.NewClient(Own(w.B));
        var current = (await a.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        var beforeB = await ScalarAsync<string>(w.Host.Database, $"""SELECT "BusinessName" FROM "BusinessSettings" WHERE "CompanyId" = '{w.B.Company}'""");

        var update = await a.PutAsJsonAsync("/api/v1/settings", new UpdateBusinessSettingsRequest("Renamed A Mart", current.AddressLine, current.City, current.State, current.Gstin, current.Phone, current.Email, current.InvoicePrefix, current.BaseCurrencyId), ApiHost.Json);

        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        Assert.Equal(beforeB, await ScalarAsync<string>(w.Host.Database, $"""SELECT "BusinessName" FROM "BusinessSettings" WHERE "CompanyId" = '{w.B.Company}'"""));
        Assert.Equal("Renamed A Mart", (await a.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!.BusinessName);
        var bSettings = (await b.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        Assert.Equal((w.B.Company, beforeB), (bSettings.CompanyId, bSettings.BusinessName));
    }

    // ---- classification of every controller action -----------------------------------------------------------------------

    private static IEnumerable<string> AllActionKeys() =>
        typeof(PurchasesController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => $"{t.Name}.{m.Name}"));

    [Fact]
    public void EveryControllerAction_IsInTheIsolationTableOrPlatformAllowlist()
    {
        var classified = IsolationCases.Reads.Select(r => r.Action)
            .Concat(IsolationCases.Writes.Select(x => x.Action))
            .Concat(IsolationCases.PlatformActions)
            .Concat(IsolationCases.CoveredElsewhere)
            .ToHashSet();
        var actions = AllActionKeys().ToHashSet();

        var unclassified = actions.Except(classified).Order().ToList();
        var stale = classified.Except(actions).Order().ToList();

        Assert.True(unclassified.Count == 0, "Controller actions with no company-isolation decision (add to IsolationCases): " + string.Join(", ", unclassified));
        Assert.True(stale.Count == 0, "IsolationCases entries naming actions that no longer exist: " + string.Join(", ", stale));
        Assert.Empty(IsolationCases.PlatformActions.Intersect(IsolationCases.Reads.Select(r => r.Action)));
    }

    private static async Task<string> SnapshotAsync(IsolationWorld w, Guid company)
    {
        var parts = new List<string>();
        foreach (var table in CompanyTables)
        {
            parts.Add(await ScalarAsync<string>(w.Host.Database,
                $"""SELECT '{table}=' || COALESCE(md5(string_agg(t::text, '|' ORDER BY t::text)), '-') FROM "{table}" t WHERE t."CompanyId" = '{company}'"""));
        }
        return string.Join(";", parts);
    }
}
