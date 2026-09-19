namespace Sellora.Domain.Entities;

/// <summary>
/// A business operating cost (rent, electricity, salary…), distinct from a purchase, which acquires stock.
/// An expense is recorded as paid. Its money fields are fixed once saved: to correct an amount, date, currency
/// or method, cancel the expense and enter a new one, so history is never silently rewritten. Only the
/// category, reference and notes can be edited while it is active. The amount is in the expense's currency;
/// the exchange rate is resolved for the expense date and snapshotted, so the base-currency amount never
/// changes when rates are later edited.
/// </summary>
public class Expense
{
    public Guid Id { get; set; }
    public required string ExpenseNo { get; set; }

    public Guid CategoryId { get; set; }
    public ExpenseCategory Category { get; set; } = null!;

    public DateOnly ExpenseDate { get; set; }

    public Guid CurrencyId { get; set; }
    public Currency Currency { get; set; } = null!;

    /// <summary>Amount in <see cref="Currency"/>.</summary>
    public decimal Amount { get; set; }

    /// <summary>Base-currency units per 1 unit of <see cref="Currency"/>, captured at save time — never recomputed.</summary>
    public decimal ExchangeRateToBase { get; set; } = 1m;

    /// <summary>The amount in the business base currency, used for reporting.</summary>
    public decimal BaseAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public ExpenseStatus Status { get; set; } = ExpenseStatus.Active;

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public User? CancelledByUser { get; set; }
    public string? CancellationReason { get; set; }
}
