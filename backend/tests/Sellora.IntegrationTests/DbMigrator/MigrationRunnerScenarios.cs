using Npgsql;
using Sellora.DbMigrator;

namespace Sellora.IntegrationTests.DbMigrator;

/// <summary>
/// Sellora.DbMigrator on real PostgreSQL (docs/features/db-release-automation.md, AC2–AC7 and edge cases).
/// The scenarios only need "give me an empty database", so they are written once here and bound to the
/// Testcontainers fixture in <see cref="MigrationRunnerTests"/>.
/// </summary>
public abstract class MigrationRunnerScenarios
{
    /// <summary>Creates an empty database and returns its connection string plus a handle that drops it.</summary>
    protected abstract Task<(string ConnectionString, IAsyncDisposable Drop)> CreateEmptyDatabaseAsync();

    private sealed class Package : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("sellora-migrator-it-").FullName;

        public Package Write(string folder, string name, string sql)
        {
            Directory.CreateDirectory(Path.Combine(Root, folder));
            File.WriteAllText(Path.Combine(Root, folder, name), sql);
            return this;
        }

        public void Delete(string folder, string name) => File.Delete(Path.Combine(Root, folder, name));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static async Task<(ExitCode Code, string Log)> RunAsync(string connectionString, Package package,
        ViolationMode validationDefault = ViolationMode.Warn, int lockWaitSeconds = 30, string trigger = "Deploy")
    {
        var log = new StringWriter();
        var options = new MigratorOptions(connectionString, package.Root,
            new RunInfo("test", "abc1234", "1.0.0", trigger, "integration-test", trigger == "Retry" ? "test retry" : null, null),
            validationDefault, TimeSpan.FromSeconds(lockWaitSeconds), EfBundlePath: null, PlanOnly: false);
        var code = await new MigrationRunner(options, log).RunAsync(CancellationToken.None);
        return (code, log.ToString());
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static Task<string?> ScriptStatusAsync(string cs, string name) =>
        ScalarAsync<string>(cs, $"""SELECT "Status" FROM ops."DbScripts" WHERE "Name" = '{name}'""");

    private const string Create = "2026.11.18.001__create-stock.sql";
    private const string Backfill = "2026.11.18.002__backfill-branch.sql";
    private const string Later = "2026.11.18.003__later.sql";

    private static Package StockPackage() => new Package()
        .Write("scripts", Create, """
            CREATE TABLE it_stock (id int PRIMARY KEY, location_id int NULL, branch_id int NULL);
            INSERT INTO it_stock VALUES (1, 10, NULL), (2, NULL, NULL);
            CREATE TABLE it_location (id int PRIMARY KEY, branch_id int NOT NULL);
            INSERT INTO it_location VALUES (10, 100);
            """)
        .Write("scripts", Backfill, """
            UPDATE it_stock s SET branch_id = l.branch_id FROM it_location l WHERE l.id = s.location_id;
            ALTER TABLE it_stock ALTER COLUMN branch_id SET NOT NULL;
            """)
        .Write("scripts", Later, "INSERT INTO it_location VALUES (99, 100);");

    [Fact]
    public async Task Failed_script_is_rolled_back_recorded_and_stops_the_run_then_a_fixed_retry_resumes()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = StockPackage();

        var (code, log) = await RunAsync(cs, package);

        Assert.Equal(ExitCode.ScriptFailed, code);
        Assert.Equal(JournalStatus.Failed, await ScriptStatusAsync(cs, Backfill));
        Assert.Null(await ScriptStatusAsync(cs, Later)); // never attempted
        Assert.Equal("23502", await ScalarAsync<string>(cs, """SELECT "SqlState" FROM ops."DbScriptAttempts" WHERE "Status" = 'Failed'"""));
        Assert.Equal(2L, await ScalarAsync<long>(cs, "SELECT count(*) FROM it_stock WHERE branch_id IS NULL")); // UPDATE rolled back
        Assert.Contains("later scripts were not run", log);

        package.Write("scripts", Backfill, """
            UPDATE it_stock s SET branch_id = l.branch_id FROM it_location l WHERE l.id = s.location_id;
            UPDATE it_stock SET branch_id = 100 WHERE branch_id IS NULL;
            ALTER TABLE it_stock ALTER COLUMN branch_id SET NOT NULL;
            """);
        (code, _) = await RunAsync(cs, package, trigger: "Retry");

        Assert.Equal(ExitCode.Succeeded, code);
        Assert.Equal(JournalStatus.Succeeded, await ScriptStatusAsync(cs, Backfill));
        Assert.Equal(JournalStatus.Succeeded, await ScriptStatusAsync(cs, Later));
        Assert.Equal(2, await ScalarAsync<int>(cs, $"""SELECT "AttemptCount" FROM ops."DbScripts" WHERE "Name" = '{Backfill}'"""));
        Assert.Equal("test retry", await ScalarAsync<string>(cs, """SELECT "RetryReason" FROM ops."DbReleaseRuns" WHERE "Trigger" = 'Retry'"""));
    }

    [Fact]
    public async Task Modified_applied_script_fails_the_release_before_anything_runs()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package().Write("scripts", Create, "CREATE TABLE it_a (id int);");
        Assert.Equal(ExitCode.Succeeded, (await RunAsync(cs, package)).Code);

        package.Write("scripts", Create, "CREATE TABLE it_a (id int); -- edited")
            .Write("scripts", Backfill, "CREATE TABLE it_b (id int);");
        var (code, log) = await RunAsync(cs, package);

        Assert.Equal(ExitCode.InvalidRelease, code);
        Assert.Contains($"applied script modified: {Create}", log);
        Assert.Null(await ScalarAsync<string>(cs, "SELECT to_regclass('it_b')::text")); // the new script did not run
        Assert.Equal(JournalStatus.Failed, await ScalarAsync<string>(cs, """SELECT "Status" FROM ops."DbReleaseRuns" ORDER BY "StartedAt" DESC LIMIT 1"""));
    }

    [Fact]
    public async Task Repeatable_runs_again_only_when_changed()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package().Write("repeatable", "R__settings.sql", """
            CREATE TABLE IF NOT EXISTS it_settings (key text PRIMARY KEY);
            INSERT INTO it_settings VALUES ('currency') ON CONFLICT DO NOTHING;
            """);

        await RunAsync(cs, package);
        await RunAsync(cs, package);
        Assert.Equal(1, await ScalarAsync<int>(cs, """SELECT "AttemptCount" FROM ops."DbScripts" WHERE "Name" = 'R__settings.sql'"""));

        package.Write("repeatable", "R__settings.sql", """
            CREATE TABLE IF NOT EXISTS it_settings (key text PRIMARY KEY);
            INSERT INTO it_settings VALUES ('currency'), ('timezone') ON CONFLICT DO NOTHING;
            """);
        await RunAsync(cs, package);

        Assert.Equal(2, await ScalarAsync<int>(cs, """SELECT "AttemptCount" FROM ops."DbScripts" WHERE "Name" = 'R__settings.sql'"""));
        Assert.Equal(2L, await ScalarAsync<long>(cs, "SELECT count(*) FROM it_settings"));
    }

    [Theory]
    [InlineData(ViolationMode.Warn, ExitCode.Succeeded, "Warning")]
    [InlineData(ViolationMode.Block, ExitCode.ScriptFailed, "Failed")]
    public async Task Validation_rows_warn_or_block(ViolationMode mode, ExitCode expectedCode, string expectedStatus)
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package()
            .Write("scripts", Create, "CREATE TABLE it_v (id int); INSERT INTO it_v VALUES (7), (8);")
            .Write("validation", "V__no-rows.sql", "SELECT id AS \"Id\" FROM it_v WHERE id > 7;");

        var (code, _) = await RunAsync(cs, package, mode);

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedStatus, await ScriptStatusAsync(cs, "V__no-rows.sql"));
        Assert.Contains("Id=8", await ScalarAsync<string>(cs, """SELECT "ErrorMessage" FROM ops."DbScriptAttempts" ORDER BY "StartedAt" DESC LIMIT 1"""));
    }

    [Fact]
    public async Task Validation_scripts_cannot_write()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package()
            .Write("scripts", Create, "CREATE TABLE it_w (id int);")
            .Write("validation", "V__sneaky-write.sql", "INSERT INTO it_w VALUES (1) RETURNING id;");

        var (code, _) = await RunAsync(cs, package);

        Assert.Equal(ExitCode.ScriptFailed, code);
        Assert.Equal("25006", await ScalarAsync<string>(cs, """SELECT "SqlState" FROM ops."DbScriptAttempts" WHERE "Status" = 'Failed'""")); // read_only_sql_transaction
        Assert.Equal(0L, await ScalarAsync<long>(cs, "SELECT count(*) FROM it_w"));
    }

    [Fact]
    public async Task Script_timeout_is_enforced_by_the_server()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package().Write("scripts", Create, "-- sellora:timeout 1\nSELECT pg_sleep(5);");

        var (code, _) = await RunAsync(cs, package);

        Assert.Equal(ExitCode.ScriptFailed, code);
        Assert.Equal("57014", await ScalarAsync<string>(cs, """SELECT "SqlState" FROM ops."DbScriptAttempts" WHERE "Status" = 'Failed'""")); // query_canceled
    }

    [Fact]
    public async Task Second_runner_gives_up_while_the_lock_is_held()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package().Write("scripts", Create, "CREATE TABLE it_lock (id int);");

        // Unpooled, like the runner: disposing it must really end the session that holds the lock.
        await using (var holder = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString))
        {
            await holder.OpenAsync();
            await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({MigrationRunner.AdvisoryLockKey})", holder))
                await take.ExecuteNonQueryAsync();

            var (code, log) = await RunAsync(cs, package, lockWaitSeconds: 1);

            Assert.Equal(ExitCode.LockTimeout, code);
            Assert.Contains("Another migrator holds the lock", log);
            Assert.Null(await ScalarAsync<string>(cs, "SELECT to_regclass('it_lock')::text"));
        }

        Assert.Equal(ExitCode.Succeeded, (await RunAsync(cs, package)).Code); // lock released with the holder's session
    }

    [Fact]
    public async Task Interrupted_run_is_closed_and_removed_scripts_stop_showing_as_failed()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package().Write("scripts", Create, "SELECT 1/0;");
        await RunAsync(cs, package);

        await using (var connection = new NpgsqlConnection(cs))
        {
            await connection.OpenAsync();
            await using var fake = new NpgsqlCommand("""UPDATE ops."DbReleaseRuns" SET "Status" = 'Running', "CompletedAt" = NULL""", connection);
            await fake.ExecuteNonQueryAsync();
        }
        package.Delete("scripts", Create);
        package.Write("scripts", Backfill, "SELECT 1;");

        var (code, log) = await RunAsync(cs, package);

        Assert.Equal(ExitCode.Succeeded, code);
        Assert.Contains("interrupted run", log);
        Assert.Equal(JournalStatus.Removed, await ScriptStatusAsync(cs, Create));
        Assert.Equal(0L, await ScalarAsync<long>(cs, """SELECT count(*) FROM ops."DbReleaseRuns" WHERE "Status" = 'Running'"""));
    }

    [Fact]
    public async Task Transaction_none_runs_a_single_concurrent_index_build_and_explains_multi_statement_failures()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package()
            .Write("scripts", Create, "CREATE TABLE it_idx (a int, b int);")
            .Write("scripts", Backfill, "-- sellora:transaction none\nCREATE INDEX CONCURRENTLY ix_it_idx_a ON it_idx (a);");

        Assert.Equal(ExitCode.Succeeded, (await RunAsync(cs, package)).Code);
        Assert.Equal("ix_it_idx_a", await ScalarAsync<string>(cs, "SELECT to_regclass('ix_it_idx_a')::text"));
        Assert.Equal("0", await ScalarAsync<string>(cs, "SHOW statement_timeout")); // a fresh session: nothing leaked server-side

        package.Write("scripts", Later, """
            -- sellora:transaction none
            CREATE INDEX CONCURRENTLY ix_it_idx_b ON it_idx (b);
            CREATE INDEX CONCURRENTLY ix_it_idx_ab ON it_idx (a, b);
            """);
        var (code, _) = await RunAsync(cs, package);

        Assert.Equal(ExitCode.ScriptFailed, code);
        Assert.Contains("exactly one statement",
            await ScalarAsync<string>(cs, $"""SELECT a."ErrorMessage" FROM ops."DbScriptAttempts" a JOIN ops."DbScripts" s ON s."Id" = a."ScriptId" WHERE s."Name" = '{Later}'"""));
    }

    [Fact]
    public async Task Plan_only_reports_without_writing_anything()
    {
        var (cs, drop) = await CreateEmptyDatabaseAsync();
        await using var dropOnExit = drop;
        using var package = new Package().Write("scripts", Create, "CREATE TABLE it_plan (id int);");

        var efList = Path.Combine(package.Root, "ef-migrations.txt");
        File.WriteAllLines(efList, ["Build started...", "20990101000000_FutureSchema", "Build succeeded."]); // build noise is ignored

        var log = new StringWriter();
        var options = new MigratorOptions(cs, package.Root, new RunInfo("test", null, null, "Deploy", "it", null, null),
            ViolationMode.Warn, TimeSpan.FromSeconds(5), EfBundlePath: null, PlanOnly: true, EfMigrationsListPath: efList);
        var runner = new MigrationRunner(options, log);
        var code = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(ExitCode.Succeeded, code);
        Assert.Contains($"RUN   {Create}", log.ToString());
        Assert.Equal(
            [("20990101000000_FutureSchema", "Planned"), (Create, "Planned")],
            runner.Outcomes.Select(o => (o.Name, o.Status)));
        Assert.Null(await ScalarAsync<string>(cs, "SELECT to_regnamespace('ops')::text"));
        Assert.Null(await ScalarAsync<string>(cs, "SELECT to_regclass('it_plan')::text"));
    }
}
