using System.Net;
using System.Text.Json;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// D10 guard: with Database:ApplyMigrationsOnStartup=false a deployment can run code ahead of its schema. /health must then report
/// 503 pending_migrations (instead of "healthy" while queries fail), and Database:FailOnPendingMigrations=true refuses to start.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StartupMigrationCheckApiTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> NoStartupMigration = new() { ["Database:ApplyMigrationsOnStartup"] = "false" };

    [Fact]
    public async Task Health_OnAnUpToDateSchema_IsHealthy_WithZeroPendingMigrations()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var client = host.NewClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("pendingMigrations").GetInt32());
    }

    [Fact]
    public async Task Health_WhenTheSchemaIsBehindTheCode_Is503PendingMigrations_AndListsOnlyTheCount()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false); // empty: every migration pending
        await using var factory = new SelloraApiFactory(database.ConnectionString, NoStartupMigration);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("pending_migrations", body.RootElement.GetProperty("reason").GetString());
        Assert.True(body.RootElement.GetProperty("pendingMigrations").GetInt32() > 0);
        Assert.DoesNotContain("_Add", await response.Content.ReadAsStringAsync()); // migration names stay in the server log
    }

    [Fact]
    public async Task Startup_WithFailOnPendingMigrations_RefusesToStartOnABehindSchema()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        var settings = new Dictionary<string, string>(NoStartupMigration) { ["Database:FailOnPendingMigrations"] = "true" };
        await using var factory = new SelloraApiFactory(database.ConnectionString, settings);

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("pending EF Core migration", Flatten(error));
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (var e = error; e is not null; e = e.InnerException) messages.Add(e.Message);
        return string.Join(" | ", messages);
    }
}
