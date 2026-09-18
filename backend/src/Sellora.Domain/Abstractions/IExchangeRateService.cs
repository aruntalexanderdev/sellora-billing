namespace Sellora.Domain.Abstractions;

public interface IExchangeRateService
{
    /// <summary>Returns how many units of the business base currency equal 1 unit of the given currency on
    /// <paramref name="asOfDate"/>: 1 for the base currency itself, otherwise the rate from the latest
    /// exchange-rate row effective on or before that date. Throws <see cref="DomainValidationException"/>
    /// if the currency has no rate effective by that date.</summary>
    Task<decimal> ResolveRateToBaseAsync(Guid currencyId, DateOnly asOfDate, CancellationToken ct = default);
}
