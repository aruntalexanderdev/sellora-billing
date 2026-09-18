namespace Sellora.Domain.Entities;

/// <summary>
/// A lot of stock at a specific cost. Created one-per-line when a purchase completes, or standalone
/// when a stock adjustment increases quantity (e.g. Opening Stock). Exactly one of
/// <see cref="PurchaseOrderLineId"/> / <see cref="StockAdjustmentId"/> is set — both are optional at the
/// schema level since a batch has exactly one of two possible origins. Sales (Phase 6) consume these
/// oldest-first (FIFO) so COGS reflects actual landed cost rather than the latest purchase price
/// (docx §12.5, §17).
/// </summary>
public class PurchaseBatch
{
    public Guid Id { get; set; }

    public Guid? PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    public Guid? StockAdjustmentId { get; set; }
    public StockAdjustment? StockAdjustment { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public decimal PurchasedBaseQty { get; set; }
    public decimal RemainingBaseQty { get; set; }
    public decimal UnitCost { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
