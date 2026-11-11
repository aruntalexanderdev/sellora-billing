using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// How much of one <see cref="PurchaseBatch"/> sits at one stock location (Phase 8, ADR-01/12). Dual-written with
/// <see cref="PurchaseBatch.RemainingBaseQty"/>: Σ balances over locations = the batch's remaining quantity. ProductId is
/// denormalised so FIFO can filter per (location, product); the row cannot exist without its (location, product) StockItem.
/// Zero rows are kept.
/// </summary>
public class BatchLocationBalance : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid PurchaseBatchId { get; set; }
    public Guid StockLocationId { get; set; }
    public Guid ProductId { get; set; }

    public decimal RemainingBaseQty { get; set; }
}
