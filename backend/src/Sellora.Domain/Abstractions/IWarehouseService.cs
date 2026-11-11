using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Abstractions;

/// <summary>Warehouse writes of the current company (Phase 4). Each write keeps the 1:1 StockLocation in the same SaveChanges.
/// Validation failures throw <see cref="DomainValidationException"/> (fields <c>Code</c>, <c>Name</c>, <c>BranchId</c>, <c>IsActive</c>).
/// Branch-access checks live in the controller. A null result means not found.</summary>
public interface IWarehouseService
{
    Task<Warehouse> CreateAsync(CreateWarehouseInput input, Guid userId, CancellationToken ct = default);
    Task<Warehouse?> UpdateAsync(Guid warehouseId, UpdateWarehouseInput input, Guid userId, CancellationToken ct = default);
    Task<Warehouse?> SetStatusAsync(Guid warehouseId, bool isActive, string? reason, Guid userId, CancellationToken ct = default);
}
