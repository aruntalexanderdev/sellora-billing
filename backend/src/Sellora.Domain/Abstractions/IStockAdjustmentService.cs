using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Domain.Abstractions;

public interface IStockAdjustmentService
{
    /// <summary>Posts a stock adjustment: decreases consume existing batches oldest-first; increases
    /// create a new batch. Always writes a StockMovement and updates the product's StockItem in one
    /// transaction. Throws <see cref="DomainValidationException"/> for business-rule violations.</summary>
    Task<StockAdjustment> CreateAdjustmentAsync(CreateAdjustmentInput input, Guid userId, CancellationToken ct = default);
}
