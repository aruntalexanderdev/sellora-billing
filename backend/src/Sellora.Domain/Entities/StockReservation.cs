using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D4/D5: base-unit stock earmarked at a source location for one consignment line. At most one Active row per consignment line (partial unique
/// index); <c>StockItem.ReservedQuantity</c> equals the sum of Active rows per (location, product) and is kept in step by the service under the
/// StockItem lock. Released and Consumed are terminal and carry their own stamp (DB CHECKs).
/// </summary>
public class StockReservation : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }

    public Guid SourceLocationId { get; set; }
    public Guid ProductId { get; set; }
    public decimal BaseQty { get; set; }

    public Guid ConsignmentLineId { get; set; }
    public StockReservationStatus Status { get; set; } = StockReservationStatus.Active;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedByUserId { get; set; }
    public string? ReleaseReason { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
