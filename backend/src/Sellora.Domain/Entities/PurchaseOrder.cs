using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A purchase is a business transaction, not a plain CRUD record (docx §10, §17): completing one
/// atomically writes its lines, a batch per line, a stock movement per line, and updates the
/// product's StockItem — all in one DB transaction.
/// </summary>
public class PurchaseOrder : ICompanyOwned
{
    public Guid CompanyId { get; set; }

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

    /// <summary>The method of the most recent payment (each payment also records its own).</summary>
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
    public Guid? CancelledByUserId { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();

    /// <summary>The payment ledger. <see cref="PaidAmount"/> and <see cref="OriginalPaidAmount"/> are the
    /// running totals of these rows, maintained only by the purchase service.</summary>
    public ICollection<PurchasePayment> Payments { get; set; } = new List<PurchasePayment>();

    /// <summary>The value of goods returned to the supplier, in the base currency. Maintained only by the purchase service.</summary>
    public decimal ReturnedAmount { get; set; }

    /// <summary>The same returned value in the purchase's transaction currency.</summary>
    public decimal OriginalReturnedAmount { get; set; }

    public ICollection<PurchaseReturn> Returns { get; set; } = new List<PurchaseReturn>();

    /// <summary>What is owed to the supplier for: the grand total less returned goods, in the base currency.</summary>
    public decimal NetTotal => GrandTotal - ReturnedAmount;

    /// <summary>The same, in the transaction currency.</summary>
    public decimal OriginalNetTotal => OriginalGrandTotal - OriginalReturnedAmount;

    /// <summary>Remaining due in the transaction currency, derived; a cancelled purchase owes nothing.</summary>
    public decimal OriginalDueAmount => Status == PurchaseStatus.Cancelled ? 0 : Math.Max(0, OriginalNetTotal - OriginalPaidAmount);

    /// <summary>Remaining due in the base currency. Zero exactly when the transaction-currency due is zero, so
    /// rounding can never leave a stray paisa of due on a settled foreign purchase.</summary>
    public decimal DueAmount => OriginalDueAmount <= 0 ? 0 : Math.Max(0, NetTotal - PaidAmount);

    /// <summary>Money held by the supplier beyond what is owed (transaction currency) and so refundable by them.</summary>
    public decimal OriginalRefundableAmount => Math.Max(0, OriginalPaidAmount - (Status == PurchaseStatus.Cancelled ? 0 : OriginalNetTotal));

    public PaymentStatus PaymentStatus => OriginalNetTotal <= 0 || OriginalPaidAmount >= OriginalNetTotal
        ? PaymentStatus.Paid
        : OriginalPaidAmount <= 0 ? PaymentStatus.Due : PaymentStatus.PartiallyPaid;
}
