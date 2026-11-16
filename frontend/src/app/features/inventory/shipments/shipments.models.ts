import type { StockTransferActor, StockTransferBranch, StockTransferLocation } from '../stock-transfers/stock-transfers.models';

/** Mirrors Domain/Entities/ShipmentEnums.cs (string enums). */
export type ShipmentStatus = 'Draft' | 'Loading' | 'ReadyToDispatch' | 'Cancelled';
export type TransportMode = 'Road' | 'Air' | 'Sea' | 'Rail' | 'Courier' | 'HandCarry';
export type TransportVia = 'OwnFleet' | 'ExternalProvider';
export type ConsignmentStatus = 'Planned' | 'Loaded' | 'Cancelled';
export type ConsignmentLineStatus = 'Active' | 'Cancelled';
export type StockReservationStatus = 'Active' | 'Released' | 'Consumed';

export const SHIPMENT_STATUSES: ShipmentStatus[] = ['Draft', 'Loading', 'ReadyToDispatch', 'Cancelled'];
export const TRANSPORT_MODES: TransportMode[] = ['Road', 'Air', 'Sea', 'Rail', 'Courier', 'HandCarry'];
export const RESERVATION_STATUSES: StockReservationStatus[] = ['Active', 'Released', 'Consumed'];

// ---- Requests (Api/Contracts/ShipmentContracts.cs) ----

export interface CreateShipmentRequest {
  sourceLocationId: string;
  transportMode: TransportMode;
  transportVia: TransportVia;
  providerName: string | null;
  providerContact: string | null;
  transportReference: string | null;
  plannedDispatchDate: string | null;
  notes: string | null;
}

export type UpdateShipmentRequest = Omit<CreateShipmentRequest, 'sourceLocationId'>;

export interface AddShipmentVehicleRequest {
  vehicleId: string | null;
  externalVehicleNumber: string | null;
  externalDescription: string | null;
  driverName: string | null;
  driverPhone: string | null;
}

export interface AllocateShipmentRequest {
  stockTransferLineId: string;
  productUnitId: string;
  quantity: number;
}

export interface LoadConsignmentLineRequest {
  consignmentLineId: string;
  loadedQuantity: number;
}

export interface LoadConsignmentRequest {
  lines: LoadConsignmentLineRequest[];
}

// ---- Responses ----

export interface ShipmentListItem {
  id: string;
  shipmentNo: string;
  status: ShipmentStatus;
  transportMode: TransportMode;
  transportVia: TransportVia;
  providerName: string | null;
  transportReference: string | null;
  plannedDispatchDate: string | null;
  branch: StockTransferBranch;
  source: StockTransferLocation;
  consignmentCount: number;
  lineCount: number;
  vehicleCount: number;
  createdAt: string;
}

export interface ShipmentVehicle {
  id: string;
  sequence: number;
  vehicleId: string | null;
  vehicleNumber: string | null;
  vehicleName: string | null;
  externalVehicleNumber: string | null;
  externalDescription: string | null;
  driverName: string | null;
  driverPhone: string | null;
}

export interface ConsignmentLineRequestInfo {
  requestedBaseQty: number;
  closedBaseQty: number;
  dispatchedBaseQty: number;
  allocatedBaseQty: number;
  remainingUnallocatedBaseQty: number;
}

export interface ConsignmentLine {
  id: string;
  stockTransferId: string;
  transferNo: string | null;
  stockTransferLineId: string;
  productId: string;
  productSku: string | null;
  productName: string | null;
  baseUnitCode: string | null;
  productUnitId: string;
  unitCode: string;
  conversionFactorToBase: number;
  enteredQty: number;
  allocatedBaseQty: number;
  loadedBaseQty: number;
  loadedQty: number;
  status: ConsignmentLineStatus;
  request: ConsignmentLineRequestInfo;
  reservationId: string | null;
  reservedBaseQty: number;
}

export interface Consignment {
  id: string;
  consignmentNo: string;
  status: ConsignmentStatus;
  destination: StockTransferLocation;
  destinationBranch: StockTransferBranch;
  shipmentVehicleId: string | null;
  loadedAt: string | null;
  cancelled: StockTransferActor | null;
  cancellationReason: string | null;
  lines: ConsignmentLine[];
}

export interface Shipment {
  id: string;
  shipmentNo: string;
  status: ShipmentStatus;
  transportMode: TransportMode;
  transportVia: TransportVia;
  providerName: string | null;
  providerContact: string | null;
  transportReference: string | null;
  transportReferenceValid: boolean;
  plannedDispatchDate: string | null;
  notes: string | null;
  branch: StockTransferBranch;
  source: StockTransferLocation;
  vehicles: ShipmentVehicle[];
  consignments: Consignment[];
  created: StockTransferActor;
  updated: StockTransferActor | null;
  cancelled: StockTransferActor | null;
  cancellationReason: string | null;
}

export interface ShipmentListQuery {
  status?: ShipmentStatus;
  mode?: TransportMode;
  sourceLocationId?: string | null;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ---- Consignments (incoming / outgoing readers) ----

export type ConsignmentDirection = 'incoming' | 'outgoing';

export interface ConsignmentListItem {
  id: string;
  consignmentNo: string;
  status: ConsignmentStatus;
  direction: ConsignmentDirection;
  shipmentId: string | null;
  shipmentNo: string;
  shipmentStatus: ShipmentStatus;
  transportMode: TransportMode;
  plannedDispatchDate: string | null;
  source: StockTransferLocation;
  destination: StockTransferLocation;
  destinationBranch: StockTransferBranch;
  lineCount: number;
  loadedAt: string | null;
}

export interface ConsignmentDetailLine {
  id: string;
  transferNo: string | null;
  productId: string;
  productSku: string | null;
  productName: string | null;
  baseUnitCode: string | null;
  productUnitId: string;
  unitCode: string;
  enteredQty: number;
  allocatedBaseQty: number;
  loadedBaseQty: number;
  loadedQty: number;
}

export interface ConsignmentLeg {
  vehicleNumber: string | null;
  vehicleName: string | null;
  driverName: string | null;
  driverPhone: string | null;
}

export interface ConsignmentDetail {
  id: string;
  consignmentNo: string;
  status: ConsignmentStatus;
  direction: ConsignmentDirection;
  /** Null for incoming readers (never leaked). */
  shipmentId: string | null;
  shipmentNo: string;
  shipmentStatus: ShipmentStatus;
  transportMode: TransportMode;
  transportVia: TransportVia;
  providerName: string | null;
  transportReference: string | null;
  plannedDispatchDate: string | null;
  source: StockTransferLocation;
  destination: StockTransferLocation;
  destinationBranch: StockTransferBranch;
  leg: ConsignmentLeg | null;
  loadedAt: string | null;
  lines: ConsignmentDetailLine[];
}

export interface ConsignmentListQuery {
  direction: ConsignmentDirection;
  status?: ConsignmentStatus;
  shipmentId?: string;
  sourceLocationId?: string | null;
  destinationLocationId?: string | null;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ---- Reservations ----

export interface StockReservation {
  id: string;
  status: StockReservationStatus;
  source: StockTransferLocation;
  productId: string;
  productSku: string | null;
  productName: string | null;
  baseUnitCode: string | null;
  baseQty: number;
  shipmentId: string;
  shipmentNo: string;
  consignmentId: string;
  consignmentNo: string;
  consignmentLineId: string;
  destination: StockTransferLocation;
  transferNo: string | null;
  createdAt: string;
  createdBy: string | null;
  ageDays: number;
  releasedAt: string | null;
  releasedBy: string | null;
  releaseReason: string | null;
  consumedAt: string | null;
}

export interface ReservationListQuery {
  locationId?: string | null;
  productId?: string | null;
  status?: StockReservationStatus;
  page?: number;
  pageSize?: number;
}
