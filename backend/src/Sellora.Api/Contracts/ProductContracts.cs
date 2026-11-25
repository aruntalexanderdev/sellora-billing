using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Api.Contracts;

/// <summary>One row of a product's unit hierarchy. <see cref="InnerUnitId"/> is the UnitId of the contained row (null
/// for the base row); <see cref="DisplayName"/> = Label ?? UnitName. Locked rows (<see cref="LockReason"/>
/// Transactions / Prices / ContainedInLockedUnit) cannot change unit, contained unit or quantity.</summary>
public record ProductUnitDto(
    Guid Id, Guid UnitId, string UnitCode, string UnitName, MeasurementCategory UnitCategory, short DecimalPlaces,
    Guid? InnerProductUnitId, Guid? InnerUnitId, decimal InnerQuantity, decimal ConversionFactorToBase, bool IsBaseUnit,
    string? Label, string DisplayName, string? Barcode, bool IsPurchasable, bool IsSellable,
    bool IsDefaultPurchaseUnit, bool IsDefaultSalesUnit, short SortOrder, int Depth, bool IsActive, bool IsLocked, string? LockReason);

/// <summary>A submitted unit row. The base row has <see cref="InnerUnitId"/> = null, <see cref="UnitId"/> = the
/// product's base unit and <see cref="InnerQuantity"/> null or 1. Other rows name their contained row by its UnitId,
/// resolved among the submitted active rows.</summary>
public record UpsertProductUnitRequest(
    Guid? Id,
    [Required] Guid? UnitId,
    Guid? InnerUnitId,
    decimal? InnerQuantity,
    [MaxLength(60)] string? Label,
    [MaxLength(50)] string? Barcode,
    bool IsPurchasable = true,
    bool IsSellable = true,
    bool IsDefaultPurchaseUnit = false,
    bool IsDefaultSalesUnit = false,
    bool IsActive = true
);

/// <summary><see cref="Category"/> is the legacy name field and equals <see cref="CategoryName"/>.</summary>
public record ProductListItemDto(
    Guid Id, string Sku, string Name, string? Category,
    Guid? CategoryId, string? CategoryName, Guid? SubcategoryId, string? SubcategoryName,
    string BaseUnitLabel, string TaxLabel, decimal ReorderLevel, bool IsActive,
    // True when the product has no history, i.e. DELETE would succeed (same rule as the delete). Independent of the
    // caller's permissions: the client still checks Inventory.Delete.
    bool CanDelete = false
);

/// <summary><see cref="IsBaseUnitLocked"/> is true when the product has stock history or any unit row is locked.
/// <see cref="Units"/> is ordered by SortOrder (base first).
/// Tax (TP-1, TP-5a): <see cref="TaxLabel"/>/<see cref="TaxRatePercent"/> are the resolved sales profile's name and the percent of its version valid today (empty/0 when none applies). <see cref="TaxId"/> is <b>deprecated</b>: the legacy mirror tax id, still written for the TP-4 build and removed in TP-5b; do not use it. The rest is what the
/// product resolves to for the company country: <see cref="SalesTaxProfile"/>/<see cref="PurchaseTaxProfile"/> with <see cref="TaxSource"/> ("Product",
/// "Subcategory", "Category", "Company") and <see cref="TaxSourceName"/>, the own override ids (null = inherited), <see cref="HsnCode"/> (own) and
/// <see cref="EffectiveHsnCode"/> (own or the subcategory's, <see cref="HsnSource"/> "Product"/"Subcategory"/"None"), and <see cref="Warnings"/>
/// (field <c>hsnCode</c>, T11). All null/empty when the company has no country or no profile applies.</summary>
public record ProductDetailDto(
    Guid Id, string Sku, string Name, string? Category,
    Guid? CategoryId, string? CategoryName, Guid? SubcategoryId, string? SubcategoryName,
    Guid BaseUnitId, string BaseUnitLabel, Guid TaxId, string TaxLabel, decimal TaxRatePercent,
    decimal ReorderLevel, bool IsActive, bool IsBaseUnitLocked, IReadOnlyList<ProductUnitDto> Units,
    string? HsnCode = null, string? EffectiveHsnCode = null, string? HsnSource = null, bool? PriceIncludesTax = null,
    TaxProfileRefDto? SalesTaxProfile = null, TaxProfileRefDto? PurchaseTaxProfile = null, TaxScope? TaxSource = null, string? TaxSourceName = null,
    Guid? SalesOverrideProfileId = null, Guid? PurchaseOverrideProfileId = null, IReadOnlyList<FieldErrorDto>? Warnings = null
);

public record UpsertProductRequest(
    [Required, MaxLength(40)] string Sku,
    [Required, MaxLength(150)] string Name,
    Guid? CategoryId,
    Guid? SubcategoryId,
    [Required] Guid? BaseUnitId,
    [Range(0, double.MaxValue)] decimal ReorderLevel,
    [MinLength(1), MaxLength(ProductUnitConfigurationValidator.MaxUnits)] UpsertProductUnitRequest[] Units,
    // Tax (TP-1). HsnCode: 4, 6 or 8 digits, empty = inherit from the subcategory. PriceIncludesTax: null = follow the company setting.
    // SalesTaxProfileId: product-level override for the company country (omitted = none, a stored override is removed);
    // PurchaseTaxProfileId: needs a sales override, omitted = same as sales. The mirror taxId is no longer sent.
    [MaxLength(8)] string? HsnCode = null,
    bool? PriceIncludesTax = null,
    Guid? SalesTaxProfileId = null,
    Guid? PurchaseTaxProfileId = null
);

/// <summary><c>POST api/v1/products/unit-preview</c>. With <see cref="ProductId"/>, the stored rows and their lock
/// state are applied exactly as on save.</summary>
public record UnitPreviewRequest(Guid? ProductId, [Required] Guid? BaseUnitId,
    [Required, MaxLength(ProductUnitConfigurationValidator.MaxUnits)] UpsertProductUnitRequest[] Units);

public record FieldErrorDto(string Field, string Message);

/// <summary><see cref="Index"/> is the row's position in the submitted <c>Units</c> array.</summary>
public record UnitPreviewRowDto(int Index, Guid UnitId, string UnitCode, string DisplayName, decimal ConversionFactorToBase,
    int Depth, short SortOrder, string Chain, bool IsLocked);

/// <summary>Returned with 200 even when the configuration is invalid (<see cref="IsValid"/> false).</summary>
public record UnitPreviewResponse(bool IsValid, IReadOnlyList<UnitPreviewRowDto> Rows,
    IReadOnlyList<FieldErrorDto> Errors, IReadOnlyList<FieldErrorDto> Warnings);
