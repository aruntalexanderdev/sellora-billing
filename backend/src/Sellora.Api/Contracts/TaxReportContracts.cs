using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

// TP-5a tax reports (docs/features/tax-profiles.md, "TP-5 design", T32/T33). GET api/v1/reports/tax/{gst-summary|hsn-summary|vat-summary}, permission Reports.Tax.
// Common rules: header document dates (returns by return date), cancelled documents excluded, base-currency amounts, taxable value counted once per line,
// legacy rows (history, never split) in their own bucket by rate, zero-treatment rows in their treatment bucket. Amounts are exact sums (not re-rounded).

/// <summary>GST report sections (GSTR-1 outward, GSTR-3B 3.1 and 4(A)(5) inward). Returns are listed on their own and subtracted in the net blocks.</summary>
public enum GstReportSection
{
    Outward,
    OutwardReturn,
    Inward,
    InwardReturn,
}

public enum VatReportSection
{
    Output,
    OutputReturn,
    Input,
    InputReturn,
}

/// <summary>VAT treatment bucket. Nil-rated lines count as exempt; non-taxable as out of scope; history rows as legacy.</summary>
public enum VatCategory
{
    StandardRated,
    ZeroRated,
    Exempt,
    OutOfScope,
    Legacy,
}

public enum HsnDirection
{
    Sales,
    Purchases,
}

/// <summary>A caveat the page must show as a banner. Codes: <c>NoPartyGstin</c> (all outward supplies are B2C until customers carry a GSTIN), <c>InputTaxAsInvoiced</c> (input tax is as invoiced,
/// not eligible ITC), <c>NoPartyTaxRegistration</c> (VAT: no B2B/B2C split), <c>HsnNotRecorded</c> (some lines predate HSN snapshots).</summary>
public record TaxReportLimitationDto(string Code, string Message);

/// <summary>Amounts of a block. <see cref="TotalTax"/> = Cgst + SgstUtgst + Igst + Cess + LegacyTax. <see cref="LegacyTax"/> is the unsplit tax of history lines.</summary>
public record TaxReportAmountsDto(decimal TaxableValue, decimal Cgst, decimal SgstUtgst, decimal Igst, decimal Cess, decimal LegacyTax, decimal TotalTax);

/// <summary>One group of the GST summary: section x seller GSTIN (the document branch's, else the company's) x place-of-supply state x supply type x treatment x rate.
/// <see cref="RatePercent"/> is the sum of the CGST + SGST/UTGST + IGST rates (the legacy rate for history). <see cref="Treatment"/> is null and <see cref="IsLegacy"/> true for history rows
/// (<see cref="SupplyType"/> and the state are then usually null too). <see cref="DocumentCount"/> is the number of documents contributing to this row: a document with lines in several rows is counted once in each, so row counts do not add up to the section's document count (use <see cref="GstSectionTotalDto.DocumentCount"/> for that).</summary>
public record GstSummaryRowDto(
    GstReportSection Section, string? Gstin, Guid? PlaceOfSupplyStateId, string? PlaceOfSupplyStateName, string? PlaceOfSupplyStateCode,
    PlaceOfSupply? SupplyType, TaxTreatment? Treatment, decimal RatePercent, bool IsLegacy,
    decimal TaxableValue, decimal Cgst, decimal SgstUtgst, decimal Igst, decimal Cess, decimal LegacyTax, int DocumentCount);

/// <summary>Total of one section (documents counted per distinct document of the GSTIN).</summary>
public record GstSectionTotalDto(GstReportSection Section, TaxReportAmountsDto Amounts, int DocumentCount);

/// <summary>Per seller GSTIN (null = none recorded): the four sections and the nets Outward - OutwardReturn and Inward - InwardReturn.</summary>
public record GstGstinBlockDto(string? Gstin, IReadOnlyList<GstSectionTotalDto> Sections, TaxReportAmountsDto OutwardNet, TaxReportAmountsDto InwardNet);

public record GstSummaryDto(
    DateOnly DateFrom, DateOnly DateTo, Guid? BranchId, string CurrencyCode,
    IReadOnlyList<GstSummaryRowDto> Rows, IReadOnlyList<GstGstinBlockDto> Blocks, IReadOnlyList<TaxReportLimitationDto> Limitations);

/// <summary>One HSN group: HSN/SAC (null = not recorded, history lines) x base unit x rate. <see cref="Uqc"/> is the unit's GST UQC, else its code. <see cref="BaseQty"/> is in the base unit,
/// net of returns (the one place quantities are summed across products: within one HSN and one base unit, F5). Amounts are net of returns too; <see cref="TotalValue"/> = taxable + tax.</summary>
public record HsnSummaryRowDto(
    string? HsnCode, Guid UnitId, string UnitCode, string Uqc, int DecimalPlaces, decimal RatePercent, decimal BaseQty,
    decimal TaxableValue, decimal Cgst, decimal SgstUtgst, decimal Igst, decimal Cess, decimal LegacyTax, decimal TotalValue);

public record HsnTotalsDto(decimal TaxableValue, decimal TotalTax, decimal TotalValue);

public record HsnSummaryDto(
    DateOnly DateFrom, DateOnly DateTo, Guid? BranchId, HsnDirection Direction, string CurrencyCode,
    IReadOnlyList<HsnSummaryRowDto> Rows, HsnTotalsDto Totals, IReadOnlyList<TaxReportLimitationDto> Limitations);

/// <summary>One VAT group: section x category x region (the document branch's state, else the company's: the UAE emirate box) x rate. History rows are <see cref="VatCategory.Legacy"/> by rate.</summary>
public record VatSummaryRowDto(
    VatReportSection Section, VatCategory Category, Guid? RegionStateId, string? RegionName, decimal RatePercent,
    decimal TaxableValue, decimal VatAmount, int DocumentCount);

public record VatSectionTotalDto(VatReportSection Section, decimal TaxableValue, decimal VatAmount, int DocumentCount);

public record VatNetDto(decimal TaxableValue, decimal VatAmount);

/// <summary><see cref="OutputVat"/> = Output - OutputReturn (sales less returns); <see cref="InputVat"/> = Input - InputReturn (purchases less returns).</summary>
public record VatSummaryDto(
    DateOnly DateFrom, DateOnly DateTo, Guid? BranchId, string CurrencyCode,
    IReadOnlyList<VatSummaryRowDto> Rows, IReadOnlyList<VatSectionTotalDto> Sections, VatNetDto OutputVat, VatNetDto InputVat,
    IReadOnlyList<TaxReportLimitationDto> Limitations);
