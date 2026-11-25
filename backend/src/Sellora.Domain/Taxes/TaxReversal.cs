namespace Sellora.Domain.Taxes;

/// <summary>
/// Return reversal of tax rows (BR25, AC15). The existing proportional rule of <c>SalesService</c>: a return takes
/// <c>R(original × returnBaseQty / lineBaseQty)</c> and the return that takes everything still returnable takes the exact remainder
/// (<c>original − Σ prior</c>), so the returns of a line always sum to the original. Rows are matched by component id, else by code,
/// so legacy rows (no component id) and non-taxable rows reverse like any other. Bill discount and charge shares are inside the line's
/// taxable value and reverse with it.
/// </summary>
public static class TaxReversal
{
    /// <summary>The key two rows are matched on: the component id, else the code (case-insensitive).</summary>
    public static string RowKey(Guid? componentId, string code) =>
        componentId is { } id ? id.ToString("N") : "code:" + code.Trim().ToUpperInvariant();

    /// <summary>
    /// One amount's share of a return. <paramref name="remainingBaseQty"/> is the base quantity still returnable before this return
    /// (<paramref name="lineBaseQty"/> − prior returns); a return of exactly that takes <c>original − priorSum</c>.
    /// Purchases call it a second time for each <c>Original*</c> value (document currency) with that currency's <paramref name="precision"/>.
    /// </summary>
    public static decimal Share(decimal original, decimal priorSum, decimal returnBaseQty, decimal remainingBaseQty, decimal lineBaseQty, int precision)
    {
        if (returnBaseQty == remainingBaseQty) return original - priorSum;
        if (lineBaseQty == 0) return 0m;
        return TaxMoney.Round(original * returnBaseQty / lineBaseQty, precision);
    }

    /// <summary>
    /// The rows of a return line: every original row reversed by <see cref="Share"/> on its tax amount and, for percent rows, on its base amount
    /// (a per-unit row's base amount is a quantity and reverses exactly as the returned base quantity, or the remainder on the last return).
    /// The return line's tax is the sum of the returned tax amounts.
    /// </summary>
    public static IReadOnlyList<TaxRowResult> ReverseRows(
        IReadOnlyList<TaxRowResult> originalRows, IReadOnlyList<TaxRowResult> priorReturnRows,
        decimal returnBaseQty, decimal remainingBaseQty, decimal lineBaseQty, int precision)
    {
        var priorTax = new Dictionary<string, decimal>();
        var priorBase = new Dictionary<string, decimal>();
        foreach (var row in priorReturnRows)
        {
            var key = RowKey(row.ComponentId, row.Code);
            priorTax[key] = priorTax.GetValueOrDefault(key) + row.TaxAmount;
            priorBase[key] = priorBase.GetValueOrDefault(key) + row.BaseAmount;
        }

        var result = new List<TaxRowResult>(originalRows.Count);
        foreach (var row in originalRows)
        {
            var key = RowKey(row.ComponentId, row.Code);
            var tax = Share(row.TaxAmount, priorTax.GetValueOrDefault(key), returnBaseQty, remainingBaseQty, lineBaseQty, precision);
            var baseAmount = row.Method == Entities.TaxCalculationMethod.AmountPerBaseUnit
                ? (returnBaseQty == remainingBaseQty ? row.BaseAmount - priorBase.GetValueOrDefault(key) : returnBaseQty)
                : Share(row.BaseAmount, priorBase.GetValueOrDefault(key), returnBaseQty, remainingBaseQty, lineBaseQty, precision);
            result.Add(row with { BaseAmount = baseAmount, TaxAmount = tax });
        }
        return result;
    }
}
