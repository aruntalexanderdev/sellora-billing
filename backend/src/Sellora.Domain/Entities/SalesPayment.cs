namespace Sellora.Domain.Entities;

/// <summary>
/// One entry in a sales invoice's money ledger: a payment received, a refund paid back, or a reversal of an earlier
/// payment (see <see cref="Kind"/>). Entries are append-only: an invoice's paid total is
/// payments less refunds and reversals, and an entry is never edited or deleted. Sales are billed in the base currency today, so the rate is always 1; it is snapshotted anyway so
/// the record keeps its shape if customer currencies are added.
/// </summary>
public class SalesPayment
{
    public Guid Id { get; set; }

    public Guid SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public PaymentEntryKind Kind { get; set; } = PaymentEntryKind.Payment;

    /// <summary>For a <see cref="PaymentEntryKind.Reversal"/>, the payment it undoes.</summary>
    public Guid? ReversesPaymentId { get; set; }
    public SalesPayment? ReversesPayment { get; set; }

    /// <summary>For a refund, the return it was made for, if any.</summary>
    public Guid? SalesReturnId { get; set; }
    public SalesReturn? SalesReturn { get; set; }

    public string? Reason { get; set; }

    public DateOnly PaymentDate { get; set; }

    /// <summary>Amount received, in the invoice's currency.</summary>
    public decimal Amount { get; set; }

    public decimal ExchangeRateToBase { get; set; } = 1m;

    /// <summary>The base-currency value of this payment.</summary>
    public decimal BaseAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
