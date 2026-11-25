using Sellora.Domain.Entities;

namespace Sellora.Domain.Taxes;

// Resolver results and the preview models of TP-2 (docs/features/tax-profiles.md, TP-2 design). Field names in validation errors are the
// request names: lines[i].productId, lines[i].qty, partyId, billDiscount, ...

/// <summary>Where a document's supply happens (BR12). <see cref="Place"/> is <see cref="PlaceOfSupply.NotApplicable"/> for VAT.
/// <see cref="Warnings"/> carries the soft findings (a supplier with no state is treated as intra-state). <see cref="OwnStateId"/> is the branch's (else the company's) state and
/// <see cref="PartyStateId"/> the customer's or supplier's; both are null for VAT, <see cref="PartyStateId"/> also when the party has none (TP-3 snapshots them).</summary>
public sealed record PlaceOfSupplyResolution(
    PlaceOfSupply Place, TaxRegime Regime, Guid CountryId, string CountryCode, IReadOnlyList<string> Warnings,
    Guid? OwnStateId = null, Guid? PartyStateId = null);

/// <summary>The rate set one product gets on a document date (BR5, BR7, BR8), with where it came from.</summary>
public sealed record ResolvedLineRates(
    Guid ProductId, TaxRateSet Rates, string ProfileCode, string ProfileName, DateOnly VersionEffectiveFrom,
    TaxScope Source, string SourceName, string? HsnCode, TaxHsnSource HsnSource, bool HsnWarning, string? HsnWarningMessage,
    Guid MirrorTaxId = default);

/// <summary>One line of a preview. <see cref="Discount"/> and <see cref="OtherCharges"/> are money in the document currency; <see cref="OtherCharges"/> only counts on purchases.</summary>
public sealed record TaxPreviewLineInput(Guid ProductId, Guid ProductUnitId, decimal Qty, decimal UnitPrice, decimal Discount, decimal OtherCharges = 0m);

/// <summary>A document as it would be saved. The branch is the working branch (DQ-TP2-5). <see cref="RatesIncludeTax"/> applies to purchases only
/// (BR15, default exclusive); sales follow the product, then the company price basis (BR14).</summary>
public sealed record TaxDocumentPreviewInput(
    TaxDirection Direction, Guid? PartyId, DateOnly Date, bool? RatesIncludeTax, decimal? BillDiscountPercent, decimal? BillDiscountAmount,
    decimal? BillCharges, IReadOnlyList<TaxPreviewLineInput> Lines);

public sealed record TaxPreviewWarning(string Field, string Message);

public sealed record TaxPreviewLine(
    int Index, Guid ProductId, string ProductName, Guid ProductUnitId, string UnitCode, decimal BaseQty,
    Guid ProfileId, string ProfileCode, string ProfileName, Guid VersionId, DateOnly VersionEffectiveFrom,
    TaxScope Source, string SourceName, string? HsnCode, TaxHsnSource HsnSource, TaxLineResult Calc);

public sealed record TaxDocumentPreview(
    TaxDirection Direction, PlaceOfSupply PlaceOfSupply, TaxRegime Regime, Guid CountryId, string CurrencyCode, int Precision,
    TaxDocumentResult Totals, IReadOnlyList<TaxPreviewLine> Lines, IReadOnlyList<TaxPreviewWarning> Warnings);
