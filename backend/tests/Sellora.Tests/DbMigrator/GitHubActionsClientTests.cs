using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sellora.Infrastructure.GitHub;

namespace Sellora.Tests.DbMigrator;

/// <summary>GitHub App client used by the Database releases retry (docs/features/db-release-automation.md, D20/D21).</summary>
public class GitHubActionsClientTests
{
    private static readonly RSA Key = RSA.Create(2048);

    private static GitHubAppOptions Options(string? privateKey = null) => new()
    {
        AppId = "123456",
        InstallationId = 987,
        PrivateKey = privateKey ?? Key.ExportPkcs8PrivateKeyPem(),
        Owner = "acme",
        Repository = "sellora-billing",
        ApiBaseUrl = "https://api.example.test",
    };

    private sealed class Throwing(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw ex;
    }

    [Fact]
    public async Task Network_failures_and_timeouts_are_reported_not_thrown()
    {
        var unreachable = await new GitHubActionsClient(new HttpClient(new Throwing(new HttpRequestException("dns"))), Microsoft.Extensions.Options.Options.Create(Options()))
            .RerunFailedJobsAsync(42, CancellationToken.None);
        var timeout = await new GitHubActionsClient(new HttpClient(new Throwing(new TaskCanceledException("timeout"))), Microsoft.Extensions.Options.Options.Create(Options()))
            .RerunFailedJobsAsync(42, CancellationToken.None);

        Assert.Equal("GitHub could not be reached (HttpRequestException).", unreachable.Message);
        Assert.Equal("GitHub did not answer in time.", timeout.Message);
    }

    [Fact]
    public void Plain_http_api_base_url_disables_the_client()
    {
        var options = Options();
        options.ApiBaseUrl = "http://api.example.test";
        Assert.False(Client(new Recorder(), options).IsConfigured);
    }

    private sealed class Recorder(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<(HttpMethod Method, string Url, string? Auth)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GitHubActionsClient Client(Recorder handler, GitHubAppOptions? options = null) =>
        new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(options ?? Options()));

    [Fact]
    public void App_jwt_is_rs256_signed_by_the_app_key_issued_by_the_app_and_short_lived()
    {
        var now = DateTimeOffset.UtcNow;
        var jwt = GitHubActionsClient.CreateAppJwt(Options(), now);

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "123456",
            ValidateAudience = false,
            IssuerSigningKey = new RsaSecurityKey(Key.ExportParameters(false)),
            ClockSkew = TimeSpan.Zero,
        };
        new JwtSecurityTokenHandler().ValidateToken(jwt, parameters, out var validated);

        var token = (JwtSecurityToken)validated;
        Assert.Equal("RS256", token.Header.Alg);
        Assert.InRange(token.ValidTo - now.UtcDateTime, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(10));
        Assert.Equal(now.AddSeconds(-60).ToUnixTimeSeconds().ToString(), token.Claims.Single(c => c.Type == "iat").Value);
    }

    [Fact]
    public void Private_key_with_escaped_newlines_is_accepted()
    {
        var escaped = Key.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");
        Assert.NotEmpty(GitHubActionsClient.CreateAppJwt(Options(escaped), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("https://github.com/acme/sellora-billing/actions/runs/1234567890", 1234567890L)]
    [InlineData("https://github.com/ACME/Sellora-Billing/actions/runs/42/attempts/2", 42L)]
    [InlineData("https://github.com/someone-else/sellora-billing/actions/runs/42", null)]
    [InlineData("https://github.com.evil.test/acme/sellora-billing/actions/runs/42", null)]
    [InlineData("http://github.com/acme/sellora-billing/actions/runs/42", null)]
    [InlineData("https://github.com/acme/sellora-billing/actions/runs/42?x=/runs/1", null)]
    [InlineData("https://github.com/acme/sellora-billing/actions/runs/42\n", null)]
    [InlineData(null, null)]
    public void Only_runs_of_the_configured_repository_are_accepted(string? url, long? expected)
    {
        Assert.Equal(expected, Client(new Recorder()).ParseRunId(url));
    }

    [Fact]
    public void Not_configured_without_a_private_key_and_then_nothing_is_parsed()
    {
        var options = Options();
        options.PrivateKey = null;
        var client = Client(new Recorder(), options);

        Assert.False(client.IsConfigured);
        Assert.Null(client.ParseRunId("https://github.com/acme/sellora-billing/actions/runs/42"));
    }

    [Fact]
    public async Task Rerun_exchanges_the_app_jwt_for_an_installation_token_then_reruns_the_failed_jobs()
    {
        var handler = new Recorder(
            Json(HttpStatusCode.Created, """{"token":"ghs_installation","expires_at":"2099-01-01T00:00:00Z"}"""),
            new HttpResponseMessage(HttpStatusCode.Created));

        var result = await Client(handler).RerunFailedJobsAsync(42, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal((HttpMethod.Post, "https://api.example.test/app/installations/987/access_tokens"), (handler.Calls[0].Method, handler.Calls[0].Url));
        Assert.StartsWith("Bearer ey", handler.Calls[0].Auth); // the app JWT
        Assert.Equal((HttpMethod.Post, "https://api.example.test/repos/acme/sellora-billing/actions/runs/42/rerun-failed-jobs", "Bearer ghs_installation"),
            handler.Calls[1]);
    }

    [Fact]
    public async Task GitHub_refusal_is_reported_with_its_status_and_message()
    {
        var handler = new Recorder(
            Json(HttpStatusCode.Created, """{"token":"ghs_installation"}"""),
            Json(HttpStatusCode.Forbidden, """{"message":"Unable to retry this workflow run because it was created over a month ago"}"""));

        var result = await Client(handler).RerunFailedJobsAsync(42, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("403 Unable to retry this workflow run because it was created over a month ago", result.Message);
    }

    [Fact]
    public async Task Bad_app_credentials_stop_before_the_rerun_call()
    {
        var handler = new Recorder(Json(HttpStatusCode.Unauthorized, """{"message":"A JSON web token could not be decoded"}"""));

        var result = await Client(handler).RerunFailedJobsAsync(42, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("GitHub refused the app credentials: 401", result.Message);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Unreadable_private_key_never_echoes_key_material()
    {
        var handler = new Recorder();
        var result = await Client(handler, Options("-----BEGIN PRIVATE KEY-----\nnot-a-key\n-----END PRIVATE KEY-----")).RerunFailedJobsAsync(42, CancellationToken.None);

        Assert.Equal("The GitHub App private key could not be read.", result.Message);
        Assert.Empty(handler.Calls);
    }
}
