using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Database releases page (docs/features/db-release-automation.md, delivery step 3): read-only views of the <c>ops</c> journal
/// written by Sellora.DbMigrator. The journal is not part of the EF model (the migrator owns it, D-design), so it is read with
/// parameterised raw SQL. Each API reads its own database, i.e. one environment. Platform admins only (Q15).
/// </summary>
[ApiController]
[Route("api/v1/platform/db-releases")]
[PlatformEndpoint]
[Authorize(Policy = PlatformPolicies.Admin)]
public class PlatformDbReleasesController(SelloraDbContext db) : ControllerBase
{
    private const string RunColumns = """
        r."Id", r."Environment", r."CommitSha", r."AppVersion", r."Trigger", r."TriggeredBy", r."RetryReason", r."Status",
        r."StartedAt", r."CompletedAt", r."PipelineUrl", r."Summary"
        """;

    // Latest attempt per script via LATERAL; EF migrations first, then data → repeatable → validation (spec R1).
    private const string ScriptSelect = """
        SELECT s."Id", s."Name", s."Kind", s."Description", s."Status", s."AttemptCount", s."LastAttemptAt", s."AppliedAt",
               last."DurationMs" AS "LastDurationMs", last."SqlState" AS "LastSqlState", last."ErrorMessage" AS "LastError"
        FROM ops."DbScripts" s
        LEFT JOIN LATERAL (
            SELECT a."DurationMs", a."SqlState", a."ErrorMessage" FROM ops."DbScriptAttempts" a
            WHERE a."ScriptId" = s."Id" ORDER BY a."StartedAt" DESC LIMIT 1) last ON true
        """;

    private const string ScriptOrder = """
        ORDER BY CASE s."Kind" WHEN 'EfMigration' THEN 0 WHEN 'Data' THEN 1 WHEN 'Repeatable' THEN 2 ELSE 3 END, s."Name"
        """;

    // No SQL text here: lists stay small however large the scripts are. The text is loaded once, for one attempt (LatestSql).
    private const string AttemptSelect = """
        SELECT a."Id", a."ScriptId", s."Name" AS "ScriptName", s."Kind" AS "ScriptKind", a."ReleaseRunId", a."Status", a."StartedAt",
               a."DurationMs", a."RowsAffected", a."SqlState", a."ErrorMessage", a."Checksum"
        FROM ops."DbScriptAttempts" a JOIN ops."DbScripts" s ON s."Id" = a."ScriptId"
        """;

    // to_jsonb(a) ->> 'Sql': a journal created before D19 has no "Sql" column yet; this reads NULL instead of failing.
    // The select list is evaluated after ORDER BY/LIMIT, so only the latest attempt is serialised.
    private const string LatestSql = """
        SELECT to_jsonb(a) ->> 'Sql' AS "Value" FROM ops."DbScriptAttempts" a WHERE a."ScriptId" = {0} ORDER BY a."StartedAt" DESC LIMIT 1
        """;

    // Raw queries are materialised as written (ToListAsync) or only wrapped by SingleOrDefault's LIMIT. Do not compose
    // .Where/.Select/.OrderBy onto them: EF would wrap SQL that has its own ORDER BY/LIMIT in a subquery.
    [HttpGet]
    public async Task<ActionResult<DbReleasesOverviewDto>> Overview([FromQuery] int runs = 20, CancellationToken ct = default)
    {
        if (!await JournalExistsAsync(ct)) return Ok(new DbReleasesOverviewDto(false, [], []));
        var take = Math.Clamp(runs, 1, 100);

        var runRows = await db.Database
            .SqlQueryRaw<RunRow>($$"""SELECT {{RunColumns}} FROM ops."DbReleaseRuns" r ORDER BY r."StartedAt" DESC LIMIT {0}""", take)
            .ToListAsync(ct);
        var scriptRows = await db.Database.SqlQueryRaw<ScriptRow>($"{ScriptSelect} {ScriptOrder}").ToListAsync(ct);

        return Ok(new DbReleasesOverviewDto(true, runRows.Select(ToDto).ToList(), scriptRows.Select(ToDto).ToList()));
    }

    [HttpGet("runs/{id:guid}")]
    public async Task<ActionResult<DbReleaseRunDetailDto>> Run(Guid id, CancellationToken ct)
    {
        if (!await JournalExistsAsync(ct)) return NotFound();
        var run = await db.Database
            .SqlQueryRaw<RunRow>($$"""SELECT {{RunColumns}} FROM ops."DbReleaseRuns" r WHERE r."Id" = {0}""", id)
            .SingleOrDefaultAsync(ct);
        if (run is null) return NotFound();

        var attempts = await db.Database
            .SqlQueryRaw<AttemptRow>($$"""{{AttemptSelect}} WHERE a."ReleaseRunId" = {0} ORDER BY a."StartedAt", s."Name" """, id)
            .ToListAsync(ct);
        return Ok(new DbReleaseRunDetailDto(ToDto(run), attempts.Select(ToDto).ToList()));
    }

    [HttpGet("scripts/{id:guid}")]
    public async Task<ActionResult<DbScriptDetailDto>> Script(Guid id, CancellationToken ct)
    {
        if (!await JournalExistsAsync(ct)) return NotFound();
        var script = await db.Database
            .SqlQueryRaw<ScriptRow>($$"""{{ScriptSelect}} WHERE s."Id" = {0}""", id)
            .SingleOrDefaultAsync(ct);
        if (script is null) return NotFound();

        var attempts = await db.Database
            .SqlQueryRaw<AttemptRow>($$"""{{AttemptSelect}} WHERE a."ScriptId" = {0} ORDER BY a."StartedAt" DESC""", id)
            .ToListAsync(ct);
        var sql = (await db.Database.SqlQueryRaw<string?>(LatestSql, id).ToListAsync(ct)).FirstOrDefault();
        return Ok(new DbScriptDetailDto(ToDto(script), sql, attempts.Select(ToDto).ToList()));
    }

    private async Task<bool> JournalExistsAsync(CancellationToken ct) =>
        await db.Database
            .SqlQueryRaw<bool>("""SELECT to_regclass('ops."DbScriptAttempts"') IS NOT NULL AS "Value" """)
            .SingleAsync(ct);

    private static DbReleaseRunDto ToDto(RunRow r) => new(r.Id, r.Environment, r.CommitSha, r.AppVersion, r.Trigger, r.TriggeredBy,
        r.RetryReason, r.Status, r.StartedAt, r.CompletedAt, r.PipelineUrl, r.Summary);

    private static DbScriptDto ToDto(ScriptRow s) => new(s.Id, s.Name, s.Kind, s.Description, s.Status, s.AttemptCount, s.LastAttemptAt,
        s.AppliedAt, s.LastDurationMs, s.LastSqlState, s.LastError);

    private static DbScriptAttemptDto ToDto(AttemptRow a) => new(a.Id, a.ScriptId, a.ScriptName, a.ScriptKind, a.ReleaseRunId, a.Status,
        a.StartedAt, a.DurationMs, a.RowsAffected, a.SqlState, a.ErrorMessage, a.Checksum);

    // Row shapes for SqlQueryRaw (unmapped types bind by column name to settable properties).
    private sealed class RunRow
    {
        public Guid Id { get; set; }
        public string Environment { get; set; } = "";
        public string? CommitSha { get; set; }
        public string? AppVersion { get; set; }
        public string Trigger { get; set; } = "";
        public string? TriggeredBy { get; set; }
        public string? RetryReason { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? PipelineUrl { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class ScriptRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Description { get; set; }
        public string Status { get; set; } = "";
        public int AttemptCount { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? AppliedAt { get; set; }
        public int? LastDurationMs { get; set; }
        public string? LastSqlState { get; set; }
        public string? LastError { get; set; }
    }

    private sealed class AttemptRow
    {
        public Guid Id { get; set; }
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; } = "";
        public string ScriptKind { get; set; } = "";
        public Guid ReleaseRunId { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public int? DurationMs { get; set; }
        public int? RowsAffected { get; set; }
        public string? SqlState { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Checksum { get; set; }
    }
}
