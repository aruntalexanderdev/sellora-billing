using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

public record SellingModeDto(Guid Id, string Label, decimal ConversionFactorToBase, bool IsActive);

public record SellingModeInput(
    [Required, MaxLength(60)] string Label,
    [Range(0.001, double.MaxValue)] decimal ConversionFactorToBase
);

public record ProductListItemDto(
    Guid Id, string Sku, string Name, string? Category,
    string BaseUnitLabel, string TaxLabel, decimal ReorderLevel, bool IsActive
);

public record ProductDetailDto(
    Guid Id, string Sku, string Name, string? Category,
    Guid BaseUnitId, string BaseUnitLabel, Guid TaxId, string TaxLabel, decimal TaxRatePercent,
    decimal ReorderLevel, bool IsActive, IReadOnlyList<SellingModeDto> SellingModes
);

public record UpsertProductRequest(
    [Required, MaxLength(40)] string Sku,
    [Required, MaxLength(150)] string Name,
    [MaxLength(60)] string? Category,
    [Required] Guid? BaseUnitId,
    [Required] Guid? TaxId,
    [Range(0, double.MaxValue)] decimal ReorderLevel,
    [MinLength(1)] SellingModeInput[] SellingModes
);
