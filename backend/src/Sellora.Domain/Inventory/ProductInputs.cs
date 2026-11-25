using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Inventory;

/// <summary>
/// One row of a product's unit hierarchy as submitted from the product form ("1 Box contains 10 Piece").
/// <para><see cref="Id"/> is null for a new row; when set it must be a row of this product, which is then updated in
/// place (its identity is kept because lines and prices reference it).</para>
/// <para>The base row is the one with <see cref="InnerUnitId"/> = null; its <see cref="UnitId"/> must be the product's
/// base unit and its <see cref="InnerQuantity"/> null or 1. Every other row names the unit it contains by that unit's
/// <see cref="InnerUnitId"/>, resolved among the submitted active rows (at most one active row per unit), so rows that
/// have no id yet can be referenced.</para>
/// </summary>
public sealed record ProductUnitInputItem(
    Guid? Id,
    Guid UnitId,
    Guid? InnerUnitId,
    decimal? InnerQuantity,
    string? Label,
    string? Barcode,
    bool IsPurchasable,
    bool IsSellable,
    bool IsDefaultPurchaseUnit,
    bool IsDefaultSalesUnit,
    bool IsActive
);

/// <summary>
/// A product as submitted. Tax (TP-1): <see cref="HsnCode"/> (4, 6 or 8 digits, empty = inherit from the subcategory), <see cref="PriceIncludesTax"/> (null =
/// follow the company setting) and the optional product-level profiles for the company country: <see cref="SalesTaxProfileId"/> overrides what the
/// subcategory, category or company default gives (omitted = no override, a stored override is removed); <see cref="PurchaseTaxProfileId"/> needs a sales
/// override and empty means same as sales. The mirror <c>Product.TaxId</c> is written by the service.
/// </summary>
public sealed record UpsertProductInput(
    string Sku,
    string Name,
    Guid? CategoryId,
    Guid? SubcategoryId,
    Guid BaseUnitId,
    decimal ReorderLevel,
    IReadOnlyList<ProductUnitInputItem> Units,
    string? HsnCode = null,
    bool? PriceIncludesTax = null,
    Guid? SalesTaxProfileId = null,
    Guid? PurchaseTaxProfileId = null
);

/// <summary>A submitted row as it would be saved. <see cref="Index"/> is the position in the submitted list.</summary>
public sealed record ProductUnitPreviewRow(
    int Index, Guid UnitId, string UnitCode, string DisplayName, decimal ConversionFactorToBase,
    int Depth, short SortOrder, string Chain, bool IsLocked);

/// <summary>Result of validating a unit hierarchy without saving it. Rows are the submitted rows whose factor could be
/// computed, ordered by <see cref="ProductUnitPreviewRow.SortOrder"/>.</summary>
public sealed record ProductUnitPreview(
    bool IsValid,
    IReadOnlyList<ProductUnitPreviewRow> Rows,
    IReadOnlyList<FieldError> Errors,
    IReadOnlyList<FieldError> Warnings);
