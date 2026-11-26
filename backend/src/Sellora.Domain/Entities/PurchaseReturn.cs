using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// Goods returned to a supplier from a purchase. Completing one takes the goods out of the batches they came in
/// on (only stock not yet sold can go back), and credits the purchase: what is owed to the supplier falls by the
/// value of the returned goods, so any amount already paid above that becomes refundable by the supplier.
/// Values are in the business base currency; <see cref="OriginalTotal"/> is the same credit in the purchase's
/// own currency.
/// </summary>
public class PurchaseReturn : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    /// <summary>The branch that owns this document (Phase 2, same-company composite FK). Never taken from a client body.</summary>
    public Guid BranchId { get; set; }

    /// <summary>The one stock location this document moves stock at (receiving / source / issuing / restock / adjusted). Phase 8:
    /// legacy rows map to the company's default branch store; same-company composite FK to StockLocations. Never taken from a client body unchecked.</summary>
    public Guid StockLocationId { get; set; }

    public Guid Id { get; set; }
    public required string ReturnNo { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public DateOnly ReturnDate { get; set; }
    public string? Reason { get; set; }

    public decimal TaxableTotal { get; set; }
    public decimal TaxTotal { get; set; }

    /// <summary>The credit received from the supplier, in the base currency.</summary>
    public decimal Total { get; set; }

    /// <summary>The same credit in the purchase's transaction currency.</summary>
    public decimal OriginalTotal { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<PurchaseReturnLine> Lines { get; set; } = new List<PurchaseReturnLine>();
}

public class PurchaseReturnLine
{
    public Guid Id { get; set; }

    public Guid PurchaseReturnId { get; set; }
    public PurchaseReturn PurchaseReturn { get; set; } = null!;

    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine PurchaseOrderLine { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The unit this return line was entered in (its own; any active unit of the product, or the purchase
    /// line's unit). The FK is (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id).</summary>
    public Guid ProductUnitId { get; set; }
    public ProductUnit ProductUnit { get; set; } = null!;

    /// <summary>Snapshot of <see cref="ProductUnit.UnitId"/> at posting time.</summary>
    public Guid UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>Snapshot of <c>Unit.ShortCode</c> at posting time.</summary>
    public string UnitCode { get; set; } = string.Empty;

    /// <summary>Snapshot of the unit's conversion factor at posting time. <see cref="Qty"/> x this = <see cref="BaseQty"/> exactly.</summary>
    public decimal ConversionFactorToBase { get; set; }

    /// <summary>Quantity returned, entered in this line's own unit (<see cref="ProductUnitId"/>).</summary>
    public decimal Qty { get; set; }
    public decimal BaseQty { get; set; }

    public decimal TaxableValue { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>The landed cost of the stock taken out.</summary>
    public decimal CostRemoved { get; set; }

    public ICollection<PurchaseReturnLineTax> Taxes { get; set; } = new List<PurchaseReturnLineTax>();
}
