using Sellora.Domain.Abstractions;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Entities;

/// <summary>
/// Warehouse subtype of a <see cref="StockLocation"/> (Phase 4 D1-D3), 1:1 with its location. Code, Name, BranchId and IsActive
/// live on the location only (no duplicated columns). <see cref="LocationType"/> (CompanyWarehouse/BranchWarehouse) is the
/// ownership kind and is immutable; together with CompanyId it forms the composite FK to the location's alternate key.
/// </summary>
public class Warehouse : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid StockLocationId { get; set; }

    /// <summary>CompanyWarehouse or BranchWarehouse; must equal the location's type (composite FK + CHECK).</summary>
    public StockLocationType LocationType { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
