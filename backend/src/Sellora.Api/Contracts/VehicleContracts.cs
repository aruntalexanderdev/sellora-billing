using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

public record VehicleDto(
    Guid Id, Guid StockLocationId, string VehicleNumber, string VehicleName, string VehicleType,
    Guid? AssignedBranchId, string? AssignedBranchCode, string? AssignedBranchName, bool IsActive, DateTimeOffset CreatedAt
);

/// <summary>The number is normalised server-side (upper case, separators to '-') and must be 1-20 characters afterwards.</summary>
public record CreateVehicleRequest(
    [Required, MaxLength(40)] string VehicleNumber,
    [Required, MaxLength(150)] string VehicleName,
    [Required, MaxLength(20)] string VehicleType,
    Guid? AssignedBranchId
);

/// <summary>The vehicle number cannot be edited.</summary>
public record UpdateVehicleRequest(
    [Required, MaxLength(150)] string VehicleName,
    [Required, MaxLength(20)] string VehicleType,
    Guid? AssignedBranchId
);
