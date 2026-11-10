using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Abstractions;

/// <remarks>Writes take an optional <see cref="ReferenceDataActor"/> (null = company caller): company callers throw
/// <see cref="ReferenceDataPlatformManagedException"/> while <c>Platform:ReferenceDataEditableByCompanies</c> is false; platform callers are audited.</remarks>
public interface IUnitService
{
    /// <summary>Creates a unit. Throws <see cref="DomainValidationException"/> for business-rule violations
    /// (duplicate name/short code, decimal places, standard factor).</summary>
    Task<Unit> CreateUnitAsync(UpsertUnitInput input, CancellationToken ct = default, ReferenceDataActor? actor = null);

    /// <summary>Updates a unit. Name and short code may always change; once the unit is in use
    /// (see <see cref="IsInUseAsync"/>) its measurement category, decimal places and standard factor are
    /// locked — resending the stored values is accepted. Returns null if the unit does not exist.</summary>
    Task<Unit?> UpdateUnitAsync(Guid id, UpsertUnitInput input, CancellationToken ct = default, ReferenceDataActor? actor = null);

    /// <summary>Activates or deactivates a unit. Deactivating a unit used by an active product row
    /// (see <see cref="IsUsedByActiveProductAsync"/>) is rejected; activation is always allowed. Returns null if the
    /// unit does not exist.</summary>
    Task<Unit?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default, ReferenceDataActor? actor = null);

    /// <summary>True when any product (active or inactive) uses this unit as its base unit, or any product unit row
    /// (active or inactive) uses it. Inactive rows count because history lines are interpreted through the unit, so
    /// its category, decimal places and standard factor are frozen.</summary>
    Task<bool> IsInUseAsync(Guid id, CancellationToken ct = default);

    /// <summary>True when any product uses this unit as its base unit, or any <em>active</em> product unit row uses it.
    /// Guards deactivation: a unit only referenced by deactivated product unit rows may be deactivated.</summary>
    Task<bool> IsUsedByActiveProductAsync(Guid id, CancellationToken ct = default);
}
