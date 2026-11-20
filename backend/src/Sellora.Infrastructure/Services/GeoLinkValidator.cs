using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Shared validation of a Country/State link on an address owner (Branch, BusinessSettings, Customer, Supplier, company onboarding).
/// Mirrors the supplier currency rule: inactive values are rejected only when they differ from the stored link (BR5/AC6);
/// the stored values are the "current" arguments (null on create). Throws <see cref="DomainValidationException"/> on <c>countryId</c> / <c>stateId</c>.
/// </summary>
public static class GeoLinkValidator
{
    public static async Task ValidateAsync(
        SelloraDbContext db, Guid? countryId, Guid? stateId, Guid? currentCountryId, Guid? currentStateId, CancellationToken ct)
    {
        if (stateId is null && countryId is null) return;
        if (countryId is null)
        {
            throw new DomainValidationException("countryId", "Select a country when a state is chosen.");
        }

        var country = await db.Countries.AsNoTracking().SingleOrDefaultAsync(c => c.Id == countryId, ct)
            ?? throw new DomainValidationException("countryId", "Select a valid country.");
        if (!country.IsActive && country.Id != currentCountryId)
        {
            throw new DomainValidationException("countryId", "This country is inactive.");
        }
        if (stateId is null) return;

        var state = await db.States.AsNoTracking().SingleOrDefaultAsync(s => s.Id == stateId, ct)
            ?? throw new DomainValidationException("stateId", "Select a valid state.");
        if (state.CountryId != country.Id)
        {
            throw new DomainValidationException("stateId", "The state does not belong to the selected country.");
        }
        var unchanged = state.Id == currentStateId;
        if (!unchanged && !state.IsActive)
        {
            throw new DomainValidationException("stateId", "This state is inactive.");
        }
        if (!unchanged && !country.IsActive)
        {
            throw new DomainValidationException("stateId", "States of an inactive country cannot be newly selected.");
        }
    }
}
