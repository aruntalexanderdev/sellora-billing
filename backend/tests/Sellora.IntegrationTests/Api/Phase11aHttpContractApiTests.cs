using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>Phase 11a WP-T1 — the HTTP contract the Angular client relies on: correlation id on MVC validation errors (D6), CORS preflight for the custom
/// request headers (D10), and the per-code <c>scope</c> on GET /permissions (D8) consistent with <see cref="PermissionCatalog"/>.</summary>
[Collection(PostgresCollection.Name)]
public class Phase11aHttpContractApiTests(PostgresFixture postgres)
{
    private const string Origin = "https://app.sellora.example";
    private const string CorrelationId = "corr-11a-0123456789";

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task ValidationProblem400_DataAnnotationsFailure_CarriesTraceIdEqualToTheSentCorrelationId_AndEchoesTheHeader()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { password = "x" }) }; // UsernameOrEmail [Required] missing
        request.Headers.Add("X-Correlation-Id", CorrelationId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CorrelationId, Header(response, "X-Correlation-Id"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("errors", out var errors), "expected an MVC ValidationProblemDetails body");
        Assert.Contains(errors.EnumerateObject(), p => p.Name.Equals("UsernameOrEmail", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CorrelationId, body.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task ValidationProblem400_WithoutACorrelationId_StillCarriesTraceId_MatchingTheEchoedHeader()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = host.NewClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { password = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var echoed = Header(response, "X-Correlation-Id");
        Assert.False(string.IsNullOrWhiteSpace(echoed));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(echoed, body.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task Preflight_WithBranchCorrelationAndIdempotencyHeaders_FromAnAllowedOrigin_Succeeds()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var factory = host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Cors:AllowedOrigins:0", Origin));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/sales");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-branch-id,x-correlation-id,idempotency-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Origin, Header(response, "Access-Control-Allow-Origin"));
        var allowed = Header(response, "Access-Control-Allow-Headers")!.ToLowerInvariant();
        Assert.All(new[] { "x-branch-id", "x-correlation-id", "idempotency-key" }, h => Assert.Contains(h, allowed));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task ActualResponse_FromAnAllowedOrigin_ExposesTheCorrelationIdHeaderToTheBrowser()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var factory = host.Factory.WithWebHostBuilder(builder => builder.UseSetting("Cors:AllowedOrigins:0", Origin));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { password = "x" }) };
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("X-Correlation-Id", CorrelationId);

        var response = await client.SendAsync(request);

        Assert.Equal(Origin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("x-correlation-id", (Header(response, "Access-Control-Expose-Headers") ?? "").ToLowerInvariant());
    }

    [Fact]
    public async Task GetPermissions_ReturnsScopePerCode_ConsistentWithThePermissionCatalog()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = await host.LoginClientAsync();

        var response = await client.GetAsync("/api/v1/permissions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(e => e.GetProperty("code").GetString()!, e => e.GetProperty("scope").GetString());
        Assert.Equal(PermissionCatalog.All.Count, items.Count);
        foreach (var definition in PermissionCatalog.All)
        {
            var expected = PermissionCatalog.IsBranchEligible(definition.Code) ? "CompanyOrBranch" : "CompanyOnly";
            Assert.True(items.TryGetValue(definition.Code, out var actual), $"missing {definition.Code}");
            Assert.True(expected == actual, $"{definition.Code}: expected {expected}, got {actual}");
        }
        Assert.Equal("CompanyOrBranch", items["Sales.View"]);
        Assert.Equal("CompanyOrBranch", items["Branch.Roles.Manage"]);
        Assert.Equal("CompanyOnly", items["Roles.Create"]);
        Assert.Contains("CompanyOrBranch", items.Values);
        Assert.Contains("CompanyOnly", items.Values);
    }
}
