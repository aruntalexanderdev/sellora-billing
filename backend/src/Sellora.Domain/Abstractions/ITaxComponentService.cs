using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Tax components (BR1, BR6): created, edited and deactivated, never deleted. Violations throw <see cref="DomainValidationException"/>
/// keyed <c>countryId</c>, <c>code</c>, <c>name</c>, <c>kind</c> or <c>method</c>. Update and status return null when the component does not exist.
/// </summary>
public interface ITaxComponentService
{
    /// <summary>Code is trimmed and upper-cased; unique per company and country (case-insensitive name too).</summary>
    Task<TaxComponent> CreateAsync(UpsertTaxComponentInput input, CancellationToken ct = default);

    /// <summary>Once a version uses the component its <c>method</c> and <c>kind</c> are fixed (400): they define how existing rates are read.
    /// Changing the method of an unused component returns a replacement row with a NEW id (the method is part of a database key).</summary>
    Task<TaxComponent?> UpdateAsync(Guid id, UpdateTaxComponentInput input, CancellationToken ct = default);

    Task<TaxComponent?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);
}
