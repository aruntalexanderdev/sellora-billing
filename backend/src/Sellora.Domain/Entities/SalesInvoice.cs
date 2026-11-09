using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A sale is a business transaction, not a plain CRUD record (docx §11, §17): completing one
/// atomically writes its lines, consumes purchase batches FIFO for real COGS, writes a stock
/// movement per line, and decrements each product's StockItem — all in one DB transaction.
/// </summary>
public class SalesInvoice : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    /// <summary>The branch that owns this document (Phase 2, same-company composite FK). Never taken from a client body.</summary>
    public Guid BranchId { get; set; }

    public Guid Id { get; set; }
    public required string InvoiceNo { get; set; }

    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public DateOnly InvoiceDate { get; set; }

    /// <summary>The method of the most recent payment (each payment also records its own).</summary>
    public PaymentMethod PaymentMethod { get; set; }
    /// <summary>Net money held from the customer: payments received less refunds and reversals.</summary>
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
    public Guid? CancelledByUserId { get; set; }

    public ICollection<SalesInvoiceLine> Lines { get; set; } = new List<SalesInvoiceLine>();

    /// <summary>The payment ledger. <see cref="PaidAmount"/> is the running total of these rows, maintained
    /// only by the sales service.</summary>
    public ICollection<SalesPayment> Payments { get; set; } = new List<SalesPayment>();

    /// <summary>The value of goods customers have returned (tax included). Maintained only by the sales service.</summary>
    public decimal ReturnedAmount { get; set; }

    public ICollection<SalesReturn> Returns { get; set; } = new List<SalesReturn>();

    /// <summary>What the customer actually owes for: the grand total less returned goods.</summary>
    public decimal NetTotal => GrandTotal - ReturnedAmount;

    /// <summary>Due amount is always derived, never stored independently (docx §17). A cancelled sale owes nothing.</summary>
    public decimal DueAmount => Status == SalesStatus.Cancelled ? 0 : Math.Max(0, NetTotal - PaidAmount);

    /// <summary>Money held from the customer beyond what they owe — paid more than the net total, or paid on a
    /// sale that was then cancelled — and so refundable to them.</summary>
    public decimal RefundableAmount => Math.Max(0, PaidAmount - (Status == SalesStatus.Cancelled ? 0 : NetTotal));

    public PaymentStatus PaymentStatus => NetTotal <= 0 || PaidAmount >= NetTotal
        ? PaymentStatus.Paid
        : PaidAmount <= 0 ? PaymentStatus.Due : PaymentStatus.PartiallyPaid;
}
