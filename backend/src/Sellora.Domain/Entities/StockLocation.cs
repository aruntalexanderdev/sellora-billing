using Sellora.Domain.Abstractions;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Entities;

/// <summary>
/// A place where stock can be held. Phase 3 creates BranchStore (one per branch) and Transit (one per company); Phase 4 adds
/// CompanyWarehouse/BranchWarehouse (subtype <see cref="Warehouse"/>) and Vehicle (subtype <see cref="Vehicle"/>), each 1:1 with its location.
/// Vehicle locations never carry a BranchId (assignment lives on the vehicle). No balances live here.
/// </summary>
public class StockLocation : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Required for BranchStore/BranchWarehouse, NULL for every other type (see <see cref="StockLocationTypeRules.RequiresBranch"/>).</summary>
    public Guid? BranchId { get; set; }
    public StockLocationType LocationType { get; set; }

    /// <summary>Upper-case A-Z0-9-, 1-20 chars; immutable after create. Unique per company.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
