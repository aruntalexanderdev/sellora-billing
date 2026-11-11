using Sellora.Domain.Abstractions;
using Sellora.Domain.Inventory;

namespace Sellora.Domain.Entities;

/// <summary>
/// Vehicle subtype of a <see cref="StockLocation"/> (Phase 4 D1-D4), 1:1 with its Vehicle-type location. VehicleNumber, VehicleName
/// and IsActive are mirrored onto the location by the service (location Code/Name/IsActive). The location's BranchId stays NULL:
/// the branch assignment lives in <see cref="AssignedBranchId"/> (composite FK, navigation-less).
/// </summary>
public class Vehicle : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid StockLocationId { get; set; }

    /// <summary>Always <see cref="StockLocationType.Vehicle"/> (CHECK + composite FK to the location's alternate key).</summary>
    public StockLocationType LocationType { get; set; } = StockLocationType.Vehicle;

    /// <summary>Normalised (trim, upper, whitespace/hyphen runs to '-'), 1-20, immutable; equals the location Code. Unique per company.</summary>
    public required string VehicleNumber { get; set; }
    public required string VehicleName { get; set; }
    public VehicleType VehicleType { get; set; }

    /// <summary>Optional operating branch; reassignment is guarded by <c>IStockLocationUsage</c>.</summary>
    public Guid? AssignedBranchId { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
