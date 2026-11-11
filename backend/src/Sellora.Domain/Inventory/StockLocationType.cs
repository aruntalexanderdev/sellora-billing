namespace Sellora.Domain.Inventory;

/// <summary>Kind of stock location (multi-company architecture §8). Persisted as a string.</summary>
public enum StockLocationType
{
    BranchStore,
    BranchWarehouse,
    CompanyWarehouse,
    Vehicle,
    Transit
}

/// <summary>Pure capability rules derived from the location type (no stored columns). Enforced by Phase 8/10 posting.</summary>
public static class StockLocationTypeRules
{
    /// <summary>BranchStore and BranchWarehouse belong to a branch; every other type is company-level (BranchId NULL).</summary>
    public static bool RequiresBranch(StockLocationType type) =>
        type is StockLocationType.BranchStore or StockLocationType.BranchWarehouse;

    /// <summary>Locations that may be sold from (POS / invoices).</summary>
    public static bool CanSell(StockLocationType type) =>
        type is StockLocationType.BranchStore or StockLocationType.Vehicle;

    /// <summary>Locations that may receive purchases directly.</summary>
    public static bool CanReceivePurchase(StockLocationType type) =>
        type is StockLocationType.BranchStore or StockLocationType.CompanyWarehouse or StockLocationType.BranchWarehouse;

    /// <summary>System-managed locations are not editable through the locations API.</summary>
    public static bool IsSystemManaged(StockLocationType type) =>
        type is StockLocationType.BranchStore or StockLocationType.Transit;
}
