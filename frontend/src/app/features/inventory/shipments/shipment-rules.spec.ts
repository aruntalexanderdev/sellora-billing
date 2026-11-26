import {
  baseOf, buildLoadRequest, eligibleVehicles, noVehiclesHint, floorUnitQty, limitsView, mapLoadProblem, maxAllocatableBase, missingLegs, plannerProblemMessage,
  referenceLabel, referenceRequired, requestRemainingBase, shipmentCapabilities, validateAllocation, validateAllocationTotal, validateHeader,
  validateLoad, validateReference, buildReceiptRequest, canReceive, mapReceiptProblem, remainingToAccountBase, validateReceipt, type ReceiptRow,
} from './shipment-rules';
import { consignment, consignmentLine } from './shipment-fixtures';

const BOX = { id: 'box', unitCode: 'box', displayName: 'Box', decimalPlaces: 0, conversionFactorToBase: 10 };
const PC = { id: 'pc', unitCode: 'pc', displayName: 'Piece', decimalPlaces: 0, conversionFactorToBase: 1 };

describe('per-mode transport reference', () => {
  it('labels each mode', () => {
    expect(referenceLabel('Air')).toBe('AWB number');
    expect(referenceLabel('Sea')).toBe('Bill of lading');
    expect(referenceLabel('Rail')).toBe('Railway receipt');
    expect(referenceLabel('Courier')).toBe('Tracking number');
    expect(referenceLabel('HandCarry')).toBe('Carrier name');
  });

  it('requires a reference for every mode except Road', () => {
    expect(referenceRequired('Road')).toBe(false);
    for (const m of ['Air', 'Sea', 'Rail', 'Courier', 'HandCarry'] as const) {
      expect(referenceRequired(m)).toBe(true);
      expect(validateReference(m, '  ')).toBeTruthy();
      expect(validateReference(m, 'X1')).toBeNull();
    }
    expect(validateReference('Road', '')).toBeNull();
    expect(validateReference('Road', 'x'.repeat(101))).toContain('100');
  });

  it('an external provider needs a name', () => {
    expect(validateHeader({ mode: 'Road', via: 'ExternalProvider', providerName: ' ', reference: '' }).providerName).toBeTruthy();
    expect(validateHeader({ mode: 'Road', via: 'OwnFleet', providerName: null, reference: '' })).toEqual({});
    expect(validateHeader({ mode: 'Sea', via: 'OwnFleet', providerName: null, reference: '' }).transportReference).toBeTruthy();
  });
});

describe('status-based enablement', () => {
  const all = () => true;
  it('edits only in Draft / Loading', () => {
    expect(shipmentCapabilities('Draft', all)).toEqual({ edit: true, load: true, cancel: true, dispatch: false });
    expect(shipmentCapabilities('Loading', all).edit).toBe(true);
    expect(shipmentCapabilities('ReadyToDispatch', all)).toEqual({ edit: false, load: true, cancel: true, dispatch: true });
    expect(shipmentCapabilities('Cancelled', all)).toEqual({ edit: false, load: false, cancel: false, dispatch: false });
  });

  it('follows the permissions', () => {
    expect(shipmentCapabilities('Draft', (c) => c === 'Shipments.Load')).toEqual({ edit: false, load: true, cancel: false, dispatch: false });
    expect(shipmentCapabilities('Draft', (c) => c === 'Shipments.Plan')).toEqual({ edit: true, load: false, cancel: true, dispatch: false });
  });

  it('own-fleet road consignments without a leg are flagged', () => {
    const c = consignment();
    expect(missingLegs('OwnFleet', 'Road', [c])).toHaveLength(1);
    expect(missingLegs('OwnFleet', 'Road', [consignment({ shipmentVehicleId: 'v' })])).toHaveLength(0);
    expect(missingLegs('ExternalProvider', 'Road', [c])).toHaveLength(0);
    expect(missingLegs('OwnFleet', 'Air', [c])).toHaveLength(0);
    expect(missingLegs('OwnFleet', 'Road', [consignment({ status: 'Cancelled' })])).toHaveLength(0);
  });
});

describe('allocation maths', () => {
  const line = { requestedBaseQty: 100, closedBaseQty: 10, allocatedBaseQty: 30 };

  it('remaining = requested - closed - allocated', () => {
    expect(requestRemainingBase(line)).toBe(60);
    expect(requestRemainingBase({ requestedBaseQty: 5, closedBaseQty: 5, allocatedBaseQty: 5 })).toBe(0);
  });

  it('the binding limit is the smaller of remaining and available', () => {
    expect(maxAllocatableBase({ remainingBase: 60, availableBase: 25 })).toBe(25);
    expect(maxAllocatableBase({ remainingBase: 60, availableBase: 90 })).toBe(60);
    expect(maxAllocatableBase({ remainingBase: 60, availableBase: null })).toBe(60);
  });

  it('converts through the unit factor and floors to the unit precision', () => {
    expect(baseOf(3, BOX)).toBe(30);
    expect(floorUnitQty(25, BOX)).toBe(2);
    expect(floorUnitQty(25, { decimalPlaces: 1, conversionFactorToBase: 10 })).toBe(2.5);
  });

  it('validates the entered quantity in the chosen unit against remaining and available', () => {
    const limits = { remainingBase: 60, availableBase: 25 };
    expect(validateAllocation(2, BOX, limits)).toBeNull(); // 20 pc
    expect(validateAllocation(3, BOX, limits)).toContain('available'); // 30 pc > 25
    expect(validateAllocation(7, BOX, { remainingBase: 60, availableBase: null })).toContain('remains');
    expect(validateAllocation(1.5, BOX, limits)).toContain('whole numbers');
    expect(validateAllocation(0, BOX, limits)).toBeTruthy();
    expect(validateAllocation(null, BOX, limits)).toBeTruthy();
    expect(validateAllocation(5, null, limits)).toBe('Select a unit.');
    expect(validateAllocation(25, PC, limits)).toBeNull();
  });

  it('shows requested / remaining / available in the selected unit with the base quantity', () => {
    const v = limitsView(100, { remainingBase: 60, availableBase: 25 }, BOX, 'pc', 0);
    expect(v.requested).toBe('10 box (100 pc)');
    expect(v.remaining).toBe('6 box (60 pc)');
    // 25 pc is not a whole number of boxes: falls back to base units rather than a rounded lie.
    expect(v.available).toBe('25 pc');
    expect(limitsView(100, { remainingBase: 60, availableBase: null }, PC, 'pc', 0).available).toBe('unknown');
  });

  it('validates a new allocation total against loaded quantity and remaining request', () => {
    const l = consignmentLine({ loadedBaseQty: 20, loadedQty: 2 });
    expect(validateAllocationTotal(1, l, 0, 50)).toContain('already loaded');
    expect(validateAllocationTotal(0, l, 0, 50)).toContain('Unload');
    expect(validateAllocationTotal(9, l, 0, 50)).toBeNull(); // +40 pc <= 50
    expect(validateAllocationTotal(11, l, 0, 50)).toBeTruthy(); // +60 pc > 50
    expect(validateAllocationTotal(1.5, l, 0, 50)).toBeTruthy();
    expect(validateAllocationTotal(0, consignmentLine(), 0, 0)).toBeNull();
  });
});

describe('load validation', () => {
  const row = (loadedQty: number | null) => ({ lineId: 'cl1', allocatedQty: 5, loadedQty, decimalPlaces: 0 });

  it('0 <= loaded <= allocated', () => {
    expect(validateLoad([row(5)]).rows).toEqual({});
    expect(validateLoad([row(0)]).rows).toEqual({});
    expect(validateLoad([row(6)]).rows[0]).toContain('allocated');
    expect(validateLoad([row(-1)]).rows[0]).toContain('negative');
    expect(validateLoad([row(null)]).rows[0]).toBeTruthy();
    expect(validateLoad([row(2.5)]).rows[0]).toContain('Whole');
    expect(validateLoad([]).form).toBeTruthy();
  });

  it('builds the request body', () => {
    expect(buildLoadRequest([row(3)])).toEqual({ lines: [{ consignmentLineId: 'cl1', loadedQuantity: 3 }] });
  });

  it('maps server row and banner errors', () => {
    const e = mapLoadProblem({ errors: { 'lines[1].loadedQuantity': ['Too much.'], status: ['This shipment is Cancelled.'] } });
    expect(e.rows[1]).toBe('Too much.');
    expect(e.form).toBe('This shipment is Cancelled.');
  });
});

describe('server problems', () => {
  it('joins known 400 keys into one message', () => {
    expect(plannerProblemMessage({ errors: { quantity: ['Only 5 pc is available at the source.'], status: ['Wrong state.'] } })).toBe('Wrong state. Only 5 pc is available at the source.');
    expect(plannerProblemMessage({ title: 'Boom' })).toBe('Boom');
  });
});

describe('vehicle picker', () => {
  const v = (id: string, branch: string | null, active = true) => ({ id, vehicleNumber: id, vehicleName: id, assignedBranchId: branch, isActive: active });
  it('active and central or assigned to the source branch, not already on the shipment', () => {
    const all = [v('central', null), v('mine', 'b1'), v('other', 'b2'), v('off', null, false), v('used', 'b1')];
    expect(eligibleVehicles(all, 'b1', ['used']).map((x) => x.id)).toEqual(['central', 'mine']);
  });
});

describe('dispatch gating', () => {
  it('Dispatch needs ReadyToDispatch and StockTransfers.Dispatch; cancel/edit/load vanish once dispatched', () => {
    const dispatchOnly = (c: string) => c === 'StockTransfers.Dispatch';
    expect(shipmentCapabilities('ReadyToDispatch', dispatchOnly).dispatch).toBe(true);
    expect(shipmentCapabilities('ReadyToDispatch', () => false).dispatch).toBe(false);
    for (const status of ['Draft', 'Loading', 'Dispatched', 'PartiallyDelivered', 'Delivered', 'Cancelled'] as const) {
      expect(shipmentCapabilities(status, () => true).dispatch).toBe(false);
    }
    for (const status of ['Dispatched', 'PartiallyDelivered', 'Delivered'] as const) {
      expect(shipmentCapabilities(status, () => true)).toEqual({ edit: false, load: false, cancel: false, dispatch: false });
    }
  });
});

describe('receipt validation', () => {
  const row = (over: Partial<ReceiptRow> = {}): ReceiptRow => ({
    lineId: 'cl1', unitCode: 'box', factor: 10, decimalPlaces: 0, remainingBase: 50, accepted: null, damaged: null, rejected: null, missing: null, ...over,
  });

  it('remaining = dispatched minus everything already accounted', () => {
    expect(remainingToAccountBase({ dispatchedBaseQty: 50, acceptedBaseQty: 10, damagedBaseQty: 5, rejectedBaseQty: 5, missingBaseQty: 10 })).toBe(20);
    expect(remainingToAccountBase({ dispatchedBaseQty: 50, acceptedBaseQty: 50, damagedBaseQty: 0, rejectedBaseQty: 0, missingBaseQty: 0 })).toBe(0);
  });

  it('accepts a total up to the remaining quantity converted with the line factor', () => {
    expect(validateReceipt([row({ accepted: 3, damaged: 1, rejected: 0, missing: 1 })])).toEqual({ rows: {}, form: null });
    expect(validateReceipt([row({ accepted: 3, damaged: 1, rejected: 1, missing: 1 })]).rows[0]).toMatch(/more than the 5 box/);
  });

  it('is exceeded by the sum of the four, not by any single one', () => {
    expect(validateReceipt([row({ accepted: 5 })]).rows).toEqual({});
    expect(validateReceipt([row({ accepted: 2, damaged: 2, rejected: 2 })]).rows[0]).toBeDefined();
    expect(validateReceipt([row({ missing: 6 })]).rows[0]).toBeDefined();
  });

  it('follows the unit decimals and rejects negatives and an empty receipt', () => {
    expect(validateReceipt([row({ accepted: 1.5 })]).rows[0]).toMatch(/whole numbers/i);
    expect(validateReceipt([row({ decimalPlaces: 1, factor: 1, remainingBase: 10, accepted: 2.5 })]).rows).toEqual({});
    expect(validateReceipt([row({ accepted: -1 })]).rows[0]).toMatch(/negative/i);
    expect(validateReceipt([row()]).form).toMatch(/at least one/i);
    expect(validateReceipt([]).form).toMatch(/nothing is left/i);
  });

  it('builds the request from non-zero rows only, in the line unit, trimming notes', () => {
    const rows = [row({ accepted: 3, missing: 1 }), row({ lineId: 'cl2' })];
    expect(buildReceiptRequest(rows, '  short one  ')).toEqual({
      lines: [{ consignmentLineId: 'cl1', acceptedQuantity: 3, damagedQuantity: 0, rejectedQuantity: 0, missingQuantity: 1 }],
      notes: 'short one',
    });
    expect(buildReceiptRequest(rows, ' ')).not.toHaveProperty('notes');
  });

  it('maps server lines[i] errors back onto the dialog rows (the request skipped zero rows)', () => {
    const rows = [row(), row({ lineId: 'cl2', accepted: 1 })];
    const mapped = mapReceiptProblem({ status: 400, title: 'x', errors: { 'lines[0].acceptedQuantity': ['Too many.'], status: ['Not in transit.'] } }, rows);
    expect(mapped.rows).toEqual({ 1: 'Too many.' });
    expect(mapped.form).toBe('Not in transit.');
  });

  it('receiving needs InTransit / PartiallyReceived and StockTransfers.Receive', () => {
    const has = (c: string) => c === 'StockTransfers.Receive';
    expect(canReceive('InTransit', has)).toBe(true);
    expect(canReceive('PartiallyReceived', has)).toBe(true);
    expect(canReceive('Received', has)).toBe(false);
    expect(canReceive('Loaded', has)).toBe(false);
    expect(canReceive('InTransit', () => false)).toBe(false);
  });
});

describe('noVehiclesHint', () => {
  const companySource = { branch: { id: null, name: null, code: null }, source: { name: 'Company warehouse', code: 'COMPANY-WH' } };
  const branchSource = { branch: { id: 'b1', name: 'Main', code: 'MAIN' }, source: { name: 'Main store', code: 'MAIN-ST' } };

  it('is null while there is something to pick', () => {
    expect(noVehiclesHint(companySource, 1, 1)).toBeNull();
  });

  it('says every vehicle is already used when the shipment holds them all', () => {
    expect(noVehiclesHint(branchSource, 0, 2)).toBe('Every available vehicle is already on this shipment.');
  });

  it('explains the central-only rule for a company-level source', () => {
    const hint = noVehiclesHint(companySource, 0, 0)!;
    expect(hint).toContain('Only central vehicles');
    expect(hint).toContain('Company warehouse');
    expect(hint).toContain('external carrier');
  });

  it('names the branch for a branch source', () => {
    expect(noVehiclesHint(branchSource, 0, 0)).toContain('assigned to Main');
  });
});
