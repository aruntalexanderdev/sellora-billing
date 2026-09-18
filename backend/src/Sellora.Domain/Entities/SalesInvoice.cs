namespace Sellora.Domain.Entities;

/// <summary>
/// A sale is a business transaction, not a plain CRUD record (docx §11, §17): completing one
/// atomically writes its lines, consumes purchase batches FIFO for real COGS, writes a stock
/// movement per line, and decrements each product's StockItem — all in one DB transaction.
/// </summary>
public class SalesInvoice
{
    public Guid Id { get; set; }
    public required string InvoiceNo { get; set; }

    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public DateOnly InvoiceDate { get; set; }

    public PaymentMethod PaymentMethod { get; set; }
    public decimal PaidAmount { get; set; }

    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal GrandTotal { get; set; }

    public SalesStatus Status { get; set; } = SalesStatus.Completed;

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CancelledAt { get; set; }

    public ICollection<SalesInvoiceLine> Lines { get; set; } = new List<SalesInvoiceLine>();

    /// <summary>Due amount is always derived from GrandTotal - PaidAmount, never stored independently (docx §17).</summary>
    public decimal DueAmount => Math.Max(0, GrandTotal - PaidAmount);

    public PaymentStatus PaymentStatus => PaidAmount <= 0
        ? PaymentStatus.Due
        : PaidAmount >= GrandTotal ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid;
}
