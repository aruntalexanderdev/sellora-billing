import type { Consignment, ConsignmentLine, Shipment, ShipmentStatus } from './shipments.models';

const loc = (id: string, name: string, type = 'BranchStore') => ({ id, code: id.toUpperCase(), name, type });
const branch = { id: 'b1', code: 'B1', name: 'Branch 1' };
const actor = { userId: 'u1', userName: 'admin', at: '2026-11-06T10:00:00Z' };

export function consignmentLine(over: Partial<ConsignmentLine> = {}): ConsignmentLine {
  return {
    id: 'cl1', stockTransferId: 't1', transferNo: 'TRF-0001', stockTransferLineId: 'l1', productId: 'pen', productSku: 'PEN-1', productName: 'Pen',
    baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box', conversionFactorToBase: 10, enteredQty: 5, allocatedBaseQty: 50, loadedBaseQty: 0, loadedQty: 0,
    status: 'Active',
    request: { requestedBaseQty: 100, closedBaseQty: 0, dispatchedBaseQty: 0, allocatedBaseQty: 50, remainingUnallocatedBaseQty: 50 },
    reservationId: 'r1', reservedBaseQty: 50,
    dispatchedBaseQty: 0, acceptedBaseQty: 0, damagedBaseQty: 0, rejectedBaseQty: 0, missingBaseQty: 0, postedBaseQty: 0,
    dispositionedDamagedBaseQty: 0, dispositionedRejectedBaseQty: 0, dispositionedMissingBaseQty: 0, openDiscrepancyBaseQty: 0, ...over,
  };
}

export function consignment(over: Partial<Consignment> = {}): Consignment {
  return {
    id: 'c1', consignmentNo: 'CON-0001', status: 'Planned', destination: loc('van', 'Van 1', 'Vehicle'), destinationBranch: branch,
    shipmentVehicleId: null, loadedAt: null, cancelled: null, cancellationReason: null, lines: [consignmentLine()], dispatchedAt: null, ...over,
  };
}

export function shipment(status: ShipmentStatus = 'Draft', over: Partial<Shipment> = {}): Shipment {
  return {
    id: 's1', shipmentNo: 'SHP-0001', status, transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, providerContact: null,
    transportReference: null, transportReferenceValid: true, plannedDispatchDate: null, notes: null, branch, source: loc('store', 'Main Store'),
    vehicles: [], consignments: [consignment()], created: actor, updated: null, cancelled: null, cancellationReason: null, dispatched: null, ...over,
  };
}
