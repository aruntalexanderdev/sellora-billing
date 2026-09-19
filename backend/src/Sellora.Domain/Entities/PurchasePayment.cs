namespace Sellora.Domain.Entities;

/// <summary>
/// One payment made against a purchase. Payments are append-only: a purchase's paid totals are the sum of
/// its payments, and a payment is never edited or deleted (corrections arrive as reversals in a later phase).
/// The amount is in the purchase's transaction currency; the rate is the purchase's captured rate, snapshotted
/// here so the base-currency figure can never be recalculated later.
/// </summary>
public class PurchasePayment
{
    public Guid Id { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

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
