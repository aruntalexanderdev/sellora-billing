using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.Infrastructure;

/// <summary>
/// Deterministic lock races (the gate pattern of <c>StockLockConcurrencyTests</c>/<c>UnitLockConcurrencyTests</c>, but
/// per writer): a gate transaction takes a conflicting lock (or inserts a conflicting uncommitted row), writers are
/// started one at a time on their own context/connection, and the test waits — by the writer's backend pid in
/// <c>pg_stat_activity</c> — until each one is waiting on a heavyweight lock before starting the next or releasing
/// the gate. Writers started in order and queued on the same row acquire it in that order (PostgreSQL's tuple-lock
/// queue), so each ordering gives a single expected outcome.
/// </summary>
public sealed class LockRace : IAsyncDisposable
{
    public static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(30);

    private readonly TestDatabase _database;
    private readonly SelloraDbContext _gate;
    private readonly NpgsqlConnection _monitor;
    private readonly List<(SelloraDbContext Db, Writer Writer)> _writers = [];
    private bool _released;

    private LockRace(TestDatabase database, SelloraDbContext gate, NpgsqlConnection monitor)
    {
        _database = database;
        _gate = gate;
        _monitor = monitor;
    }

    /// <summary>A started writer: its backend pid and its outcome (null = success).</summary>
    public sealed record Writer(string Name, int Pid, Task<Exception?> Outcome);

    /// <summary>Opens the gate transaction and runs <paramref name="holdGate"/> inside it (lock rows, insert rows…).</summary>
    public static async Task<LockRace> BeginAsync(TestDatabase database, Func<SelloraDbContext, Task> holdGate)
    {
        var gate = database.CreateDbContext();
        var monitor = new NpgsqlConnection(database.ConnectionString);
        await monitor.OpenAsync();
        await gate.Database.OpenConnectionAsync();
        await gate.Database.BeginTransactionAsync();
        var race = new LockRace(database, gate, monitor);
        await holdGate(gate);
        return race;
    }

    /// <summary>Gate helper: <c>SELECT … FOR UPDATE</c> (or another lock clause) on one row; asserts the row exists.</summary>
    public static Func<SelloraDbContext, Task> LockRow(string table, string keyColumn, Guid key, string clause = "FOR UPDATE")
    {
        // Identifiers cannot be parameters: accept only plain names and the fixed lock clauses; the key is a parameter.
        if (!Identifier.IsMatch(table) || !Identifier.IsMatch(keyColumn) || !LockClauses.Contains(clause))
        {
            throw new ArgumentException($"Unsupported gate target {table}.{keyColumn} {clause}.");
        }
        var sql = string.Concat("SELECT \"", keyColumn, "\" AS \"Value\" FROM \"", table, "\" WHERE \"", keyColumn, "\" = {0} ", clause);
        return async gate =>
        {
            var found = await gate.Database.SqlQueryRaw<Guid>(sql, key).ToListAsync();
            Assert.True(found.Count == 1, $"Gate row {table}.{keyColumn} = {key} not found.");
        };
    }

    private static readonly System.Text.RegularExpressions.Regex Identifier = new("^[A-Za-z][A-Za-z0-9_]*$");
    private static readonly HashSet<string> LockClauses = ["FOR UPDATE", "FOR NO KEY UPDATE", "FOR SHARE", "FOR KEY SHARE"];

    /// <summary>Starts <paramref name="work"/> on a fresh context whose connection is already open (so its pid is known).</summary>
    public async Task<Writer> StartAsync(string name, Func<SelloraDbContext, Task> work)
    {
        var db = _database.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        var pid = await db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var outcome = Task.Run(async () =>
        {
            try
            {
                await work(db);
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });
        var writer = new Writer(name, pid, outcome);
        _writers.Add((db, writer));
        return writer;
    }

    /// <summary>Waits until <paramref name="writer"/> is waiting on a lock in a statement matching
    /// <paramref name="queryPattern"/> ('%' wildcards, case-insensitive; null = any statement). Fails if the writer
    /// finishes first: it did not block where required.</summary>
    public async Task WaitBlockedAsync(Writer writer, string? queryPattern = null, string? hint = null)
    {
        if (!await WaitBlockedOrFinishedAsync(writer, queryPattern))
        {
            var outcome = await writer.Outcome;
            Assert.Fail($"{(hint is null ? "" : hint + " ")}{writer.Name} finished while the gate held the conflicting lock, so it did not block " +
                        $"{(queryPattern is null ? "on a lock" : $"in '{queryPattern}'")}. Outcome: {outcome?.ToString() ?? "succeeded"}");
        }
    }

    /// <summary>True once the writer waits on a lock (in a matching statement); false if it finished instead.</summary>
    public async Task<bool> WaitBlockedOrFinishedAsync(Writer writer, string? queryPattern = null)
    {
        var deadline = DateTime.UtcNow + BlockTimeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            await using (var cmd = new NpgsqlCommand("SELECT wait_event_type, query FROM pg_stat_activity WHERE pid = @pid", _monitor))
            {
                cmd.Parameters.AddWithValue("pid", writer.Pid);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var waitType = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var query = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    last = $"{waitType ?? "-"}: {query.ReplaceLineEndings(" ")}";
                    if (waitType == "Lock" && (queryPattern is null || Like(query, queryPattern))) return true;
                }
            }
            if (writer.Outcome.IsCompleted) return false;
            await Task.Delay(25);
        }
        Assert.Fail($"{writer.Name} did not block {(queryPattern is null ? "on a lock" : $"in '{queryPattern}'")} within {BlockTimeout.TotalSeconds:0} s. Last state: {last}");
        return false;
    }

    /// <summary>Ends the gate transaction (rollback by default) and collects every writer's outcome.</summary>
    public async Task<Exception?[]> ReleaseAsync(bool commit = false)
    {
        _released = true;
        if (commit) await _gate.Database.CommitTransactionAsync();
        else await _gate.Database.RollbackTransactionAsync();
        return await Task.WhenAll(_writers.Select(w => w.Writer.Outcome)).WaitAsync(BlockTimeout);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_released && _gate.Database.CurrentTransaction is not null) await _gate.Database.RollbackTransactionAsync();
        await _gate.DisposeAsync();
        try { await Task.WhenAll(_writers.Select(w => w.Writer.Outcome)).WaitAsync(BlockTimeout); } catch { /* reported by the test */ }
        foreach (var (db, _) in _writers) await db.DisposeAsync();
        await _monitor.DisposeAsync();
    }

    private static bool Like(string query, string pattern)
    {
        var index = 0;
        foreach (var part in pattern.Split('%', StringSplitOptions.RemoveEmptyEntries))
        {
            var found = query.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            index = found + part.Length;
        }
        return true;
    }
}
