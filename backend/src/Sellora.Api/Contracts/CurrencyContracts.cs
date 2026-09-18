using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

public record CurrencyDto(Guid Id, string Code, string Name, string Symbol, int DecimalPrecision, bool IsActive, bool IsBase);

public record UpsertCurrencyRequest(
    [Required, RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "Currency code must be 3 letters (ISO 4217).")] string Code,
    [Required, MaxLength(80)] string Name,
    [Required, MaxLength(8)] string Symbol,
    [Range(0, 6)] int DecimalPrecision
);

public record ExchangeRateDto(
    Guid Id, Guid CurrencyId, string CurrencyCode, string CurrencySymbol,
    decimal RateToBase, DateOnly EffectiveDate, DateTimeOffset CreatedAt
);

public record UpsertExchangeRateRequest(
    [Required] Guid? CurrencyId,
    [Range(0.000001, 999999999)] decimal RateToBase,
    [Required] DateOnly EffectiveDate
);

public record ResolvedExchangeRateDto(Guid CurrencyId, DateOnly AsOfDate, decimal RateToBase, string BaseCurrencyCode, string BaseCurrencySymbol);
