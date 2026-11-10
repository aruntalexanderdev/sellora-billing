namespace Sellora.Domain.Entities;

/// <summary>
/// Append-only audit record of a platform operation (Phase 6 D12). Platform-owned: <see cref="CompanyId"/> is optional (the
/// company the operation concerns, when any) so AuditEvents keeps its NOT NULL company. Never updated or deleted: the SaveChanges
/// guard rejects it and database triggers reject UPDATE, DELETE and TRUNCATE. Payloads must not contain secrets.
/// </summary>
public class PlatformAuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorUserId { get; set; }

    /// <summary>See <see cref="PlatformAuditActions"/>.</summary>
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public Guid TargetId { get; set; }

    /// <summary>The company the event concerns, when any (no navigation: platform rows are not filtered by company).</summary>
    public Guid? CompanyId { get; set; }
    public string? Reason { get; set; }

    /// <summary>JSON documents (jsonb) of the state before/after.</summary>
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>Action codes written to <see cref="PlatformAuditEvent.Action"/>.</summary>
public static class PlatformAuditActions
{
    public const string Login = "platform.login";
    public const string LoginFailed = "platform.login_failed";
    public const string CompanyCreated = "platform.company_created";
    public const string CompanyStatusChanged = "platform.company_status_changed";
    public const string AdminGranted = "platform.admin_granted";
    public const string AdminRevoked = "platform.admin_revoked";
}

/// <summary>Values of <see cref="PlatformAuditEvent.TargetType"/>.</summary>
public static class PlatformAuditTargets
{
    public const string User = "User";
    public const string Company = "Company";
    public const string PlatformAdministrator = "PlatformAdministrator";
}
