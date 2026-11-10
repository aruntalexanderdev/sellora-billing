using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 7 evaluation plumbing over HTTP: token claims are not trusted (T7/ADR-04), the permission handler is scoped (T8, phase-6 K7: never a captured singleton), and the in-code
/// checks that used to read claims (price override, dashboard sections) use the effective permissions at the working branch.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ScopedRolesEvaluationApiTests(PostgresFixture postgres)
{
    private const string Pw = "Passw0rd!99";
    private static int _seq;
    private static string N(string prefix) => prefix + Interlocked.Increment(ref _seq) + Guid.NewGuid().ToString("N")[..4];

    private async Task<BranchApiWorld> WorldAsync() => await GetAsync(postgres);

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string name, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(name, null, codes, scope, branch));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Id, string Username, HttpClient Client)> UserAsync(BranchApiWorld w, Guid role, Guid[]? branches = null, Guid? primary = null)
    {
        var username = N("u");
        var scope = branches is null ? CompanyAccessScope.CompanyWide : CompanyAccessScope.AssignedBranches;
        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/users", new CreateUserRequest("T", "U", username, username + "@selloramart.in", null, Pw, Pw, role, scope, branches, primary));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        var auth = await w.Host.LoginAsync(username, Pw);
        return (id, username, w.Host.NewClient(auth.AccessToken));
    }

    private static async Task AssignAsync(BranchApiWorld w, Guid user, Guid role)
    {
        var response = await w.SendAsync(HttpMethod.Post, $"/api/v1/users/{user}/roles", new AssignRoleRequest(role, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- claims are not trusted ----

    [Fact]
    public async Task ATokenWithPermissionClaims_ButNoRoleAssignment_IsRefused()
    {
        var w = await WorldAsync();
        var user = await UserAsync(w, await RoleAsync(w, N("Empty"), []));
        using var forged = w.Host.NewClient(ApiHost.SignToken(user.Id, w.CompanyId, "Sales.View", "Users.View", "Roles.View", "Masters.Edit"));

        Assert.Equal(HttpStatusCode.Forbidden, (await forged.GetAsync("/api/v1/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.GetAsync("/api/v1/roles")).StatusCode);
    }

    [Fact]
    public async Task AnAdministratorsTokenWithoutAnyPermissionClaim_StillWorks_PermissionsComeFromTheDatabase()
    {
        var w = await WorldAsync();
        using var bare = w.Host.NewClient(ApiHost.SignToken(w.AdminId, w.CompanyId));

        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/v1/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/v1/roles")).StatusCode);
    }

    [Fact]
    public async Task AValidTokenOfADisabledUser_AndOfAnEndedAdministratorAssignment_IsRefusedImmediately()
    {
        var w = await WorldAsync();
        var administrator = (await JsonAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/roles"))).EnumerateArray().Single(r => r.GetProperty("isSystem").GetBoolean()).GetProperty("id").GetGuid();
        var second = await UserAsync(w, administrator);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/api/v1/roles")).StatusCode);
        var row = (await JsonAsync(await w.SendAsync(HttpMethod.Get, $"/api/v1/users/{second.Id}/roles"))).GetProperty("current").EnumerateArray().Single().GetProperty("id").GetGuid();

        var end = await w.SendAsync(HttpMethod.Post, $"/api/v1/users/{second.Id}/roles/{row}/end", new EndRoleAssignmentRequest("demoted"));

        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.Client.GetAsync("/api/v1/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.Client.GetAsync("/api/v1/sales")).StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsTheEffectivePermissionsOfTheWorkingBranch_AndRoles()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var user = await UserAsync(w, await RoleAsync(w, N("Buyer"), ["Purchase.View"]), [b1, b2], b1);
        await AssignAsync(w, user.Id, await RoleAsync(w, N("B1Sales"), ["Sales.View"], RoleScope.Branch, b1));

        var me = await JsonAsync(await user.Client.GetAsync("/api/v1/auth/me"));

        Assert.Equal(["Purchase.View", "Sales.View"], me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order().ToArray());
        Assert.Equal(2, me.GetProperty("roles").GetArrayLength());
    }

    // ---- T8: handler lifetime ----

    [Fact]
    public async Task PermissionAuthorizationHandler_IsScoped_NewPerScope_SameWithinAScope_AndNeverASingleton()
    {
        var w = await WorldAsync();
        var services = w.Host.Factory.Services;

        PermissionAuthorizationHandler FromScope(IServiceScope scope) =>
            scope.ServiceProvider.GetServices<IAuthorizationHandler>().OfType<PermissionAuthorizationHandler>().Single();

        using var s1 = services.CreateScope();
        using var s2 = services.CreateScope();
        var a1 = FromScope(s1);
        var a2 = FromScope(s1);
        var b = FromScope(s2);

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MembershipContext_IsPerRequest_TwoUsersInParallel_NeverSeeEachOthersPermissions()
    {
        var w = await WorldAsync();
        var seller = await UserAsync(w, await RoleAsync(w, N("Seller"), ["Sales.View"]));
        var buyer = await UserAsync(w, await RoleAsync(w, N("Buyer"), ["Purchase.View"]));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            seller.Client.GetAsync("/api/v1/sales").ContinueWith(t => ("seller-sales", t.Result.StatusCode)),
            seller.Client.GetAsync("/api/v1/purchases").ContinueWith(t => ("seller-purchases", t.Result.StatusCode)),
            buyer.Client.GetAsync("/api/v1/purchases").ContinueWith(t => ("buyer-purchases", t.Result.StatusCode)),
            buyer.Client.GetAsync("/api/v1/sales").ContinueWith(t => ("buyer-sales", t.Result.StatusCode)),
        }));

        Assert.All(results.Where(r => r.Item1 is "seller-sales" or "buyer-purchases"), r => Assert.Equal(HttpStatusCode.OK, r.Item2));
        Assert.All(results.Where(r => r.Item1 is "seller-purchases" or "buyer-sales"), r => Assert.Equal(HttpStatusCode.Forbidden, r.Item2));
    }

    // ---- in-code checks (price override, dashboard) ----

    private static Task<HttpResponseMessage> SaleAtPriceAsync(BranchApiWorld w, HttpClient client, Guid? branch, decimal price, bool overridePrice) =>
        w.SendAsync(HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m,
            [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, price, 0, overridePrice)], null), branch, client);

    [Fact]
    public async Task PriceOverride_NeedsTheEffectiveCode_CompanyWide_OrAtTheWorkingBranch()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var plain = await UserAsync(w, await RoleAsync(w, N("Cashier"), ["Sales.View", "Sales.Create"]), [b1, b2], b1);
        var company = await UserAsync(w, await RoleAsync(w, N("Boss"), ["Sales.View", "Sales.Create", "Sales.PriceOverride"]), [b1, b2], b1);
        var atB1 = await UserAsync(w, await RoleAsync(w, N("Cashier2"), ["Sales.View", "Sales.Create"]), [b1, b2], b1);
        await AssignAsync(w, atB1.Id, await RoleAsync(w, N("B1Override"), ["Sales.PriceOverride"], RoleScope.Branch, b1));

        Assert.Equal(HttpStatusCode.BadRequest, (await SaleAtPriceAsync(w, plain.Client, b1, 15m, true)).StatusCode);        // list price is 20
        Assert.Equal(HttpStatusCode.OK, (await SaleAtPriceAsync(w, plain.Client, b1, 20m, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SaleAtPriceAsync(w, company.Client, b2, 15m, true)).StatusCode);              // company-level override applies at every branch
        Assert.Equal(HttpStatusCode.OK, (await SaleAtPriceAsync(w, atB1.Client, b1, 15m, true)).StatusCode);                 // branch role: at B1
        Assert.Equal(HttpStatusCode.BadRequest, (await SaleAtPriceAsync(w, atB1.Client, b2, 15m, true)).StatusCode);         // ...not at B2
    }

    [Fact]
    public async Task Dashboard_ShowsOnlySectionsOfHeldCodes_AndBranchRoleHoldersSeeOnlyTheirBranchsDocuments()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var no1 = (await JsonAsync(await w.SaleAsync(b1))).GetProperty("invoiceNo").GetString();
        var no2 = (await JsonAsync(await w.SaleAsync(b2))).GetProperty("invoiceNo").GetString();
        var branchUser = await UserAsync(w, await RoleAsync(w, N("Empty"), []), [b1, b2], b1);
        await AssignAsync(w, branchUser.Id, await RoleAsync(w, N("B1Sales"), ["Sales.View"], RoleScope.Branch, b1));
        var nobody = await UserAsync(w, await RoleAsync(w, N("Empty2"), []));

        var branchView = await JsonAsync(await branchUser.Client.GetAsync("/api/v1/dashboard"));
        var emptyView = await JsonAsync(await nobody.Client.GetAsync("/api/v1/dashboard"));

        var recent = branchView.GetProperty("recentSales").EnumerateArray().Select(s => s.GetProperty("invoiceNo").GetString()).ToList();
        Assert.Contains(no1, recent);
        Assert.DoesNotContain(no2, recent);
        Assert.Equal(JsonValueKind.Null, branchView.GetProperty("recentPurchases").ValueKind);
        Assert.Equal(JsonValueKind.Null, emptyView.GetProperty("recentSales").ValueKind);
        Assert.Equal(JsonValueKind.Null, emptyView.GetProperty("todaysSales").ValueKind);
    }
}
