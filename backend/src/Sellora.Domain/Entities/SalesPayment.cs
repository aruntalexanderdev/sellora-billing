namespace Sellora.Domain.Entities;

/// <summary>
/// One payment received against a sales invoice. Payments are append-only: an invoice's paid total is the
/// sum of its payments, and a payment is never edited or deleted (corrections arrive as reversals in a later
/// phase). Sales are billed in the base currency today, so the rate is always 1; it is snapshotted anyway so
/// the record keeps its shape if customer currencies are added.
/// </summary>
public class SalesPayment
{
    public Guid Id { get; set; }

    public Guid SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

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
