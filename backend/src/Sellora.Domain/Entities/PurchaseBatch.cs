namespace Sellora.Domain.Entities;

/// <summary>
/// Created one-per-line when a purchase completes. Sales (Phase 6) consume these oldest-first (FIFO)
/// so COGS reflects actual landed cost rather than the latest purchase price (docx §12.5, §17).
/// </summary>
public class PurchaseBatch
{
    public Guid Id { get; set; }

    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine PurchaseOrderLine { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public decimal PurchasedBaseQty { get; set; }
    public decimal RemainingBaseQty { get; set; }
    public decimal UnitCost { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
