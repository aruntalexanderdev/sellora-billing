using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>A vehicle (leg) of a <see cref="Shipment"/>: exactly one of an own-fleet <see cref="VehicleId"/> or an <see cref="ExternalVehicleNumber"/> (DB CHECK).</summary>
public class ShipmentVehicle : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ShipmentId { get; set; }
    public int Sequence { get; set; }

    public Guid? VehicleId { get; set; }
    public string? ExternalVehicleNumber { get; set; }
    public string? ExternalDescription { get; set; }
    public string? DriverName { get; set; }
    public string? DriverPhone { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
