using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sellora.Domain.Releases;

namespace Sellora.Infrastructure.GitHub;

/// <summary>Configuration section <c>GitHubApp</c> (D20). The private key is a secret: App Service setting or Key Vault reference only.</summary>
public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHubApp";

    public string? AppId { get; set; }
    public long InstallationId { get; set; }

    /// <summary>PEM (PKCS#1 or PKCS#8). "\n" escapes are accepted, for settings that cannot hold newlines.</summary>
    public string? PrivateKey { get; set; }

    public string? Owner { get; set; }
    public string? Repository { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(AppId) && InstallationId > 0 && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repository)
        && ApiBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase); // tokens never travel over plain http
}

/// <summary>
/// GitHub App authentication: a short-lived app JWT (RS256, signed with the app's private key) is exchanged for an installation
/// token, which then calls the Actions API. Tokens are not cached: a retry is a rare, human-triggered action.
/// </summary>
public sealed partial class GitHubActionsClient(HttpClient http, IOptions<GitHubAppOptions> options) : IGitHubActionsClient
{
    private readonly GitHubAppOptions _options = options.Value;

    public bool IsConfigured => _options.IsComplete;

    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+)/actions/runs/(?<id>\d{1,19})(/attempts/\d+)?/?\z")]
    private static partial Regex RunUrl();

    public long? ParseRunId(string? pipelineUrl)
    {
        if (!IsConfigured || pipelineUrl is null) return null;
        var match = RunUrl().Match(pipelineUrl);
        if (!match.Success) return null;
        var sameRepository =
            string.Equals(match.Groups["owner"].Value, _options.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(match.Groups["repo"].Value, _options.Repository, StringComparison.OrdinalIgnoreCase);
        return sameRepository && long.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    public async Task<GitHubCallResult> RerunFailedJobsAsync(long runId, CancellationToken ct)
    {
        if (!IsConfigured) return new GitHubCallResult(false, "The GitHub App is not configured.");
        try
        {
            return await RerunCoreAsync(runId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested)) // HttpClient timeout
        {
            return new GitHubCallResult(false, ex is OperationCanceledException
                ? "GitHub did not answer in time."
                : $"GitHub could not be reached ({ex.GetType().Name}).");
        }
    }

    private async Task<GitHubCallResult> RerunCoreAsync(long runId, CancellationToken ct)
    {
        var installationToken = await InstallationTokenAsync(ct);
        if (installationToken.Token is null) return new GitHubCallResult(false, installationToken.Error);

        using var request = NewRequest(HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(_options.Owner!)}/{Uri.EscapeDataString(_options.Repository!)}/actions/runs/{runId}/rerun-failed-jobs",
            installationToken.Token);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? GitHubCallResult.Ok : new GitHubCallResult(false, await DescribeAsync(response, ct));
    }

    private async Task<(string? Token, string? Error)> InstallationTokenAsync(CancellationToken ct)
    {
        string jwt;
        try
        {
            jwt = CreateAppJwt(_options, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return (null, "The GitHub App private key could not be read."); // never echo key material
        }

        using var request = NewRequest(HttpMethod.Post, $"app/installations/{_options.InstallationId}/access_tokens", jwt);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return (null, "GitHub refused the app credentials: " + await DescribeAsync(response, ct));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } value
            ? (value, null)
            : (null, "GitHub returned no installation token.");
    }

    /// <summary>The app JWT GitHub expects: iss = app id, iat 60 s in the past (clock drift), exp within 10 minutes.</summary>
    public static string CreateAppJwt(GitHubAppOptions options, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.PrivateKey!.Replace("\\n", "\n"));
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            // The RSA instance is disposed after this call; do not let the factory cache a provider that holds it.
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        var token = new JwtSecurityToken(
            issuer: options.AppId,
            claims: [new Claim(JwtRegisteredClaimNames.Iat, now.AddSeconds(-60).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)],
            notBefore: null,
            expires: now.AddMinutes(9).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relativePath, string bearer)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/"), relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Sellora-DbReleases", "1.0"));
        return request;
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? message = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var m)) message = m.GetString();
        }
        catch (JsonException) { /* not JSON */ }
        var text = $"{(int)response.StatusCode} {message ?? response.ReasonPhrase}".Trim();
        return text.Length <= 300 ? text : text[..300];
    }
}
