namespace Sellora.Domain.Inventory;

/// <summary>A selling mode as submitted from the product form. <see cref="Id"/> is null for a newly
/// added mode; when set, it must match a mode already on the product and that mode is updated in place
/// rather than replaced.</summary>
public sealed record SellingModeInputItem(
    Guid? Id,
    string Label,
    decimal ConversionFactorToBase
);

public sealed record UpsertProductInput(
    string Sku,
    string Name,
    string? Category,
    Guid BaseUnitId,
    Guid TaxId,
    decimal ReorderLevel,
    IReadOnlyList<SellingModeInputItem> SellingModes
);
