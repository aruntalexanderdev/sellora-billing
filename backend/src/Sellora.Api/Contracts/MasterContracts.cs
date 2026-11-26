using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <summary><see cref="IsInUse"/>: a product uses the unit as base unit or any product unit row (active or inactive) uses
/// it; category, decimal places and standard factor are then frozen. <see cref="CanDeactivate"/>: no product uses it as
/// base unit and no <em>active</em> product unit row uses it.</summary>
public record UnitDto(Guid Id, string Name, string ShortCode, MeasurementCategory MeasurementCategory, short DecimalPlaces, decimal? StandardFactor, bool IsActive, bool IsInUse, bool CanDeactivate);

public record UpsertUnitRequest(
    [Required, MaxLength(60)] string Name,
    [Required, MaxLength(10)] string ShortCode,
    [Required] MeasurementCategory? MeasurementCategory,
    [Range(0, 3)] short DecimalPlaces,
    [Range(typeof(decimal), "0.0000000001", "1000000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal? StandardFactor
);

public record TaxDto(Guid Id, string Name, decimal RatePercent, bool IsActive);

public record UpsertTaxRequest(
    [Required, MaxLength(60)] string Name,
    [Range(0, 100)] decimal RatePercent
);

public record BusinessSettingsDto(
    Guid Id, string BusinessName, string? AddressLine, string? City, string? State,
    string? Gstin, string? Phone, string? Email, string InvoicePrefix,
    Guid BaseCurrencyId, string BaseCurrencyCode, string BaseCurrencySymbol, Guid CompanyId,
    ReceiptPostingMode ReceiptPostingMode = ReceiptPostingMode.Direct, bool ReceiptSeparationOfDuties = false,
    Guid? CountryId = null, string? CountryName = null, Guid? StateId = null, string? StateName = null,
    // Tax profiles (TP-1): whether stored selling prices include tax (T2, new companies true, existing false) and the cap of a whole-bill discount in percent (T15, null = none).
    bool SellingPricesIncludeTax = true, decimal? MaxBillDiscountPercent = null,
    // TP-5a (T11): documents dated on/after this date are refused for GST-taxable products without an HSN/SAC (null = warning only), and product saves are refused once it is set.
    // ProductsWithoutHsnCount is read-only: active products under a GST-taxable profile with no HSN of their own or their subcategory's (null when the company has no country).
    DateOnly? HsnRequiredFrom = null, int? ProductsWithoutHsnCount = null,
    bool TransferApprovalSeparationOfDuties = true,
    decimal? TransferRequesterApprovalThreshold = null // FI-D48: null = two-level approval off
);

/// <summary>The settings PUT is a full replace for the nullable fields marked below: an omitted <c>maxBillDiscountPercent</c> clears the cap and an omitted
/// <c>countryId</c> clears the country link (T25, D17). The form always posts the current values. <c>countryId</c> may change only while the company has no
/// products and no sales or purchase documents (400 on <c>countryId</c>, T24; the new country's standard tax profiles are then seeded); while it has them,
/// an omitted <c>countryId</c> leaves country and state unchanged instead of clearing them.</summary>
public record UpdateBusinessSettingsRequest(
    [Required, MaxLength(150)] string BusinessName,
    [MaxLength(300)] string? AddressLine,
    [MaxLength(80)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(20)] string? Gstin,
    [MaxLength(30)] string? Phone,
    [EmailAddress, MaxLength(200)] string? Email,
    [Required, MaxLength(10)] string InvoicePrefix,
    [Required] Guid? BaseCurrencyId,
    ReceiptPostingMode? ReceiptPostingMode = null, // Phase 06 (FI-D39): omitted = unchanged
    bool? ReceiptSeparationOfDuties = null,
    Guid? CountryId = null, // country-state-masters D17: null clears the link; the form always posts the current value
    Guid? StateId = null,
    bool? SellingPricesIncludeTax = null, // TP-1: omitted = unchanged
    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal? MaxBillDiscountPercent = null, // TP-1 (T15): null clears the cap (no limit); like CountryId, the form always posts the current value
    DateOnly? HsnRequiredFrom = null, // TP-5a (T11): omitted/null = unchanged (an old client that does not post it must not lift the control)
    bool? ClearHsnRequiredFrom = null, // TP-5a: true removes the date (warning only again); wins over HsnRequiredFrom
    bool? TransferApprovalSeparationOfDuties = null, // omitted = unchanged; pending requests keep their submit-time snapshot
    // FI-D48: omitted/null = unchanged (an old client must not switch the control off); ClearTransferRequesterApprovalThreshold = true turns it off.
    [Range(typeof(decimal), "0", "9999999999999999.99", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal? TransferRequesterApprovalThreshold = null,
    bool? ClearTransferRequesterApprovalThreshold = null
);

public record SetActiveRequest(bool IsActive);

/// <summary>Status change of a branch / warehouse / vehicle (Phase 11a D14). <c>Reason</c> is required (trimmed, 1-500 chars); validated by the service so the 400 names field <c>reason</c>.</summary>
public record SetStatusRequest(bool IsActive, string? Reason);
