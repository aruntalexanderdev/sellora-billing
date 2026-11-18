using Npgsql;

namespace Sellora.DbMigrator;

/// <summary>
/// The <c>ops</c> journal: one row per run, current state per script, and every attempt. Owned by the migrator (not EF) so it
/// exists before EF migrations run and survives a failed EF step. All writes happen outside the script's own transaction.
/// </summary>
public sealed class Journal(NpgsqlConnection connection)
{
    private const string Schema = """
        CREATE SCHEMA IF NOT EXISTS ops;

        CREATE TABLE IF NOT EXISTS ops."DbReleaseRuns" (
            "Id"          uuid PRIMARY KEY,
            "Environment" text NOT NULL,
            "CommitSha"   text NULL,
            "AppVersion"  text NULL,
            "Trigger"     text NOT NULL,
            "TriggeredBy" text NULL,
            "RetryReason" text NULL,
            "Status"      text NOT NULL,
            "StartedAt"   timestamptz NOT NULL,
            "CompletedAt" timestamptz NULL,
            "PipelineUrl" text NULL,
            "Summary"     text NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_DbReleaseRuns_Environment_StartedAt" ON ops."DbReleaseRuns" ("Environment", "StartedAt" DESC);

        CREATE TABLE IF NOT EXISTS ops."DbScripts" (
            "Id"            uuid PRIMARY KEY,
            "Name"          text NOT NULL,
            "Kind"          text NOT NULL,
            "Description"   text NULL,
            "Checksum"      text NULL,
            "Status"        text NOT NULL,
            "AttemptCount"  int NOT NULL DEFAULT 0,
            "LastAttemptAt" timestamptz NULL,
            "AppliedAt"     timestamptz NULL,
            CONSTRAINT "UX_DbScripts_Name" UNIQUE ("Name")
        );

        CREATE TABLE IF NOT EXISTS ops."DbScriptAttempts" (
            "Id"           uuid PRIMARY KEY,
            "ScriptId"     uuid NOT NULL REFERENCES ops."DbScripts" ("Id"),
            "ReleaseRunId" uuid NOT NULL REFERENCES ops."DbReleaseRuns" ("Id"),
            "Checksum"     text NULL,
            "Status"       text NOT NULL,
            "StartedAt"    timestamptz NOT NULL,
            "DurationMs"   int NULL,
            "RowsAffected" int NULL,
            "SqlState"     text NULL,
            "ErrorMessage" text NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_DbScriptAttempts_ScriptId" ON ops."DbScriptAttempts" ("ScriptId");
        CREATE INDEX IF NOT EXISTS "IX_DbScriptAttempts_ReleaseRunId" ON ops."DbScriptAttempts" ("ReleaseRunId");
        """;

    public Task EnsureSchemaAsync(CancellationToken ct) => ExecAsync(Schema, ct);

    /// <summary>A run left 'Running' means its process died; we hold the lock, so no other runner owns it.</summary>
    public Task<int> CloseInterruptedRunsAsync(CancellationToken ct) => ExecAsync("""
        UPDATE ops."DbReleaseRuns" SET "Status" = 'Failed', "CompletedAt" = now(),
               "Summary" = coalesce("Summary" || ' ', '') || 'Interrupted: the migrator stopped before finishing.'
        WHERE "Status" = 'Running'
        """, ct);

    public async Task<Guid> StartRunAsync(RunInfo run, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await ExecAsync("""
            INSERT INTO ops."DbReleaseRuns" ("Id","Environment","CommitSha","AppVersion","Trigger","TriggeredBy","RetryReason","Status","StartedAt","PipelineUrl")
            VALUES (@id,@env,@sha,@ver,@trigger,@by,@reason,'Running',now(),@url)
            """, ct, ("id", id), ("env", run.Environment), ("sha", run.CommitSha), ("ver", run.AppVersion),
            ("trigger", run.Trigger), ("by", run.TriggeredBy), ("reason", run.RetryReason), ("url", run.PipelineUrl));
        return id;
    }

    public Task CompleteRunAsync(Guid runId, string status, string summary, CancellationToken ct) => ExecAsync("""
        UPDATE ops."DbReleaseRuns" SET "Status" = @status, "CompletedAt" = now(), "Summary" = @summary WHERE "Id" = @id
        """, ct, ("id", runId), ("status", status), ("summary", summary));

    public async Task<Dictionary<string, JournalEntry>> ReadScriptsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("""SELECT "Name","Status","Checksum" FROM ops."DbScripts" """, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = new JournalEntry(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        return result;
    }

    /// <summary>Copies EF migrations applied (by the bundle) into the journal and links the new ones to this run.</summary>
    public async Task<int> MirrorEfMigrationsAsync(Guid runId, CancellationToken ct)
    {
        await using var exists = new NpgsqlCommand("""SELECT to_regclass('public."__EFMigrationsHistory"') IS NOT NULL""", connection);
        if (!(bool)(await exists.ExecuteScalarAsync(ct))!) return 0;

        return await ExecAsync("""
            WITH added AS (
                INSERT INTO ops."DbScripts" ("Id","Name","Kind","Description","Status","AttemptCount","LastAttemptAt","AppliedAt")
                SELECT gen_random_uuid(), h."MigrationId", 'EfMigration', 'EF Core migration', 'Succeeded', 1, now(), now()
                FROM public."__EFMigrationsHistory" h
                ON CONFLICT ("Name") DO NOTHING
                RETURNING "Id")
            INSERT INTO ops."DbScriptAttempts" ("Id","ScriptId","ReleaseRunId","Status","StartedAt")
            SELECT gen_random_uuid(), a."Id", @run, 'Succeeded', now() FROM added a
            """, ct, ("run", runId));
    }

    /// <summary>
    /// Scripts no longer in the release package stop showing as failing: a failed/pending data or repeatable script that was
    /// replaced, or any validation script that was deleted. Succeeded data scripts keep their status (they are history).
    /// </summary>
    public Task<int> MarkRemovedAsync(IEnumerable<string> namesInPackage, CancellationToken ct) => ExecAsync("""
        UPDATE ops."DbScripts" SET "Status" = 'Removed'
        WHERE "Kind" <> 'EfMigration' AND "Name" <> ALL(@names) AND "Status" <> 'Removed'
          AND ("Kind" = 'Validation' OR "Status" IN ('Failed', 'Pending'))
        """, ct, ("names", namesInPackage.ToArray()));

    public async Task RecordAttemptAsync(Guid runId, string name, ScriptKind kind, string? description, string? checksum,
        AttemptResult result, CancellationToken ct)
    {
        var finalState = result.Status is JournalStatus.Succeeded or JournalStatus.Passed or JournalStatus.Warning;
        await ExecAsync("""
            WITH script AS (
                INSERT INTO ops."DbScripts" ("Id","Name","Kind","Description","Checksum","Status","AttemptCount","LastAttemptAt","AppliedAt")
                VALUES (gen_random_uuid(), @name, @kind, @desc, CASE WHEN @ok THEN @checksum END, @status, 1, @started,
                        CASE WHEN @ok THEN now() END)
                ON CONFLICT ("Name") DO UPDATE SET
                    "Kind" = EXCLUDED."Kind",
                    "Description" = EXCLUDED."Description",
                    "Checksum" = CASE WHEN @ok THEN @checksum ELSE ops."DbScripts"."Checksum" END,
                    "Status" = EXCLUDED."Status",
                    "AttemptCount" = ops."DbScripts"."AttemptCount" + 1,
                    "LastAttemptAt" = EXCLUDED."LastAttemptAt",
                    "AppliedAt" = CASE WHEN @ok THEN now() ELSE ops."DbScripts"."AppliedAt" END
                RETURNING "Id")
            INSERT INTO ops."DbScriptAttempts" ("Id","ScriptId","ReleaseRunId","Checksum","Status","StartedAt","DurationMs","RowsAffected","SqlState","ErrorMessage")
            SELECT gen_random_uuid(), s."Id", @run, @checksum, @status, @started, @ms, @rows::int, @sqlstate, @error FROM script s
            """, ct,
            ("name", name), ("kind", kind.ToString()), ("desc", description), ("checksum", checksum), ("status", result.Status),
            ("ok", finalState), ("started", result.StartedAt), ("run", runId), ("ms", (int)result.Duration.TotalMilliseconds),
            ("rows", result.Rows), ("sqlstate", result.SqlState), ("error", result.Error));
    }

    private async Task<int> ExecAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            cmd.Parameters.Add(value is null
                ? new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value }
                : new NpgsqlParameter(name, value));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed record RunInfo(string Environment, string? CommitSha, string? AppVersion, string Trigger, string? TriggeredBy,
    string? RetryReason, string? PipelineUrl);

public sealed record AttemptResult(string Status, DateTimeOffset StartedAt, TimeSpan Duration, int? Rows, string? SqlState, string? Error);
