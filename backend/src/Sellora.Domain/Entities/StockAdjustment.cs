namespace Sellora.Domain.Entities;

/// <summary>
/// A manual correction to stock, always traceable to a StockMovement (docx §9.4, §17). Decreases consume
/// existing PurchaseBatches oldest-first, exactly like a future sale would, so batch remaining quantities
/// never drift out of sync with StockItem.QuantityOnHand. Increases (e.g. Opening Stock) create a new
/// batch of their own so that stock they add is just as FIFO-traceable as purchased stock.
/// </summary>
public class StockAdjustment
{
    public Guid Id { get; set; }
    public required string AdjustmentNo { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public AdjustmentType Type { get; set; }
    public AdjustmentDirection Direction { get; set; }

    /// <summary>Quantity as the user entered it, in <see cref="ProductUnit"/> (snapshot; never recomputed).</summary>
    public decimal EnteredQuantity { get; set; }

    /// <summary>The product unit the quantity was entered in. The FK is (ProductId, ProductUnitId) -> ProductUnits
    /// (ProductId, Id), so the database guarantees the unit belongs to the same product. A referenced row is
    /// immutable (deactivate-and-replace).</summary>
    public Guid ProductUnitId { get; set; }
    public ProductUnit ProductUnit { get; set; } = null!;

    /// <summary>Snapshot of <see cref="Entities.ProductUnit.UnitId"/> at posting time.</summary>
    public Guid UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>Snapshot of the unit's ShortCode at posting time, for history display.</summary>
    public required string UnitCode { get; set; }

    /// <summary>Snapshot of <see cref="Entities.ProductUnit.ConversionFactorToBase"/> at posting time.
    /// <see cref="EnteredQuantity"/> x this factor = <see cref="QuantityBaseUnits"/> exactly (DB CHECK).</summary>
    public decimal ConversionFactorToBase { get; set; }

    /// <summary>Positive quantity in base units; the sign comes from <see cref="Direction"/>.</summary>
    public decimal QuantityBaseUnits { get; set; }

    /// <summary>Cost per entered unit as typed (Increase only; null for decreases). Null exactly when
    /// <see cref="UnitCost"/> is null (DB CHECK).</summary>
    public decimal? EnteredUnitCost { get; set; }

    /// <summary>Required for Increase adjustments — the cost basis, per BASE unit, for the batch this creates
    /// (equals the batch UnitCost).</summary>
    public decimal? UnitCost { get; set; }

    public string? Reason { get; set; }

    public decimal BeforeQty { get; set; }
    public decimal AfterQty { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public PurchaseBatch? Batch { get; set; }
}
