using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Tests.TaxProfiles;

/// <summary>Shared builders for the pure tax calculator tests (TP-2).</summary>
internal static class TaxCalcBuilders
{
    public static TaxRateComponent Percent(TaxComponentKind kind, string code, decimal rate, TaxApplicability applicability, int sort = 0) =>
        new(Guid.NewGuid(), code, kind, TaxCalculationMethod.Percent, applicability, rate, null, sort);

    public static TaxRateComponent PerUnit(TaxComponentKind kind, string code, decimal amount, int sort = 0) =>
        new(Guid.NewGuid(), code, kind, TaxCalculationMethod.AmountPerBaseUnit, TaxApplicability.Always, null, amount, sort);

    private static TaxRateSet Set(TaxRegime regime, TaxTreatment treatment, params TaxRateComponent[] components) =>
        new(Guid.NewGuid(), Guid.NewGuid(), regime, treatment, components);

    /// <summary>GST profile: CGST and SGST at half the rate (intra-state), IGST at the full rate (inter-state).</summary>
    public static TaxRateSet Gst(decimal totalRate, params TaxRateComponent[] extra) =>
        Set(TaxRegime.Gst, TaxTreatment.Taxable,
        [
            Percent(TaxComponentKind.Cgst, "CGST", totalRate / 2, TaxApplicability.IntraState, 1),
            Percent(TaxComponentKind.Sgst, "SGST", totalRate / 2, TaxApplicability.IntraState, 2),
            Percent(TaxComponentKind.Igst, "IGST", totalRate, TaxApplicability.InterState, 3),
            .. extra,
        ]);

    public static TaxRateSet Vat(decimal rate) =>
        Set(TaxRegime.Vat, TaxTreatment.Taxable, Percent(TaxComponentKind.Vat, "VAT", rate, TaxApplicability.Always));

    public static TaxRateSet NonTaxable(TaxTreatment treatment) => Set(TaxRegime.Gst, treatment);

    public static TaxRateSet Custom(TaxRegime regime, params TaxRateComponent[] components) => Set(regime, TaxTreatment.Taxable, components);

    public static TaxCalcLine Line(TaxRateSet rates, decimal price, decimal qty = 1, decimal discount = 0, decimal other = 0, bool inclusive = false, decimal? baseQty = null) =>
        new(qty, baseQty ?? qty, price, discount, other, inclusive, rates);

    public static TaxCalcDocument Doc(IReadOnlyList<TaxCalcLine> lines, int precision = 2, PlaceOfSupply pos = PlaceOfSupply.IntraState,
        decimal? pct = null, decimal? amount = null, decimal charges = 0) =>
        new(precision, pos, PartyTaxStatus.Unspecified, pct, amount, charges, lines);

    public static TaxDocumentResult Calc(TaxCalcDocument doc) => TaxCalculator.Calculate(doc);

    public static TaxDocumentResult Calc(TaxCalcLine line, int precision = 2, PlaceOfSupply pos = PlaceOfSupply.IntraState) => TaxCalculator.Calculate(Doc([line], precision, pos));

    public static TaxRowResult Row(TaxLineResult line, string code) => line.Rows.Single(r => r.Code == code);

    /// <summary>Field names of the validation error raised by the calculator (empty when it does not throw).</summary>
    public static IReadOnlyList<string> ErrorFields(TaxCalcDocument doc)
    {
        try { TaxCalculator.Calculate(doc); return []; }
        catch (DomainValidationException ex) { return ex.Errors.Select(e => e.Field).ToList(); }
    }
}
