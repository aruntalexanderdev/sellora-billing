namespace Sellora.Domain.Entities;

/// <summary>
/// One entry in a purchase's money ledger: a payment made, a refund received from the supplier, or a reversal of an
/// earlier payment (see <see cref="Kind"/>). Entries are append-only: a purchase's paid totals are payments less
/// refunds and reversals, and an entry is never edited or deleted.
/// The amount is in the purchase's transaction currency; the rate is the purchase's captured rate, snapshotted
/// here so the base-currency figure can never be recalculated later.
/// </summary>
public class PurchasePayment
{
    public Guid Id { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public PaymentEntryKind Kind { get; set; } = PaymentEntryKind.Payment;

    /// <summary>For a <see cref="PaymentEntryKind.Reversal"/>, the payment it undoes.</summary>
    public Guid? ReversesPaymentId { get; set; }
    public PurchasePayment? ReversesPayment { get; set; }

    /// <summary>For a refund, the return it was made for, if any.</summary>
    public Guid? PurchaseReturnId { get; set; }
    public PurchaseReturn? PurchaseReturn { get; set; }

    public string? Reason { get; set; }

    public DateOnly PaymentDate { get; set; }

    /// <summary>Amount paid, in the purchase's transaction currency.</summary>
    public decimal Amount { get; set; }

    /// <summary>Base-currency units per 1 unit of the transaction currency, snapshotted from the purchase.</summary>
    public decimal ExchangeRateToBase { get; set; } = 1m;

    /// <summary>The base-currency value of this payment; these sum to the purchase's PaidAmount.</summary>
    public decimal BaseAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
