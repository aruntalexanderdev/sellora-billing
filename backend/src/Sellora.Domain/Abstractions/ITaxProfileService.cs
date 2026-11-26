using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Tax profiles and their dated versions (BR2–BR6). Every profile keeps a mirror <see cref="Tax"/> row (D3) whose rate is the total of the version
/// valid today. Violations throw <see cref="DomainValidationException"/> keyed by the request field. Methods return null when the profile does not exist.
/// </summary>
public interface ITaxProfileService
{
    /// <summary>Creates a profile with its mirror tax. The regime must match the country (India: GST, every other country: VAT).</summary>
    Task<TaxProfile> CreateAsync(CreateTaxProfileInput input, Guid? userId, CancellationToken ct = default);

    /// <summary>Code and name only.</summary>
    Task<TaxProfile?> UpdateAsync(Guid id, UpdateTaxProfileInput input, CancellationToken ct = default);

    /// <summary>
    /// Adds a version under a <c>FOR UPDATE</c> lock on the profile (BR3 set rules, BR4): an earlier date than the latest version is refused
    /// (<c>effectiveFrom</c>); the same date replaces the latest version only while no document line uses it; a later date closes the previous
    /// version the day before. A future date is allowed since TP-4 (T19 lifted). Returns the saved version with its components.
    /// </summary>
    Task<TaxProfileVersion?> AddVersionAsync(Guid profileId, AddTaxVersionInput input, Guid? userId, CancellationToken ct = default);

    /// <summary>Deactivating a profile that is still assigned is refused (<c>isActive</c>).</summary>
    Task<TaxProfile?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);

    /// <summary>Seeds the standard set of the company's country (T12); idempotent. 400 <c>taxProfile</c> when the company has no country.</summary>
    Task<TaxSeedResult> SeedStandardAsync(CancellationToken ct = default);
}
