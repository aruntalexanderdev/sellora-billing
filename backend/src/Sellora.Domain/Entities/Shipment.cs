using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D29 (Phase 04): a planned movement out of ONE source location carrying one or more consignments (one per destination).
/// <see cref="BranchId"/> is the owning/source branch (NULL when the source location is company-level) and the number-series owner.
/// External transport needs a provider name (DB CHECK); Cancelled iff CancelledAt (DB CHECK). Navigation-less towards masters.
/// </summary>
public class Shipment : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }

    /// <summary>Unique per company; allocated on the owning branch's series.</summary>
    public required string ShipmentNo { get; set; }

    public Guid? BranchId { get; set; }
    public Guid SourceLocationId { get; set; }

    public ShipmentStatus Status { get; set; } = ShipmentStatus.Draft;
    public TransportMode TransportMode { get; set; }
    public TransportVia TransportVia { get; set; }

    public string? ProviderName { get; set; }
    public string? ProviderContact { get; set; }
    public string? TransportReference { get; set; }
    public DateOnly? PlannedDispatchDate { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancellationReason { get; set; }

    public List<ShipmentVehicle> Vehicles { get; set; } = [];
    public List<Consignment> Consignments { get; set; } = [];
}
