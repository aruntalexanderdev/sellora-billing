import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { dispatchedLine, line, transfer } from '../../inventory/stock-transfers/test-fixtures';
import { ChainKeys, runVanChain } from './van-transfer-chain';

const ALL = ['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Dispatch', 'StockTransfers.Receive'];
const has = (perms: string[]) => (c: string) => perms.includes(c);
const request = { sourceLocationId: 'store', destinationLocationId: 'van', notes: null, lines: [{ productId: 'pen', productUnitId: 'box', quantity: 5 }] };

function services() {
  const requested = transfer('Requested', [line()]);
  const approved = transfer('Approved', [line()]);
  const dispatched = transfer('Dispatched', [dispatchedLine()]);
  const completed = transfer('Completed', [dispatchedLine({ outstandingBaseQty: 0, receivedBaseQty: 50 })]);
  return {
    requested, approved, dispatched, completed,
    service: {
      create: vi.fn((_r: unknown, _k: string) => of(requested)),
      submit: vi.fn((_id: string, _k?: string) => of(requested)),
      approve: vi.fn((_id: string, _k?: string) => of(approved)),
      dispatch: vi.fn((_id: string, _b: unknown, _k: string) => of(dispatched)),
      receive: vi.fn((_id: string, _b: unknown, _k: string) => of({ receiptId: 'r1', transfer: completed })),
    },
  };
}

describe('runVanChain', () => {
  it('runs create(submit) -> approve -> dispatch -> receive with a distinct Idempotency-Key per step', async () => {
    const { service } = services();
    const res = await runVanChain(service, request, null, has(ALL), new ChainKeys());

    expect(res).toMatchObject({ stoppedAt: null, reason: null, completed: ['create', 'approve', 'dispatch', 'receive'] });
    expect(res.transfer!.status).toBe('Completed');
    expect(service.create).toHaveBeenCalledWith({ ...request, submit: true }, expect.any(String));
    expect(service.approve).toHaveBeenCalledWith('t1', expect.any(String));
    // Dispatch lists every line at the requested quantity in the requested unit.
    expect(service.dispatch).toHaveBeenCalledWith('t1', { lines: [{ lineId: 'l1', productUnitId: 'box', quantity: 5 }] }, expect.any(String));
    // Receive takes the dispatched quantity in the dispatch unit, nothing damaged.
    expect(service.receive).toHaveBeenCalledWith('t1', { notes: null, lines: [{ lineId: 'l1', productUnitId: 'box', acceptedQuantity: 5, damagedQuantity: 0 }] }, expect.any(String));
    const keys = [service.create.mock.calls[0][1], service.approve.mock.calls[0][1], service.dispatch.mock.calls[0][2], service.receive.mock.calls[0][2]];
    expect(new Set(keys).size).toBe(4);
  });

  it('stops at the first missing permission and reports it (user can only create)', async () => {
    const { service } = services();
    const res = await runVanChain(service, request, null, has(['StockTransfers.Create']), new ChainKeys());
    expect(res).toMatchObject({ stoppedAt: 'approve', reason: 'permission', missingPermission: 'StockTransfers.Approve', completed: ['create'] });
    expect(res.transfer!.status).toBe('Requested');
    expect(service.approve).not.toHaveBeenCalled();
    expect(service.dispatch).not.toHaveBeenCalled();
  });

  it('does not skip a missing permission to run a later step', async () => {
    const { service } = services();
    const res = await runVanChain(service, request, null, has(['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Receive']), new ChainKeys());
    expect(res).toMatchObject({ stoppedAt: 'dispatch', reason: 'permission', missingPermission: 'StockTransfers.Dispatch', completed: ['create', 'approve'] });
    expect(service.receive).not.toHaveBeenCalled();
  });

  it('creates nothing without StockTransfers.Create', async () => {
    const { service } = services();
    const res = await runVanChain(service, request, null, has([]), new ChainKeys());
    expect(res).toMatchObject({ stoppedAt: 'create', reason: 'permission', transfer: null });
    expect(service.create).not.toHaveBeenCalled();
  });

  it('stops on a server rejection and links the transfer as it stands', async () => {
    const { service } = services();
    const error = new HttpErrorResponse({ status: 400, error: { errors: { 'lines[0].quantity': ['Not enough stock.'] } } });
    service.dispatch.mockReturnValue(throwError(() => error));
    const res = await runVanChain(service, request, null, has(ALL), new ChainKeys());
    expect(res).toMatchObject({ stoppedAt: 'dispatch', reason: 'error', completed: ['create', 'approve'], error });
    expect(res.transfer!.status).toBe('Approved');
  });

  it('reports a create validation failure with no transfer', async () => {
    const { service } = services();
    service.create.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 400 })));
    const res = await runVanChain(service, request, null, has(ALL), new ChainKeys());
    expect(res).toMatchObject({ stoppedAt: 'create', reason: 'error', transfer: null });
  });

  it('resumes an existing transfer with the same step keys after a network failure', async () => {
    const { service, approved, dispatched } = services();
    const keys = new ChainKeys();
    service.dispatch.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 }))).mockReturnValue(of(dispatched));
    const first = await runVanChain(service, request, null, has(ALL), keys);
    expect(first.stoppedAt).toBe('dispatch');

    const second = await runVanChain(service, null, first.transfer ?? approved, has(ALL), keys);
    expect(second.stoppedAt).toBeNull();
    expect(service.create).toHaveBeenCalledTimes(1);
    expect(service.dispatch.mock.calls[1][2]).toBe(service.dispatch.mock.calls[0][2]);
  });

  it('stops without acting on a rejected or cancelled transfer', async () => {
    const { service } = services();
    const res = await runVanChain(service, null, transfer('Cancelled'), has(ALL), new ChainKeys());
    expect(res).toMatchObject({ reason: 'status' });
    expect(service.approve).not.toHaveBeenCalled();
  });
});
