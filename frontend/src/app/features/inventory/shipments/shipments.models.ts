import type { StockTransferActor, StockTransferBranch, StockTransferLocation } from '../stock-transfers/stock-transfers.models';

/** Mirrors Domain/Entities/ShipmentEnums.cs (string enums). */
export type ShipmentStatus = 'Draft' | 'Loading' | 'ReadyToDispatch' | 'Dispatched' | 'PartiallyDelivered' | 'Delivered' | 'Cancelled';
export type TransportMode = 'Road' | 'Air' | 'Sea' | 'Rail' | 'Courier' | 'HandCarry';
export type TransportVia = 'OwnFleet' | 'ExternalProvider';
export type ConsignmentStatus = 'Planned' | 'Loaded' | 'InTransit' | 'PartiallyReceived' | 'Received' | 'Cancelled';
export type ConsignmentReceiptStatus = 'Verified' | 'Posted';
export type ConsignmentLineStatus = 'Active' | 'Cancelled';
export type StockReservationStatus = 'Active' | 'Released' | 'Consumed';

export const SHIPMENT_STATUSES: ShipmentStatus[] = ['Draft', 'Loading', 'ReadyToDispatch', 'Dispatched', 'PartiallyDelivered', 'Delivered', 'Cancelled'];
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
  dispatchedBaseQty: number;
  acceptedBaseQty: number;
  damagedBaseQty: number;
  rejectedBaseQty: number;
  missingBaseQty: number;
  postedBaseQty: number;
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
  dispatchedAt: string | null;
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
  dispatched: StockTransferActor | null;
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
  dispatchedAt: string | null;
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
  dispatchedBaseQty: number;
  acceptedBaseQty: number;
  damagedBaseQty: number;
  rejectedBaseQty: number;
  missingBaseQty: number;
  postedBaseQty: number;
  conversionFactorToBase: number;
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
  dispatchedAt: string | null;
  receiptCount: number;
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

// ---- Receipts (Phase 05, FI-D35) ----

export interface ReceiptLineRequest {
  consignmentLineId: string;
  acceptedQuantity: number;
  damagedQuantity: number;
  rejectedQuantity: number;
  missingQuantity: number;
}

/** Quantities are entered in the consignment line's own unit (`ConsignmentDetailLine.unitCode`). */
export interface RecordReceiptRequest {
  lines: ReceiptLineRequest[];
  notes?: string | null;
}

export interface ConsignmentReceiptLine {
  consignmentLineId: string;
  productId: string;
  productSku: string | null;
  productName: string | null;
  baseUnitCode: string | null;
  productUnitId: string;
  unitCode: string;
  conversionFactorToBase: number;
  acceptedQty: number;
  acceptedBaseQty: number;
  damagedQty: number;
  damagedBaseQty: number;
  rejectedQty: number;
  rejectedBaseQty: number;
  missingQty: number;
  missingBaseQty: number;
}

export interface ConsignmentReceipt {
  id: string;
  consignmentId: string;
  consignmentNo: string;
  consignmentStatus: ConsignmentStatus;
  shipmentStatus: ShipmentStatus;
  sequence: number;
  status: ConsignmentReceiptStatus;
  verified: StockTransferActor;
  posted: StockTransferActor | null;
  notes: string | null;
  lines: ConsignmentReceiptLine[];
}

// ---- Transit (Phase 05, FI-D37) ----

export type TransitView = 'current' | 'incoming' | 'outgoing' | 'completed' | 'discrepancies';
export const TRANSIT_VIEWS: TransitView[] = ['current', 'incoming', 'outgoing', 'completed', 'discrepancies'];

export interface TransitLine {
  consignmentLineId: string;
  productId: string;
  productSku: string | null;
  productName: string | null;
  baseUnitCode: string | null;
  unitCode: string;
  dispatchedBaseQty: number;
  acceptedBaseQty: number;
  damagedBaseQty: number;
  rejectedBaseQty: number;
  missingBaseQty: number;
  postedBaseQty: number;
  outstandingBaseQty: number;
  inTransitBaseQty: number;
}

export interface TransitItem {
  consignmentId: string;
  consignmentNo: string;
  status: ConsignmentStatus;
  direction: ConsignmentDirection;
  shipmentId: string | null;
  shipmentNo: string;
  shipmentStatus: ShipmentStatus;
  transportMode: TransportMode;
  transportReference: string | null;
  source: StockTransferLocation;
  destination: StockTransferLocation;
  destinationBranch: StockTransferBranch;
  dispatchedAt: string | null;
  receiptCount: number;
  unpostedReceiptCount: number;
  hasDiscrepancy: boolean;
  lines: TransitLine[];
}

export interface TransitQuery {
  view: TransitView;
  search?: string;
  page?: number;
  pageSize?: number;
}
