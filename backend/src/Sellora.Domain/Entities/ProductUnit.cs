using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A unit a product is stocked, bought or sold in, arranged as a packaging hierarchy over the product's base unit
/// ("1 Box contains 10 Piece"). Formerly the "selling mode" table; Ids of migrated rows are preserved because
/// purchase lines, sales lines and prices reference them.
/// <para>A row referenced by history (lines or prices) is immutable in <see cref="UnitId"/>,
/// <see cref="InnerProductUnitId"/>, <see cref="InnerQuantity"/> and <see cref="ConversionFactorToBase"/>:
/// deactivate it and add a new row instead.</para>
/// </summary>
public class ProductUnit : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The global unit (Units master) this row represents, e.g. Box. For the base row it equals
    /// <see cref="Entities.Product.BaseUnitId"/>.</summary>
    public Guid UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>The unit of the same product this one contains; null only for the base row. Always set the scalar,
    /// never assign <see cref="InnerProductUnit"/> = null: the FK is (ProductId, InnerProductUnitId) and EF could
    /// otherwise try to null the shared ProductId.</summary>
    public Guid? InnerProductUnitId { get; set; }
    public ProductUnit? InnerProductUnit { get; set; }

    /// <summary>How many inner units one of this unit contains (1 for the base row).</summary>
    public decimal InnerQuantity { get; set; } = 1m;

    /// <summary>Materialised factor to the base unit: InnerQuantity x factor(inner); 1 for the base row. Set only
    /// by the product service when the hierarchy is saved; transactions snapshot it and never recompute it.</summary>
    public decimal ConversionFactorToBase { get; set; }

    public bool IsBaseUnit { get; set; }

    /// <summary>Optional display override, e.g. "10 kg bag". Display name = Label ?? Unit.Name.</summary>
    public string? Label { get; set; }

    public string? Barcode { get; set; }

    public bool IsPurchasable { get; set; } = true;
    public bool IsSellable { get; set; } = true;
    public bool IsDefaultPurchaseUnit { get; set; }
    public bool IsDefaultSalesUnit { get; set; }

    public short SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}
