namespace Sellora.Domain.Inventory;

/// <summary>Kind of vehicle (Phase 4 D7). Persisted as a string (varchar(20)); additions are non-breaking.</summary>
public enum VehicleType
{
    Van,
    Truck,
    Car,
    Motorcycle,
    ThreeWheeler,
    Other
}
