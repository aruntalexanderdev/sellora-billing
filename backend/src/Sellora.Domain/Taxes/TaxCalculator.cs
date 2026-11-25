using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;

namespace Sellora.Domain.Taxes;

/// <summary>
/// The tax calculator (BR16–BR22, T7, T9; docs/features/tax-profiles.md, TP-2 design). Pure and deterministic: no EF, no I/O, no clock, decimals only.
/// Money is rounded at the document precision per component and per line (AwayFromZero). Validation problems are collected into one
/// <see cref="DomainValidationException"/> keyed <c>lines[i].field</c>, <c>billDiscount</c>, <c>billCharges</c> or <c>lines</c>.
/// </summary>
public static class TaxCalculator
{
    private const int MaxPrecision = 6;
    private const int UnitPriceDecimals = 4;

    public static TaxDocumentResult Calculate(TaxCalcDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var precision = document.Precision;
        var errors = new List<FieldError>();
        if (precision < 0 || precision > MaxPrecision) errors.Add(new FieldError("precision", $"Precision must be between 0 and {MaxPrecision}."));
        if (document.Lines is null || document.Lines.Count == 0)
        {
            errors.Add(new FieldError("lines", "Add at least one item."));
            throw new DomainValidationException(errors);
        }
        if (errors.Count > 0) throw new DomainValidationException(errors);

        var lines = document.Lines;
        var grosses = new decimal[lines.Count];
        var discounts = new decimal[lines.Count];
        var weights = new decimal[lines.Count];

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string Key(string field) => $"lines[{i}].{field}";
            if (line.Rates is null) errors.Add(new FieldError(Key("productId"), "The line has no tax profile."));
            if (line.Qty < 0) errors.Add(new FieldError(Key("qty"), "Quantity cannot be negative."));
            if (line.UnitPrice < 0) errors.Add(new FieldError(Key("unitPrice"), "Unit price cannot be negative."));
            else if (TaxMoney.Round(line.UnitPrice, UnitPriceDecimals) != line.UnitPrice) errors.Add(new FieldError(Key("unitPrice"), $"Unit price has too many decimals (at most {UnitPriceDecimals})."));
            if (line.LineDiscount < 0) errors.Add(new FieldError(Key("discount"), "Discount cannot be negative."));
            else if (TaxMoney.Round(line.LineDiscount, precision) != line.LineDiscount) errors.Add(new FieldError(Key("discount"), $"Discount can have at most {precision} decimal places."));
            if (line.OtherCharges < 0) errors.Add(new FieldError(Key("otherCharges"), "Other charges cannot be negative."));
            else if (TaxMoney.Round(line.OtherCharges, precision) != line.OtherCharges) errors.Add(new FieldError(Key("otherCharges"), $"Other charges can have at most {precision} decimal places."));

            grosses[i] = TaxMoney.Round(line.Qty * line.UnitPrice, precision);
            discounts[i] = TaxMoney.Round(line.LineDiscount, precision);
            if (discounts[i] > grosses[i] && line.LineLooksValid())
            {
                errors.Add(new FieldError(Key("discount"), $"Discount cannot exceed the line amount of {grosses[i].ToString("F" + precision, System.Globalization.CultureInfo.InvariantCulture)}."));
            }
            weights[i] = Math.Max(0, grosses[i] - discounts[i]);
        }

        // ---- bill level (BR19)
        var percent = document.BillDiscountPercent;
        var amount = document.BillDiscountAmount;
        if (percent is not null && amount is not null) errors.Add(new FieldError("billDiscount", "Give the bill discount as a percent or as an amount, not both."));
        else if (percent is { } p && (p < 0 || p > 100)) errors.Add(new FieldError("billDiscount", "The bill discount percent must be between 0 and 100."));
        else if (amount is { } a && a < 0) errors.Add(new FieldError("billDiscount", "The bill discount cannot be negative."));
        else if (amount is { } a2 && TaxMoney.Round(a2, precision) != a2) errors.Add(new FieldError("billDiscount", $"The bill discount can have at most {precision} decimal places."));
        if (document.BillCharges < 0) errors.Add(new FieldError("billCharges", "Bill charges cannot be negative."));
        else if (TaxMoney.Round(document.BillCharges, precision) != document.BillCharges) errors.Add(new FieldError("billCharges", $"Bill charges can have at most {precision} decimal places."));

        var totalWeight = weights.Sum();
        var billDiscount = 0m;
        if (!errors.Any(e => e.Field == "billDiscount"))
        {
            billDiscount = amount is { } given ? given : percent is { } pct ? TaxMoney.Round(totalWeight * pct / 100m, precision) : 0m;
            if (billDiscount > totalWeight) errors.Add(new FieldError("billDiscount", $"The bill discount cannot exceed the bill amount of {totalWeight.ToString("F" + precision, System.Globalization.CultureInfo.InvariantCulture)}."));
        }

        if (errors.Count > 0) throw new DomainValidationException(errors);

        var discountShares = Spread(billDiscount, weights, precision, capByWeight: true);
        var chargeWeights = totalWeight > 0 ? weights : weights.Select(_ => 1m).ToArray();
        var chargeShares = Spread(document.BillCharges, chargeWeights, precision, capByWeight: false);

        // ---- lines
        var results = new List<TaxLineResult>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var result = CalculateLine(i, lines[i], document, grosses[i], discounts[i], discountShares[i], chargeShares[i], errors);
            results.Add(result);
        }
        if (errors.Count > 0) throw new DomainValidationException(errors);

        var subtotal = results.Sum(l => l.Gross);
        var discountTotal = results.Sum(l => l.LineDiscount + l.BillDiscountShare);
        var chargesTotal = results.Sum(l => l.BillChargeShare + l.OtherCharges);
        var taxable = results.Sum(l => l.Taxable);
        var tax = results.Sum(l => l.TaxTotal);
        var effective = totalWeight > 0 ? TaxMoney.Round(billDiscount / totalWeight * 100m, 4) : 0m;
        return new TaxDocumentResult(results, subtotal, discountTotal, chargesTotal, taxable, tax, taxable + tax, billDiscount, effective);
    }

    private static bool LineLooksValid(this TaxCalcLine line) => line.Qty >= 0 && line.UnitPrice >= 0 && line.LineDiscount >= 0;

    private static TaxLineResult CalculateLine(
        int index, TaxCalcLine line, TaxCalcDocument document, decimal gross, decimal lineDiscount, decimal billDiscountShare, decimal billChargeShare,
        List<FieldError> errors)
    {
        var precision = document.Precision;
        var charges = billChargeShare + line.OtherCharges;
        var set = line.Rates;
        var components = ApplicableComponents(set, document.PlaceOfSupply);

        // Non-taxable treatments (AC19): one zero row carrying the treatment, never "0% taxable".
        if (set.Treatment != TaxTreatment.Taxable)
        {
            var taxableNonTax = gross - lineDiscount - billDiscountShare + charges;
            var row = new TaxRowResult(null, set.Treatment.ToString().ToUpperInvariant(), TaxComponentKind.Other, TaxCalculationMethod.Percent, 0m, null, taxableNonTax, 0m);
            return new TaxLineResult(index, line.PriceIncludesTax, gross, lineDiscount, billDiscountShare, billChargeShare, line.OtherCharges, taxableNonTax, 0m, [row], 0m, taxableNonTax);
        }

        var percentSum = components.Where(c => c.Method == TaxCalculationMethod.Percent).Sum(c => (c.RatePercent ?? 0m) / 100m);
        var percentRate = components.Where(c => c.Method == TaxCalculationMethod.Percent).Sum(c => c.RatePercent ?? 0m);
        var rows = new List<TaxRowResult>(components.Count);

        decimal taxable;
        if (!line.PriceIncludesTax)
        {
            // BR16: exclusive.
            taxable = gross - lineDiscount - billDiscountShare + charges;
            foreach (var c in components)
            {
                rows.Add(c.Method == TaxCalculationMethod.Percent
                    ? Row(c, taxable, TaxMoney.Round(taxable * (c.RatePercent ?? 0m) / 100m, precision))
                    : Row(c, line.BaseQty, TaxMoney.Round(line.BaseQty * (c.AmountPerBaseUnit ?? 0m), precision)));
            }
        }
        else
        {
            // BR17 / T9: inclusive. The customer pays exactly Incl; the rounding difference lives in the taxable value.
            var incl = gross - lineDiscount - billDiscountShare;
            var perUnit = components.Where(c => c.Method == TaxCalculationMethod.AmountPerBaseUnit)
                .Sum(c => TaxMoney.Round(line.BaseQty * (c.AmountPerBaseUnit ?? 0m), precision));
            if (perUnit > incl)
            {
                errors.Add(new FieldError($"lines[{index}].unitPrice", "The per-unit tax is more than the tax-inclusive amount of the line. Check the price and the discount."));
                return new TaxLineResult(index, true, gross, lineDiscount, billDiscountShare, billChargeShare, line.OtherCharges, 0m, percentRate, [], 0m, 0m);
            }
            var goodsBasis = incl - perUnit;
            var g0 = TaxMoney.Round(goodsBasis / (1m + percentSum), precision);
            var goodsTax = new Dictionary<TaxRateComponent, decimal>();
            decimal percentTax = 0;
            foreach (var c in components.Where(c => c.Method == TaxCalculationMethod.Percent))
            {
                var t = TaxMoney.Round(g0 * (c.RatePercent ?? 0m) / 100m, precision);
                goodsTax[c] = t;
                percentTax += t;
            }
            var goodsTaxable = goodsBasis - percentTax;
            if (goodsTaxable < 0)
            {
                errors.Add(new FieldError($"lines[{index}].unitPrice", "The tax-inclusive amount is too small to split into taxable value and tax at this precision."));
                return new TaxLineResult(index, true, gross, lineDiscount, billDiscountShare, billChargeShare, line.OtherCharges, 0m, percentRate, [], 0m, 0m);
            }
            taxable = goodsTaxable + charges;
            foreach (var c in components)
            {
                if (c.Method == TaxCalculationMethod.Percent)
                {
                    var onCharges = TaxMoney.Round(charges * (c.RatePercent ?? 0m) / 100m, precision);
                    rows.Add(Row(c, taxable, goodsTax[c] + onCharges));
                }
                else
                {
                    rows.Add(Row(c, line.BaseQty, TaxMoney.Round(line.BaseQty * (c.AmountPerBaseUnit ?? 0m), precision)));
                }
            }
        }

        var taxTotal = rows.Sum(r => r.TaxAmount);
        return new TaxLineResult(index, line.PriceIncludesTax, gross, lineDiscount, billDiscountShare, billChargeShare, line.OtherCharges, taxable, percentRate, rows, taxTotal, taxable + taxTotal);
    }

    private static TaxRowResult Row(TaxRateComponent c, decimal baseAmount, decimal tax) =>
        new(c.ComponentId, c.Code, c.Kind, c.Method, c.Method == TaxCalculationMethod.Percent ? c.RatePercent : null,
            c.Method == TaxCalculationMethod.AmountPerBaseUnit ? c.AmountPerBaseUnit : null, baseAmount, tax);

    /// <summary>GST: the set of the place of supply plus the "always" lines (cess); VAT: every line (BR3).</summary>
    private static List<TaxRateComponent> ApplicableComponents(TaxRateSet set, PlaceOfSupply place)
    {
        var applicable = set.Components.Where(c => set.Regime == TaxRegime.Vat || c.Applicability == TaxApplicability.Always
            || (c.Applicability == TaxApplicability.IntraState && place == PlaceOfSupply.IntraState)
            || (c.Applicability == TaxApplicability.InterState && place == PlaceOfSupply.InterState));
        return applicable.Select((c, order) => (c, order)).OrderBy(x => x.c.SortOrder).ThenBy(x => x.order).Select(x => x.c).ToList();
    }

    /// <summary>
    /// Spreads <paramref name="total"/> over the weights: each share is <c>R(total × w / W)</c>; the rounding remainder goes to the largest-weight
    /// line, ties to the last (DQ-TP2-1), so the shares sum exactly to the total. With <paramref name="capByWeight"/> (discounts) a share never exceeds
    /// its weight and none goes negative; any overflow moves to the next largest line.
    /// </summary>
    internal static decimal[] Spread(decimal total, decimal[] weights, int precision, bool capByWeight)
    {
        var shares = new decimal[weights.Length];
        var sum = weights.Sum();
        if (total == 0 || sum <= 0) return shares;

        for (var i = 0; i < shares.Length; i++) shares[i] = TaxMoney.Round(total * weights[i] / sum, precision);
        var remainder = total - shares.Sum();
        if (remainder == 0) return shares;

        // Largest weight first; ties: the later line first.
        var order = Enumerable.Range(0, weights.Length).OrderByDescending(i => weights[i]).ThenByDescending(i => i).ToArray();
        foreach (var i in order)
        {
            if (remainder == 0) break;
            var room = remainder > 0 ? (capByWeight ? weights[i] - shares[i] : decimal.MaxValue) : shares[i];
            if (room < 0) room = 0;
            var delta = Math.Min(Math.Abs(remainder), room);
            if (remainder < 0) delta = -delta;
            shares[i] += delta;
            remainder -= delta;
        }
        return shares;
    }
}
