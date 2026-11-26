using Sellora.Domain.Entities;

namespace Sellora.Domain.Taxes;

// Inputs and results of the pure tax calculator (docs/features/tax-profiles.md, TP-2 design). No EF, no I/O.

/// <summary>One component line of the version valid on the document date (BR3).</summary>
public sealed record TaxRateComponent(
    Guid ComponentId, string Code, TaxComponentKind Kind, TaxCalculationMethod Method, TaxApplicability Applicability,
    decimal? RatePercent, decimal? AmountPerBaseUnit, int SortOrder);

/// <summary>The tax setup of one line: profile, version, regime, treatment and the components of that version. Non-taxable treatments carry no components.</summary>
public sealed record TaxRateSet(Guid ProfileId, Guid VersionId, TaxRegime Regime, TaxTreatment Treatment, IReadOnlyList<TaxRateComponent> Components)
{
    /// <summary>The rate set of a version the resolver returned (<see cref="Abstractions.ITaxResolver.ResolveVersionAsync"/>).</summary>
    public static TaxRateSet From(TaxVersionResolution resolution) => new(
        resolution.Profile.Id, resolution.Version.Id, resolution.Profile.Regime, resolution.Profile.Treatment,
        resolution.Version.Components
            .OrderBy(c => c.SortOrder)
            .Select(c => new TaxRateComponent(
                c.TaxComponentId, c.TaxComponent.Code, c.TaxComponent.Kind, c.Method, c.Applicability, c.RatePercent, c.AmountPerBaseUnit, c.SortOrder))
            .ToList());
}

/// <summary>One document line. <see cref="BaseQty"/> feeds per-base-unit components; <see cref="OtherCharges"/> is the purchase line charge (taxable, BR16).
/// <see cref="UnitPrice"/> is in the line's price basis (<see cref="PriceIncludesTax"/>).</summary>
public sealed record TaxCalcLine(
    decimal Qty, decimal BaseQty, decimal UnitPrice, decimal LineDiscount, decimal OtherCharges, bool PriceIncludesTax, TaxRateSet Rates);

/// <summary>A document. <see cref="Precision"/> is the currency's decimals. At most one of the bill discount percent and amount may be set (BR19).</summary>
public sealed record TaxCalcDocument(
    int Precision, PlaceOfSupply PlaceOfSupply, PartyTaxStatus PartyStatus, decimal? BillDiscountPercent, decimal? BillDiscountAmount,
    decimal BillCharges, IReadOnlyList<TaxCalcLine> Lines);

/// <summary>A tax row of a line (BR23). For per-base-unit components <see cref="BaseAmount"/> is the base quantity, not money (DQ-TP2-6).
/// Non-taxable treatments give one row with a null component id, the treatment name upper-case as code and a zero amount.</summary>
public sealed record TaxRowResult(
    Guid? ComponentId, string Code, TaxComponentKind Kind, TaxCalculationMethod Method, decimal? RatePercent, decimal? AmountPerBaseUnit,
    decimal BaseAmount, decimal TaxAmount);

/// <summary><see cref="Taxable"/> is the line's taxable value (goods plus charges); <see cref="TaxRatePercent"/> the sum of the applied percent rates;
/// <see cref="LineTotal"/> = Taxable + TaxTotal. For an inclusive line <see cref="Gross"/> is the tax-inclusive amount (BR21).</summary>
public sealed record TaxLineResult(
    int Index, bool PriceIncludesTax, decimal Gross, decimal LineDiscount, decimal BillDiscountShare, decimal BillChargeShare, decimal OtherCharges,
    decimal Taxable, decimal TaxRatePercent, IReadOnlyList<TaxRowResult> Rows, decimal TaxTotal, decimal LineTotal);

/// <summary>Header totals are sums of the lines (BR20): GrandTotal = TaxableTotal + TaxTotal = Σ LineTotal.
/// <see cref="EffectiveBillDiscountPercent"/> is the bill discount over the sum of the line weights (Gross − LineDiscount), for the T15 cap.</summary>
public sealed record TaxDocumentResult(
    IReadOnlyList<TaxLineResult> Lines, decimal Subtotal, decimal DiscountTotal, decimal ChargesTotal, decimal TaxableTotal, decimal TaxTotal,
    decimal GrandTotal, decimal BillDiscountAmount, decimal EffectiveBillDiscountPercent);
