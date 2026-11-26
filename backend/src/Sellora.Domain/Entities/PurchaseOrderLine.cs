namespace Sellora.Domain.Entities;

public class PurchaseOrderLine
{
    public Guid Id { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The product unit purchased in, e.g. "10 kg bag". The FK is (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id).</summary>
    public Guid ProductUnitId { get; set; }
    public ProductUnit ProductUnit { get; set; } = null!;

    /// <summary>Snapshot of <see cref="ProductUnit.UnitId"/> at posting time.</summary>
    public Guid UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>Snapshot of <c>Unit.ShortCode</c> at posting time (legacy rows: the code current at migration).</summary>
    public string UnitCode { get; set; } = string.Empty;

    /// <summary>Quantity entered, in <see cref="ProductUnit"/>.</summary>
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

    // ---- Tax snapshot (TP-3). History keeps NULL / the defaults; document-currency twins are NULL on history. ----

    /// <summary>The purchase profile and the exact dated version the line was calculated with (BR24). Simple FKs: lines carry no CompanyId; "version belongs to the profile
    /// and company" is a blocking V__tax-purchase-snapshot.sql check. NULL on history until the TP-5 backfill.</summary>
    public Guid? TaxProfileId { get; set; }
    public Guid? TaxProfileVersionId { get; set; }

    /// <summary>Place of supply the line was calculated with; NULL on history.</summary>
    public PlaceOfSupply? PlaceOfSupply { get; set; }

    /// <summary>HSN/SAC snapshot (BR14), NULL when none.</summary>
    public string? HsnCode { get; set; }

    /// <summary>The line's <see cref="Rate"/> included tax (the calculator's inclusive mode).</summary>
    public bool PriceIncludesTax { get; set; }

    /// <summary>Shares of the bill discount / bill charges allocated to this line (base currency).</summary>
    public decimal BillDiscountShare { get; set; }
    public decimal BillChargeShare { get; set; }

    /// <summary>The same figures in the document currency, exactly as the calculator produced them.</summary>
    public decimal? OriginalRate { get; set; }
    public decimal? OriginalDiscount { get; set; }
    public decimal? OriginalOtherCharges { get; set; }
    public decimal? OriginalBillDiscountShare { get; set; }
    public decimal? OriginalBillChargeShare { get; set; }
    public decimal? OriginalTaxableValue { get; set; }
    public decimal? OriginalTaxAmount { get; set; }
    public decimal? OriginalLineTotal { get; set; }

    /// <summary>Landed cost per base unit (pre-tax) — feeds the PurchaseBatch this line creates.</summary>
    public decimal UnitCost { get; set; }

    public ICollection<PurchaseOrderLineTax> Taxes { get; set; } = new List<PurchaseOrderLineTax>();

    public PurchaseBatch? Batch { get; set; }
}
