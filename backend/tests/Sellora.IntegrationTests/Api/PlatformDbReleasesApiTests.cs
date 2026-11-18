using System.Net;
using System.Net.Http.Json;
using Sellora.Api.Contracts;
using Sellora.DbMigrator;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Database releases page API (docs/features/db-release-automation.md, "API contract", AC8): read-only views of the ops journal,
/// platform admins only. The journal is produced by running the real migrator against the test database.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlatformDbReleasesApiTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/platform/db-releases";

    private static async Task<ApiHost> StartAsync(PostgresFixture postgres)
    {
        var host = await ApiHost.StartAsync(postgres);
        var ops = await Phase6Support.AddUserAsync(host, "ops.releases", administrator: true, company: null);
        await Phase6Support.GrantPlatformAdminAsync(host, ops);
        return host;
    }

    /// <summary>One run: a script that succeeds, one that fails (stops the run), and nothing after it.</summary>
    private static async Task RunMigratorAsync(ApiHost host)
    {
        var root = Directory.CreateTempSubdirectory("sellora-releases-api-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts", "2026.11.18.001__create-demo.sql"),
                "-- sellora:description Create the demo table\nCREATE TABLE api_demo (id int PRIMARY KEY);");
            File.WriteAllText(Path.Combine(root, "scripts", "2026.11.18.002__broken.sql"),
                "-- sellora:description Fails on purpose\nINSERT INTO api_demo VALUES (1), (1);");
            var options = new MigratorOptions(host.Database.ConnectionString, root,
                new RunInfo("claude-dev", "abc1234def", "48.1", "Deploy", "ci-bot", null, "https://github.com/example/actions/runs/1"),
                ViolationMode.Warn, TimeSpan.FromSeconds(30), EfBundlePath: null, PlanOnly: false);
            Assert.Equal(ExitCode.ScriptFailed, await new MigrationRunner(options, new StringWriter()).RunAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Overview_without_a_journal_reports_it_as_unavailable()
    {
        await using var host = await StartAsync(postgres);
        using var client = await Phase6Support.PlatformClientAsync(host, "ops.releases");

        var overview = await client.GetFromJsonAsync<DbReleasesOverviewDto>(Route, ApiHost.Json);

        Assert.False(overview!.JournalAvailable);
        Assert.Empty(overview.Runs);
        Assert.Empty(overview.Scripts);
    }

    [Fact]
    public async Task Overview_run_and_script_detail_show_what_the_migrator_recorded()
    {
        await using var host = await StartAsync(postgres);
        await RunMigratorAsync(host);
        using var client = await Phase6Support.PlatformClientAsync(host, "ops.releases");

        var overview = (await client.GetFromJsonAsync<DbReleasesOverviewDto>(Route, ApiHost.Json))!;

        Assert.True(overview.JournalAvailable);
        var run = Assert.Single(overview.Runs);
        Assert.Equal(("claude-dev", "Failed", "Deploy", "ci-bot"), (run.Environment, run.Status, run.Trigger, run.TriggeredBy));
        Assert.Contains("2026.11.18.002__broken.sql failed", run.Summary);

        var data = overview.Scripts.Where(s => s.Kind == "Data").ToList();
        Assert.Equal(["2026.11.18.001__create-demo.sql", "2026.11.18.002__broken.sql"], data.Select(s => s.Name));
        var broken = data[1];
        Assert.Equal(("Failed", 1, "23505"), (broken.Status, broken.AttemptCount, broken.LastSqlState));
        Assert.NotNull(broken.LastError);
        Assert.Contains(overview.Scripts, s => s.Kind == "EfMigration"); // the API's own EF migrations are mirrored
        Assert.Equal("EfMigration", overview.Scripts[0].Kind); // EF first, then data (R1)

        var detail = (await client.GetFromJsonAsync<DbScriptDetailDto>($"{Route}/scripts/{broken.Id}", ApiHost.Json))!;
        Assert.Equal("Fails on purpose", detail.Script.Description);
        Assert.Contains("INSERT INTO api_demo VALUES (1), (1);", detail.Sql);
        var attempt = Assert.Single(detail.Attempts);
        Assert.Equal(run.Id, attempt.ReleaseRunId);

        var runDetail = (await client.GetFromJsonAsync<DbReleaseRunDetailDto>($"{Route}/runs/{run.Id}", ApiHost.Json))!;
        Assert.Equal(["2026.11.18.001__create-demo.sql", "2026.11.18.002__broken.sql"],
            runDetail.Attempts.Where(a => a.ScriptKind == "Data").Select(a => a.ScriptName));
    }

    [Fact]
    public async Task Retry_without_github_app_settings_is_503_and_the_overview_says_why()
    {
        await using var host = await StartAsync(postgres);
        await RunMigratorAsync(host);
        using var client = await Phase6Support.PlatformClientAsync(host, "ops.releases");

        var overview = (await client.GetFromJsonAsync<DbReleasesOverviewDto>(Route, ApiHost.Json))!;
        var tooShort = await client.PostAsJsonAsync($"{Route}/retry", new RetryDbReleaseRequest("claude-dev", "short"), ApiHost.Json);
        var unconfigured = await client.PostAsJsonAsync($"{Route}/retry", new RetryDbReleaseRequest("claude-dev", "Retry after fixing the data"), ApiHost.Json);

        Assert.False(overview.Retry.Configured);
        Assert.False(overview.Retry.CanRetry);
        Assert.NotNull(overview.Retry.UnavailableReason);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("\"reason\"", await tooShort.Content.ReadAsStringAsync());
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "retry_not_configured"), (unconfigured.StatusCode, await unconfigured.ProblemCodeAsync()));
    }

    [Fact]
    public async Task Unknown_ids_are_404()
    {
        await using var host = await StartAsync(postgres);
        await RunMigratorAsync(host);
        using var client = await Phase6Support.PlatformClientAsync(host, "ops.releases");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/scripts/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Only_a_platform_admin_session_can_read_it()
    {
        await using var host = await StartAsync(postgres);
        using var anonymous = host.NewClient();
        using var company = await host.LoginClientAsync(); // company Administrator: a company token, not a platform session
        using var forged = host.NewClient(PlatformApiTests.SignPlatformToken(await Phase6Support.AdminUserIdAsync(host))); // platform claim, no grant

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        var asCompany = await company.GetAsync(Route);
        Assert.Equal((HttpStatusCode.Unauthorized, "platform_session_required"), (asCompany.StatusCode, await asCompany.ProblemCodeAsync()));
        var asForged = await forged.GetAsync($"{Route}/runs/{Guid.NewGuid()}");
        Assert.Equal((HttpStatusCode.Forbidden, "platform_admin_required"), (asForged.StatusCode, await asForged.ProblemCodeAsync()));
    }
}
