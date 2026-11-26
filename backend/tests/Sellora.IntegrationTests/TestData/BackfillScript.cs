using Npgsql;
using Sellora.DbMigrator;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// The TP-5a data script <c>backend/db/scripts/2026.11.25.001__tax-history-profile-backfill.sql</c> as the two operators run it: through the house migrator (Npgsql, <c>transaction: none</c>) and directly. psql is never
/// needed: the script is ONE DO block whose COMMITs are legal because it is the top-level statement outside a transaction block, and the batch size is the custom setting
/// <c>sellora.backfill_batch_size</c>, which psql users pass as <c>PGOPTIONS="-c sellora.backfill_batch_size=N"</c> and Npgsql as the <c>Options</c> connection string keyword (the same start-up option).
/// </summary>
public static class BackfillScript
{
    public const string FileName = "2026.11.25.001__tax-history-profile-backfill.sql";

    public static string RepoPath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{string.Join('/', parts)} not found above {AppContext.BaseDirectory}");
    }

    public static string Text => File.ReadAllText(RepoPath("backend", "db", "scripts", FileName));

    /// <summary>The connection string with the batch size start-up option (the equivalent of PGOPTIONS).</summary>
    public static string WithBatch(string connectionString, int batchSize) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Options = $"-c sellora.backfill_batch_size={batchSize}", Pooling = false }.ConnectionString;

    /// <summary>The result of one direct run: the NOTICE lines of the script and how long it took.</summary>
    public sealed record Run(List<string> Notices, TimeSpan Elapsed);

    /// <summary>Runs the script text on its own connection, outside a transaction (a session <c>statement_timeout</c> in milliseconds is applied first when given). Throws what the server raises.</summary>
    public static async Task<Run> RunAsync(TestDatabase d, int? batchSize = null, int? statementTimeoutMs = null, string? text = null)
    {
        var connectionString = batchSize is null ? new NpgsqlConnectionStringBuilder(d.ConnectionString) { Pooling = false }.ConnectionString : WithBatch(d.ConnectionString, batchSize.Value);
        await using var connection = new NpgsqlConnection(connectionString);
        var notices = new List<string>();
        connection.Notice += (_, e) => { lock (notices) notices.Add(e.Notice.MessageText); };
        await connection.OpenAsync();
        if (statementTimeoutMs is not null)
        {
            await using var set = new NpgsqlCommand($"SET statement_timeout = {statementTimeoutMs}", connection);
            await set.ExecuteNonQueryAsync();
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var command = new NpgsqlCommand(text ?? Text, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync();
        return new Run(notices, clock.Elapsed);
    }

    /// <summary>Runs ONLY this script (a copy of the real file in a private package folder) through <see cref="MigrationRunner"/>, as the Database releases page does. Returns the step outcome of the script.</summary>
    public static async Task<(StepOutcome Step, ExitCode Exit)> RunViaMigratorAsync(TestDatabase d, int? batchSize = null)
    {
        var root = Directory.CreateTempSubdirectory("sellora-bf-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "validation"));
            File.WriteAllText(Path.Combine(root, "scripts", FileName), Text);
            var connectionString = batchSize is null ? d.ConnectionString : WithBatch(d.ConnectionString, batchSize.Value);
            var runner = new MigrationRunner(new MigratorOptions(connectionString, root, new RunInfo("test", null, null, "Deploy", "integration-test", null, null),
                ViolationMode.Warn, TimeSpan.FromSeconds(120), EfBundlePath: null, PlanOnly: false), new StringWriter());
            var exit = await runner.RunAsync(CancellationToken.None);
            return (runner.Outcomes.Single(o => o.Name.EndsWith(FileName, StringComparison.Ordinal)), exit);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
