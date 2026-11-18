namespace Sellora.Api.Contracts;

// Database releases page (docs/features/db-release-automation.md, "API contract"). Read-only views of the ops journal that
// Sellora.DbMigrator writes; the API never writes it (R12).

public record DbReleaseRunDto(
    Guid Id,
    string Environment,
    string? CommitSha,
    string? AppVersion,
    string Trigger,
    string? TriggeredBy,
    string? RetryReason,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? PipelineUrl,
    string? Summary);

public record DbScriptDto(
    Guid Id,
    string Name,
    string Kind,
    string? Description,
    string Status,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? AppliedAt,
    int? LastDurationMs,
    string? LastSqlState,
    string? LastError);

public record DbScriptAttemptDto(
    Guid Id,
    Guid ScriptId,
    string ScriptName,
    string ScriptKind,
    Guid ReleaseRunId,
    string Status,
    DateTimeOffset StartedAt,
    int? DurationMs,
    int? RowsAffected,
    string? SqlState,
    string? ErrorMessage,
    string? Checksum);

public record DbReleasesOverviewDto(
    bool JournalAvailable, IReadOnlyList<DbReleaseRunDto> Runs, IReadOnlyList<DbScriptDto> Scripts, DbReleaseRetryInfoDto Retry);

// Step 4: retry (D20–D22).
public record DbReleaseRetryLastRequestDto(string RequestedBy, string Reason, DateTimeOffset RequestedAt);

public record DbReleaseRetryInfoDto(bool Configured, bool CanRetry, string? UnavailableReason, DbReleaseRetryLastRequestDto? LastRequest);

public record RetryDbReleaseRequest(string? Environment, string? Reason);

public record DbReleaseRetryResponseDto(Guid RunId, string PipelineUrl, DateTimeOffset RequestedAt);

public record DbReleaseRunDetailDto(DbReleaseRunDto Run, IReadOnlyList<DbScriptAttemptDto> Attempts);

public record DbScriptDetailDto(DbScriptDto Script, string? Sql, IReadOnlyList<DbScriptAttemptDto> Attempts);
