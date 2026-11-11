using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Sellora.Api.Authorization;
using Sellora.Domain.Auth;
using Sellora.Infrastructure.Data;
using Sellora.Api.Contracts;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>T6 — the <c>X-Branch-Id</c> header and the <c>branchId</c> list/report filters through the real pipeline.</summary>
[Collection(PostgresCollection.Name)]
public class BranchHeaderApiTests(PostgresFixture postgres)
{
    private static readonly string[] ListRoutes =
    [
        "/api/v1/purchases", "/api/v1/purchase-returns", "/api/v1/sales", "/api/v1/sales-returns", "/api/v1/stock-adjustments",
        "/api/v1/expenses", "/api/v1/financial-accounts", "/api/v1/reports/purchases", "/api/v1/reports/sales", "/api/v1/reports/profit-summary",
    ];

    public static TheoryData<string> Routes => new(ListRoutes);

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("12345")]
    public async Task Header_MalformedOrEmpty_Returns400BranchHeaderInvalid(string value)
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Get, "/api/v1/sales", rawBranchHeader: value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("branch_header_invalid", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Middleware_EmptyHeaderValue_Returns400BranchHeaderInvalid()
    {
        // HttpClient/TestServer do not deliver a literally empty header value, so the middleware is driven directly (same database, real membership).
        var w = await GetAsync(postgres);
        await using var db = w.Host.Database.CreateDbContext();
        var context = new DefaultHttpContext();
        context.RequestServices = w.Host.Factory.Services;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", w.AdminId.ToString()), new Claim(AuthClaimTypes.CompanyId, w.CompanyId.ToString())], "test"));
        context.Request.Headers["X-Branch-Id"] = "";
        var called = false;
        var middleware = new CompanyContextMiddleware(_ => { called = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, db, new CompanyContext(), new BranchContext(), new MembershipContext());

        Assert.False(called);
        Assert.Equal(400, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("branch_header_invalid", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Header_RepeatedValues_Returns400BranchHeaderInvalid()
    {
        var w = await GetAsync(postgres);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sales");
        request.Headers.Add("X-Branch-Id", [w.MainId.ToString(), w.MainId.ToString()]);

        var response = await w.Admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("branch_header_invalid", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Header_UnknownAndInactiveBranch_AnswerIdentically403BranchAccessDenied()
    {
        var w = await GetAsync(postgres);
        var (inactive, _) = await w.NewBranchAsync();
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{inactive}/status", new SetStatusRequest(false, "test"));
        var unknown = Guid.NewGuid();

        var unknownResponse = await w.SendAsync(HttpMethod.Get, "/api/v1/sales", branch: unknown);
        var inactiveResponse = await w.SendAsync(HttpMethod.Get, "/api/v1/sales", branch: inactive);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (unknownResponse.StatusCode, inactiveResponse.StatusCode));
        Assert.Equal("branch_access_denied", await unknownResponse.ProblemCodeAsync());
        Assert.Equal("branch_access_denied", await inactiveResponse.ProblemCodeAsync());
        var (u, i) = (await JsonAsync(unknownResponse), await JsonAsync(inactiveResponse));
        Assert.Equal((u.GetProperty("title").GetString(), u.GetProperty("status").GetInt32()), (i.GetProperty("title").GetString(), i.GetProperty("status").GetInt32()));
        Assert.DoesNotContain(unknown.ToString(), await unknownResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Header_ValidActiveBranchOrAbsent_PassesThrough()
    {
        var w = await GetAsync(postgres);
        var (id, _) = await w.NewBranchAsync();

        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, "/api/v1/sales", branch: id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, "/api/v1/sales", branch: w.MainId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, "/api/v1/sales")).StatusCode);
    }

    [Fact]
    public async Task Header_IsValidatedOnEveryAuthenticatedRoute_ButIgnoredOnAnonymousOnes()
    {
        var w = await GetAsync(postgres);

        var settings = await w.SendAsync(HttpMethod.Get, "/api/v1/settings", rawBranchHeader: "garbage");
        var accessible = await w.SendAsync(HttpMethod.Get, "/api/v1/branches/accessible", branch: Guid.NewGuid());
        using var anonymous = w.Host.NewClient();
        var login = await w.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(ApiHost.AdminEmail, ApiHost.AdminPassword), rawBranchHeader: "garbage", client: anonymous);

        Assert.Equal(HttpStatusCode.BadRequest, settings.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, accessible.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Header_ForAnotherBranchWithAValidTokenOfAnotherUser_StillNeedsAnActiveMembership()
    {
        var w = await GetAsync(postgres);
        using var stranger = w.Host.NewClient(ApiHost.SignToken(Guid.NewGuid(), w.CompanyId, "Sales.View"));

        var response = await w.SendAsync(HttpMethod.Get, "/api/v1/sales", branch: w.MainId, client: stranger);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // the company check comes first; the header cannot bypass it
        Assert.Equal("company_context_invalid", await response.ProblemCodeAsync());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task BranchIdFilter_UnknownBranch_Returns403BranchAccessDenied(string route)
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Get, $"{route}?branchId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("branch_access_denied", await response.ProblemCodeAsync());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task BranchIdFilter_OwnBranchOrAbsent_Returns200(string route)
    {
        var w = await GetAsync(postgres);
        var (id, _) = await w.NewBranchAsync();

        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"{route}?branchId={id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, route)).StatusCode);
    }
}
