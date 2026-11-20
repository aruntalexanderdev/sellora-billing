using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

/// <summary>Input of a country create. <see cref="IsActive"/> defaults to true.</summary>
public sealed record CreateCountryInput(string Code, string? Iso3Code, string Name, bool IsActive = true);

/// <summary>Input of a country update (activation goes through <see cref="ICountryService.SetActiveAsync"/>).</summary>
public sealed record UpdateCountryInput(string Code, string? Iso3Code, string Name);

public sealed record CreateStateInput(Guid CountryId, string Code, string Name, StateKind Kind, string? GstStateCode, bool IsActive = true);

/// <summary>Input of a state update. There is deliberately no CountryId: it is immutable (part of the alternate key the address FKs target).</summary>
public sealed record UpdateStateInput(string Code, string Name, StateKind Kind, string? GstStateCode);

public enum GeoStatusOutcome { Changed, NotFound }

/// <summary>Writes of the global Countries master. Company actors are refused (<see cref="ReferenceDataPlatformManagedException"/>) while the platform
/// manages reference data; every write by either actor kind is audited with the real user (and company, for company actors).
/// Rule violations throw <see cref="DomainValidationException"/> keyed by the request field (camelCase).</summary>
public interface ICountryService
{
    Task<Country> CreateAsync(CreateCountryInput input, ReferenceDataActor actor, CancellationToken ct = default);

    /// <summary>Null when the country does not exist. IsSystem rows cannot change <c>code</c> / <c>iso3Code</c>.</summary>
    Task<Country?> UpdateAsync(Guid id, UpdateCountryInput input, ReferenceDataActor actor, CancellationToken ct = default);

    /// <summary>Never cascades to states or to existing address links. A status already equal to the target is a no-op (no audit).</summary>
    Task<GeoStatusOutcome> SetActiveAsync(Guid id, bool isActive, ReferenceDataActor actor, CancellationToken ct = default);
}

/// <summary>Writes of the global States master (same guard and audit rules as <see cref="ICountryService"/>).</summary>
public interface IStateService
{
    Task<State> CreateAsync(CreateStateInput input, ReferenceDataActor actor, CancellationToken ct = default);

    /// <summary>Null when the state does not exist. IsSystem rows cannot change <c>code</c> / <c>gstStateCode</c>.</summary>
    Task<State?> UpdateAsync(Guid id, UpdateStateInput input, ReferenceDataActor actor, CancellationToken ct = default);

    /// <summary>Activating a state under an inactive country throws on <c>isActive</c> (BR6).</summary>
    Task<GeoStatusOutcome> SetActiveAsync(Guid id, bool isActive, ReferenceDataActor actor, CancellationToken ct = default);
}
