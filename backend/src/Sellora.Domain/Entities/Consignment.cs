using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D29: the part of a <see cref="Shipment"/> bound for ONE destination location (at most one non-cancelled per destination, partial unique index).
/// <see cref="ShipmentVehicleId"/> is the leg carrying it; the composite FK (ShipmentId, ShipmentVehicleId) keeps it inside the same shipment.
/// "Destination differs from the shipment source" crosses tables and is a service rule.
/// </summary>
public class Consignment : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ShipmentId { get; set; }
    public required string ConsignmentNo { get; set; }

    public Guid DestinationLocationId { get; set; }
    public Guid? DestinationBranchId { get; set; }
    public Guid? ShipmentVehicleId { get; set; }

    public ConsignmentStatus Status { get; set; } = ConsignmentStatus.Planned;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset? LoadedAt { get; set; }
    public Guid? LoadedByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancellationReason { get; set; }

    public List<ConsignmentLine> Lines { get; set; } = [];
}
