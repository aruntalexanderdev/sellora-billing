using Sellora.DbMigrator;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.DbMigrator;

[Collection(PostgresCollection.Name)]
public sealed class MigrationRunnerTests(PostgresFixture postgres) : MigrationRunnerScenarios
{
    protected override async Task<(string ConnectionString, IAsyncDisposable Drop)> CreateEmptyDatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        return (database.ConnectionString, database);
    }

    /// <summary>
    /// The release package in the repository (backend/db) applies cleanly to the current schema and is idempotent:
    /// a second run executes only the validation scripts. This is the "scripts run on a throwaway Postgres" CI check.
    /// </summary>
    [Fact]
    public async Task Repository_release_package_applies_to_the_current_schema_and_reruns_cleanly()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: true);
        var root = RepoPath("backend", "db");

        MigratorOptions Options() => new(database.ConnectionString, root,
            new RunInfo("test", null, null, "Deploy", "integration-test", null, null),
            ViolationMode.Block, TimeSpan.FromSeconds(30), EfBundlePath: null, PlanOnly: false);

        var first = new MigrationRunner(Options(), new StringWriter());
        Assert.Equal(ExitCode.Succeeded, await first.RunAsync(CancellationToken.None));
        Assert.All(first.Outcomes, o => Assert.Contains(o.Status, new[] { "Succeeded", "Passed", "Skipped" }));

        var second = new MigrationRunner(Options(), new StringWriter());
        Assert.Equal(ExitCode.Succeeded, await second.RunAsync(CancellationToken.None));
        Assert.All(second.Outcomes.Where(o => o.Kind is ScriptKind.Data or ScriptKind.Repeatable), o => Assert.Equal("Skipped", o.Status));
    }

    private static string RepoPath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"{string.Join('/', parts)} not found above {AppContext.BaseDirectory}");
    }
}
