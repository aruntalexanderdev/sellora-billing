using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A managed exchange rate from a non-base currency to the business base currency, effective from a
/// given date until superseded by a later-dated row. Purchases snapshot the resolved rate onto
/// themselves (PurchaseOrder.ExchangeRateToBase) at save time, so editing or adding rows here never
/// changes an already-saved transaction.
/// </summary>
public class ExchangeRate : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    public Guid CurrencyId { get; set; }
    public Currency Currency { get; set; } = null!;

    /// <summary>How many units of the business base currency equal 1 unit of <see cref="Currency"/>.</summary>
    public decimal RateToBase { get; set; }

    public DateOnly EffectiveDate { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
