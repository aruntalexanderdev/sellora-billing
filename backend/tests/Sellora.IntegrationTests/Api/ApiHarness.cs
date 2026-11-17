using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Sellora.Api.Contracts;
using Sellora.Domain.Auth;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// The real API (<c>WebApplicationFactory&lt;Program&gt;</c>) on a Testcontainers PostgreSQL database: startup runs the migrations
/// and the seeder (default company, admin membership) exactly as in dev. JWT settings are fixed test values.
/// </summary>
public sealed class SelloraApiFactory(string connectionString, IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    public const string Issuer = "sellora-test";
    public const string Audience = "sellora-test";
    public static readonly string SigningKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    /// <summary>Error-level log entries of the hosted API (unhandled exceptions behind a 500), so a test that sees a 500 can say why.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> ServerErrors { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(logging.Services, Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<Microsoft.Extensions.Logging.ILoggerProvider>(new ErrorCaptureProvider(ServerErrors))));
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:SelloraDb", connectionString);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("Jwt:AccessTokenMinutes", "15");
        builder.UseSetting("Jwt:RefreshTokenDays", "14");
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>()) builder.UseSetting(key, value);
    }
}

internal sealed class ErrorCaptureProvider(System.Collections.Concurrent.ConcurrentQueue<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(categoryName, sink);
    public void Dispose() { }

    private sealed class Capture(string category, System.Collections.Concurrent.ConcurrentQueue<string> sink) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = category + ": " + formatter(state, exception) + (exception is null ? "" : " :: " + exception);
            sink.Enqueue(text.Length > 3000 ? text[..3000] : text);
            while (sink.Count > 50) sink.TryDequeue(out _);
        }
    }
}

/// <summary>A started API plus helpers (login, bearer clients, JSON options matching the API's).</summary>
public sealed class ApiHost : IAsyncDisposable
{
    public const string AdminEmail = "admin@selloramart.in";
    public const string AdminPassword = "Passw0rd!23";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public TestDatabase Database { get; }
    public SelloraApiFactory Factory { get; }

    private ApiHost(TestDatabase database, SelloraApiFactory factory)
    {
        Database = database;
        Factory = factory;
    }

    public static async Task<ApiHost> StartAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        var factory = new SelloraApiFactory(database.ConnectionString, settings);
        using var warmup = factory.CreateClient(); // builds the host: migrations + seed
        return new ApiHost(database, factory);
    }

    public HttpClient NewClient(string? accessToken = null)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (accessToken is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public async Task<AuthResponse> LoginAsync(string emailOrUsername = AdminEmail, string password = AdminPassword)
    {
        using var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(emailOrUsername, password), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
    }

    public async Task<HttpClient> LoginClientAsync(string emailOrUsername = AdminEmail, string password = AdminPassword) =>
        NewClient((await LoginAsync(emailOrUsername, password)).AccessToken);

    /// <summary>A validly signed access token with arbitrary claims (the middleware must not trust it blindly).</summary>
    public static string SignToken(Guid userId, Guid? companyId, params string[] permissions)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.ToString()) };
        if (companyId is { } c) claims.Add(new Claim(AuthClaimTypes.CompanyId, c.ToString()));
        claims.AddRange(permissions.Select(p => new Claim(AuthClaimTypes.Permission, p)));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(SelloraApiFactory.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(SelloraApiFactory.Issuer, SelloraApiFactory.Audience, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Phase 7: permissions are evaluated server-side from role assignments, so a "token with only these codes" no longer exists. This creates a real user who holds exactly
    /// <paramref name="permissions"/> through a fresh company role (created through the API by <paramref name="admin"/>) and signs in as them.
    /// </summary>
    public async Task<HttpClient> LimitedClientAsync(HttpClient admin, params string[] permissions)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var role = await admin.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest("Limited " + tag, null, permissions), Json);
        if (!role.IsSuccessStatusCode) throw new InvalidOperationException($"create role: {(int)role.StatusCode} {await role.Content.ReadAsStringAsync()}");
        var roleId = (await role.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        var username = "limited" + tag;
        var user = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("Limited", "User", username, username + "@selloramart.in", null, AdminPassword, AdminPassword, roleId), Json);
        if (!user.IsSuccessStatusCode) throw new InvalidOperationException($"create user: {(int)user.StatusCode} {await user.Content.ReadAsStringAsync()}");
        return NewClient((await LoginAsync(username, AdminPassword)).AccessToken);
    }

    public static string? ClaimOf(string accessToken, string type) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.FirstOrDefault(c => c.Type == type)?.Value;

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }
}

public static class HttpExtensions
{
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
