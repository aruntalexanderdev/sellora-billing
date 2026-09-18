namespace Sellora.Domain.Entities;

/// <summary>
/// A currency the business can transact in — the business's own base currency (Business Settings)
/// or a supplier's purchase currency. Inactive currencies stay selectable for historical records but
/// cannot be assigned to new suppliers/purchases.
/// </summary>
public class Currency
{
    public Guid Id { get; set; }

    /// <summary>ISO 4217 code, e.g. "INR", "USD". Stored uppercase.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Symbol { get; set; }

    /// <summary>Number of decimal places this currency is normally quoted/rounded to.</summary>
    public int DecimalPrecision { get; set; } = 2;

    public bool IsActive { get; set; } = true;
}
