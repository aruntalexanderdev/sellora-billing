namespace Sellora.Api.Contracts;

/// <summary>A stock location. Capability flags are derived from the type (StockLocationTypeRules); branch code/name are resolved by id.</summary>
public sealed record StockLocationDto(
    Guid Id,
    string Code,
    string Name,
    string LocationType,
    Guid? BranchId,
    string? BranchCode,
    string? BranchName,
    bool IsActive,
    bool CanSell,
    bool CanReceivePurchase,
    bool IsSystemManaged);
