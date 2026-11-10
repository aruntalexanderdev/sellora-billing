using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

/// <summary>Who performs a write of platform-shared reference data (Units, Currencies). Phase 7 D11/Q3: company callers are refused
/// while <c>Platform:ReferenceDataEditableByCompanies</c> is false; platform callers are always allowed and their writes are audited.</summary>
public sealed record ReferenceDataActor(bool IsPlatform, Guid? UserId = null, string? CorrelationId = null)
{
    public static ReferenceDataActor Company { get; } = new(false);

    public static ReferenceDataActor Platform(Guid userId, string? correlationId) => new(true, userId, correlationId);
}

/// <summary>Raised when a company caller writes Units/Currencies while they are platform-managed. Maps to 403 <c>reference_data_platform_managed</c>.</summary>
public sealed class ReferenceDataPlatformManagedException()
    : Exception("Units and currencies are managed by the platform administrator.");

public sealed record UpsertCurrencyInput(string Code, string Name, string Symbol, int DecimalPrecision);

public enum CurrencyStatusOutcome { Changed, NotFound, IsBaseCurrency }

public interface ICurrencyService
{
    /// <summary>Creates a currency. Duplicate code throws <see cref="DomainValidationException"/> on field <c>Code</c>.</summary>
    Task<Currency> CreateAsync(UpsertCurrencyInput input, ReferenceDataActor actor, CancellationToken ct = default);

    /// <summary>Updates a currency; null when it does not exist.</summary>
    Task<Currency?> UpdateAsync(Guid id, UpsertCurrencyInput input, ReferenceDataActor actor, CancellationToken ct = default);

    /// <summary>Activates/deactivates; a currency that is any company's base currency cannot be deactivated.</summary>
    Task<CurrencyStatusOutcome> SetActiveAsync(Guid id, bool isActive, ReferenceDataActor actor, CancellationToken ct = default);
}
