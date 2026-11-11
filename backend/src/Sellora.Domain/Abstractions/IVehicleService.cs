using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Abstractions;

/// <summary>Vehicle writes of the current company (Phase 4). Each write keeps the 1:1 StockLocation in the same SaveChanges.
/// Validation failures throw <see cref="DomainValidationException"/> (fields <c>VehicleNumber</c>, <c>VehicleName</c>,
/// <c>VehicleType</c>, <c>AssignedBranchId</c>, <c>IsActive</c>). A null result means not found.</summary>
public interface IVehicleService
{
    Task<Vehicle> CreateAsync(CreateVehicleInput input, Guid userId, CancellationToken ct = default);
    Task<Vehicle?> UpdateAsync(Guid vehicleId, UpdateVehicleInput input, Guid userId, CancellationToken ct = default);
    Task<Vehicle?> SetStatusAsync(Guid vehicleId, bool isActive, string? reason, Guid userId, CancellationToken ct = default);
}
