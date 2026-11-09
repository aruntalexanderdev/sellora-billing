using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A lot of stock at a specific cost. Created one-per-line when a purchase completes, or standalone
/// when a stock adjustment increases quantity (e.g. Opening Stock). Exactly one of
/// <see cref="PurchaseOrderLineId"/> / <see cref="StockAdjustmentId"/> is set — both are optional at the
/// schema level since a batch has exactly one of two possible origins. Sales (Phase 6) consume these
/// oldest-first (FIFO) so COGS reflects actual landed cost rather than the latest purchase price
/// (docx §12.5, §17).
/// </summary>
public class PurchaseBatch : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    public Guid? PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    public Guid? StockAdjustmentId { get; set; }
    public StockAdjustment? StockAdjustment { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>
    /// Sellora's own identity for this lot, assigned by the database (never entered by users). Supplier batch
    /// numbers can repeat or be absent, so this — not <see cref="SupplierBatchNumber"/> — is what identifies a batch.
    /// </summary>
    public long BatchSequence { get; set; }

    /// <summary>Human-readable internal batch number, e.g. BATCH-0007.</summary>
    public string InternalBatchNo => FormatInternalBatchNo(BatchSequence);

    public static string FormatInternalBatchNo(long sequence) => $"BATCH-{sequence:D4}";

    /// <summary>The supplier/manufacturer's own lot number, exactly as entered. Null when the supplier gave none;
    /// the same value may legitimately appear on batches from different purchases.</summary>
    public string? SupplierBatchNumber { get; set; }

    /// <summary>The day the stock was received — the purchase date, or the adjustment date for adjustment batches.</summary>
    public DateOnly ReceivedDate { get; set; }

    public decimal PurchasedBaseQty { get; set; }
    public decimal RemainingBaseQty { get; set; }
    public decimal UnitCost { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
