using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Api.Contracts;

// Tax components, profiles and assignments (docs/features/tax-profiles.md, TP-1 design › API). Enums travel as strings, dates as yyyy-MM-dd.

public record TaxComponentDto(
    Guid Id, Guid CountryId, string CountryCode, string Code, string Name, TaxComponentKind Kind, TaxCalculationMethod Method, bool IsActive,
    // True once a profile version or a document line uses it: kind and method are then fixed.
    bool IsUsed);

public record CreateTaxComponentRequest(
    [Required] Guid? CountryId,
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(60)] string Name,
    [Required] TaxComponentKind? Kind,
    [Required] TaxCalculationMethod? Method);

public record UpdateTaxComponentRequest(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(60)] string Name,
    [Required] TaxComponentKind? Kind,
    [Required] TaxCalculationMethod? Method);

public record TaxVersionComponentDto(
    Guid Id, Guid TaxComponentId, string ComponentCode, string ComponentName, TaxComponentKind Kind, TaxCalculationMethod Method,
    TaxApplicability Applicability, decimal? RatePercent, decimal? AmountPerBaseUnit, int SortOrder);

/// <summary><see cref="IsCurrent"/>: valid today. <see cref="IsUsed"/>: a document line uses it, so it is immutable (BR4).</summary>
public record TaxVersionDto(
    Guid Id, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal TotalPercent, bool IsCurrent, bool IsUsed, DateTime CreatedAt,
    IReadOnlyList<TaxVersionComponentDto> Components);

/// <summary>List item: the version valid today (<see cref="CurrentVersion"/>, null when none). <see cref="MirrorTaxId"/> is the legacy tax row kept in step (D3).</summary>
public record TaxProfileDto(
    Guid Id, Guid CountryId, string CountryCode, string CountryName, string Code, string Name, TaxRegime Regime, TaxTreatment Treatment,
    bool IsActive, Guid MirrorTaxId, bool IsAssigned, int VersionCount, TaxVersionDto? CurrentVersion);

/// <summary>Detail: every version, newest first.</summary>
public record TaxProfileDetailDto(
    Guid Id, Guid CountryId, string CountryCode, string CountryName, string Code, string Name, TaxRegime Regime, TaxTreatment Treatment,
    bool IsActive, Guid MirrorTaxId, bool IsAssigned, int VersionCount, TaxVersionDto? CurrentVersion, IReadOnlyList<TaxVersionDto> Versions);

public record TaxVersionComponentRequest(
    [Required] Guid? TaxComponentId,
    [Required] TaxApplicability? Applicability,
    decimal? RatePercent,
    decimal? AmountPerBaseUnit);

public record AddTaxVersionRequest(
    [Required] DateOnly? EffectiveFrom,
    [Required] TaxVersionComponentRequest[] Components);

public record CreateTaxProfileRequest(
    [Required] Guid? CountryId,
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(60)] string Name,
    [Required] TaxRegime? Regime,
    [Required] TaxTreatment? Treatment,
    // Optional: creates the first version in the same transaction (a profile without a version cannot be resolved).
    AddTaxVersionRequest? InitialVersion = null);

public record UpdateTaxProfileRequest(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(60)] string Name);

public record TaxSeedResultDto(bool CountrySupported, int ComponentsAdded, int ProfilesAdded, int ProfilesSkipped, bool CompanyDefaultSet);

// ---- assignments

public record TaxProfileRefDto(Guid Id, string Code, string Name, TaxRegime Regime, TaxTreatment Treatment, bool IsActive);

/// <summary><see cref="ScopeId"/> is null for the company default. <see cref="PurchaseProfile"/> null = same as sales (BR7).</summary>
public record TaxAssignmentDto(
    Guid Id, TaxScope ScopeType, Guid? ScopeId, string ScopeName, Guid CountryId,
    TaxProfileRefDto SalesProfile, TaxProfileRefDto? PurchaseProfile);

public record UpsertTaxAssignmentRequest(
    [Required] TaxScope? ScopeType,
    Guid? ScopeId,
    [Required] Guid? CountryId,
    [Required] Guid? SalesProfileId,
    Guid? PurchaseProfileId);

/// <summary>Where a scope's effective profile comes from: <see cref="Source"/> is the level ("Company", "Category", "Subcategory", "Product") and <see cref="SourceName"/> its target.</summary>
public record TaxEffectiveDto(TaxProfileRefDto SalesProfile, TaxProfileRefDto PurchaseProfile, TaxScope Source, string SourceName);

public record TaxTreeProductDto(Guid ProductId, string Sku, string Name, TaxAssignmentDto Assignment);

/// <summary><see cref="Effective"/> null when nothing applies (no company default). <see cref="OverrideCount"/> counts the product overrides under it; <see cref="Overrides"/> lists them unless <c>includeOverrides=false</c>.</summary>
public record TaxTreeSubcategoryDto(
    Guid Id, string Code, string Name, string? HsnCode, bool IsActive, int ProductCount, int OverrideCount,
    TaxAssignmentDto? Assignment, TaxEffectiveDto? Effective, IReadOnlyList<TaxTreeProductDto> Overrides);

public record TaxTreeCategoryDto(
    Guid Id, string Code, string Name, bool IsActive, int ProductCount, int OverrideCount,
    TaxAssignmentDto? Assignment, TaxEffectiveDto? Effective, IReadOnlyList<TaxTreeSubcategoryDto> Subcategories, IReadOnlyList<TaxTreeProductDto> Overrides);

/// <summary><see cref="Overrides"/> of <see cref="UncategorisedOverrideCount"/>: product overrides of products with no category.</summary>
public record TaxAssignmentTreeDto(
    Guid CountryId, string CountryCode, string CountryName, TaxAssignmentDto? CompanyDefault,
    IReadOnlyList<TaxTreeCategoryDto> Categories, int UncategorisedOverrideCount, IReadOnlyList<TaxTreeProductDto> UncategorisedOverrides);

/// <summary>What a product resolves to (BR8). <see cref="Resolved"/> false: no profile applies for the country (the other fields are null).</summary>
public record TaxResolutionDto(
    bool Resolved, Guid CountryId, TaxProfileRefDto? SalesProfile, TaxProfileRefDto? PurchaseProfile, bool PurchaseSameAsSales,
    TaxScope? Source, string? SourceName, string? HsnCode, string? HsnSource, bool HsnWarning, string? HsnWarningMessage);

public record ClearRedundantOverridesRequest([Required] Guid? CountryId);

public record ClearRedundantOverridesResultDto(int Cleared);

// ---- document preview (TP-2): POST /api/v1/tax/preview

/// <summary><see cref="Discount"/> and <see cref="OtherCharges"/> are money in the document currency; <see cref="OtherCharges"/> counts on purchases only. A zero quantity previews zero rows.</summary>
public record TaxPreviewLineRequest(
    [Required] Guid? ProductId,
    [Required] Guid? ProductUnitId,
    decimal Qty,
    decimal UnitPrice,
    decimal Discount = 0m,
    decimal? OtherCharges = null);

/// <summary>The branch is the working branch (X-Branch-Id), as on save. <see cref="PartyId"/> is the customer (null = walk-in) or, for purchases, the supplier (required).
/// <see cref="RatesIncludeTax"/> applies to purchases (default false); sales follow the product, then the company price basis. At most one of the bill discount percent and amount.</summary>
public record TaxDocumentPreviewRequest(
    [Required] TaxDirection? Direction,
    Guid? PartyId,
    [Required] DateOnly? Date,
    bool? RatesIncludeTax,
    decimal? BillDiscountPercent,
    decimal? BillDiscountAmount,
    decimal? BillCharges,
    [Required, MinLength(1)] TaxPreviewLineRequest[] Lines);

/// <summary>For a per-base-unit row <see cref="BaseAmount"/> is the base quantity, not money.</summary>
public record TaxRowDto(
    Guid? ComponentId, string Code, TaxComponentKind Kind, TaxCalculationMethod Method, decimal? RatePercent, decimal? AmountPerBaseUnit,
    decimal BaseAmount, decimal TaxAmount);

/// <summary><see cref="Gross"/> is tax-inclusive when <see cref="PriceIncludesTax"/>. <see cref="Taxable"/> includes the bill charge share and other charges. <see cref="HsnSource"/>: None, Product or Subcategory.</summary>
public record TaxPreviewLineDto(
    int Index, Guid ProductId, string ProductName, Guid ProductUnitId, string UnitCode, decimal BaseQty,
    Guid ProfileId, string ProfileCode, string ProfileName, Guid VersionId, DateOnly VersionEffectiveFrom, TaxScope Source, string SourceName,
    string? HsnCode, string HsnSource, bool PriceIncludesTax,
    decimal Gross, decimal LineDiscount, decimal BillDiscountShare, decimal BillChargeShare, decimal OtherCharges, decimal Taxable,
    decimal TaxRatePercent, decimal TaxTotal, decimal LineTotal, IReadOnlyList<TaxRowDto> Rows);

public record TaxPreviewWarningDto(string Field, string Message);

public record TaxDocumentPreviewDto(
    TaxDirection Direction, PlaceOfSupply PlaceOfSupply, TaxRegime Regime, Guid CountryId, string CurrencyCode, int Precision,
    decimal Subtotal, decimal DiscountTotal, decimal ChargesTotal, decimal TaxableTotal, decimal TaxTotal, decimal GrandTotal,
    decimal BillDiscountAmount, decimal EffectiveBillDiscountPercent,
    IReadOnlyList<TaxPreviewLineDto> Lines, IReadOnlyList<TaxPreviewWarningDto> Warnings);
