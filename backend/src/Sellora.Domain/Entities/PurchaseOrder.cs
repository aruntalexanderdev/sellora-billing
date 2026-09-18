namespace Sellora.Domain.Entities;

/// <summary>
/// A purchase is a business transaction, not a plain CRUD record (docx §10, §17): completing one
/// atomically writes its lines, a batch per line, a stock movement per line, and updates the
/// product's StockItem — all in one DB transaction.
/// </summary>
public class PurchaseOrder
{
    public Guid Id { get; set; }
    public required string PurchaseNo { get; set; }

    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    public DateOnly PurchaseDate { get; set; }
    public string? SupplierInvoiceNumber { get; set; }
    public string? Notes { get; set; }

    /// <summary>The transaction currency, snapshotted from the supplier when the purchase was saved.</summary>
    public Guid CurrencyId { get; set; }
    public Currency Currency { get; set; } = null!;

    /// <summary>Base-currency units per 1 unit of <see cref="Currency"/>, captured at save time — never recomputed.</summary>
    public decimal ExchangeRateToBase { get; set; } = 1m;

    /// <summary>The purchase total in the original transaction currency. Every other monetary field on this
    /// purchase and its lines is already in the business base currency.</summary>
    public decimal OriginalGrandTotal { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    /// <summary>Amount paid, in the business base currency (compared against <see cref="GrandTotal"/>).</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>The same payment as entered, in the purchase's transaction currency. Kept alongside
    /// <see cref="PaidAmount"/> so the supplier-currency figure is never lost to the conversion.</summary>
    public decimal OriginalPaidAmount { get; set; }

    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal OtherChargesTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal GrandTotal { get; set; }

    public PurchaseStatus Status { get; set; } = PurchaseStatus.Completed;

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CancelledAt { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();

    /// <summary>Due amount is always derived from GrandTotal - PaidAmount, never stored independently (docx §17).</summary>
    public decimal DueAmount => Math.Max(0, GrandTotal - PaidAmount);

    /// <summary>Remaining due in the transaction currency, derived from the original totals.</summary>
    public decimal OriginalDueAmount => Math.Max(0, OriginalGrandTotal - OriginalPaidAmount);

    public PaymentStatus PaymentStatus => PaidAmount <= 0
        ? PaymentStatus.Due
        : PaidAmount >= GrandTotal ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid;
}
