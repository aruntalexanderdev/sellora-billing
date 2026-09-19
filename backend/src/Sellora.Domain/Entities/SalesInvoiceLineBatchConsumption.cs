namespace Sellora.Domain.Entities;

/// <summary>
/// Records exactly which purchase batch a sale line drew stock from, and how much (docx §17 —
/// adjustments/consumption must be traceable). This is what lets <see cref="SalesInvoice"/> cancellation
/// restore the precise batches it consumed, independent of any other sale's activity in between.
/// </summary>
public class SalesInvoiceLineBatchConsumption
{
    public Guid Id { get; set; }

    public Guid SalesInvoiceLineId { get; set; }
    public SalesInvoiceLine SalesInvoiceLine { get; set; } = null!;

    public Guid PurchaseBatchId { get; set; }
    public PurchaseBatch PurchaseBatch { get; set; } = null!;

    public decimal QtyBaseUnits { get; set; }

    /// <summary>How much of <see cref="QtyBaseUnits"/> a customer has since returned to this batch.</summary>
    public decimal ReturnedQtyBaseUnits { get; set; }
    public decimal UnitCost { get; set; }
}
