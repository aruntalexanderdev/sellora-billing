import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import { StockTransferDetail } from './stock-transfer-detail';
import { StockTransfersService } from '../stock-transfers.service';
import { ProductUnitsLookup } from '../product-units-lookup';
import type { StockTransfer, StockTransferStatus } from '../stock-transfers.models';
import { dispatchedLine, line, transfer } from '../test-fixtures';

const ALL = ['StockTransfers.View', 'StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Dispatch', 'StockTransfers.Receive', 'StockTransfers.Cancel', 'StockTransfers.Resolve'];

function setup(t: StockTransfer, permissions: string[] = ALL, overrides: Record<string, unknown> = {}) {
  const service = { get: vi.fn(() => of(t)), submit: vi.fn(() => of(t)), approve: vi.fn(() => of(t)), reject: vi.fn(() => of(t)), cancel: vi.fn(() => of(t)), ...overrides };
  TestBed.configureTestingModule({
    imports: [StockTransferDetail],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: t.id }) } } },
      { provide: StockTransfersService, useValue: service },
      { provide: ProductUnitsLookup, useValue: { units: () => of([{ id: 'box', unitCode: 'box', displayName: 'box', decimalPlaces: 0, conversionFactorToBase: 10, isBaseUnit: false, isActive: true }]) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(StockTransferDetail);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const buttons = () => Array.from(el.querySelectorAll('[data-testid^="act-"]')).map((b) => b.getAttribute('data-testid')!.replace('act-', ''));
  const click = (id: string) => {
    (el.querySelector(`[data-testid="act-${id}"] button`) as HTMLButtonElement).click();
    fixture.detectChanges();
  };
  return { fixture, component: fixture.componentInstance, el, service, buttons, click, navigate };
}

describe('StockTransferDetail action visibility', () => {
  const dispatched = [dispatchedLine()];
  const cases: [StockTransferStatus, string[]][] = [
    ['Draft', ['edit', 'submit', 'cancel']],
    ['Requested', ['approve', 'reject', 'cancel']],
    ['Approved', ['dispatch', 'cancel']],
    ['Dispatched', ['receive', 'resolve']],
    ['PartiallyReceived', ['receive', 'resolve']],
    ['Completed', []],
    ['Rejected', []],
    ['Cancelled', []],
  ];
  for (const [status, expected] of cases) {
    it(`${status} with every permission shows ${expected.join(', ') || 'no actions'}`, () => {
      const { buttons } = setup(transfer(status, dispatched));
      expect([...buttons()].sort()).toEqual([...expected].sort());
    });
  }

  it('shows nothing to a view-only user', () => {
    expect(setup(transfer('Requested'), ['StockTransfers.View']).buttons()).toEqual([]);
  });

  it('shows only the permitted subset', () => {
    expect(setup(transfer('Requested'), ['StockTransfers.View', 'StockTransfers.Cancel']).buttons()).toEqual(['cancel']);
  });

  it('hides Resolve from a branch-level receiver', () => {
    const { buttons } = setup(transfer('Dispatched', dispatched), ['StockTransfers.View', 'StockTransfers.Receive']);
    expect(buttons()).toEqual(['receive']);
  });
});

describe('StockTransferDetail content', () => {
  it('shows request and dispatch unit snapshots with the counters and per-batch rows', () => {
    const l = dispatchedLine({
      requestedQty: 5, dispatchedQty: 2, dispatchProductUnitId: 'pc', dispatchUnitCode: 'pc', dispatchFactor: 1, dispatchedBaseQty: 20,
      receivedBaseQty: 8, damagedBaseQty: 2, writtenOffBaseQty: 1, returnedBaseQty: 3, outstandingBaseQty: 6,
      batches: [{ purchaseBatchId: 'pb1', internalBatchNo: 'B-77', unitCost: 2.5, dispatchedBaseQty: 20, receivedBaseQty: 8, damagedBaseQty: 2, writtenOffBaseQty: 1, returnedBaseQty: 3, outstandingBaseQty: 6 }],
    });
    const { el } = setup(transfer('PartiallyReceived', [l], { receipts: [{ id: 'r1', sequence: 1, kind: 'Receipt', at: '2026-11-07T10:00:00Z', byUserId: 'u', byUserName: 'sam', notes: 'ok', lines: [] }] }));
    const row = el.querySelector('[data-testid="line-1"]')!.textContent!;
    expect(row).toContain('5 box');
    expect(row).toContain('= 50 pc');
    expect(row).toContain('2 pc');
    expect(row).toContain('= 20 pc');
    expect(row).toContain('6 pc');
    expect(el.querySelector('[data-testid="batches-1"]')!.textContent).toContain('B-77');
    expect(el.querySelector('[data-testid="receipt-1"]')!.textContent).toContain('Receipt');
  });

  it('lists actors in the timeline', () => {
    const { el } = setup(transfer('Rejected', [line()], { rejected: { userId: 'u', userName: 'boss', at: '2026-11-07T10:00:00Z' }, rejectionReason: 'Not needed' }));
    const text = el.querySelector('[data-testid="timeline"]')!.textContent!;
    expect(text).toContain('Created');
    expect(text).toContain('Rejected');
    expect(text).toContain('Not needed');
  });

  it('shows the load error', () => {
    TestBed.configureTestingModule({
      imports: [StockTransferDetail],
      providers: [
        provideRouter([]), ...provideScopeStubs(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'x' }) } } },
        { provide: StockTransfersService, useValue: { get: () => throwError(() => new HttpErrorResponse({ status: 500, error: { title: 'Boom' } })) } },
        { provide: AuthService, useValue: { hasPermission: () => true } },
      ],
    });
    const fixture = TestBed.createComponent(StockTransferDetail);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="load-error"]')).not.toBeNull();
  });
});

describe('StockTransferDetail actions', () => {
  it('approve posts once per click with a key that is stable across retries and fresh after success', () => {
    const calls: string[] = [];
    let fail: HttpErrorResponse | null = new HttpErrorResponse({ status: 0 });
    const t = transfer('Requested');
    const approve = vi.fn((_id: string, key: string) => {
      calls.push(key);
      if (fail) return throwError(() => fail);
      return of({ ...t, status: 'Approved' as const });
    });
    const { click, fixture } = setup(t, ALL, { approve });

    click('approve');
    expect(fixture.componentInstance.actionError()).toBeTruthy();
    click('approve');
    expect(calls[1]).toBe(calls[0]);

    fail = null;
    click('approve');
    expect(calls[2]).toBe(calls[0]);
    expect(fixture.componentInstance.transfer()!.status).toBe('Approved');
  });

  it('rotates the key after a definitive 400 rejection', () => {
    const keys: string[] = [];
    const t = transfer('Requested');
    const approve = vi.fn((_id: string, key: string) => {
      keys.push(key);
      return throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { status: ['Not requested.'] } } }));
    });
    const { click, fixture } = setup(t, ALL, { approve });
    click('approve');
    click('approve');
    expect(keys[1]).not.toBe(keys[0]);
    expect(fixture.componentInstance.actionError()).toContain('Not requested.');
  });

  it('reject asks for a reason and posts it', () => {
    const t = transfer('Requested');
    const { click, fixture, service, el } = setup(t);
    click('reject');
    const textarea = el.querySelector('app-reason-prompt textarea') as HTMLTextAreaElement;
    textarea.value = 'Not needed';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el.querySelector('app-reason-prompt .actions ui-button button') as HTMLButtonElement).click();
    expect(service.reject).toHaveBeenCalledWith('t1', 'Not needed', expect.any(String));
  });

  it('submit calls the service for a draft', () => {
    const { click, service } = setup(transfer('Draft'));
    click('submit');
    expect(service.submit).toHaveBeenCalledWith('t1', expect.any(String));
  });

  it('edit navigates to the form', () => {
    const { click, navigate } = setup(transfer('Draft'));
    click('edit');
    expect(navigate).toHaveBeenCalledWith(['/inventory/stock-transfers', 't1', 'edit']);
  });

  describe('dialog idempotency survives close and reopen', () => {
    const dispatched = () => transfer('Dispatched', [dispatchedLine()]);
    const submitDialog = (el: HTMLElement, fixture: { detectChanges(): void }) => {
      const buttons = Array.from(el.querySelectorAll('app-transfer-receive-dialog .actions ui-button button')) as HTMLButtonElement[];
      buttons.find((b) => /Receive/.test(b.textContent ?? '') && !/All:/.test(b.textContent ?? ''))!.click();
      fixture.detectChanges();
    };

    it('replays the same key after an ambiguous failure and reloads the transfer when the dialog closes', () => {
      const keys: string[] = [];
      const outcomes = [() => throwError(() => new HttpErrorResponse({ status: 0 })), () => of({ receiptId: 'r', transfer: transfer('Completed') })];
      const receive = vi.fn((_id: string, _body: unknown, key: string) => {
        keys.push(key);
        return outcomes[keys.length - 1]();
      });
      const { click, fixture, component, el, service } = setup(dispatched(), ALL, { receive });
      click('receive');
      submitDialog(el, fixture);
      const gets = service.get.mock.calls.length;
      component.close();
      fixture.detectChanges();
      expect(service.get.mock.calls.length).toBe(gets + 1);

      click('receive');
      submitDialog(el, fixture);
      expect(keys).toHaveLength(2);
      expect(keys[1]).toBe(keys[0]);
    });

    it('does not reload on close after a definitive rejection, and uses a new key afterwards', () => {
      const keys: string[] = [];
      const receive = vi.fn((_id: string, _body: unknown, key: string) => {
        keys.push(key);
        return throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { status: ['No.'] } } }));
      });
      const { click, fixture, component, el, service } = setup(dispatched(), ALL, { receive });
      click('receive');
      submitDialog(el, fixture);
      const gets = service.get.mock.calls.length;
      component.close();
      fixture.detectChanges();
      expect(service.get.mock.calls.length).toBe(gets);
      click('receive');
      submitDialog(el, fixture);
      expect(keys[1]).not.toBe(keys[0]);
    });
  });
});
