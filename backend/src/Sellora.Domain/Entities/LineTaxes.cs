namespace Sellora.Domain.Entities;

/// <summary>One tax row of a document line: the snapshot of one component (BR23), or one legacy row for history (T10).
/// A legacy row has <see cref="TaxComponentId"/> NULL, <see cref="ComponentCode"/> = the old tax name, Kind Other, Method Percent.
/// Rows are never recomputed (BR24). The four concrete types map one table each; the lines are filtered through their parent.</summary>
public abstract class LineTaxBase
{
    public Guid Id { get; set; }

    /// <summary>NULL for a legacy row.</summary>
    public Guid? TaxComponentId { get; set; }

    public string ComponentCode { get; set; } = string.Empty;
    public TaxComponentKind Kind { get; set; }
    public TaxCalculationMethod Method { get; set; }

    /// <summary>Set when <see cref="Method"/> is Percent.</summary>
    public decimal? RatePercent { get; set; }

    /// <summary>Set when <see cref="Method"/> is AmountPerBaseUnit.</summary>
    public decimal? AmountPerBaseUnit { get; set; }

    /// <summary>The amount this row was calculated on (the line's taxable value for percent rows, the base quantity's value for per-unit rows).</summary>
    public decimal BaseAmount { get; set; }
    public decimal TaxAmount { get; set; }
}

/// <summary>Purchase rows also carry the document-currency amounts (NULL on legacy rows: not recorded).</summary>
public abstract class PurchaseLineTaxBase : LineTaxBase
{
    public decimal? OriginalBaseAmount { get; set; }
    public decimal? OriginalTaxAmount { get; set; }
}

public class SalesInvoiceLineTax : LineTaxBase
{
    public Guid SalesInvoiceLineId { get; set; }
    public SalesInvoiceLine Line { get; set; } = null!;
}

public class PurchaseOrderLineTax : PurchaseLineTaxBase
{
    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine Line { get; set; } = null!;
}

public class SalesReturnLineTax : LineTaxBase
{
    public Guid SalesReturnLineId { get; set; }
    public SalesReturnLine Line { get; set; } = null!;
}

public class PurchaseReturnLineTax : PurchaseLineTaxBase
{
    public Guid PurchaseReturnLineId { get; set; }
    public PurchaseReturnLine Line { get; set; } = null!;
}
