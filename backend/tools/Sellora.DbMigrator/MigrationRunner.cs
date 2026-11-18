using System.Diagnostics;
using System.Text;
using Npgsql;

namespace Sellora.DbMigrator;

public sealed record MigratorOptions(
    string ConnectionString,
    string ScriptsRoot,
    RunInfo Run,
    ViolationMode DefaultViolationMode,
    TimeSpan LockWait,
    string? EfBundlePath,
    bool PlanOnly);

public enum ExitCode { Succeeded = 0, ScriptFailed = 1, InvalidRelease = 2, LockTimeout = 3, Unexpected = 4 }

public sealed record StepOutcome(string Name, ScriptKind Kind, string Status, TimeSpan? Duration, int? Rows, string? Detail);

/// <summary>
/// One migrator run (spec rules R1–R12): lock → journal → EF bundle → plan → data → repeatable → validation.
/// Holds one connection for the whole run; the session-level advisory lock lives and dies with it.
/// </summary>
public sealed class MigrationRunner(MigratorOptions options, TextWriter log)
{
    // Arbitrary but fixed: every Sellora migrator, in every pipeline, competes for this key.
    internal const long AdvisoryLockKey = 0x53_45_4C_4C_44_42_01; // "SELLDB" v1

    public List<StepOutcome> Outcomes { get; } = [];

    public async Task<ExitCode> RunAsync(CancellationToken ct)
    {
        var (scripts, loadErrors) = ScriptCatalog.Load(options.ScriptsRoot);

        // No pooling: the advisory lock belongs to the session, and a pooled session would keep holding it (and let a second
        // runner in the same process re-enter it) after this run ends. Closing the physical connection always releases it.
        var unpooled = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false }.ConnectionString;
        await using var connection = new NpgsqlConnection(unpooled);
        await OpenWithRetryAsync(connection, ct);

        if (!await AcquireLockAsync(connection, ct))
        {
            log.WriteLine($"Another migrator holds the lock and it was not released within {options.LockWait.TotalSeconds:0} s.");
            return ExitCode.LockTimeout;
        }

        var journal = new Journal(connection);
        if (options.PlanOnly) // read-only: no journal schema, no run row, no closing of interrupted runs
        {
            var known = await journal.ExistsAsync(ct) ? await journal.ReadScriptsAsync(ct) : new Dictionary<string, JournalEntry>();
            var preview = ScriptPlanner.Plan(scripts, known);
            PrintPlan(preview, loadErrors);
            return loadErrors.Count + preview.Errors.Count > 0 ? ExitCode.InvalidRelease : ExitCode.Succeeded;
        }

        await journal.EnsureSchemaAsync(ct);
        if (await journal.CloseInterruptedRunsAsync(ct) > 0)
            log.WriteLine("Marked an earlier interrupted run as Failed.");

        var runId = await journal.StartRunAsync(options.Run, ct);
        log.WriteLine($"Run {runId} on '{options.Run.Environment}' ({options.Run.Trigger}).");

        if (loadErrors.Count > 0)
            return await FailReleaseAsync(journal, runId, loadErrors, ct);

        if (options.EfBundlePath is not null)
        {
            var ef = await RunEfBundleAsync(ct);
            await journal.RecordAttemptAsync(runId, "ef-migration-bundle", ScriptKind.EfMigration, "EF Core migration bundle", null, null, ef, CancellationToken.None);
            Outcomes.Add(new StepOutcome("ef-migration-bundle", ScriptKind.EfMigration, ef.Status, ef.Duration, null, ef.Error));
            if (ef.Status == JournalStatus.Failed)
                return await FinishAsync(journal, runId, ExitCode.ScriptFailed, "EF migration bundle failed; no SQL scripts were run.", CancellationToken.None);
        }
        var mirrored = await journal.MirrorEfMigrationsAsync(runId, ct);
        if (mirrored > 0) log.WriteLine($"Recorded {mirrored} newly applied EF migration(s).");

        var plan = ScriptPlanner.Plan(scripts, await journal.ReadScriptsAsync(ct));
        if (plan.Errors.Count > 0)
            return await FailReleaseAsync(journal, runId, plan.Errors, ct);

        var removed = await journal.MarkRemovedAsync(scripts.Select(s => s.Name), ct);
        if (removed > 0) log.WriteLine($"Marked {removed} script(s) no longer in the release as Removed.");

        var blockingViolation = false;
        foreach (var step in plan.Steps)
        {
            var script = step.Script;
            if (step.Action == StepAction.Skip)
            {
                Outcomes.Add(new StepOutcome(script.Name, script.Kind, "Skipped", null, null, step.Reason));
                continue;
            }

            if (ct.IsCancellationRequested)
                return await FinishAsync(journal, runId, ExitCode.ScriptFailed, "Cancelled before " + script.Name + ".", CancellationToken.None);

            log.WriteLine($"→ {script.Name} ({step.Reason})");
            var result = script.Kind == ScriptKind.Validation
                ? await RunValidationAsync(connection, script, ct)
                : await RunScriptAsync(connection, script, ct);

            await journal.RecordAttemptAsync(runId, script.Name, script.Kind, script.Description, script.Checksum, script.Sql, result, CancellationToken.None);
            // Violation rows stay in the journal (platform admins only); pipeline logs and the step summary get the count only.
            var shown = script.Kind == ScriptKind.Validation && result.Rows > 0
                ? $"{result.Rows} violation row(s); see the Database releases page or ops.\"DbScriptAttempts\""
                : result.Error;
            Outcomes.Add(new StepOutcome(script.Name, script.Kind, result.Status, result.Duration, result.Rows, shown));
            log.WriteLine($"  {result.Status} in {result.Duration.TotalMilliseconds:0} ms{(shown is null ? "" : $": {shown}")}");

            if (result.Status == JournalStatus.Failed && script.Kind != ScriptKind.Validation)
                return await FinishAsync(journal, runId, ExitCode.ScriptFailed, $"{script.Name} failed; later scripts were not run.", CancellationToken.None);
            if (result.Status == JournalStatus.Failed)
                blockingViolation = true; // keep running the other validations so the report is complete
        }

        return blockingViolation
            ? await FinishAsync(journal, runId, ExitCode.ScriptFailed, "A blocking validation found violations.", CancellationToken.None)
            : await FinishAsync(journal, runId, ExitCode.Succeeded, Summarise(), CancellationToken.None);
    }

    /// <summary>A just-created Azure firewall rule can take a few seconds to apply; retry the first connection briefly.</summary>
    private async Task OpenWithRetryAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(ct);
                return;
            }
            catch (NpgsqlException ex) when (attempt < 6 && ex is not PostgresException { SqlState: "28P01" }) // not: wrong password
            {
                log.WriteLine($"Cannot connect yet ({ex.Message}); retrying in 10 s ({attempt}/5).");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    private async Task<bool> AcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + options.LockWait;
        var announced = false;
        while (true)
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            cmd.Parameters.AddWithValue("key", AdvisoryLockKey);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return true;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            if (!announced) { log.WriteLine("Waiting for another migrator to finish…"); announced = true; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task<AttemptResult> RunScriptAsync(NpgsqlConnection connection, SqlScript script, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        NpgsqlTransaction? tx = null;
        try
        {
            if (script.UseTransaction) tx = await connection.BeginTransactionAsync(ct);
            await SetTimeoutsAsync(connection, tx, script, ct);

            await using var cmd = new NpgsqlCommand(script.Sql, connection, tx)
            {
                CommandTimeout = (int)script.Timeout.TotalSeconds + 30, // server-side statement_timeout fires first
            };
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return new AttemptResult(JournalStatus.Succeeded, started, clock.Elapsed, rows < 0 ? null : rows, null, null);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            // Best effort: on a broken connection rollback/reset throw too, and must not hide the original failure.
            if (tx is not null) await QuietlyAsync(() => tx.RollbackAsync(CancellationToken.None));
            return Failure(started, clock.Elapsed, ex);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
            else await QuietlyAsync(() => ResetTimeoutsAsync(connection));
        }
    }

    private async Task<AttemptResult> RunValidationAsync(NpgsqlConnection connection, SqlScript script, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
                await readOnly.ExecuteNonQueryAsync(ct);
            await SetTimeoutsAsync(connection, tx, script, ct);

            var violations = 0;
            var samples = new List<string>();
            await using (var cmd = new NpgsqlCommand(script.Sql, connection, tx) { CommandTimeout = (int)script.Timeout.TotalSeconds + 30 })
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                do
                {
                    while (await reader.ReadAsync(ct))
                    {
                        violations++;
                        if (samples.Count < 5) samples.Add(DescribeRow(reader));
                    }
                } while (await reader.NextResultAsync(ct));
            }
            await tx.RollbackAsync(ct); // read-only; nothing to keep

            if (violations == 0)
                return new AttemptResult(JournalStatus.Passed, started, clock.Elapsed, 0, null, null);

            var mode = script.OnViolation ?? options.DefaultViolationMode;
            var message = $"{violations} violation row(s). First: {string.Join(" | ", samples)}";
            return new AttemptResult(mode == ViolationMode.Block ? JournalStatus.Failed : JournalStatus.Warning,
                started, clock.Elapsed, violations, null, Truncate(message));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            await QuietlyAsync(() => tx.RollbackAsync(CancellationToken.None));
            return Failure(started, clock.Elapsed, ex);
        }
    }

    private static async Task QuietlyAsync(Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException) { /* connection already broken */ }
    }

    private static async Task SetTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, SqlScript script, CancellationToken ct)
    {
        // SET LOCAL ends with the transaction; without one we SET and reset in RunScriptAsync's finally.
        var scope = tx is null ? "SET" : "SET LOCAL";
        await using var cmd = new NpgsqlCommand(
            $"{scope} statement_timeout = {(int)script.Timeout.TotalMilliseconds}; {scope} lock_timeout = {(int)script.LockTimeout.TotalMilliseconds};",
            connection, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ResetTimeoutsAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open) return;
        await using var cmd = new NpgsqlCommand("RESET statement_timeout; RESET lock_timeout;", connection);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<AttemptResult> RunEfBundleAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        log.WriteLine($"→ EF migration bundle {options.EfBundlePath}");
        var info = new ProcessStartInfo(options.EfBundlePath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Environment, not argv: argv is visible in the process list. SelloraDbContextFactory reads this variable.
        info.Environment["SELLORA_DESIGN_CONNECTION"] = options.ConnectionString;

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = (await stdout) + (await stderr);
        log.Write(Indent(WithoutEfNoise(output)));

        return process.ExitCode == 0
            ? new AttemptResult(JournalStatus.Succeeded, started, clock.Elapsed, null, null, null)
            : new AttemptResult(JournalStatus.Failed, started, clock.Elapsed, null, null,
                Truncate($"exit code {process.ExitCode}: {Tail(output, 1500)}"));
    }

    private async Task<ExitCode> FailReleaseAsync(Journal journal, Guid runId, IReadOnlyList<string> errors, CancellationToken ct)
    {
        log.WriteLine("The release package is invalid; nothing was executed:");
        foreach (var error in errors) log.WriteLine($"  ✘ {error}");
        return await FinishAsync(journal, runId, ExitCode.InvalidRelease, Truncate("Invalid release, nothing executed: " + string.Join("; ", errors)), ct);
    }

    private async Task<ExitCode> FinishAsync(Journal journal, Guid runId, ExitCode code, string summary, CancellationToken ct)
    {
        await journal.CompleteRunAsync(runId, code == ExitCode.Succeeded ? JournalStatus.Succeeded : JournalStatus.Failed, summary, ct);
        log.WriteLine(code == ExitCode.Succeeded ? $"✔ {summary}" : $"✘ {summary}");
        return code;
    }

    private string Summarise()
    {
        int Count(string status) => Outcomes.Count(o => o.Status == status);
        var ran = Outcomes.Count(o => o.Status != "Skipped");
        return $"{ran} executed, {Count("Skipped")} skipped, {Count(JournalStatus.Warning)} validation warning(s).";
    }

    private void PrintPlan(MigrationPlan plan, IReadOnlyList<string> loadErrors)
    {
        log.WriteLine("Plan (nothing executed):");
        foreach (var step in plan.Steps)
            log.WriteLine($"  {(step.Action == StepAction.Run ? "RUN " : "skip")}  {step.Script.Name}  ({step.Reason})");
        foreach (var error in loadErrors.Concat(plan.Errors)) log.WriteLine($"  ✘ {error}");
    }

    /// <summary>
    /// Error text kept in the journal. PostgreSQL's DETAIL can quote row values (customer data), so it is not stored (spec R11).
    /// </summary>
    private static AttemptResult Failure(DateTimeOffset started, TimeSpan elapsed, Exception ex)
    {
        if (ex is PostgresException pg)
        {
            var text = new StringBuilder(pg.MessageText);
            if (pg.TableName is not null) text.Append($" [table {pg.SchemaName}.{pg.TableName}]");
            if (pg.ColumnName is not null) text.Append($" [column {pg.ColumnName}]");
            if (pg.ConstraintName is not null) text.Append($" [constraint {pg.ConstraintName}]");
            if (pg.Hint is not null) text.Append($" Hint: {pg.Hint}");
            if (pg.Position > 0) text.Append($" (at character {pg.Position})");
            if (pg.SqlState == "25001") text.Append(" A 'transaction: none' script must contain exactly one statement (several are sent as one implicit transaction).");
            return new AttemptResult(JournalStatus.Failed, started, elapsed, null, pg.SqlState, Truncate(text.ToString()));
        }
        var message = ex switch
        {
            OperationCanceledException => "Cancelled: the migrator was stopped while this script ran.",
            NpgsqlException { InnerException: TimeoutException } => "Client timeout: the script ran longer than its timeout.",
            _ => ex.Message,
        };
        return new AttemptResult(JournalStatus.Failed, started, elapsed, null, null, Truncate(message));
    }

    private static string DescribeRow(NpgsqlDataReader reader) =>
        string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "null" : reader.GetValue(i))}"));

    /// <summary>
    /// EF logs its migration-lock notice once per migration, and on a new database a failed SELECT on the not-yet-created
    /// history table. Both are normal; hiding them keeps real errors visible in the pipeline log.
    /// </summary>
    private static string WithoutEfNoise(string output)
    {
        var lines = output.Split('\n');
        var kept = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Acquiring an exclusive lock for migration application")) continue;
            if (lines[i].StartsWith("Failed executing DbCommand") && i + 1 < lines.Length && lines[i + 1].Contains("\"MigrationId\", \"ProductVersion\""))
            {
                i += 3; // the SELECT on __EFMigrationsHistory before it exists
                continue;
            }
            kept.Add(lines[i]);
        }
        return string.Join('\n', kept);
    }

    private static string Truncate(string text) => text.Length <= 4000 ? text : text[..4000] + "…";
    private static string Tail(string text, int length) => text.Length <= length ? text.Trim() : "…" + text[^length..].Trim();
    private static string Indent(string text) => string.Concat(text.Split('\n').Where(l => l.Length > 0).Select(l => $"    {l}\n"));
}
