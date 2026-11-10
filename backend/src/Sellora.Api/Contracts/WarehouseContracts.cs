using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

/// <summary>A warehouse. <see cref="Ownership"/> is "Company" or "Branch"; branch code/name are resolved by id (null for company warehouses).</summary>
public record WarehouseDto(
    Guid Id, Guid StockLocationId, string Code, string Name, string Ownership,
    Guid? BranchId, string? BranchCode, string? BranchName, bool IsActive, DateTimeOffset CreatedAt
);

/// <summary>A null BranchId creates a company warehouse, otherwise a warehouse of that branch.</summary>
public record CreateWarehouseRequest(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(150)] string Name,
    Guid? BranchId
);

/// <summary>Code, branch and ownership cannot be edited.</summary>
public record UpdateWarehouseRequest([Required, MaxLength(150)] string Name);
