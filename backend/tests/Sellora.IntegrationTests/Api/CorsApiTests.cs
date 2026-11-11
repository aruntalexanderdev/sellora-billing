using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>T10 — CORS policy built from <c>Cors:AllowedOrigins</c> (D10): allowed origin gets its header and the custom request headers on preflight,
/// an unlisted origin gets nothing, credentials are never allowed, an empty list fails closed.</summary>
[Collection(PostgresCollection.Name)]
public class CorsApiTests(PostgresFixture postgres)
{
    private const string Allowed = "https://app.sellora.example";
    private const string RequestedHeaders = "authorization,content-type,idempotency-key,x-branch-id";

    private static HttpClient Client(ApiHost host, params string[] origins)
    {
        var factory = host.Factory.WithWebHostBuilder(builder =>
        {
            // Index 0..n replace the appsettings.json defaults; an empty string disables the slot.
            for (var i = 0; i < Math.Max(origins.Length, 1); i++) builder.UseSetting($"Cors:AllowedOrigins:{i}", i < origins.Length ? origins[i] : "");
        });
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient client, string origin, string route = "/api/v1/sales")
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, route);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", RequestedHeaders);
        return await client.SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task Preflight_FromAConfiguredOrigin_IsAllowedWithTheCustomHeadersAndNoCredentials()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = Client(host, Allowed);

        var response = await PreflightAsync(client, Allowed);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Allowed, Header(response, "Access-Control-Allow-Origin"));
        var headers = Header(response, "Access-Control-Allow-Headers")!.ToLowerInvariant();
        Assert.All(new[] { "authorization", "content-type", "idempotency-key", "x-branch-id" }, h => Assert.Contains(h, headers));
        Assert.Contains("POST", Header(response, "Access-Control-Allow-Methods")!);
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
        Assert.Equal("600", Header(response, "Access-Control-Max-Age"));
    }

    [Fact]
    public async Task Preflight_FromAnUnlistedOrigin_GetsNoCorsHeaders()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = Client(host, Allowed);

        var response = await PreflightAsync(client, "https://evil.example");

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Headers"));
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_HeaderOutsideTheAllowList_IsNeverListedAsAllowed()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = Client(host, Allowed);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/sales");
        request.Headers.Add("Origin", Allowed);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-forwarded-company");

        var response = await client.SendAsync(request);

        // ASP.NET Core still answers the origin; what protects the API is that the unknown header is never listed as allowed (the browser then refuses the real request).
        Assert.DoesNotContain("x-forwarded-company", (Header(response, "Access-Control-Allow-Headers") ?? "").ToLowerInvariant());
    }

    [Fact]
    public async Task ActualRequest_FromAConfiguredOrigin_CarriesTheAllowOriginHeader_TrailingSlashInConfigIsTolerated()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = Client(host, Allowed + "/", "https://second.sellora.example");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/branches/accessible");
        request.Headers.Add("Origin", "https://second.sellora.example");
        request.Headers.Add("Authorization", "Bearer " + (await host.LoginAsync()).AccessToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://second.sellora.example", Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal(Allowed, Header(await PreflightAsync(client, Allowed), "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task EmptyOriginList_FailsClosed_NoCorsHeadersEvenForTheShippedDevOrigin()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = Client(host); // overrides slot 0 with an empty string

        var preflight = await PreflightAsync(client, "http://localhost:4200");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/branches/accessible");
        request.Headers.Add("Origin", "http://localhost:4200");
        var actual = await client.SendAsync(request);

        Assert.Null(Header(preflight, "Access-Control-Allow-Origin"));
        Assert.Null(Header(actual, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ShippedDefault_AllowsTheLocalAngularDevOrigin()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = host.NewClient();

        var response = await PreflightAsync(client, "http://localhost:4200");

        Assert.Equal("http://localhost:4200", Header(response, "Access-Control-Allow-Origin"));
    }
}
