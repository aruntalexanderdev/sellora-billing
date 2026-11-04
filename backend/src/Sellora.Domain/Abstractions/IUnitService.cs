using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Abstractions;

public interface IUnitService
{
    /// <summary>Creates a unit. Throws <see cref="DomainValidationException"/> for business-rule violations
    /// (duplicate name/short code, decimal places, standard factor).</summary>
    Task<Unit> CreateUnitAsync(UpsertUnitInput input, CancellationToken ct = default);

    /// <summary>Updates a unit. Name and short code may always change; once the unit is in use
    /// (see <see cref="IsInUseAsync"/>) its measurement category, decimal places and standard factor are
    /// locked — resending the stored values is accepted. Returns null if the unit does not exist.</summary>
    Task<Unit?> UpdateUnitAsync(Guid id, UpsertUnitInput input, CancellationToken ct = default);

    /// <summary>Activates or deactivates a unit. Deactivating an in-use unit is rejected; activation is
    /// always allowed. Returns null if the unit does not exist.</summary>
    Task<Unit?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);

    /// <summary>True when any product (active or inactive) uses this unit as its base unit.
    /// Phase 2 extends this to <c>ProductUnits.UnitId</c> once the product unit hierarchy exists.</summary>
    Task<bool> IsInUseAsync(Guid id, CancellationToken ct = default);
}
