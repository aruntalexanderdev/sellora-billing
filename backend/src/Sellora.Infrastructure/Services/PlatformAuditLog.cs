using System.Text.Json;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Stages append-only <see cref="PlatformAuditEvent"/> rows in the caller's unit of work. Payloads are plain state
/// snapshots, never secrets.</summary>
internal static class PlatformAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Append(
        SelloraDbContext db, string action, string targetType, Guid targetId, Guid? actorUserId, Guid? companyId,
        string? reason, object? before, object? after, string? correlationId)
    {
        db.PlatformAuditEvents.Add(new PlatformAuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            CompanyId = companyId,
            Reason = reason is { Length: > 500 } r ? r[..500] : reason,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            CorrelationId = correlationId is { Length: > 64 } c ? c[..64] : correlationId,
        });
    }
}
