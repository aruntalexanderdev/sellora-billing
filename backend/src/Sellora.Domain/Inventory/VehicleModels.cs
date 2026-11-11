namespace Sellora.Domain.Inventory;

public sealed record CreateVehicleInput(string VehicleNumber, string VehicleName, VehicleType VehicleType, Guid? AssignedBranchId);

/// <summary>The vehicle number is immutable. Changing <paramref name="AssignedBranchId"/> (including to/from null) is usage-guarded.</summary>
public sealed record UpdateVehicleInput(string VehicleName, VehicleType VehicleType, Guid? AssignedBranchId);
