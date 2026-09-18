namespace Sellora.Domain.Entities;

/// <summary>
/// A price effective for a date range, never overwritten in place (docx §9.3, §17). Setting a new
/// price closes the currently-open row (EffectiveTo = now) and inserts a new one with EffectiveTo =
/// null. Sales snapshot the price they charged onto the invoice line itself, so this table exists for
/// "what do we currently charge" plus a visible audit trail — not for historical lookups.
/// </summary>
public class SellingPrice
{
    public Guid Id { get; set; }

    public Guid ProductSellingModeId { get; set; }
    public ProductSellingMode ProductSellingMode { get; set; } = null!;

    public decimal Price { get; set; }

    public DateTimeOffset EffectiveFrom { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EffectiveTo { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
