using Sellora.Domain.Entities;
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

    private static readonly string[] TaxReportRoutes = ["gst-summary", "hsn-summary?direction=Sales", "hsn-summary?direction=Purchases", "vat-summary"];

    private static string TaxReportUrl(string route, DateOnly from, DateOnly to, Guid? branchId = null) =>
        $"/api/v1/reports/tax/{route}{(route.Contains('?') ? "&" : "?")}dateFrom={from:yyyy-MM-dd}&dateTo={to:yyyy-MM-dd}{(branchId is { } b ? $"&branchId={b}" : "")}";

    [Fact]
    public async Task TaxReports_NamingTheOtherCompanysBranch_IsForbidden_AndOwnBranchStillWorks()
    {
        var w = await WorldAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var failures = new List<string>();

        foreach (var (self, other) in new[] { (w.A, w.B), (w.B, w.A) })
        {
            using var client = w.Host.NewClient(Own(self));
            foreach (var route in TaxReportRoutes)
            {
                var foreign = await client.GetAsync(TaxReportUrl(route, today.AddDays(-30), today, other.BranchId));
                if (foreign.StatusCode != HttpStatusCode.Forbidden) failures.Add($"{route}: other company's branch answered {(int)foreign.StatusCode}, expected 403");
                var own = await client.GetAsync(TaxReportUrl(route, today.AddDays(-30), today, self.BranchId));
                if (own.StatusCode != HttpStatusCode.OK) failures.Add($"{route}: own branch answered {(int)own.StatusCode}, expected 200");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task TaxReports_APeriodWithOnlyCompanyAsDocuments_ShowsNothingToCompanyB_AndAFindsThem()
    {
        var w = await WorldAsync();
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-100);
        await using (var db = w.Host.Database.CreateDbContext(w.A.Company))
        {
            var unit = w.A.ProductUnitId;
            await new Sellora.Infrastructure.Services.SalesService(db).CreateSaleAsync(new Sellora.Domain.Sales.CreateSaleInput(w.A.CustomerId, day, Sellora.Domain.Entities.PaymentMethod.Cash, 0m,
                [new Sellora.Domain.Sales.SaleLineInput(w.A.ProductId, unit, 1m, 10m, 0)]), w.A.UserId);
            await new Sellora.Infrastructure.Services.PurchaseService(db, new Sellora.Infrastructure.Services.ExchangeRateService(db)).CreatePurchaseAsync(new Sellora.Domain.Purchasing.CreatePurchaseInput(
                w.A.SupplierId, day, null, null, Sellora.Domain.Entities.PaymentMethod.Cash, 5m, [new Sellora.Domain.Purchasing.PurchaseLineInput(w.A.ProductId, unit, 1m, 5m, 0, 0)], w.A.AccountId), w.A.UserId);
        }
        var (from, to) = (day.AddDays(-5), day.AddDays(5));
        using var a = w.Host.NewClient(Own(w.A));
        using var b = w.Host.NewClient(Own(w.B));
        var failures = new List<string>();

        foreach (var route in TaxReportRoutes)
        {
            var asB = await b.GetAsync(TaxReportUrl(route, from, to));
            var bodyB = await asB.Content.ReadAsStringAsync();
            if (asB.StatusCode != HttpStatusCode.OK) { failures.Add($"B {route}: {(int)asB.StatusCode}"); continue; }
            var jsonB = JsonDocument.Parse(bodyB).RootElement;
            if (jsonB.GetProperty("rows").GetArrayLength() != 0) failures.Add($"B {route}: rows of the other company: {bodyB[..Math.Min(300, bodyB.Length)]}");
            if (bodyB.Contains(w.A.BranchId.ToString(), StringComparison.OrdinalIgnoreCase)) failures.Add($"B {route}: names company A's branch");
            foreach (var amount in AmountsOf(jsonB)) if (amount != 0m) failures.Add($"B {route}: non-zero amount {amount}");

            if (route.StartsWith("vat", StringComparison.Ordinal)) continue;   // company A is a GST country: its documents appear in the GST and HSN reports
            var asA = await a.GetAsync(TaxReportUrl(route, from, to));
            var jsonA = JsonDocument.Parse(await asA.Content.ReadAsStringAsync()).RootElement;
            if (asA.StatusCode != HttpStatusCode.OK || jsonA.GetProperty("rows").GetArrayLength() == 0) failures.Add($"A {route}: own documents not found (vacuous probe): {(int)asA.StatusCode}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<decimal> AmountsOf(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number: yield return e.GetDecimal(); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) foreach (var x in AmountsOf(p.Value)) yield return x; break;
            case JsonValueKind.Array: foreach (var i in e.EnumerateArray()) foreach (var x in AmountsOf(i)) yield return x; break;
        }
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
    public async Task Create_SameNameTaxProfileInBothCompanies_SucceedsButDuplicateWithinACompanyIsRefused()
    {
        var w = await WorldAsync();
        using var a = w.Host.NewClient(Own(w.A));
        using var b = w.Host.NewClient(Own(w.B));
        var name = "Scoped " + Guid.NewGuid().ToString("N")[..6];
        var code = "SC-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        CreateTaxProfileRequest Profile(Ids ids, string c, string n) => new(ids.CountryId, c, n, TaxRegime.Gst, TaxTreatment.Exempt, new AddTaxVersionRequest(new DateOnly(2020, 1, 1), []));

        var createA = await a.PostAsJsonAsync("/api/v1/tax-profiles", Profile(w.A, code, name), ApiHost.Json);
        var createB = await b.PostAsJsonAsync("/api/v1/tax-profiles", Profile(w.B, code, name), ApiHost.Json);
        var duplicateA = await a.PostAsJsonAsync("/api/v1/tax-profiles", Profile(w.A, code + "X", name.ToUpperInvariant()), ApiHost.Json);
        var categoryA = await a.PostAsJsonAsync("/api/v1/expense-categories", new UpsertExpenseCategoryRequest(name), ApiHost.Json);
        var categoryB = await b.PostAsJsonAsync("/api/v1/expense-categories", new UpsertExpenseCategoryRequest(name), ApiHost.Json);

        Assert.True(createA.IsSuccessStatusCode, await createA.Content.ReadAsStringAsync());
        Assert.True(createB.IsSuccessStatusCode, await createB.Content.ReadAsStringAsync());
        Assert.True(categoryA.IsSuccessStatusCode && categoryB.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateA.StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "TaxProfiles" WHERE "Name" = '{name}' AND "CompanyId" = '{w.A.Company}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "TaxProfiles" WHERE "Name" = '{name}' AND "CompanyId" = '{w.B.Company}'"""));
        // The new rows are stamped with the caller's company, never the other one.
        var listB = await b.GetStringAsync("/api/v1/tax-profiles?includeInactive=true");
        Assert.Contains(code, listB);
        Assert.Equal(1, listB.Split(code).Length - 1);
    }

    [Fact]
    public async Task WarehousesAndVehicles_SameCodeAndNumberInBothCompanies_SucceedsButDuplicateWithinACompanyIsRefused()
    {
        var w = await WorldAsync();
        using var a = w.Host.NewClient(Own(w.A));
        using var b = w.Host.NewClient(Own(w.B));
        var code = "SC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var whA = await a.PostAsJsonAsync("/api/v1/warehouses", new CreateWarehouseRequest(code, "Scoped " + code, null), ApiHost.Json);
        var whB = await b.PostAsJsonAsync("/api/v1/warehouses", new CreateWarehouseRequest(code, "Scoped " + code, null), ApiHost.Json);
        var whDup = await a.PostAsJsonAsync("/api/v1/warehouses", new CreateWarehouseRequest(code.ToLowerInvariant(), "Other name " + code, null), ApiHost.Json);
        var vA = await a.PostAsJsonAsync("/api/v1/vehicles", new CreateVehicleRequest("V-" + code, "Van", "Van", null), ApiHost.Json);
        var vB = await b.PostAsJsonAsync("/api/v1/vehicles", new CreateVehicleRequest("V-" + code, "Van", "Van", null), ApiHost.Json);
        var vDup = await a.PostAsJsonAsync("/api/v1/vehicles", new CreateVehicleRequest("v " + code, "Van 2", "Van", null), ApiHost.Json);

        Assert.True(whA.IsSuccessStatusCode && whB.IsSuccessStatusCode && vA.IsSuccessStatusCode && vB.IsSuccessStatusCode,
            string.Join(" | ", new[] { whA, whB, vA, vB }.Select(r => (int)r.StatusCode)));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (whDup.StatusCode, vDup.StatusCode));
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "Warehouses" x JOIN "StockLocations" l ON l."Id" = x."StockLocationId" WHERE l."Code" = '{code}' AND x."CompanyId" = '{w.A.Company}' AND l."CompanyId" = '{w.A.Company}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "Vehicles" WHERE "VehicleNumber" = 'V-{code}' AND "CompanyId" = '{w.B.Company}'"""));
        // Within company B the warehouse code is taken for a branch too (one code space per company: the live Phase 3 branch rule).
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest(code, "Branch " + code, null, null, null, null, null, null, null), ApiHost.Json)).StatusCode);
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

    [Fact]
    public async Task Branches_SameCodeInBothCompanies_Succeeds_AndAnotherCompanysBranchIsDeniedAsHeaderAndFilter()
    {
        var w = await WorldAsync();
        using var a = w.Host.NewClient(Own(w.A));
        using var b = w.Host.NewClient(Own(w.B));
        var request = new CreateBranchRequest("ISO", "Isolation branch", null, null, null, null, null, null, null);

        var createA = await a.PostAsJsonAsync("/api/v1/branches", request, ApiHost.Json);
        var createB = await b.PostAsJsonAsync("/api/v1/branches", request, ApiHost.Json);

        Assert.True(createA.IsSuccessStatusCode, await createA.Content.ReadAsStringAsync());
        Assert.True(createB.IsSuccessStatusCode, await createB.Content.ReadAsStringAsync());
        var aBranches = await a.GetFromJsonAsync<List<BranchDto>>("/api/v1/branches?includeInactive=true", ApiHost.Json);
        var bBranches = await b.GetFromJsonAsync<List<BranchDto>>("/api/v1/branches?includeInactive=true", ApiHost.Json);
        Assert.Empty(aBranches!.Select(x => x.Id).Intersect(bBranches!.Select(x => x.Id)));
        Assert.Equal(["ISO", "MAIN"], aBranches!.Select(x => x.Code).Order().ToArray());

        // B naming A's branch (header or list filter) is refused exactly like an unknown id (no existence leak).
        var foreign = w.A.BranchId;
        var unknown = Guid.NewGuid();
        var (fh, fhBody) = await GetWithHeaderAsync(b, "/api/v1/sales", foreign);
        var (uh, uhBody) = await GetWithHeaderAsync(b, "/api/v1/sales", unknown);
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (fh, uh));
        string Normalise(string body, Guid id) => System.Text.RegularExpressions.Regex.Replace(body.Replace(id.ToString(), "X"), "\"traceId\":\"[^\"]*\"", "\"traceId\":\"T\"");
        Assert.Equal(Normalise(uhBody, unknown), Normalise(fhBody, foreign)); // identical apart from the per-request trace id
        var filtered = await b.GetAsync($"/api/v1/sales?branchId={foreign}");
        Assert.Equal(HttpStatusCode.Forbidden, filtered.StatusCode);
        Assert.Equal("branch_access_denied", await filtered.ProblemCodeAsync());
        // The company-A-only branch id cannot be used to create documents as B either.
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales") { Content = JsonContent.Create(new CreateSaleRequest(w.B.CustomerId, DateOnly.FromDateTime(DateTime.UtcNow), Sellora.Domain.Entities.PaymentMethod.Cash, 0m, []), options: ApiHost.Json) };
        create.Headers.Add("X-Branch-Id", foreign.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(create)).StatusCode);
    }

    private static async Task<(HttpStatusCode, string)> GetWithHeaderAsync(HttpClient client, string route, Guid branch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add("X-Branch-Id", branch.ToString());
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
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
        foreach (var table in CompanyTables.Concat(["StockLocations", "Warehouses", "Vehicles", "CompanyGovernanceMembers", "GovernanceMemberPermissions", "Roles", "UserRoleAssignments"]))
        {
            parts.Add(await ScalarAsync<string>(w.Host.Database,
                $"""SELECT '{table}=' || COALESCE(md5(string_agg(t::text, '|' ORDER BY t::text)), '-') FROM "{table}" t WHERE t."CompanyId" = '{company}'"""));
        }
        return string.Join(";", parts);
    }
}
