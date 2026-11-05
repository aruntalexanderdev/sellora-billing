namespace Sellora.Domain.Entities;

public class PurchaseOrderLine
{
    public Guid Id { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The product unit purchased in, e.g. "10 kg bag".</summary>
    public Guid ProductUnitId { get; set; }
    public ProductUnit ProductUnit { get; set; } = null!;

    public decimal Qty { get; set; }

    /// <summary>Snapshot of the product unit's conversion factor at purchase time — never recomputed later (docx §17).</summary>
    public decimal ConversionFactorToBase { get; set; }
    public decimal BaseQty { get; set; }

    /// <summary>Rate per purchase unit (not per base unit).</summary>
    public decimal Rate { get; set; }

    public Guid TaxId { get; set; }
    public Tax Tax { get; set; } = null!;

    /// <summary>Snapshot of the tax rate at purchase time.</summary>
    public decimal TaxRatePercent { get; set; }

    public decimal OtherCharges { get; set; }
    public decimal Discount { get; set; }

    /// <summary>Qty*Rate + OtherCharges - Discount, before tax.</summary>
    public decimal TaxableValue { get; set; }
    public decimal TaxAmount { get; set; }

    /// <summary>TaxableValue + TaxAmount.</summary>
    public decimal LineTotal { get; set; }

    /// <summary>Landed cost per base unit (pre-tax) — feeds the PurchaseBatch this line creates.</summary>
    public decimal UnitCost { get; set; }

    public PurchaseBatch? Batch { get; set; }
}
