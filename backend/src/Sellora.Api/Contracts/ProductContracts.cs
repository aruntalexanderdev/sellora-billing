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

public record ProductListItemDto(
    Guid Id, string Sku, string Name, string? Category,
    string BaseUnitLabel, string TaxLabel, decimal ReorderLevel, bool IsActive,
    // True when the product has no history, i.e. DELETE would succeed (same rule as the delete). Independent of the
    // caller's permissions: the client still checks Inventory.Delete.
    bool CanDelete = false
);

/// <summary><see cref="IsBaseUnitLocked"/> is true when the product has stock history or any unit row is locked.
/// <see cref="Units"/> is ordered by SortOrder (base first).</summary>
public record ProductDetailDto(
    Guid Id, string Sku, string Name, string? Category,
    Guid BaseUnitId, string BaseUnitLabel, Guid TaxId, string TaxLabel, decimal TaxRatePercent,
    decimal ReorderLevel, bool IsActive, bool IsBaseUnitLocked, IReadOnlyList<ProductUnitDto> Units
);

public record UpsertProductRequest(
    [Required, MaxLength(40)] string Sku,
    [Required, MaxLength(150)] string Name,
    [MaxLength(60)] string? Category,
    [Required] Guid? BaseUnitId,
    [Required] Guid? TaxId,
    [Range(0, double.MaxValue)] decimal ReorderLevel,
    [MinLength(1), MaxLength(ProductUnitConfigurationValidator.MaxUnits)] UpsertProductUnitRequest[] Units
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
