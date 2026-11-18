using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 5 T3/T5/T6 — branch-restricted (AssignedBranches) users through the real pipeline on PostgreSQL: the "Branch" filter on lists, details
/// and child tables, explicit branchId 403, working-branch resolution, X-Branch-Id 403, branch_required, immediate revocation, login/me fields,
/// reports, Phase 3/4 location visibility, and the integrity-read bypasses (numbering, names, unit locks, batch labels).
/// </summary>
[Collection(PostgresCollection.Name)]
public class RestrictedUserApiTests(PostgresFixture postgres)
{
    private static Guid[] Ids(JsonElement list, string property = "items") =>
        list.GetProperty(property).EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    private static async Task<JsonElement> GetJsonAsync(RestrictedUser user, string route, Guid? header = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (header is { } h) request.Headers.Add("X-Branch-Id", h.ToString());
        var response = await user.Client.SendAsync(request);
        return await RestrictedUserWorld.JsonOkAsync(response);
    }

    private static async Task<Guid> StoreOfAsync(BranchApiWorld w, Guid branch)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        return await Sellora.IntegrationTests.TestData.StockSeed.StoreOfAsync(db, branch);
    }

    private static Task<HttpResponseMessage> SendAsAsync(RestrictedUserWorld r, RestrictedUser user, HttpMethod method, string route, object? body = null, Guid? header = null) =>
        r.World.SendAsync(method, route, body, branch: header, client: user.Client);

    // ---- lists narrow, details 404, explicit branchId 403 -------------------------------------------------------------------------

    [Fact]
    public async Task Lists_NarrowToAllocatedBranches_AcrossEveryBranchDocumentType()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var px = IdOf(await JsonAsync(await w.PurchaseAsync(x))); var py = IdOf(await JsonAsync(await w.PurchaseAsync(y)));
        var sx = IdOf(await JsonAsync(await w.SaleAsync(x))); var sy = IdOf(await JsonAsync(await w.SaleAsync(y)));
        var ax = IdOf(await JsonAsync(await w.AdjustmentAsync(x))); var ay = IdOf(await JsonAsync(await w.AdjustmentAsync(y)));
        var user = await r.CreateAtAsync(x);

        var purchases = Ids(await GetJsonAsync(user, "/api/v1/purchases?pageSize=100"));
        var sales = Ids(await GetJsonAsync(user, "/api/v1/sales?pageSize=100"));
        var adjustments = Ids(await GetJsonAsync(user, "/api/v1/stock-adjustments?pageSize=100"));

        Assert.Contains(px, purchases); Assert.DoesNotContain(py, purchases);
        Assert.Contains(sx, sales); Assert.DoesNotContain(sy, sales);
        Assert.Contains(ax, adjustments); Assert.DoesNotContain(ay, adjustments);
        // the user never sees any other branch's rows at all
        var allowed = new[] { x };
        Assert.All((await GetJsonAsync(user, "/api/v1/sales?pageSize=100")).GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("branchId").GetGuid(), allowed));
        Assert.All((await GetJsonAsync(user, "/api/v1/purchases?pageSize=100")).GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("branchId").GetGuid(), allowed));
    }

    [Fact]
    public async Task Returns_OfAnotherBranch_AreHiddenFromLists_AndTheOwnBranchReturnsShow()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        async Task<Guid> ReturnAsync(Guid branch)
        {
            var sale = await JsonAsync(await w.SaleAsync(branch, qty: 3m));
            var body = new CreateSaleReturnRequest(IdOf(sale), Today, null, [new SaleReturnLineRequest(sale.GetProperty("lines")[0].GetProperty("id").GetGuid(), null, 1m)]);
            return IdOf(await JsonAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/sales-returns", body)));
        }
        var rx = await ReturnAsync(x); var ry = await ReturnAsync(y);
        var purchase = await JsonAsync(await w.PurchaseAsync(y, qty: 3m));
        var prBody = new CreatePurchaseReturnRequest(IdOf(purchase), Today, null, [new PurchaseReturnLineRequest(purchase.GetProperty("lines")[0].GetProperty("id").GetGuid(), null, 1m)]);
        var pry = IdOf(await JsonAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/purchase-returns", prBody)));
        var user = await r.CreateAtAsync(x);

        var salesReturns = Ids(await GetJsonAsync(user, "/api/v1/sales-returns?pageSize=100"));
        var purchaseReturns = Ids(await GetJsonAsync(user, "/api/v1/purchase-returns?pageSize=100"));

        Assert.Contains(rx, salesReturns); Assert.DoesNotContain(ry, salesReturns);
        Assert.DoesNotContain(pry, purchaseReturns);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/sales-returns/{ry}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/purchase-returns/{pry}")).StatusCode);
    }

    [Fact]
    public async Task Details_OfAnInaccessibleBranchDocument_AreNotFound_AndOwnDetailsWork()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var sx = IdOf(await JsonAsync(await w.SaleAsync(x))); var sy = IdOf(await JsonAsync(await w.SaleAsync(y)));
        var py = IdOf(await JsonAsync(await w.PurchaseAsync(y)));
        var ay = IdOf(await JsonAsync(await w.AdjustmentAsync(y)));
        var user = await r.CreateAtAsync(x);

        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync($"/api/v1/sales/{sx}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/sales/{sy}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/purchases/{py}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/stock-adjustments/{ay}")).StatusCode);
    }

    [Fact]
    public async Task PaymentAndReturnOnAnotherBranchsDocument_AreNotFound_AndNothingChanges()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var sale = await JsonAsync(await w.SaleAsync(y, qty: 2m));
        var saleId = IdOf(sale); var due = sale.GetProperty("dueAmount").GetDecimal();
        var user = await r.CreateAtAsync(x);

        var pay = await SendAsAsync(r, user, HttpMethod.Post, $"/api/v1/sales/{saleId}/payments", new AddSalePaymentRequest(due, PaymentMethod.Cash, due));
        var ret = await SendAsAsync(r, user, HttpMethod.Post, "/api/v1/sales-returns",
            new CreateSaleReturnRequest(saleId, Today, null, [new SaleReturnLineRequest(sale.GetProperty("lines")[0].GetProperty("id").GetGuid(), null, 1m)]));

        Assert.Equal(HttpStatusCode.NotFound, pay.StatusCode);
        Assert.True(ret.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, "return of an invisible invoice: " + (int)ret.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, ret.StatusCode);
        var after = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/sales/{saleId}", branch: y)); // FI-D11: the admin reads y with y as the active branch
        Assert.Equal(due, after.GetProperty("dueAmount").GetDecimal());
    }

    [Theory]
    [InlineData("/api/v1/purchases")]
    [InlineData("/api/v1/sales")]
    [InlineData("/api/v1/stock-adjustments")]
    [InlineData("/api/v1/expenses")]
    [InlineData("/api/v1/financial-accounts")]
    [InlineData("/api/v1/reports/sales")]
    [InlineData("/api/v1/reports/purchases")]
    [InlineData("/api/v1/reports/profit-summary")]
    public async Task ExplicitBranchIdFilter_OfANonAllocatedBranch_Returns403_OfAnAllocatedBranch200(string route)
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);

        var denied = await user.Client.GetAsync($"{route}?branchId={y}");
        var ok = await user.Client.GetAsync($"{route}?branchId={x}");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // ---- company-level data stays visible -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Expenses_CompanyLevelVisible_OtherBranchTaggedHidden()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var company = IdOf(await JsonAsync(await w.ExpenseAsync(null)));
        var tagX = IdOf(await JsonAsync(await w.ExpenseAsync(x)));
        var tagY = IdOf(await JsonAsync(await w.ExpenseAsync(y)));
        var user = await r.CreateAtAsync(x);

        var ids = Ids(await GetJsonAsync(user, "/api/v1/expenses?pageSize=100"));

        Assert.Contains(company, ids); Assert.Contains(tagX, ids); Assert.DoesNotContain(tagY, ids);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/expenses/{tagY}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync($"/api/v1/expenses/{company}")).StatusCode);
    }

    [Fact]
    public async Task FinancialAccounts_CompanyLevelVisible_OtherBranchTaggedHidden_AndTheirTransactionsToo()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var shared = await w.NewAccountAsync("Shared " + RestrictedUserWorld.NewName(""), null);
        var tagY = await w.NewAccountAsync("Till Y " + RestrictedUserWorld.NewName(""), y);
        var saleY = await JsonAsync(await w.SaleAsync(y, qty: 1m, paid: 20m, account: tagY));
        var saleShared = await JsonAsync(await w.SaleAsync(x, qty: 1m, paid: 20m, account: shared));
        var user = await r.CreateAtAsync(x);

        var accounts = Ids(await GetJsonAsync(user, "/api/v1/financial-accounts"));
        var options = (await GetJsonAsync(user, "/api/v1/financial-accounts/options")).EnumerateArray().Select(IdOf).ToArray();
        var transactions = await GetJsonAsync(user, "/api/v1/financial-transactions?pageSize=100");
        var accountIds = transactions.GetProperty("items").EnumerateArray().Select(t => t.TryGetProperty("accountId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetGuid() : (Guid?)null).ToArray();
        var adminTransactions = await JsonAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/financial-transactions?pageSize=100", branch: y)); // FI-D11: admin reads at Y

        Assert.Contains(shared, accounts); Assert.DoesNotContain(tagY, accounts);
        Assert.Contains(shared, options); Assert.DoesNotContain(tagY, options);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/financial-accounts/{tagY}")).StatusCode);
        Assert.Contains((Guid?)shared, accountIds);
        Assert.DoesNotContain((Guid?)tagY, accountIds);
        Assert.Contains(adminTransactions.GetProperty("items").EnumerateArray(), t => t.TryGetProperty("accountId", out var a) && a.ValueKind == JsonValueKind.String && a.GetGuid() == tagY);
        Assert.True(saleY.GetProperty("id").GetGuid() != saleShared.GetProperty("id").GetGuid());
    }

    // ---- reports & dashboard ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProfitSummaryAndSalesReport_CoverOnlyAllocatedBranches_WithoutAFilter()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        await w.SaleAsync(x, qty: 2m);
        await w.SaleAsync(y, qty: 5m);
        var user = await r.CreateAtAsync(x);

        var profit = await GetJsonAsync(user, "/api/v1/reports/profit-summary");
        var sales = Ids(await GetJsonAsync(user, "/api/v1/reports/sales?pageSize=100"));
        var adminX = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/reports/profit-summary?branchId={x}", branch: x)); // FI-D11: branchId must be the active branch

        Assert.Equal(1, profit.GetProperty("invoiceCount").GetInt32());
        Assert.Equal(40m, profit.GetProperty("salesRevenue").GetDecimal());
        Assert.Equal(adminX.GetProperty("salesRevenue").GetDecimal(), profit.GetProperty("salesRevenue").GetDecimal());
        Assert.Single(sales);
    }

    [Fact]
    public async Task Dashboard_ListsOnlyAllocatedBranchDocuments()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var sy = await JsonAsync(await w.SaleAsync(y));
        var sx = await JsonAsync(await w.SaleAsync(x));
        var user = await r.CreateAtAsync(x);

        var body = await (await user.Client.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync();

        Assert.Contains(sx.GetProperty("invoiceNo").GetString()!, body);
        Assert.DoesNotContain(sy.GetProperty("invoiceNo").GetString()!, body);
    }

    // ---- working branch & X-Branch-Id ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task HeaderlessWrite_LandsInThePrimaryBranch_HeaderOfAnAllocatedBranchWins()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, xCode) = await w.NewBranchAsync(); var (z, zCode) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, z], primary: z);
        await w.EnsureBranchStockAsync(x, 1m); // 8b: x sells from its own store

        var headerless = await JsonAsync(await SendAsAsync(r, user, HttpMethod.Post, "/api/v1/purchases",
            new CreatePurchaseRequest(w.SupplierId, Today, null, null, PaymentMethod.Cash, 0m, [new CreatePurchaseLineRequest(w.ProductId, w.ProductUnitId, 1m, 5m, 0, 0)], null)));
        var withHeader = await JsonAsync(await SendAsAsync(r, user, HttpMethod.Post, "/api/v1/sales",
            new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m, [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, 20m, 0)], null), header: x));

        Assert.Equal((z, $"PUR-{zCode}-0001"), (headerless.GetProperty("branchId").GetGuid(), headerless.GetProperty("purchaseNo").GetString()));
        Assert.Equal((x, xCode), (withHeader.GetProperty("branchId").GetGuid(), withHeader.GetProperty("branchCode").GetString()));
    }

    [Fact]
    public async Task Header_OfANonAllocatedBranch_Returns403BranchAccessDenied_OnReadsAndWrites()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);

        var read = await SendAsAsync(r, user, HttpMethod.Get, "/api/v1/sales", header: y);
        var write = await SendAsAsync(r, user, HttpMethod.Post, "/api/v1/stock-adjustments",
            new CreateStockAdjustmentRequest(w.ProductId, w.ProductUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 5m, "x"), header: y);
        var main = await SendAsAsync(r, user, HttpMethod.Get, "/api/v1/sales", header: w.MainId);
        var own = await SendAsAsync(r, user, HttpMethod.Get, "/api/v1/sales", header: x);

        Assert.All(new[] { read, write, main }, resp => Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode));
        Assert.Equal("branch_access_denied", await read.ProblemCodeAsync());
        Assert.Equal("branch_access_denied", await write.ProblemCodeAsync());
        Assert.Equal("branch_access_denied", await main.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    // ---- branch_required, login / me ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoActiveAssignments_BranchRequired403_ExceptAllowWithoutBranchEndpoints()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        await ExecuteAsync(w.Host.Database, $"""DELETE FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" IN (SELECT "Id" FROM "UserCompanyMemberships" WHERE "UserId" = '{user.Id}')""");

        var sales = await user.Client.GetAsync("/api/v1/sales");
        var accessible = await user.Client.GetAsync("/api/v1/branches/accessible");
        var me = await user.Client.GetAsync("/api/v1/auth/me");
        var password = await user.Client.PostAsJsonAsync("/api/v1/users/me/change-password", new ChangeOwnPasswordRequest("wrong-password", "Passw0rd!77", "Passw0rd!77"), ApiHost.Json);

        Assert.Equal(HttpStatusCode.Forbidden, sales.StatusCode);
        Assert.Equal("branch_required", await sales.ProblemCodeAsync());
        Assert.Equal("branch_required", await accessible.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode); // got through the branch gate; the wrong current password is a field error
        var meJson = await JsonAsync(me);
        Assert.Equal(0, meJson.GetProperty("allowedBranches").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, meJson.GetProperty("primaryBranchId").ValueKind);
    }

    [Fact]
    public async Task Login_WithNoUsableMembership_Returns403NoBranchAccess_AlsoOnRefresh()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        await ExecuteAsync(w.Host.Database, $"""DELETE FROM "UserBranchAssignments" WHERE "UserCompanyMembershipId" IN (SELECT "Id" FROM "UserCompanyMemberships" WHERE "UserId" = '{user.Id}')""");
        using var anonymous = w.Host.NewClient();

        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Username, user.Password), ApiHost.Json);
        var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(user.Auth.RefreshToken), ApiHost.Json);

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("no_branch_access", await login.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
        Assert.Equal("no_branch_access", await refresh.ProblemCodeAsync());
    }

    [Fact]
    public async Task LoginAndMe_CarryScopePrimaryAndAllowedBranches_ForRestrictedAndCompanyWideUsers()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, xCode) = await w.NewBranchAsync(); var (z, zCode) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, z], primary: z);

        var me = await GetJsonAsync(user, "/api/v1/auth/me");
        var admin = await JsonAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/auth/me"));
        var accessible = (await GetJsonAsync(user, "/api/v1/branches/accessible")).EnumerateArray().Select(IdOf).ToArray();

        Assert.Equal(CompanyAccessScope.AssignedBranches, user.Auth.User.AccessScope);
        Assert.Equal(z, user.Auth.User.PrimaryBranchId);
        Assert.Equal(new[] { x, z }.Order(), user.Auth.User.AllowedBranches.Select(b => b.Id).Order());
        Assert.Equal(z, user.Auth.User.AllowedBranches.Single(b => b.IsPrimary).Id);
        Assert.Contains(user.Auth.User.AllowedBranches, b => b.Id == x && b.Code == xCode && !b.IsDefault && !b.IsPrimary);
        Assert.Equal("AssignedBranches", me.GetProperty("accessScope").GetString());
        Assert.Equal(z, me.GetProperty("primaryBranchId").GetGuid());
        Assert.Equal(2, me.GetProperty("allowedBranches").GetArrayLength());
        Assert.Equal(new[] { x, z }.Order(), accessible.Order());
        Assert.Equal("CompanyWide", admin.GetProperty("accessScope").GetString());
        Assert.Equal(JsonValueKind.Null, admin.GetProperty("primaryBranchId").ValueKind);
        var adminBranches = admin.GetProperty("allowedBranches").EnumerateArray().Select(IdOf).ToArray();
        Assert.Contains(w.MainId, adminBranches); Assert.Contains(x, adminBranches); Assert.Contains(z, adminBranches);
        Assert.Contains(admin.GetProperty("allowedBranches").EnumerateArray(), b => b.GetProperty("isDefault").GetBoolean() && IdOf(b) == w.MainId);
        Assert.Contains(zCode, user.Auth.User.AllowedBranches.Select(b => b.Code));
    }

    // ---- immediate revocation -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EndingAnAssignment_TakesEffectOnTheVeryNextRequest_WithTheSameToken()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAsync("ru", r.StaffRoleId, CompanyAccessScope.AssignedBranches, [x, y], primary: x);
        var saleY = IdOf(await JsonAsync(await w.SaleAsync(y)));
        // FI-D11: reads cover the ACTIVE branch only (primary x without a header), so branch y is addressed explicitly.
        Assert.Equal(HttpStatusCode.OK, (await SendAsAsync(r, user, HttpMethod.Get, $"/api/v1/sales/{saleY}", header: y)).StatusCode);
        var access = await r.GetAccessAsync(user.Id);
        var assignmentY = access.GetProperty("current").EnumerateArray().Single(a => a.GetProperty("branchId").GetGuid() == y).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await r.AccessAsync(HttpMethod.Post, user.Id, $"/assignments/{assignmentY}/end", new EndAssignmentRequest("rota change"))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/sales/{saleY}")).StatusCode);
        var detailWithHeader = await SendAsAsync(r, user, HttpMethod.Get, $"/api/v1/sales/{saleY}", header: y);
        Assert.Equal(HttpStatusCode.Forbidden, detailWithHeader.StatusCode); // the same token can no longer even address y
        var header = await SendAsAsync(r, user, HttpMethod.Get, "/api/v1/sales", header: y);
        Assert.Equal("branch_access_denied", await header.ProblemCodeAsync());
        Assert.DoesNotContain(y, (await GetJsonAsync(user, "/api/v1/branches/accessible")).EnumerateArray().Select(IdOf));
    }

    [Fact]
    public async Task AddingAnAssignment_GrantsAccessOnTheVeryNextRequest()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var saleY = IdOf(await JsonAsync(await w.SaleAsync(y)));
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/sales/{saleY}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsAsync(r, user, HttpMethod.Get, $"/api/v1/sales/{saleY}", header: y)).StatusCode);

        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/assignments", new AddAssignmentRequest(y, false, "cover")));

        // FI-D11: y is now allowed but still not the ACTIVE branch (primary x): it is read through the header.
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/sales/{saleY}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsAsync(r, user, HttpMethod.Get, $"/api/v1/sales/{saleY}", header: y)).StatusCode);
    }

    [Fact]
    public async Task SwitchingToCompanyWide_WidensTheVeryNextRequest_AndBackNarrowsIt()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var saleY = IdOf(await JsonAsync(await w.SaleAsync(y)));

        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.CompanyWide, null, null, "promoted")));
        Assert.Equal(HttpStatusCode.OK, (await SendAsAsync(r, user, HttpMethod.Get, $"/api/v1/sales/{saleY}", header: y)).StatusCode); // FI-D11: active branch via header

        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Put, user.Id, "/scope", new SetAccessScopeRequest(CompanyAccessScope.AssignedBranches, [x], x, "demoted")));
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/sales/{saleY}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsAsync(r, user, HttpMethod.Get, $"/api/v1/sales/{saleY}", header: y)).StatusCode);
    }

    [Fact]
    public async Task SuspendingTheMembership_Returns401CompanyContextInvalid_OnTheNextRequest()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/v1/sales")).StatusCode);

        await RestrictedUserWorld.JsonOkAsync(await r.AccessAsync(HttpMethod.Post, user.Id, "/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "leave", null, null, null)));

        var response = await user.Client.GetAsync("/api/v1/sales");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("company_context_invalid", await response.ProblemCodeAsync());
    }

    // ---- Phase 3/4 visibility is now live -----------------------------------------------------------------------------------------

    [Fact]
    public async Task StoresAndWarehouses_OfANonAllocatedBranch_AreHiddenFromRestrictedUsers_CompanyLevelStaysVisible()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var whY = await w.SendAsync(HttpMethod.Post, "/api/v1/warehouses", new CreateWarehouseRequest("WY" + RestrictedUserWorld.NewName("")[..4], "Y warehouse", y));
        Check(whY, "branch warehouse"); var whYId = IdOf(await JsonAsync(whY));
        var whCompany = await w.SendAsync(HttpMethod.Post, "/api/v1/warehouses", new CreateWarehouseRequest("WC" + RestrictedUserWorld.NewName("")[..4], "Company warehouse", null));
        Check(whCompany, "company warehouse"); var whCompanyId = IdOf(await JsonAsync(whCompany));
        var user = await r.CreateAtAsync(x);

        var locations = (await GetJsonAsync(user, "/api/v1/stock-locations")).EnumerateArray().ToArray();
        var warehouses = (await GetJsonAsync(user, "/api/v1/warehouses")).EnumerateArray().Select(IdOf).ToArray();

        var branchIds = locations.Where(l => l.GetProperty("branchId").ValueKind == JsonValueKind.String).Select(l => l.GetProperty("branchId").GetGuid()).Distinct().ToArray();
        Assert.Equal([x], branchIds);
        Assert.Contains(locations, l => l.GetProperty("branchId").ValueKind == JsonValueKind.Null); // company-level (e.g. Transit) stays visible
        Assert.DoesNotContain(whYId, warehouses); Assert.Contains(whCompanyId, warehouses);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/warehouses/{whYId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync($"/api/v1/warehouses/{whCompanyId}")).StatusCode);
        var yStore = (await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/stock-locations?branchId={y}", branch: y))).EnumerateArray().First(l => l.GetProperty("locationType").GetString() == "BranchStore");
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/v1/stock-locations/{IdOf(yStore)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync($"/api/v1/stock-locations?branchId={y}")).StatusCode);
    }

    // ---- bypasses (integrity reads see every branch) ------------------------------------------------------------------------------

    [Fact]
    public async Task ExpenseNumbers_StayUniqueAcrossBranchesAndUsers_EvenWhenOtherBranchExpensesAreInvisible()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var ux = await r.CreateAtAsync(x); var uy = await r.CreateAtAsync(y);
        Task<HttpResponseMessage> Expense(RestrictedUser u, Guid? tag) => SendAsAsync(r, u, HttpMethod.Post, "/api/v1/expenses", new CreateExpenseRequest(w.CategoryId, Today, w.CurrencyId, 10m, PaymentMethod.Cash, null, null, null, tag));

        var numbers = new List<string>();
        foreach (var response in new[] { await Expense(uy, y), await Expense(uy, y), await Expense(ux, x), await Expense(ux, null), await w.ExpenseAsync(y), await Expense(ux, x) })
        {
            Check(response, "expense");
            numbers.Add((await JsonAsync(response)).GetProperty("expenseNo").GetString()!);
        }

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        var sequence = numbers.Select(n => int.Parse(n.Split('-')[1])).ToArray();
        Assert.Equal(sequence.Order(), sequence); // strictly increasing, never reusing a number hidden from the caller
        Assert.Equal(await ScalarAsync<long>(w.Host.Database, """SELECT COUNT(*) FROM "Expenses" """), await ScalarAsync<long>(w.Host.Database, """SELECT COUNT(DISTINCT "ExpenseNo") FROM "Expenses" """));
    }

    [Fact]
    public async Task AccountName_ClashingWithAnInvisibleBranchAccount_Is400OnName_Not500()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var name = "Till " + RestrictedUserWorld.NewName("");
        await w.NewAccountAsync(name, y);
        var user = await r.CreateAtAsync(x);

        var clash = await SendAsAsync(r, user, HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest(name.ToUpperInvariant(), FinancialAccountKind.Cash, null, 0m, null, x));
        var fresh = await SendAsAsync(r, user, HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest(name + " 2", FinancialAccountKind.Cash, null, 0m, null, x));

        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Contains("name", await ErrorFieldsAsync(clash));
        Assert.True(fresh.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ProductUnitLock_SeesUsageInBranchesTheRestrictedUserCannotAccess()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x); // x has no documents at all; every use of the Pen unit is in branches the user cannot see

        var detail = await GetJsonAsync(user, $"/api/v1/products/{w.ProductId}");

        Assert.True(detail.GetProperty("isBaseUnitLocked").GetBoolean());
        Assert.All(detail.GetProperty("units").EnumerateArray().Where(u => u.GetProperty("id").GetGuid() == w.ProductUnitId), u => Assert.True(u.GetProperty("isLocked").GetBoolean()));
    }

    [Fact]
    public async Task BatchLabels_OfAnotherBranchsPurchase_StayInSaleDetailAndStockDetail()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        // 8b: a branch sells from its OWN store, so a batch of another branch's purchase can only sit at x's store after a transfer (Phase 9):
        // purchase at y's store, then a transfer y -> x (create, approve, legacy-dispatched state seeded; receive at x).
        var purchaseY = await JsonAsync(await w.PurchaseAsync(y, qty: 4000m));
        var yStore = await StoreOfAsync(w, y); var xStore = await StoreOfAsync(w, x);
        var transfer = await RestrictedUserWorld.JsonOkAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(yStore, xStore, "batch label test", true, [new StockTransferLineRequest(w.ProductId, w.ProductUnitId, 4000m)]), branch: y));
        var transferId = IdOf(transfer);
        var approved = await RestrictedUserWorld.JsonOkAsync(await w.SendAsync(HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/approve", branch: y));
        var lineId = approved.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await using (var seedDb = w.Host.Database.CreateDbContext(w.CompanyId)) // FI-D3: production dispatches through shipments; seed the legacy-dispatched state this receive scenario needs
        {
            await Sellora.IntegrationTests.TestData.LegacyTransferSeed.DispatchAsync(seedDb, w.AdminId, transferId, [new Sellora.Domain.Stock.DispatchStockTransferLineInput(lineId, w.ProductUnitId, 4000m)]);
        }
        var received = await RestrictedUserWorld.JsonOkAsync(await w.SendAsync(HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/receipts",
            new ReceiveStockTransferRequest(null, [new ReceiveStockTransferLineRequest(lineId, w.ProductUnitId, 4000m, 0m)]), branch: x));
        Assert.Equal("Completed", received.GetProperty("transfer").GetProperty("status").GetString());
        var user = await r.CreateAtAsync(x);

        var sale = await RestrictedUserWorld.JsonOkAsync(await SendAsAsync(r, user, HttpMethod.Post, "/api/v1/sales",
            new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m, [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, 20m, 0)], null)));
        var detail = await GetJsonAsync(user, $"/api/v1/sales/{IdOf(sale)}");
        var stock = await GetJsonAsync(user, $"/api/v1/stock/{w.ProductId}");

        var consumptions = detail.GetProperty("lines")[0].GetProperty("batchConsumptions").EnumerateArray().ToArray();
        Assert.NotEmpty(consumptions);
        Assert.All(consumptions, c =>
        {
            Assert.Equal("Purchase", c.GetProperty("sourceType").GetString());
            Assert.StartsWith("PUR-", c.GetProperty("sourceRefNo").GetString());
            Assert.NotEqual(JsonValueKind.Null, c.GetProperty("purchaseOrderId").ValueKind);
        });
        var batches = stock.GetProperty("batches").EnumerateArray().ToArray();
        Assert.Contains(batches, b => b.GetProperty("purchaseOrderId").ValueKind == JsonValueKind.String && b.GetProperty("purchaseOrderId").GetGuid() == IdOf(purchaseY));
        Assert.All(batches.Where(b => b.GetProperty("sourceType").GetString() == "Purchase"), b => Assert.StartsWith("PUR-", b.GetProperty("sourceRefNo").GetString()));
    }
}
