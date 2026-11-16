using Sellora.Domain.Entities;

namespace Sellora.Domain.Stock;

/// <summary>FI-D29: creates a Draft shipment out of ONE source location. The transport reference is validated per mode (field "transportReference").</summary>
public sealed record CreateShipmentInput(
    Guid SourceLocationId,
    TransportMode TransportMode,
    TransportVia TransportVia,
    string? ProviderName,
    string? ProviderContact,
    string? TransportReference,
    DateOnly? PlannedDispatchDate,
    string? Notes,
    string? IdempotencyKey = null);

/// <summary>Replaces the editable header fields (Draft / Loading only). The source location never changes.</summary>
public sealed record UpdateShipmentInput(
    TransportMode TransportMode,
    TransportVia TransportVia,
    string? ProviderName,
    string? ProviderContact,
    string? TransportReference,
    DateOnly? PlannedDispatchDate,
    string? Notes);

/// <summary>A vehicle leg: exactly one of <paramref name="VehicleId"/> (own fleet) or <paramref name="ExternalVehicleNumber"/> (external provider).</summary>
public sealed record ShipmentVehicleInput(
    Guid? VehicleId,
    string? ExternalVehicleNumber,
    string? ExternalDescription,
    string? DriverName,
    string? DriverPhone);

/// <summary>Allocates <paramref name="Quantity"/> (entered in <paramref name="ProductUnitId"/>) of one request line to the shipment; adds to an existing allocation.</summary>
public sealed record AllocateShipmentInput(Guid StockTransferLineId, Guid ProductUnitId, decimal Quantity, string? IdempotencyKey = null);

/// <summary>New TOTAL entered quantity (in the allocation's own unit) of a consignment line; 0 cancels the line.</summary>
public sealed record ChangeAllocationInput(decimal Quantity, string? IdempotencyKey = null);

/// <summary>Loaded quantity of one consignment line in the line's unit (absolute, not a delta; lower values unload).</summary>
public sealed record LoadConsignmentLineInput(Guid ConsignmentLineId, decimal LoadedQuantity);

public sealed record LoadConsignmentInput(IReadOnlyList<LoadConsignmentLineInput> Lines, string? IdempotencyKey = null);
