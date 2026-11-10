using System.Text.Json;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Stages append-only <see cref="AuditEvent"/> rows in the caller's unit of work (same transaction as the change).
/// Payloads are plain state snapshots - never secrets.</summary>
internal static class AuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Append(
        SelloraDbContext db, Domain.Abstractions.AccessActor actor, string action, string targetType, Guid targetId,
        Guid? subjectUserId, string? reason, object? before, object? after)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CompanyId = db.CompanyContext.RequireCompanyId(),
            OccurredAt = DateTimeOffset.UtcNow,
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            SubjectUserId = subjectUserId,
            Reason = reason,
            Before = before is null ? null : JsonSerializer.Serialize(before, Json),
            After = after is null ? null : JsonSerializer.Serialize(after, Json),
            CorrelationId = actor.CorrelationId is { Length: > 64 } c ? c[..64] : actor.CorrelationId,
        });
    }
}
