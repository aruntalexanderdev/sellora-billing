using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Branch behaviour of documents over HTTP (T4–T6 contract): stamping and per-branch numbering, DTO branch fields, the branchId filters,
/// returns following the original's branch, the account rule, SellerGstin, inactive branches, settings prefix collisions.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BranchDocumentsApiTests(PostgresFixture postgres)
{
    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // ---- numbering & stamping ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Purchase_AtMainKeepsTheLegacyShape_AtABranchCarriesTheCode_AndEachBranchCountsFromOne()
    {
        var w = await GetAsync(postgres);
        var (_, code) = await w.NewBranchAsync();
        var branch = (await w.Admin.GetFromJsonAsync<List<BranchDto>>("/api/v1/branches", ApiHost.Json))!.Single(b => b.Code == code).Id;

        var first = await JsonAsync(await w.PurchaseAsync(branch));
        var second = await JsonAsync(await w.PurchaseAsync(branch));
        var main = await JsonAsync(await w.PurchaseAsync(null));
        var mainExplicit = await JsonAsync(await w.PurchaseAsync(w.MainId));

        Assert.Equal(($"PUR-{code}-0001", $"PUR-{code}-0002"), (Str(first, "purchaseNo"), Str(second, "purchaseNo")));
        Assert.Matches(@"^PUR-\d{4}$", Str(main, "purchaseNo"));
        Assert.Matches(@"^PUR-\d{4}$", Str(mainExplicit, "purchaseNo"));
        Assert.Equal(branch, first.GetProperty("branchId").GetGuid());
        Assert.Equal((w.MainId, code), (main.GetProperty("branchId").GetGuid(), Str(first, "branchCode")));
        Assert.Equal("MAIN", Str(main, "branchCode"));
    }

    [Fact]
    public async Task Sale_InvoiceNumberShapes_CompanyPrefixAtMain_PrefixPlusCodeOrOwnPrefixAtABranch()
    {
        var w = await GetAsync(postgres);
        var (derived, derivedCode) = await w.NewBranchAsync();
        var ownPrefix = "O" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var (own, _) = await w.NewBranchAsync(invoicePrefix: ownPrefix);

        var main = await JsonAsync(await w.SaleAsync(null));
        var d1 = await JsonAsync(await w.SaleAsync(derived));
        var d2 = await JsonAsync(await w.SaleAsync(derived));
        var o1 = await JsonAsync(await w.SaleAsync(own));

        Assert.Matches($"^{w.CompanyPrefix}-\\d{{4}}$", Str(main, "invoiceNo"));
        Assert.Equal(($"{w.CompanyPrefix}-{derivedCode}-0001", $"{w.CompanyPrefix}-{derivedCode}-0002"), (Str(d1, "invoiceNo"), Str(d2, "invoiceNo")));
        Assert.Equal($"{ownPrefix}-0001", Str(o1, "invoiceNo"));
    }

    [Fact]
    public async Task Adjustment_AtABranch_IsNumberedAdjCodeAndStamped_ExpenseStaysCompanyLevelWhateverTheHeader()
    {
        var w = await GetAsync(postgres);
        var (branch, code) = await w.NewBranchAsync();

        var adjustment = await JsonAsync(await w.AdjustmentAsync(branch));
        var untagged = await JsonAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/expenses", new CreateExpenseRequest(w.CategoryId, Today, w.CurrencyId, 10m, PaymentMethod.Cash, null, null), branch: branch));
        var tagged = await JsonAsync(await w.ExpenseAsync(branch));

        Assert.Equal($"ADJ-{code}-0001", Str(adjustment, "adjustmentNo"));
        Assert.Equal(branch, adjustment.GetProperty("branchId").GetGuid());
        Assert.Matches(@"^EXP-\d{4}$", Str(untagged, "expenseNo"));
        Assert.Matches(@"^EXP-\d{4}$", Str(tagged, "expenseNo"));
        Assert.Equal(JsonValueKind.Null, untagged.GetProperty("branchId").ValueKind); // the header does not tag expenses
        Assert.Equal(branch, tagged.GetProperty("branchId").GetGuid());
    }

    [Fact]
    public async Task CreateDocument_InactiveBranchHeader_Returns403_AndNothingIsWritten()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branch}/status", new SetStatusRequest(false, "test"));

        var responses = new[] { await w.PurchaseAsync(branch), await w.SaleAsync(branch), await w.AdjustmentAsync(branch) };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(0L, await CompanyMigrationsCount(w, branch));
    }

    private static Task<long> CompanyMigrationsCount(BranchApiWorld w, Guid branch) => Sellora.IntegrationTests.TestData.CompanyMigrations.ScalarAsync<long>(w.Host.Database,
        $"""SELECT (SELECT COUNT(*) FROM "PurchaseOrders" WHERE "BranchId" = '{branch}') + (SELECT COUNT(*) FROM "SalesInvoices" WHERE "BranchId" = '{branch}') + (SELECT COUNT(*) FROM "StockAdjustments" WHERE "BranchId" = '{branch}')""");

    [Fact]
    public async Task Payment_OnAnInvoiceOfAnInactiveBranch_StillWorksWithoutAHeader()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        var sale = await JsonAsync(await w.SaleAsync(branch));
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branch}/status", new SetStatusRequest(false, "test"));
        var due = sale.GetProperty("dueAmount").GetDecimal();

        var pay = await w.SendAsync(HttpMethod.Post, $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/payments", new AddSalePaymentRequest(due, PaymentMethod.Cash, due));

        Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
        Assert.Equal(0m, (await JsonAsync(pay)).GetProperty("dueAmount").GetDecimal());
    }

    // ---- DTO fields & filters ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lists_BranchIdFilterShowsOnlyThatBranch_AbsentShowsAll_AndRowsCarryBranchFields()
    {
        var w = await GetAsync(postgres);
        var (x, xCode) = await w.NewBranchAsync();
        var (y, yCode) = await w.NewBranchAsync();
        var px = await JsonAsync(await w.PurchaseAsync(x));
        var py = await JsonAsync(await w.PurchaseAsync(y));
        var sx = await JsonAsync(await w.SaleAsync(x));
        var sy = await JsonAsync(await w.SaleAsync(y));
        var ax = await JsonAsync(await w.AdjustmentAsync(x));
        var ex = await JsonAsync(await w.ExpenseAsync(x));
        var ey = await JsonAsync(await w.ExpenseAsync(y));

        async Task<JsonElement[]> Items(string route) => (await JsonAsync(await w.SendAsync(HttpMethod.Get, route))).GetProperty("items").EnumerateArray().ToArray();
        var purchasesX = await Items($"/api/v1/purchases?branchId={x}&pageSize=100");
        var purchasesAll = await Items("/api/v1/purchases?pageSize=100");
        var salesY = await Items($"/api/v1/sales?branchId={y}&pageSize=100");
        var adjustmentsX = await Items($"/api/v1/stock-adjustments?branchId={x}&pageSize=100");
        var expensesY = await Items($"/api/v1/expenses?branchId={y}&pageSize=100");

        Assert.Equal([px.GetProperty("id").GetGuid()], purchasesX.Select(p => p.GetProperty("id").GetGuid()));
        Assert.All(purchasesX, p => Assert.Equal((x, xCode), (p.GetProperty("branchId").GetGuid(), Str(p, "branchCode"))));
        Assert.Contains(purchasesAll, p => p.GetProperty("id").GetGuid() == py.GetProperty("id").GetGuid());
        Assert.Contains(purchasesAll, p => p.GetProperty("branchId").GetGuid() == w.MainId);
        Assert.Equal([sy.GetProperty("id").GetGuid()], salesY.Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal(yCode, Str(salesY.Single(), "branchCode"));
        Assert.Equal([ax.GetProperty("id").GetGuid()], adjustmentsX.Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal([ey.GetProperty("id").GetGuid()], expensesY.Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal((y, $"Test {yCode}"), (expensesY.Single().GetProperty("branchId").GetGuid(), Str(expensesY.Single(), "branchName")));
        Assert.NotEqual(sx.GetProperty("id").GetGuid(), salesY.Single().GetProperty("id").GetGuid());
        Assert.NotEqual(ex.GetProperty("id").GetGuid(), expensesY.Single().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ReportsAndProfitSummary_FilterByBranch_AndProfitSummaryEchoesTheBranch()
    {
        var w = await GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        var (y, _) = await w.NewBranchAsync();
        await w.SaleAsync(x, qty: 2m);
        await w.SaleAsync(y, qty: 1m);
        await w.PurchaseAsync(x);

        var profitX = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/profit-summary?branchId={x}"));
        var profitY = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/profit-summary?branchId={y}"));
        var salesX = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/sales?branchId={x}"));
        var purchasesY = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/purchases?branchId={y}"));

        Assert.Equal((1, 1), (profitY.GetProperty("invoiceCount").GetInt32(), profitX.GetProperty("invoiceCount").GetInt32()));
        Assert.Equal(x, profitX.GetProperty("branchId").GetGuid());
        Assert.Equal(40m, profitX.GetProperty("salesRevenue").GetDecimal());
        Assert.Equal(20m, profitY.GetProperty("salesRevenue").GetDecimal());
        Assert.Equal(1, salesX.GetProperty("items").GetArrayLength());
        Assert.Equal(0, purchasesY.GetProperty("items").GetArrayLength());
    }

    // ---- returns follow the original's branch ----------------------------------------------------------------------------

    [Fact]
    public async Task SalesReturn_HeaderlessTakesTheInvoicesBranch_MatchingHeaderOk_OtherHeaderIs400OnBranchId()
    {
        var w = await GetAsync(postgres);
        var (x, xCode) = await w.NewBranchAsync();
        var sale = await JsonAsync(await w.SaleAsync(x, qty: 3m));
        var body = new CreateSaleReturnRequest(sale.GetProperty("id").GetGuid(), Today, null, [new SaleReturnLineRequest(sale.GetProperty("lines")[0].GetProperty("id").GetGuid(), null, 1m)]);

        var mismatch = await w.SendAsync(HttpMethod.Post, "/api/v1/sales-returns", body, branch: w.MainId);
        var headerless = await JsonAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/sales-returns", body));
        var matching = await JsonAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/sales-returns", body, branch: x));

        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("branchid", await ErrorFieldsAsync(mismatch));
        Assert.Equal((x, $"SRT-{xCode}-0001", xCode), (headerless.GetProperty("branchId").GetGuid(), Str(headerless, "returnNo"), Str(headerless, "branchCode")));
        Assert.Equal($"SRT-{xCode}-0002", Str(matching, "returnNo"));
    }

    [Fact]
    public async Task PurchaseReturn_FollowsThePurchasesBranch_AndOtherBranchHeaderIs400()
    {
        var w = await GetAsync(postgres);
        var (x, xCode) = await w.NewBranchAsync();
        var purchase = await JsonAsync(await w.PurchaseAsync(x, qty: 3m));
        var body = new CreatePurchaseReturnRequest(purchase.GetProperty("id").GetGuid(), Today, null, [new PurchaseReturnLineRequest(purchase.GetProperty("lines")[0].GetProperty("id").GetGuid(), null, 1m)]);

        var mismatch = await w.SendAsync(HttpMethod.Post, "/api/v1/purchase-returns", body, branch: w.MainId);
        var ok = await JsonAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/purchase-returns", body));

        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("branchid", await ErrorFieldsAsync(mismatch));
        Assert.Equal((x, $"PRT-{xCode}-0001"), (ok.GetProperty("branchId").GetGuid(), Str(ok, "returnNo")));
        var returns = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/purchase-returns?branchId={x}"));
        Assert.Equal(1, returns.GetProperty("items").GetArrayLength());
    }

    // ---- account rule -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Account_TaggedToABranch_IsRefusedForAnotherBranchsDocuments_CompanyLevelWorksEverywhere_ListsExposeTheTag()
    {
        var w = await GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        var tagged = await w.NewAccountAsync("Till " + Guid.NewGuid().ToString("N")[..5], x);
        var shared = await w.NewAccountAsync("Bank " + Guid.NewGuid().ToString("N")[..5], null);

        var wrongBranch = await w.SaleAsync(w.MainId, paid: 10m, account: tagged);
        var wrongPurchase = await w.PurchaseAsync(w.MainId, paid: 10m, account: tagged);
        var ownBranch = await w.SaleAsync(x, paid: 10m, account: tagged);
        var companyLevelAtMain = await w.SaleAsync(w.MainId, paid: 10m, account: shared);
        var companyLevelAtX = await w.SaleAsync(x, paid: 10m, account: shared);

        Assert.Equal(HttpStatusCode.BadRequest, wrongBranch.StatusCode);
        Assert.Contains("accountid", await ErrorFieldsAsync(wrongBranch));
        Assert.Equal(HttpStatusCode.BadRequest, wrongPurchase.StatusCode);
        Assert.All(new[] { ownBranch, companyLevelAtMain, companyLevelAtX }, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));

        var options = await JsonAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/financial-accounts/options"));
        Assert.Equal(x, options.EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == tagged).GetProperty("branchId").GetGuid());
        Assert.Equal(JsonValueKind.Null, options.EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == shared).GetProperty("branchId").ValueKind);
        var onlyX = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/financial-accounts?branchId={x}"));
        Assert.Contains(onlyX.GetProperty("items").EnumerateArray(), a => a.GetProperty("id").GetGuid() == tagged);
        Assert.DoesNotContain(onlyX.GetProperty("items").EnumerateArray(), a => a.GetProperty("id").GetGuid() == shared);
    }

    [Fact]
    public async Task Account_CreateWithAnUnknownOrInactiveBranch_Returns400OnBranchId()
    {
        var w = await GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{x}/status", new SetStatusRequest(false, "test"));

        var unknown = await w.SendAsync(HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest("Ghost", FinancialAccountKind.Cash, null, 0m, null, Guid.NewGuid()));
        var inactive = await w.SendAsync(HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest("Closed", FinancialAccountKind.Cash, null, 0m, null, x));

        Assert.All(new[] { unknown, inactive }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.All(new[] { unknown, inactive }, r => Assert.Contains("branchid", ErrorFieldsAsync(r).Result));
    }

    // ---- SellerGstin --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaleDetail_SellerGstin_IsTheBranchGstinElseTheCompanyGstin()
    {
        var w = await GetAsync(postgres);
        var (withGstin, _) = await w.NewBranchAsync(gstin: "33BBBBB1111B1Z5");
        var (withoutGstin, _) = await w.NewBranchAsync();

        var atMain = await JsonAsync(await w.SaleAsync(null));
        var atBranch = await JsonAsync(await w.SaleAsync(withGstin));
        var atPlain = await JsonAsync(await w.SaleAsync(withoutGstin));
        var detail = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/sales/{atBranch.GetProperty("id").GetGuid()}"));

        Assert.Equal(CompanyGstin, Str(atMain, "sellerGstin"));
        Assert.Equal("33BBBBB1111B1Z5", Str(atBranch, "sellerGstin"));
        Assert.Equal("33BBBBB1111B1Z5", Str(detail, "sellerGstin"));
        Assert.Equal(CompanyGstin, Str(atPlain, "sellerGstin"));
    }

    // ---- settings prefix collisions -----------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> PutSettingsAsync(BranchApiWorld w, string prefix)
    {
        var current = (await w.Admin.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        return await w.SendAsync(HttpMethod.Put, "/api/v1/settings", new UpdateBusinessSettingsRequest(current.BusinessName, current.AddressLine, current.City, current.State, current.Gstin, current.Phone, current.Email, prefix, current.BaseCurrencyId));
    }

    [Fact]
    public async Task Settings_CompanyPrefixEqualToABranchOwnPrefix_Returns400_AFreePrefixIsAcceptedAndRestored()
    {
        var w = await GetAsync(postgres);
        var branchPrefix = "S" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        await w.NewBranchAsync(invoicePrefix: branchPrefix);

        var collision = await PutSettingsAsync(w, branchPrefix);

        Assert.Equal(HttpStatusCode.BadRequest, collision.StatusCode);
        Assert.Contains("invoiceprefix", await ErrorFieldsAsync(collision));
        Assert.Equal(w.CompanyPrefix, (await w.Admin.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!.InvoicePrefix); // unchanged by the refused PUT
        Assert.Equal(HttpStatusCode.OK, (await PutSettingsAsync(w, w.CompanyPrefix)).StatusCode);
    }

    [Fact]
    public async Task Settings_CompanyPrefixThatMakesABranchsInvoiceNumbersTooLong_Returns400()
    {
        var w = await GetAsync(postgres);
        await w.NewBranchAsync(); // derives {CompanyPrefix}-T1xx: a 10-letter company prefix would make that 20 characters

        var tooLong = await PutSettingsAsync(w, "ABCDEFGHIJ");
        var unchanged = await PutSettingsAsync(w, w.CompanyPrefix);

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("invoiceprefix", await ErrorFieldsAsync(tooLong));
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
    }
}
