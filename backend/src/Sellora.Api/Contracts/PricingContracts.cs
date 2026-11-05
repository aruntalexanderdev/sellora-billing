using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

/// <summary>One pricing-grid row per active, sellable product unit. <c>SuggestedPrice</c> scales the base price (or,
/// without one, the smallest-factor priced unit's per-base price) by this unit's factor; null when nothing is priced.</summary>
public record PricingRowDto(
    Guid ProductId, string ProductSku, string ProductName,
    Guid ProductUnitId, string UnitCode, string UnitLabel,
    decimal ConversionFactorToBase, string BaseUnitLabel, short SortOrder, bool IsBaseUnit,
    decimal CostPerUnit, decimal? CurrentPrice, decimal? SuggestedPrice, decimal? MarginPercent, string TaxLabel
);

public record SellingPriceHistoryDto(Guid Id, decimal Price, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo);

public record SetPriceRequest(
    [Range(0, double.MaxValue)] decimal Price
);
