namespace Sellora.Domain.Entities;

/// <summary>
/// Remembers that a create request (identified by the client-generated Idempotency-Key it carried) already
/// produced a document, so a double-click, retry or replayed request returns that document instead of creating
/// a second purchase/sale/adjustment and moving stock twice.
/// </summary>
public class IdempotencyKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>What was created: "purchase", "sale" or "stock-adjustment".</summary>
    public required string Scope { get; set; }
    public required string Key { get; set; }

    /// <summary>The document the first request created.</summary>
    public Guid ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
