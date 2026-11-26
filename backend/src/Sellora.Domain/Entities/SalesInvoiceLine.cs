namespace Sellora.Domain.Entities;

public class SalesInvoiceLine
{
    public Guid Id { get; set; }

    public Guid SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The product unit sold in at the till, e.g. "Loose kg".</summary>
    public Guid ProductUnitId { get; set; }
    public ProductUnit ProductUnit { get; set; } = null!;

    /// <summary>Snapshot of <see cref="ProductUnit.UnitId"/> at sale time. The FK is (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id).</summary>
    public Guid UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>Snapshot of <c>Unit.ShortCode</c> at sale time.</summary>
    public string UnitCode { get; set; } = string.Empty;

    public decimal Qty { get; set; }

    /// <summary>Snapshot of the product unit's conversion factor at sale time — never recomputed later (docx §17).</summary>
    public decimal ConversionFactorToBase { get; set; }
    public decimal BaseQty { get; set; }

    /// <summary>Price per selling unit actually charged (the list price unless <see cref="PriceOverridden"/>).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>The open list price of the product unit at sale time. NULL only for legacy rows ("not recorded").</summary>
    public decimal? ListUnitPrice { get; set; }

    /// <summary>True when <see cref="UnitPrice"/> differs from <see cref="ListUnitPrice"/> by an authorised override (Sales.PriceOverride).</summary>
    public bool PriceOverridden { get; set; }

    public Guid TaxId { get; set; }
    public Tax Tax { get; set; } = null!;

    /// <summary>Snapshot of the tax rate at sale time.</summary>
    public decimal TaxRatePercent { get; set; }

    public decimal Discount { get; set; }

    /// <summary>Qty*UnitPrice - Discount, before tax.</summary>
    public decimal TaxableValue { get; set; }
    public decimal TaxAmount { get; set; }

    /// <summary>TaxableValue + TaxAmount.</summary>
    public decimal LineTotal { get; set; }

    /// <summary>Actual FIFO-consumed landed cost for this line — the real COGS basis (docx §12.5, §17).</summary>
    public decimal CostOfGoodsSold { get; set; }

    public ICollection<SalesInvoiceLineTax> Taxes { get; set; } = new List<SalesInvoiceLineTax>();

    public ICollection<SalesInvoiceLineBatchConsumption> BatchConsumptions { get; set; } = new List<SalesInvoiceLineBatchConsumption>();
}
