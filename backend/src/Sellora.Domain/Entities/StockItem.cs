using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// Derived, always-consistent snapshot of one product's stock position (docx §9.1). QuantityOnHand and
/// AverageCost are maintained transactionally alongside StockMovements/PurchaseBatches — never
/// recomputed from scratch on read. AverageCost is a moving average for display only; actual COGS on
/// sale uses FIFO batch consumption (Phase 6), which is a separate, more precise figure.
/// </summary>
public class StockItem : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public decimal QuantityOnHand { get; set; }
    public decimal AverageCost { get; set; }
}
