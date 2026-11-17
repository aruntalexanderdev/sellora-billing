import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { ProductUnitsLookup } from '../product-units-lookup';
import { StockTransfersService } from '../stock-transfers.service';
import { TransferReceiveDialog } from './transfer-receive-dialog';
import { TransferResolveDialog } from './transfer-resolve-dialog';
import { dispatchedLine, line, transfer } from '../test-fixtures';

const unit = (id: string, unitCode: string, factor: number, dp: number, isBaseUnit = false) => ({
  id, unitCode, displayName: unitCode, decimalPlaces: dp, conversionFactorToBase: factor, isBaseUnit, isActive: true,
});
const PEN_UNITS = [unit('pc', 'pc', 1, 0, true), unit('box', 'box', 10, 0)];

function setup<T>(component: new () => T, t = transfer('Approved', [line(), line({ id: 'l2', lineNo: 2, productId: 'rice', productName: 'Rice' })]), service: Record<string, unknown> = {}, submission?: IdempotentSubmission) {
  TestBed.configureTestingModule({
    imports: [component],
    providers: [
      { provide: StockTransfersService, useValue: service },
      { provide: ProductUnitsLookup, useValue: { units: () => of(PEN_UNITS) } },
    ],
  });
  const fixture = TestBed.createComponent(component);
  fixture.componentRef.setInput('transfer', t);
  if (submission) fixture.componentRef.setInput('submission', submission);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const input = (id: string, value: string) => {
    const e = el.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | HTMLSelectElement;
    e.value = value;
    e.dispatchEvent(new Event(e instanceof HTMLSelectElement ? 'change' : 'input'));
    fixture.detectChanges();
  };
  const submit = () => {
    const buttons = Array.from(el.querySelectorAll('.actions ui-button button')) as HTMLButtonElement[];
    buttons.find((b) => /Dispatch|Receive|Resolve/.test(b.textContent ?? '') && !/All:/.test(b.textContent ?? ''))!.click();
    fixture.detectChanges();
  };
  return { fixture, component: fixture.componentInstance, el, input, submit };
}

const http = (status: number, errors?: Record<string, string[]>) => new HttpErrorResponse({ status, error: errors ? { errors } : undefined });

describe('TransferReceiveDialog', () => {
  const dispatched = transfer('Dispatched', [dispatchedLine(), dispatchedLine({ id: 'l2', lineNo: 2, productId: 'rice', productName: 'Rice' }), dispatchedLine({ id: 'l3', lineNo: 3, outstandingBaseQty: 0, receivedBaseQty: 50 })]);

  it('shows only outstanding lines, defaulting accepted to the outstanding quantity', () => {
    const receive = vi.fn(() => of({ receiptId: 'r', transfer: transfer('Completed') }));
    const { component, submit } = setup(TransferReceiveDialog, dispatched, { receive });
    expect(component.rows().map((r) => r.lineId)).toEqual(['l1', 'l2']);
    submit();
    expect(receive).toHaveBeenCalledWith('t1', {
      notes: null,
      lines: [{ lineId: 'l1', productUnitId: 'box', acceptedQuantity: 5, damagedQuantity: 0 }, { lineId: 'l2', productUnitId: 'box', acceptedQuantity: 5, damagedQuantity: 0 }],
    }, expect.any(String));
  });

  it('allows a partial receipt with damaged goods and refuses excess', () => {
    const receive = vi.fn(() => of({ receiptId: 'r', transfer: transfer('PartiallyReceived') }));
    const { el, input, submit } = setup(TransferReceiveDialog, dispatched, { receive });
    input('receive-accepted-0', '3');
    input('receive-damaged-0', '1');
    input('receive-accepted-1', '0');
    submit();
    expect((receive.mock.calls[0] as unknown as [string, { lines: unknown[] }])[1].lines).toEqual([
      { lineId: 'l1', productUnitId: 'box', acceptedQuantity: 3, damagedQuantity: 1 },
      { lineId: 'l2', productUnitId: 'box', acceptedQuantity: 0, damagedQuantity: 0 },
    ]);

    input('receive-accepted-0', '5');
    input('receive-damaged-0', '1');
    submit();
    expect(el.querySelector('[data-testid="receive-row-error-0"]')!.textContent).toMatch(/exceeds the outstanding/i);
    expect(receive).toHaveBeenCalledTimes(1);
  });

  it('reuses the key on retry after a timeout and uses a new one for the next receipt', () => {
    const keys: string[] = [];
    let n = 0;
    const receive = vi.fn((_id: string, _b: unknown, key: string) => {
      keys.push(key);
      return ++n === 1 ? throwError(() => http(504)) : of({ receiptId: 'r', transfer: transfer('PartiallyReceived') });
    });
    const { submit } = setup(TransferReceiveDialog, dispatched, { receive });
    submit();
    submit();
    expect(keys[1]).toBe(keys[0]);
    submit();
    expect(keys[2]).not.toBe(keys[1]);
  });
});

describe('TransferResolveDialog', () => {
  const dispatched = transfer('PartiallyReceived', [dispatchedLine(), dispatchedLine({ id: 'l2', lineNo: 2, outstandingBaseQty: 0, receivedBaseQty: 50 }), dispatchedLine({ id: 'l3', lineNo: 3, outstandingBaseQty: 7 })]);

  it('lists outstanding lines and requires an action per line and a reason', () => {
    const resolve = vi.fn(() => of({ receiptId: 'r', transfer: transfer('Completed') }));
    const { component, el, input, submit } = setup(TransferResolveDialog, dispatched, { resolve });
    expect(component.rows().map((r) => r.lineId)).toEqual(['l1', 'l3']);
    submit();
    expect(el.querySelector('[data-testid="resolve-row-error-0"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="resolve-error"]')!.textContent).toMatch(/reason is required/i);
    expect(resolve).not.toHaveBeenCalled();

    input('resolve-action-0', 'WriteOff');
    input('resolve-action-1', 'ReturnToSource');
    input('resolve-reason', ' Lost in transit ');
    submit();
    expect(resolve).toHaveBeenCalledWith('t1', { reason: 'Lost in transit', lines: [{ lineId: 'l1', action: 'WriteOff' }, { lineId: 'l3', action: 'ReturnToSource' }] }, expect.any(String));
  });

  it('shows a server refusal (company-level only) in the banner and keeps the dialog open', () => {
    const resolve = vi.fn(() => throwError(() => http(400, { sourceLocationId: ['Resolve is company-level only.'] })));
    const { component, el, input, submit } = setup(TransferResolveDialog, dispatched, { resolve });
    const done = vi.fn();
    component.done.subscribe(done);
    input('resolve-action-0', 'WriteOff');
    input('resolve-action-1', 'WriteOff');
    input('resolve-reason', 'x');
    submit();
    expect(el.querySelector('[data-testid="resolve-error"]')!.textContent).toContain('company-level only');
    expect(done).not.toHaveBeenCalled();
  });
});
