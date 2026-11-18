namespace Sellora.Domain.Releases;

// Retry of a failed database release from the platform console (docs/features/db-release-automation.md, step 4, D20–D22).

/// <summary>What the page needs to show or hide the retry action for the latest run.</summary>
public sealed record DbReleaseRetryInfo(bool Configured, bool CanRetry, string? UnavailableReason, DbReleaseRetryRequestInfo? LastRequest);

/// <summary>The last retry requested for the latest run (from the platform audit).</summary>
public sealed record DbReleaseRetryRequestInfo(string RequestedBy, string Reason, DateTimeOffset RequestedAt);

/// <summary>A retry GitHub accepted: <paramref name="RunId"/> is the journal run being retried.</summary>
public sealed record DbReleaseRetryAccepted(Guid RunId, string PipelineUrl, DateTimeOffset RequestedAt);

/// <summary>A retry that cannot be started. <see cref="Status"/> is the HTTP status (409, 502, 503), <see cref="Code"/> the problem code.</summary>
public sealed class DbReleaseRetryException(int status, string code, string message) : Exception(message)
{
    public const string NotAllowed = "retry_not_allowed";
    public const string GitHubRejected = "github_rejected";
    public const string NotConfigured = "retry_not_configured";

    public int Status { get; } = status;
    public string Code { get; } = code;
}

public interface IDbReleaseRetryService
{
    Task<DbReleaseRetryInfo> GetInfoAsync(CancellationToken ct);

    /// <summary>Validates (400 via DomainValidationException, 409/503 via DbReleaseRetryException), asks GitHub to re-run the failed
    /// jobs of the latest failed pipeline run (502 when GitHub refuses), and audits the request.</summary>
    Task<DbReleaseRetryAccepted> RequestAsync(string? environment, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct);
}

/// <summary>The GitHub Actions calls the retry needs, authenticated as the Sellora GitHub App (D20).</summary>
public interface IGitHubActionsClient
{
    bool IsConfigured { get; }

    /// <summary>The workflow run id in <paramref name="pipelineUrl"/>, only when it is a run of the configured repository.</summary>
    long? ParseRunId(string? pipelineUrl);

    /// <summary>POST /repos/{owner}/{repo}/actions/runs/{runId}/rerun-failed-jobs.</summary>
    Task<GitHubCallResult> RerunFailedJobsAsync(long runId, CancellationToken ct);
}

public sealed record GitHubCallResult(bool Succeeded, string? Message)
{
    public static readonly GitHubCallResult Ok = new(true, null);
}
