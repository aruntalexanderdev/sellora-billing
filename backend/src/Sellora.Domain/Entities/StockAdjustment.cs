namespace Sellora.Domain.Entities;

/// <summary>
/// A manual correction to stock, always traceable to a StockMovement (docx §9.4, §17). Decreases consume
/// existing PurchaseBatches oldest-first, exactly like a future sale would, so batch remaining quantities
/// never drift out of sync with StockItem.QuantityOnHand. Increases (e.g. Opening Stock) create a new
/// batch of their own so that stock they add is just as FIFO-traceable as purchased stock.
/// </summary>
public class StockAdjustment
{
    public Guid Id { get; set; }
    public required string AdjustmentNo { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public AdjustmentType Type { get; set; }
    public AdjustmentDirection Direction { get; set; }
    public decimal QuantityBaseUnits { get; set; }

    /// <summary>Required for Increase adjustments — the cost basis for the batch this creates.</summary>
    public decimal? UnitCost { get; set; }

    public string? Reason { get; set; }

    public decimal BeforeQty { get; set; }
    public decimal AfterQty { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public PurchaseBatch? Batch { get; set; }
}
