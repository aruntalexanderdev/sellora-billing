using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

public record PricingRowDto(
    Guid ProductId, string ProductSku, string ProductName,
    Guid SellingModeId, string SellingModeLabel, decimal ConversionFactorToBase, string BaseUnitLabel,
    decimal CostPerUnit, decimal? CurrentPrice, decimal? MarginPercent, string TaxLabel
);

public record SellingPriceHistoryDto(Guid Id, decimal Price, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo);

public record SetPriceRequest(
    [Range(0, double.MaxValue)] decimal Price
);
