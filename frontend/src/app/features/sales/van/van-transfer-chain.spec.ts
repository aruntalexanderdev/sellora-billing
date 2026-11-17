import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { consignment, consignmentLine, shipment as shipmentFixture } from '../../inventory/shipments/shipment-fixtures';
import type { ConsignmentReceipt, Shipment } from '../../inventory/shipments/shipments.models';
import { dispatchedLine, line, transfer } from '../../inventory/stock-transfers/test-fixtures';
import { ChainKeys, runVanChain, type ChainServices } from './van-transfer-chain';

const ALL = ['Company.Vehicles.View', 'StockTransfers.Create', 'StockTransfers.Approve', 'Shipments.Plan', 'Shipments.Load', 'StockTransfers.Dispatch', 'StockTransfers.Receive'];
const has = (perms: string[]) => (c: string) => perms.includes(c);
const request = { sourceLocationId: 'store', destinationLocationId: 'van', notes: null, lines: [{ productId: 'pen', productUnitId: 'box', quantity: 5 }] };
const options = { vehicleLocationId: 'van' };
const actor = { userId: 'u', userName: 'admin', at: '2026-11-16T10:00:00Z' };

/** A tiny stateful stand-in for the server, so resumed runs see what earlier runs did. */
function fake(vehicles = [{ id: 'v1', stockLocationId: 'van', isActive: true }]) {
  const requested = transfer('Requested', [line()]);
  const approved = transfer('Approved', [line()]);
  const completed = transfer('Completed', [dispatchedLine({ outstandingBaseQty: 0, receivedBaseQty: 50 })]);
  const state = { shipment: null as Shipment | null, receipts: [] as ConsignmentReceipt[] };
  const created = new Map<string, Shipment>();
  const set = (s: Shipment) => (state.shipment = s);
  const cur = () => state.shipment!;
  const withCons = (fn: (c: ReturnType<typeof consignment>) => ReturnType<typeof consignment>, over: Partial<Shipment> = {}) =>
    set({ ...cur(), ...over, consignments: cur().consignments.map(fn) });

  const shipments = {
    // Idempotent like the server: a key seen before replays the shipment it created.
    create: vi.fn((_r: unknown, k: string) => {
      if (!created.has(k)) created.set(k, set(shipmentFixture('Draft', { consignments: [], vehicles: [] })));
      return of(cur());
    }),
    get: vi.fn((_id: string) => of(cur())),
    addVehicle: vi.fn((_id: string, _r: unknown) =>
      of(set({ ...cur(), vehicles: [{ id: 'sv1', sequence: 1, vehicleId: 'v1', vehicleNumber: 'V1', vehicleName: null, externalVehicleNumber: null, externalDescription: null, driverName: null, driverPhone: null }] }))),
    allocate: vi.fn((_id: string, r: { stockTransferLineId: string; productUnitId: string; quantity: number }, _k: string) =>
      of(set({
        ...cur(),
        status: 'Draft',
        consignments: [consignment({ lines: [consignmentLine({ id: 'cl1', stockTransferLineId: r.stockTransferLineId, productUnitId: r.productUnitId, enteredQty: r.quantity, allocatedBaseQty: r.quantity * 10, loadedBaseQty: 0, loadedQty: 0 })] })],
      }))),
    assignVehicle: vi.fn((_id: string, _c: string, svId: string | null) => of(withCons((c) => ({ ...c, shipmentVehicleId: svId })))),
    load: vi.fn((_id: string, _c: string, _b: unknown, _k: string) =>
      of(withCons((c) => ({ ...c, status: 'Loaded', lines: c.lines.map((l) => ({ ...l, loadedBaseQty: l.allocatedBaseQty, loadedQty: l.enteredQty })) }), { status: 'ReadyToDispatch' }))),
    dispatch: vi.fn((_id: string, _k: string) =>
      of(withCons((c) => ({ ...c, status: 'InTransit', dispatchedAt: actor.at, lines: c.lines.map((l) => ({ ...l, dispatchedBaseQty: l.allocatedBaseQty })) }), { status: 'Dispatched', dispatched: actor }))),
  };
  const consignments = {
    receipts: vi.fn((_id: string) => of([...state.receipts])),
    recordReceipt: vi.fn((_id: string, _b: unknown, _k: string) => {
      const r = { id: 'r1', sequence: 1, status: 'Verified' } as ConsignmentReceipt;
      state.receipts = [r];
      withCons((c) => ({ ...c, lines: c.lines.map((l) => ({ ...l, acceptedBaseQty: l.dispatchedBaseQty })) }));
      return of(r);
    }),
    postReceipt: vi.fn((_id: string, _r: string, _k: string) => {
      state.receipts = state.receipts.map((x) => ({ ...x, status: 'Posted' as const }));
      return of(state.receipts[0]);
    }),
  };
  const transfers = {
    create: vi.fn((_r: unknown, _k: string) => of(requested)),
    submit: vi.fn((_id: string, _k?: string) => of(requested)),
    approve: vi.fn((_id: string, _k?: string) => of(approved)),
    get: vi.fn((_id: string) => of(completed)),
  };
  const vehiclesSvc = { list: vi.fn(() => of(vehicles)) };
  const svc = { transfers, shipments, consignments, vehicles: vehiclesSvc } as unknown as ChainServices;
  return { svc, state, transfers, shipments, consignments, vehicles: vehiclesSvc, requested, approved, completed };
}

const net = () => new HttpErrorResponse({ status: 0 });

describe('runVanChain', () => {
  it('create(submit) -> approve -> shipment -> allocate -> load -> dispatch -> receive -> post, then returns the refreshed transfer', async () => {
    const f = fake();
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), options);

    expect(res).toMatchObject({ stoppedAt: null, reason: null });
    expect(res.completed).toEqual(['create', 'approve', 'shipment', 'allocate', 'load', 'dispatch', 'receive', 'post']);
    expect(res.transfer!.status).toBe('Completed');
    // Own-fleet Road shipment out of the transfer's source, carried by the van's vehicle record.
    expect(f.shipments.create).toHaveBeenCalledWith(expect.objectContaining({ sourceLocationId: 'store', transportMode: 'Road', transportVia: 'OwnFleet' }), expect.any(String));
    expect(f.shipments.addVehicle).toHaveBeenCalledWith('s1', expect.objectContaining({ vehicleId: 'v1' }));
    // Every line at its full requested quantity, in the requested unit.
    expect(f.shipments.allocate).toHaveBeenCalledWith('s1', { stockTransferLineId: 'l1', productUnitId: 'box', quantity: 5 }, expect.any(String));
    expect(f.shipments.assignVehicle).toHaveBeenCalledWith('s1', 'c1', 'sv1');
    expect(f.shipments.load).toHaveBeenCalledWith('s1', 'c1', { lines: [{ consignmentLineId: 'cl1', loadedQuantity: 5 }] }, expect.any(String));
    // Everything accepted, in the line's unit.
    expect(f.consignments.recordReceipt).toHaveBeenCalledWith('c1', { lines: [{ consignmentLineId: 'cl1', acceptedQuantity: 5, damagedQuantity: 0, rejectedQuantity: 0, missingQuantity: 0 }] }, expect.any(String));
    expect(f.consignments.postReceipt).toHaveBeenCalledWith('c1', 'r1', expect.any(String));
  });

  it('gives every step its own stable key derived from the run key', async () => {
    const f = fake();
    const keys = new ChainKeys();
    await runVanChain(f.svc, request, null, has(ALL), keys, options);
    const used = [
      f.transfers.create.mock.calls[0][1], f.transfers.approve.mock.calls[0][1], f.shipments.create.mock.calls[0][1], f.shipments.allocate.mock.calls[0][2],
      f.shipments.load.mock.calls[0][3], f.shipments.dispatch.mock.calls[0][1], f.consignments.recordReceipt.mock.calls[0][2], f.consignments.postReceipt.mock.calls[0][2],
    ];
    expect(new Set(used).size).toBe(used.length);
    for (const k of used.slice(2) as string[]) {
      expect(k.startsWith('t1:')).toBe(true); // scope = the transfer id
      expect(keys.scope).toBe('t1');
      expect(k.length).toBeLessThanOrEqual(100);
    }
  });

  it('a fresh run after a completed chain uses new keys', async () => {
    const f = fake();
    const first = new ChainKeys();
    await runVanChain(f.svc, request, null, has(ALL), first, options);
    const second = new ChainKeys();
    expect(second.run).not.toBe(first.run);
    // A second van operation is a new transfer, so its derived keys differ.
    f.transfers.create.mockReturnValueOnce(of(transfer('Requested', [line()], { id: 't2' })));
    f.transfers.approve.mockReturnValueOnce(of(transfer('Approved', [line()], { id: 't2' })));
    await runVanChain(f.svc, request, null, has(ALL), second, options);
    expect(f.shipments.create.mock.calls[1][1]).not.toBe(f.shipments.create.mock.calls[0][1]);
    expect(f.shipments.dispatch.mock.calls[1][1]).not.toBe(f.shipments.dispatch.mock.calls[0][1]);
    expect(f.consignments.recordReceipt.mock.calls[1][2]).not.toBe(f.consignments.recordReceipt.mock.calls[0][2]);
  });

  it('stops at the first missing permission and reports it (user can only create)', async () => {
    const f = fake();
    const res = await runVanChain(f.svc, request, null, has(['StockTransfers.Create']), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt: 'approve', reason: 'permission', missingPermission: 'StockTransfers.Approve', completed: ['create'] });
    expect(res.transfer!.status).toBe('Requested');
    expect(f.transfers.approve).not.toHaveBeenCalled();
    expect(f.shipments.create).not.toHaveBeenCalled();
  });

  it.each([
    ['shipment', 'Shipments.Plan', ['StockTransfers.Create', 'StockTransfers.Approve']],
    ['load', 'Shipments.Load', ['Company.Vehicles.View', 'StockTransfers.Create', 'StockTransfers.Approve', 'Shipments.Plan']],
    ['dispatch', 'StockTransfers.Dispatch', ['Company.Vehicles.View', 'StockTransfers.Create', 'StockTransfers.Approve', 'Shipments.Plan', 'Shipments.Load']],
    ['receive', 'StockTransfers.Receive', ['Company.Vehicles.View', 'StockTransfers.Create', 'StockTransfers.Approve', 'Shipments.Plan', 'Shipments.Load', 'StockTransfers.Dispatch']],
  ])('does not skip a missing permission: stops before %s without %s', async (stoppedAt, missing, perms) => {
    const f = fake();
    const res = await runVanChain(f.svc, request, null, has(perms), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt, reason: 'permission', missingPermission: missing });
    if (stoppedAt === 'dispatch') expect(f.shipments.dispatch).not.toHaveBeenCalled();
    if (stoppedAt === 'receive') expect(f.consignments.recordReceipt).not.toHaveBeenCalled();
  });

  it('creates nothing without StockTransfers.Create', async () => {
    const f = fake();
    const res = await runVanChain(f.svc, request, null, has([]), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt: 'create', reason: 'permission', transfer: null });
    expect(f.transfers.create).not.toHaveBeenCalled();
  });

  it('reports the gap when the van has no active vehicle record (nothing is allocated)', async () => {
    const f = fake([]);
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt: 'shipment', reason: 'vehicle' });
    expect(f.shipments.allocate).not.toHaveBeenCalled();
    // Resolved before the first write: nothing was created, submitted or approved.
    expect(f.transfers.create).not.toHaveBeenCalled();
    expect(f.shipments.create).not.toHaveBeenCalled();
  });

  it('stops before any write when the user may ship but cannot read vehicles', async () => {
    const f = fake();
    const res = await runVanChain(f.svc, request, null, has(ALL.filter((p) => p !== 'Company.Vehicles.View')), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt: 'create', reason: 'permission', missingPermission: 'Company.Vehicles.View', transfer: null });
    expect(f.vehicles.list).not.toHaveBeenCalled();
    expect(f.transfers.create).not.toHaveBeenCalled();
  });

  it('does not need vehicle access for a user who stops before the shipment anyway (create only)', async () => {
    const f = fake();
    const res = await runVanChain(f.svc, request, null, has(['StockTransfers.Create']), new ChainKeys(), options);
    expect(res.completed).toEqual(['create']);
    expect(f.vehicles.list).not.toHaveBeenCalled();
  });

  it('resume with FRESH keys (reload / new wizard) on an already-shipped request creates no second shipment', async () => {
    const f = fake();
    f.shipments.dispatch.mockReturnValueOnce(throwError(net));
    const first = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), options);
    expect(first).toMatchObject({ stoppedAt: 'dispatch', reason: 'error' });

    const second = await runVanChain(f.svc, null, f.approved, has(ALL), new ChainKeys(), options);
    expect(second.stoppedAt).toBeNull();
    // Same transfer-derived create key: the server replays the existing shipment; no extra allocation, vehicle or load either.
    expect(f.shipments.create.mock.calls[1][1]).toBe(f.shipments.create.mock.calls[0][1]);
    expect(f.state.shipment!.id).toBe('s1');
    expect(f.shipments.allocate).toHaveBeenCalledTimes(1);
    expect(f.shipments.addVehicle).toHaveBeenCalledTimes(1);
    expect(f.shipments.load).toHaveBeenCalledTimes(1);
    expect(f.shipments.dispatch.mock.calls[1][1]).toBe(f.shipments.dispatch.mock.calls[0][1]);
  });

  it('stops on a server rejection and links the transfer as it stands', async () => {
    const f = fake();
    const error = new HttpErrorResponse({ status: 400, error: { errors: { quantity: ['Not enough stock.'] } } });
    f.shipments.allocate.mockReturnValue(throwError(() => error));
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt: 'allocate', reason: 'error', error });
    expect(res.completed).toEqual(['create', 'approve', 'shipment']);
    expect(res.transfer!.status).toBe('Approved');
  });

  it('reports a create validation failure with no transfer', async () => {
    const f = fake();
    f.transfers.create.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 400 })));
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt: 'create', reason: 'error', transfer: null });
  });

  it('resume after a failed dispatch does not repeat allocate or load and replays dispatch with the same key', async () => {
    const f = fake();
    const keys = new ChainKeys();
    f.shipments.dispatch.mockReturnValueOnce(throwError(net));
    const first = await runVanChain(f.svc, request, null, has(ALL), keys, options);
    expect(first).toMatchObject({ stoppedAt: 'dispatch', reason: 'error' });

    const second = await runVanChain(f.svc, null, first.transfer, has(ALL), keys, options);
    expect(second.stoppedAt).toBeNull();
    expect(f.transfers.create).toHaveBeenCalledTimes(1);
    expect(f.shipments.create).toHaveBeenCalledTimes(1);
    expect(f.shipments.allocate).toHaveBeenCalledTimes(1);
    expect(f.shipments.load).toHaveBeenCalledTimes(1);
    expect(f.shipments.addVehicle).toHaveBeenCalledTimes(1);
    expect(f.shipments.dispatch).toHaveBeenCalledTimes(2);
    expect(f.shipments.dispatch.mock.calls[1][1]).toBe(f.shipments.dispatch.mock.calls[0][1]);
  });

  it('resume after a failed receipt skips everything up to dispatch (server state is authoritative)', async () => {
    const f = fake();
    const keys = new ChainKeys();
    f.consignments.recordReceipt.mockReturnValueOnce(throwError(net));
    const first = await runVanChain(f.svc, request, null, has(ALL), keys, options);
    expect(first).toMatchObject({ stoppedAt: 'receive', reason: 'error' });

    const second = await runVanChain(f.svc, null, first.transfer, has(ALL), keys, options);
    expect(second.stoppedAt).toBeNull();
    expect(f.shipments.dispatch).toHaveBeenCalledTimes(1);
    expect(f.consignments.recordReceipt).toHaveBeenCalledTimes(2);
    expect(f.consignments.recordReceipt.mock.calls[1][2]).toBe(f.consignments.recordReceipt.mock.calls[0][2]);
  });

  it('resume after a failed post does not record a second receipt', async () => {
    const f = fake();
    const keys = new ChainKeys();
    f.consignments.postReceipt.mockReturnValueOnce(throwError(net));
    const first = await runVanChain(f.svc, request, null, has(ALL), keys, options);
    expect(first).toMatchObject({ stoppedAt: 'post', reason: 'error' });

    const second = await runVanChain(f.svc, null, first.transfer, has(ALL), keys, options);
    expect(second.stoppedAt).toBeNull();
    expect(f.consignments.recordReceipt).toHaveBeenCalledTimes(1);
    expect(f.consignments.postReceipt).toHaveBeenCalledTimes(2);
    expect(f.consignments.postReceipt.mock.calls[1][2]).toBe(f.consignments.postReceipt.mock.calls[0][2]);
  });

  it('stops without acting on a rejected or cancelled transfer', async () => {
    const f = fake();
    const res = await runVanChain(f.svc, null, transfer('Cancelled'), has(ALL), new ChainKeys(), options);
    expect(res).toMatchObject({ reason: 'status' });
    expect(f.transfers.approve).not.toHaveBeenCalled();
    expect(f.shipments.create).not.toHaveBeenCalled();
  });
});

describe('runVanChain under SupervisorApproval (FI-D39)', () => {
  const pending = (over: Partial<ConsignmentReceipt> = {}) => ({ id: 'r1', sequence: 1, status: 'PendingApproval', policyMode: 'SupervisorApproval', ...over }) as ConsignmentReceipt;

  /** The server records a PendingApproval receipt; nothing is posted until a supervisor approves (which posts it). */
  function supervisorFake() {
    const f = fake();
    f.consignments.recordReceipt.mockImplementation((_id: string, _b: unknown, _k: string) => {
      const r = pending();
      f.state.receipts = [r];
      f.state.shipment = { ...f.state.shipment!, consignments: f.state.shipment!.consignments.map((c) => ({ ...c, lines: c.lines.map((l) => ({ ...l, acceptedBaseQty: l.dispatchedBaseQty })) })) };
      return of(r);
    });
    return f;
  }

  it('stops with reason "approval" after recording, without calling post and without an error', async () => {
    const f = supervisorFake();
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), options);

    expect(res).toMatchObject({ stoppedAt: 'post', reason: 'approval', error: null });
    expect(res.completed).toEqual(expect.arrayContaining(['dispatch', 'receive']));
    expect(res.completed).not.toContain('post');
    expect(f.consignments.postReceipt).not.toHaveBeenCalled();
    expect(res.transfer).not.toBeNull();
  });

  it('does not stop on a permission error: a user without Receive is stopped at receive, not at approval', async () => {
    const f = supervisorFake();
    const noReceive = ALL.filter((p) => p !== 'StockTransfers.Receive');
    const res = await runVanChain(f.svc, request, null, has(noReceive), new ChainKeys(), options);
    expect(res).toMatchObject({ stoppedAt: 'receive', reason: 'permission' });
  });

  it('resumes: while still pending it stops again without a second receipt; once approved (Posted) it completes', async () => {
    const f = supervisorFake();
    const keys = new ChainKeys();
    const first = await runVanChain(f.svc, request, null, has(ALL), keys, options);
    expect(first.reason).toBe('approval');

    const again = await runVanChain(f.svc, null, first.transfer, has(ALL), keys, options);
    expect(again).toMatchObject({ stoppedAt: 'post', reason: 'approval' });
    expect(f.consignments.recordReceipt).toHaveBeenCalledTimes(1);
    expect(f.shipments.dispatch).toHaveBeenCalledTimes(1);

    // A supervisor approves: the receipt is now Posted.
    f.state.receipts = f.state.receipts.map((r) => ({ ...r, status: 'Posted' as const }));
    const done = await runVanChain(f.svc, null, first.transfer, has(ALL), keys, options);
    expect(done).toMatchObject({ stoppedAt: null, reason: null });
    expect(f.consignments.recordReceipt).toHaveBeenCalledTimes(1);
    expect(f.consignments.postReceipt).not.toHaveBeenCalled();
    expect(done.transfer!.status).toBe('Completed');
  });

  it('under Direct policy the chain still posts a Verified receipt (unchanged)', async () => {
    const f = fake();
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), options);
    expect(res.stoppedAt).toBeNull();
    expect(f.consignments.postReceipt).toHaveBeenCalledTimes(1);
  });
});

describe('runVanChain under Direct with separation of duties (FI-D39)', () => {
  const sepOptions = { vehicleLocationId: 'van', currentUserId: 'rec' };

  /** Direct policy; the receipt snapshots requireSeparation and is recorded by user "rec". */
  function separationFake() {
    const f = fake();
    f.consignments.recordReceipt.mockImplementation((_id: string, _b: unknown, _k: string) => {
      const r = { id: 'r1', sequence: 1, status: 'Verified', policyMode: 'Direct', requireSeparation: true, verified: { userId: 'rec', userName: 'rec', at: actor.at } } as ConsignmentReceipt;
      f.state.receipts = [r];
      f.state.shipment = { ...f.state.shipment!, consignments: f.state.shipment!.consignments.map((c) => ({ ...c, lines: c.lines.map((l) => ({ ...l, acceptedBaseQty: l.dispatchedBaseQty })) })) };
      return of(r);
    });
    return f;
  }

  it('stops with reason "separation" before posting, without an error', async () => {
    const f = separationFake();
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), sepOptions);
    expect(res).toMatchObject({ stoppedAt: 'post', reason: 'separation', error: null });
    expect(f.consignments.postReceipt).not.toHaveBeenCalled();
  });

  it('resumes: still stops while unposted; completes once someone else posted, without a second receipt', async () => {
    const f = separationFake();
    const keys = new ChainKeys();
    const first = await runVanChain(f.svc, request, null, has(ALL), keys, sepOptions);
    expect(first.reason).toBe('separation');
    expect((await runVanChain(f.svc, null, first.transfer, has(ALL), keys, sepOptions)).reason).toBe('separation');

    f.state.receipts = f.state.receipts.map((r) => ({ ...r, status: 'Posted' as const }));
    const done = await runVanChain(f.svc, null, first.transfer, has(ALL), keys, sepOptions);
    expect(done).toMatchObject({ stoppedAt: null, reason: null });
    expect(f.consignments.recordReceipt).toHaveBeenCalledTimes(1);
    expect(f.consignments.postReceipt).not.toHaveBeenCalled();
  });

  it('a different user (or no snapshot) still posts', async () => {
    const f = separationFake();
    const res = await runVanChain(f.svc, request, null, has(ALL), new ChainKeys(), { vehicleLocationId: 'van', currentUserId: 'other' });
    expect(res.stoppedAt).toBeNull();
    expect(f.consignments.postReceipt).toHaveBeenCalledTimes(1);
  });
});
