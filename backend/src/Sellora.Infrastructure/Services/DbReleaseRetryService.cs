using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Releases;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Page retry of a failed database release (docs/features/db-release-automation.md, step 4, D20–D22): GitHub re-runs the failed
/// jobs of the latest failed pipeline run, so the same release package is applied again (R12). The API itself never runs scripts.
/// </summary>
public sealed class DbReleaseRetryService(SelloraDbContext db, IGitHubActionsClient github, TimeProvider clock) : IDbReleaseRetryService
{
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    public const int ReasonMinLength = 10;
    public const int ReasonMaxLength = 500;

    public async Task<DbReleaseRetryInfo> GetInfoAsync(CancellationToken ct)
    {
        var latest = await LatestRunAsync(ct);
        var last = latest is null ? null : await LastRequestAsync(latest.Id, ct);
        return new DbReleaseRetryInfo(github.IsConfigured, Unavailable(latest, last) is null, Unavailable(latest, last), last);
    }

    public async Task<DbReleaseRetryAccepted> RequestAsync(string? environment, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct)
    {
        var trimmedReason = reason?.Trim() ?? "";
        if (trimmedReason.Length is < ReasonMinLength or > ReasonMaxLength)
            throw new DomainValidationException("reason", $"Enter a reason of {ReasonMinLength}–{ReasonMaxLength} characters; it is kept in the platform audit.");

        if (!github.IsConfigured)
            throw new DbReleaseRetryException(503, DbReleaseRetryException.NotConfigured,
                "Retry from the console is not set up for this environment (GitHub App settings missing). Re-run the failed job in GitHub.");

        // Claim the retry before calling GitHub: the check and the audit row are serialised by a transaction-scoped advisory lock,
        // so two admins (or a double submit) cannot both pass the 10-minute window (D22). The row is committed first; if GitHub then
        // refuses, a "failed" row follows and releases the window.
        LatestRun latest;
        long runId;
        var requestedAt = clock.GetUtcNow();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [RetryLockKey], ct);

            var current = await LatestRunAsync(ct);
            if (current is not null && !string.Equals(environment?.Trim(), current.Environment, StringComparison.Ordinal))
                throw new DomainValidationException("environment", $"This API serves '{current.Environment}'. Reload the page and try again.");
            var last = current is null ? null : await LastRequestAsync(current.Id, ct);
            if (Unavailable(current, last) is { } why)
                throw new DbReleaseRetryException(409, DbReleaseRetryException.NotAllowed, why);

            latest = current!;
            runId = github.ParseRunId(latest.PipelineUrl)!.Value; // Unavailable() guarantees a parsable run of this repository
            PlatformAuditLog.Append(db, PlatformAuditActions.DbReleaseRetryRequested, PlatformAuditTargets.DbReleaseRun, latest.Id,
                actorUserId, companyId: null, trimmedReason, before: new { latest.Status, latest.PipelineUrl },
                after: new { GitHubRunId = runId, Action = "rerun-failed-jobs" }, correlationId);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var result = await github.RerunFailedJobsAsync(runId, ct);
        if (!result.Succeeded)
        {
            PlatformAuditLog.Append(db, PlatformAuditActions.DbReleaseRetryFailed, PlatformAuditTargets.DbReleaseRun, latest.Id,
                actorUserId, companyId: null, result.Message, before: null, after: new { GitHubRunId = runId }, correlationId);
            await db.SaveChangesAsync(CancellationToken.None);
            throw new DbReleaseRetryException(502, DbReleaseRetryException.GitHubRejected, $"GitHub did not start the re-run: {result.Message}");
        }

        return new DbReleaseRetryAccepted(latest.Id, latest.PipelineUrl!, requestedAt);
    }

    // Distinct from the migrator's key: this only serialises retry requests between API instances.
    private const long RetryLockKey = 0x53_45_4C_4C_52_54_01; // "SELLRT" v1

    /// <summary>Null when a retry may start; otherwise one sentence for the page (D21, D22).</summary>
    private string? Unavailable(LatestRun? latest, DbReleaseRetryRequestInfo? last)
    {
        if (!github.IsConfigured) return "Retry from the console is not set up for this environment. Re-run the failed job in GitHub.";
        if (latest is null) return "No migrator run is recorded on this database yet.";
        if (latest.Status != "Failed") return "Only a failed latest run can be retried.";
        if (github.ParseRunId(latest.PipelineUrl) is null) return "The latest run has no pipeline run of this repository to re-run.";
        if (last is not null && clock.GetUtcNow() - last.RequestedAt < RepeatWindow)
            return $"A retry was requested at {last.RequestedAt:HH:mm} UTC. Wait for the new pipeline run (up to 10 minutes) before trying again.";
        return null;
    }

    private async Task<LatestRun?> LatestRunAsync(CancellationToken ct)
    {
        var exists = await db.Database
            .SqlQueryRaw<bool>("""SELECT to_regclass('ops."DbReleaseRuns"') IS NOT NULL AS "Value" """)
            .SingleAsync(ct);
        if (!exists) return null;
        var rows = await db.Database
            .SqlQueryRaw<LatestRun>("""SELECT "Id", "Environment", "Status", "PipelineUrl" FROM ops."DbReleaseRuns" ORDER BY "StartedAt" DESC LIMIT 1""")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private async Task<DbReleaseRetryRequestInfo?> LastRequestAsync(Guid runId, CancellationToken ct)
    {
        // The newest retry event for the run; a "failed" one means GitHub refused, so no request is pending.
        var last = await db.PlatformAuditEvents.AsNoTracking()
            .Where(e => (e.Action == PlatformAuditActions.DbReleaseRetryRequested || e.Action == PlatformAuditActions.DbReleaseRetryFailed)
                        && e.TargetId == runId)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.Action, e.ActorUserId, e.Reason, e.OccurredAt })
            .FirstOrDefaultAsync(ct);
        if (last is null || last.Action == PlatformAuditActions.DbReleaseRetryFailed) return null;

        var name = last.ActorUserId is { } id
            ? await db.Users.AsNoTracking().Where(u => u.Id == id) // Users carry no tenant filter
                .Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(ct)
            : null;
        return new DbReleaseRetryRequestInfo(name ?? "Unknown user", last.Reason ?? "", last.OccurredAt);
    }

    /// <summary>Row shape for SqlQueryRaw (binds by column name).</summary>
    private sealed class LatestRun
    {
        public Guid Id { get; set; }
        public string Environment { get; set; } = "";
        public string Status { get; set; } = "";
        public string? PipelineUrl { get; set; }
    }
}
