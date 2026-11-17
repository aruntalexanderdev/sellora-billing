import { firstValueFrom } from 'rxjs';
import { newIdempotencyKey, IdempotentSubmission } from '../../../core/idempotency';
import { convert } from '../../../shared/units/unit-conversion';
import type { VehiclesService } from '../../company/vehicles/vehicles.service';
import type { ConsignmentsService } from '../../inventory/consignments/consignments.service';
import { remainingToAccountBase } from '../../inventory/shipments/shipment-rules';
import type { Consignment, ConsignmentReceipt, Shipment } from '../../inventory/shipments/shipments.models';
import type { ShipmentsService } from '../../inventory/shipments/shipments.service';
import { actionPermission } from '../../inventory/stock-transfers/stock-transfer-rules';
import type { CreateStockTransferRequest, StockTransfer } from '../../inventory/stock-transfers/stock-transfers.models';
import type { StockTransfersService } from '../../inventory/stock-transfers/stock-transfers.service';

/**
 * D11 / O6 on the Phase 04/05 endpoints: create(submit) -> approve -> shipment (own-fleet Road, the van's vehicle) -> allocate + load every
 * line at the requested quantity -> dispatch -> record a receipt with everything accepted -> post it. Legacy transfer dispatch/receive is gone.
 */
export type ChainStep = 'create' | 'submit' | 'approve' | 'shipment' | 'allocate' | 'load' | 'dispatch' | 'receive' | 'post';

/**
 * Keys of one wizard run. create/submit/approve use one IdempotentSubmission each; every shipment-leg step derives a stable key from the run key
 * (`<scope>:<step>[:<id>]`, <= 100 chars; scope = run key, then the transfer id), so a resumed run replays instead of repeating a step. The shipment id is remembered once known.
 */
export class ChainKeys {
  readonly run = newIdempotencyKey();
  /**
   * Prefix of every shipment-leg key. The chain sets it to the transfer id as soon as the transfer is known, so a resumed run with fresh
   * ChainKeys (page reload, a new wizard on the same request) replays the same keys instead of creating a second shipment.
   */
  scope: string = this.run;
  readonly create = new IdempotentSubmission();
  readonly submit = new IdempotentSubmission();
  readonly approve = new IdempotentSubmission();
  shipmentId: string | null = null;

  step(name: string, id?: string): string {
    return id ? `${this.scope}:${name}:${id}` : `${this.scope}:${name}`;
  }
}

export interface ChainResult {
  /** Latest known state of the transfer (null when create did not happen). */
  transfer: StockTransfer | null;
  /** Steps that succeeded in this run. */
  completed: ChainStep[];
  /** Step the chain stopped before / at; null = everything done. */
  stoppedAt: ChainStep | null;
  reason: 'permission' | 'error' | 'status' | 'vehicle' | null;
  /** Permission code the user lacks (reason = permission). */
  missingPermission: string | null;
  /** Raw HTTP error (reason = error). */
  error: unknown;
}

type Has = (code: string) => boolean;

export interface ChainServices {
  transfers: Pick<StockTransfersService, 'create' | 'submit' | 'approve' | 'get'>;
  shipments: Pick<ShipmentsService, 'create' | 'addVehicle' | 'allocate' | 'assignVehicle' | 'load' | 'dispatch' | 'get'>;
  consignments: Pick<ConsignmentsService, 'receipts' | 'recordReceipt' | 'postReceipt'>;
  vehicles: Pick<VehiclesService, 'list'>;
}

export interface ChainOptions {
  /** The van's stock location (the vehicle record is found through it). */
  vehicleLocationId: string;
}

const PERM = { vehicles: 'Company.Vehicles.View', plan: 'Shipments.Plan', load: 'Shipments.Load', dispatch: 'StockTransfers.Dispatch', receive: 'StockTransfers.Receive' } as const;

function stop(transfer: StockTransfer | null, completed: ChainStep[], step: ChainStep, reason: ChainResult['reason'], extra: Partial<ChainResult> = {}): ChainResult {
  return { transfer, completed, stoppedAt: step, reason, missingPermission: null, error: null, ...extra };
}

const live = (s: Shipment): Consignment[] => s.consignments.filter((c) => c.status !== 'Cancelled');

/**
 * Runs the chain. Pass `existing` to resume a transfer a previous run left part-way. Each step first reads the server state (shipment graph,
 * receipts) and only does what is still missing; every write carries a stable key. Stops at the first step the user has no permission for
 * (`permission`), the first server rejection (`error`), or a state the chain cannot continue from (`status` / `vehicle`).
 */
export async function runVanChain(
  svc: ChainServices,
  request: Omit<CreateStockTransferRequest, 'submit'> | null,
  existing: StockTransfer | null,
  has: Has,
  keys: ChainKeys,
  options: ChainOptions,
): Promise<ChainResult> {
  const completed: ChainStep[] = [];
  let t = existing;
  let step: ChainStep = 'create';

  const attempt = async <T>(name: ChainStep, submission: IdempotentSubmission | null, call: () => Promise<T>): Promise<{ value: T } | ChainResult> => {
    step = name;
    try {
      const value = await call();
      submission?.succeeded();
      if (!completed.includes(name)) completed.push(name);
      return { value };
    } catch (error) {
      submission?.failed(error);
      return stop(t, completed, name, 'error', { error });
    }
  };
  const isStop = <T>(r: { value: T } | ChainResult): r is ChainResult => !('value' in r);
  const needs = (name: ChainStep, code: string): ChainResult | null => (has(code) ? null : stop(t, completed, name, 'permission', { missingPermission: code }));

  // ---- before any write: when this run will reach the shipment step, the van's vehicle must be readable and exist ----
  let vehicleId: string | null = null;
  const reachesShipment = has(PERM.plan) && (has(actionPermission('approve')) || (!!t && t.status !== 'Draft' && t.status !== 'Requested'));
  if (reachesShipment) {
    const blockedUpFront = needs(t ? 'shipment' : 'create', PERM.vehicles);
    if (blockedUpFront) return blockedUpFront;
    step = 'shipment';
    try {
      vehicleId = (await firstValueFrom(svc.vehicles.list({}))).find((v) => v.stockLocationId === options.vehicleLocationId && v.isActive)?.id ?? null;
    } catch (error) {
      return stop(t, completed, 'shipment', 'error', { error });
    }
    if (!vehicleId) return stop(t, completed, 'shipment', 'vehicle');
    step = 'create';
  }

  // ---- create / submit / approve ----
  if (!t) {
    const blocked = needs('create', actionPermission('submit'));
    if (blocked) return blocked;
    if (!request) return stop(null, completed, 'create', 'status');
    const body = { ...request, submit: true };
    const r = await attempt('create', keys.create, () => firstValueFrom(svc.transfers.create(body, keys.create.keyFor(body))));
    if (isStop(r)) return r;
    t = r.value;
  }
  for (let guard = 0; guard < 3 && t; guard++) {
    const current: StockTransfer = t;
    if (current.status === 'Draft') {
      const blocked = needs('submit', actionPermission('submit'));
      if (blocked) return blocked;
      const r = await attempt('submit', keys.submit, () => firstValueFrom(svc.transfers.submit(current.id, keys.submit.key)));
      if (isStop(r)) return r;
      t = r.value;
    } else if (current.status === 'Requested') {
      const blocked = needs('approve', actionPermission('approve'));
      if (blocked) return blocked;
      const r = await attempt('approve', keys.approve, () => firstValueFrom(svc.transfers.approve(current.id, keys.approve.key)));
      if (isStop(r)) return r;
      t = r.value;
    } else break;
  }
  if (!t) return stop(null, completed, step, 'status');
  if (t.status === 'Completed' && !keys.shipmentId) return { transfer: t, completed, stoppedAt: null, reason: null, missingPermission: null, error: null };
  if (t.status === 'Rejected' || t.status === 'Cancelled' || t.status === 'Draft' || t.status === 'Requested') return stop(t, completed, step, 'status');
  const transfer: StockTransfer = t;
  keys.scope = transfer.id;
  const refreshTransfer = async () => {
    t = await firstValueFrom(svc.transfers.get(transfer.id));
  };

  // ---- shipment: header (own fleet, Road, the van's vehicle) ----
  let blocked = needs('shipment', PERM.plan);
  if (blocked) return blocked;
  let shipment: Shipment;
  {
    const r = await attempt('shipment', null, async () =>
      keys.shipmentId
        ? firstValueFrom(svc.shipments.get(keys.shipmentId))
        // The key derives from the transfer id: replaying it returns the shipment an earlier run (any page session) created.
        : firstValueFrom(svc.shipments.create({
            sourceLocationId: transfer.from.id, transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, providerContact: null,
            transportReference: null, plannedDispatchDate: null, notes: transfer.transferNo,
          }, keys.step('shipment'))),
    );
    if (isStop(r)) return r;
    shipment = r.value;
    keys.shipmentId = shipment.id;
  }
  if (shipment.status === 'Cancelled') return stop(t, completed, 'shipment', 'status');

  const open = shipment.status === 'Draft' || shipment.status === 'Loading' || shipment.status === 'ReadyToDispatch';
  if (open && shipment.vehicles.length === 0 && vehicleId) {
    const r = await attempt('shipment', null, () =>
      firstValueFrom(svc.shipments.addVehicle(shipment.id, { vehicleId, externalVehicleNumber: null, externalDescription: null, driverName: null, driverPhone: null })),
    );
    if (isStop(r)) return r;
    shipment = r.value;
  }

  // ---- allocate every request line at its full requested quantity, then give each consignment the van as its leg ----
  const hasLine = (s: Shipment, lineId: string) => live(s).some((c) => c.lines.some((l) => l.status === 'Active' && l.stockTransferLineId === lineId));
  const toAllocate = transfer.lines.filter((l) => !hasLine(shipment, l.id));
  if (toAllocate.length > 0) {
    blocked = needs('allocate', PERM.plan);
    if (blocked) return blocked;
    for (const l of toAllocate) {
      const r = await attempt('allocate', null, () =>
        firstValueFrom(svc.shipments.allocate(shipment.id, { stockTransferLineId: l.id, productUnitId: l.productUnitId, quantity: l.requestedQty }, keys.step('allocate', l.id))),
      );
      if (isStop(r)) return r;
      shipment = r.value;
    }
  }
  if (open && shipment.vehicles.length > 0) {
    for (const c of live(shipment).filter((x) => !x.shipmentVehicleId)) {
      const r = await attempt('allocate', null, () => firstValueFrom(svc.shipments.assignVehicle(shipment.id, c.id, shipment.vehicles[0].id)));
      if (isStop(r)) return r;
      shipment = r.value;
    }
  }

  // ---- load every line fully ----
  for (const c of live(shipment)) {
    const lines = c.lines.filter((l) => l.status === 'Active');
    if (lines.every((l) => l.loadedBaseQty >= l.allocatedBaseQty) || shipment.status === 'Dispatched') continue;
    blocked = needs('load', PERM.load);
    if (blocked) return blocked;
    const body = { lines: lines.map((l) => ({ consignmentLineId: l.id, loadedQuantity: l.enteredQty })) };
    const r = await attempt('load', null, () => firstValueFrom(svc.shipments.load(shipment.id, c.id, body, keys.step('load', c.id))));
    if (isStop(r)) return r;
    shipment = r.value;
  }

  // ---- dispatch ----
  if (shipment.status === 'ReadyToDispatch') {
    blocked = needs('dispatch', PERM.dispatch);
    if (blocked) return blocked;
    const r = await attempt('dispatch', null, () => firstValueFrom(svc.shipments.dispatch(shipment.id, keys.step('dispatch'))));
    if (isStop(r)) return r;
    shipment = r.value;
  } else if (!shipment.dispatched) {
    return stop(t, completed, 'dispatch', 'status'); // loading is not complete (Draft / Loading)
  }

  // ---- receive: everything dispatched is accepted, then the receipt is posted ----
  for (const c of live(shipment)) {
    const r = await attempt('receive', null, () => firstValueFrom(svc.consignments.receipts(c.id)));
    if (isStop(r)) return r;
    let receipts: ConsignmentReceipt[] = r.value;
    const open2 = c.lines.filter((l) => l.status === 'Active' && remainingToAccountBase(l) > 0);
    if (open2.length > 0 && !receipts.some((x) => x.status === 'Verified')) {
      blocked = needs('receive', PERM.receive);
      if (blocked) return blocked;
      const body = {
        lines: open2.map((l) => ({
          consignmentLineId: l.id,
          acceptedQuantity: remainingToAccountBase(l) === l.dispatchedBaseQty ? l.loadedQty : convert(remainingToAccountBase(l), 1, l.conversionFactorToBase),
          damagedQuantity: 0, rejectedQuantity: 0, missingQuantity: 0,
        })),
      };
      const rec = await attempt('receive', null, () => firstValueFrom(svc.consignments.recordReceipt(c.id, body, keys.step('receipt', c.id))));
      if (isStop(rec)) return rec;
      receipts = [...receipts, rec.value];
    }
    for (const rc of receipts.filter((x) => x.status === 'Verified')) {
      blocked = needs('post', PERM.receive);
      if (blocked) return blocked;
      const p = await attempt('post', null, () => firstValueFrom(svc.consignments.postReceipt(c.id, rc.id, keys.step('post', rc.id))));
      if (isStop(p)) return p;
    }
  }

  try {
    await refreshTransfer();
  } catch (error) {
    return stop(t, completed, 'post', 'error', { error });
  }
  return { transfer: t, completed, stoppedAt: null, reason: null, missingPermission: null, error: null };
}
