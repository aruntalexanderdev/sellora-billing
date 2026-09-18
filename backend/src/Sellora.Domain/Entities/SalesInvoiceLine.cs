namespace Sellora.Domain.Entities;

public class SalesInvoiceLine
{
    public Guid Id { get; set; }

    public Guid SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The selling mode used at the till, e.g. "Loose kg".</summary>
    public Guid SellingModeId { get; set; }
    public ProductSellingMode SellingMode { get; set; } = null!;

    public decimal Qty { get; set; }

    /// <summary>Snapshot of the mode's conversion factor at sale time — never recomputed later (docx §17).</summary>
    public decimal ConversionFactorToBase { get; set; }
    public decimal BaseQty { get; set; }

    /// <summary>Price per selling unit charged, snapshot from the catalogue at sale time (docx §9.3, §17).</summary>
    public decimal UnitPrice { get; set; }

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

    public ICollection<SalesInvoiceLineBatchConsumption> BatchConsumptions { get; set; } = new List<SalesInvoiceLineBatchConsumption>();
}
