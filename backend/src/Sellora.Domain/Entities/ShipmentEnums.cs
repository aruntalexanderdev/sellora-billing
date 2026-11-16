namespace Sellora.Domain.Entities;

/// <summary>FI-D29/D31 shipment lifecycle (Phase 04). Phase 05 appends the dispatch states. Persisted as its name.</summary>
public enum ShipmentStatus { Draft, Loading, ReadyToDispatch, Cancelled, Dispatched, PartiallyDelivered, Delivered }

/// <summary>FI-D29 transport mode; the mode decides which <see cref="Shipment.TransportReference"/> rule applies.</summary>
public enum TransportMode { Road, Air, Sea, Rail, Courier, HandCarry }

/// <summary>FI-D29: own fleet (vehicles from the vehicle master) or an external provider (ProviderName mandatory).</summary>
public enum TransportVia { OwnFleet, ExternalProvider }

/// <summary>FI-D29 consignment lifecycle.</summary>
public enum ConsignmentStatus { Planned, Loaded, Cancelled, InTransit, PartiallyReceived, Received }

/// <summary>An allocation of one transfer-request line to a consignment.</summary>
public enum ConsignmentLineStatus { Active, Cancelled }

/// <summary>FI-D5 reservation lifecycle: Active until released (allocation reduced/cancelled) or consumed (dispatch, Phase 05).</summary>
public enum StockReservationStatus { Active, Released, Consumed }

/// <summary>FI-D35 receipt lifecycle: Verified (counts recorded, no stock moved) then Posted (accepted quantity moved Transit to destination, exactly once).</summary>
public enum ConsignmentReceiptStatus { Verified, Posted }
