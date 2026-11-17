import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { IdempotentSubmission } from '../../../../core/idempotency';
import type { ConsignmentDetail, ConsignmentDetailLine, ConsignmentReceipt } from '../../shipments/shipments.models';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import { ConsignmentsService } from '../consignments.service';
import { ReceiptDialog } from './receipt-dialog';

const loc = (id: string) => ({ id, code: id.toUpperCase(), name: id, type: 'BranchStore' });
const BOX = { id: 'box', unitCode: 'box', displayName: 'Box', decimalPlaces: 0, conversionFactorToBase: 10, isBaseUnit: false, isActive: true };

const line = (over: Partial<ConsignmentDetailLine> = {}): ConsignmentDetailLine => ({
  id: 'cl1', transferNo: 'TRF-1', productId: 'pen', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box',
  enteredQty: 5, allocatedBaseQty: 50, loadedBaseQty: 50, loadedQty: 5, dispatchedBaseQty: 50, acceptedBaseQty: 0, damagedBaseQty: 0, rejectedBaseQty: 0,
  missingBaseQty: 0, postedBaseQty: 0, conversionFactorToBase: 10, ...over,
});
const consignment = (lines: ConsignmentDetailLine[]): ConsignmentDetail => ({
  id: 'c1', consignmentNo: 'CON-1', status: 'InTransit', direction: 'incoming', shipmentId: null, shipmentNo: 'SHP-1', shipmentStatus: 'Dispatched',
  transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, transportReference: null, plannedDispatchDate: null,
  source: loc('store'), destination: loc('van'), destinationBranch: { id: 'b', code: 'B', name: 'B' }, leg: null, loadedAt: null,
  lines, dispatchedAt: '2026-11-16T10:00:00Z', receiptCount: 0,
});
const receipt = { id: 'r1', sequence: 1, status: 'Verified' } as ConsignmentReceipt;

function setup(recordReceipt: ReturnType<typeof vi.fn>, lines = [line(), line({ id: 'cl2', productName: 'Done', acceptedBaseQty: 50 })], submission?: IdempotentSubmission, units$: unknown = of([BOX])) {
  TestBed.configureTestingModule({
    imports: [ReceiptDialog],
    providers: [
      { provide: ConsignmentsService, useValue: { recordReceipt } },
      { provide: ProductUnitsLookup, useValue: { units: () => units$ } },
    ],
  });
  const fixture = TestBed.createComponent(ReceiptDialog);
  fixture.componentRef.setInput('consignment', consignment(lines));
  if (submission) fixture.componentRef.setInput('submission', submission);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const type = (id: string, value: string) => {
    const e = el.querySelector(`[data-testid="${id}"]`) as HTMLInputElement;
    e.value = value;
    e.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const click = (id: string) => {
    (el.querySelector(`[data-testid="${id}"] button`) as HTMLButtonElement).click();
    fixture.detectChanges();
  };
  return { fixture, component: fixture.componentInstance, el, type, click };
}

describe('ReceiptDialog', () => {
  it('lists only lines with something left to account for, in the line unit', () => {
    const { component, el } = setup(vi.fn());
    expect(component.rows()).toHaveLength(1);
    expect(el.querySelector('[data-testid="receipt-remaining-0"]')?.textContent).toContain('5 box');
  });

  it('says that damaged, rejected and missing stay in transit', () => {
    const { el } = setup(vi.fn());
    expect(el.querySelector('[data-testid="shortfall-note"]')?.textContent).toMatch(/stay in transit/i);
  });

  it('blocks accepted + damaged + rejected + missing above the remaining quantity', () => {
    const record = vi.fn();
    const { el, type, click } = setup(record);
    type('receipt-accepted-0', '3');
    type('receipt-damaged-0', '1');
    type('receipt-rejected-0', '1');
    type('receipt-missing-0', '1');
    click('receipt-submit');
    expect(record).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="receipt-row-error-0"]')?.textContent).toMatch(/more than the 5 box/);
  });

  it('honours the already-accounted quantity (remaining = dispatched minus accepted/damaged/rejected/missing)', () => {
    const record = vi.fn();
    const { el, type, click } = setup(record, [line({ acceptedBaseQty: 20, damagedBaseQty: 10 })]);
    expect(el.querySelector('[data-testid="receipt-remaining-0"]')?.textContent).toContain('2 box');
    type('receipt-accepted-0', '3');
    click('receipt-submit');
    expect(record).not.toHaveBeenCalled();
    type('receipt-accepted-0', '2');
    expect(el.querySelector('[data-testid="receipt-row-error-0"]')).toBeNull();
  });

  it('refuses an empty receipt', () => {
    const record = vi.fn();
    const { el, click } = setup(record);
    click('receipt-submit');
    expect(record).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="receipt-error"]')?.textContent).toMatch(/at least one/i);
  });

  it('posts the quantities with an idempotency key, then emits the receipt', () => {
    const record = vi.fn((_id: string, _body: unknown, _key: string) => of(receipt));
    const { component, type, click } = setup(record);
    const done = vi.fn();
    component.done.subscribe(done);
    type('receipt-accepted-0', '3');
    type('receipt-missing-0', '1');
    type('receipt-notes', 'one short');
    click('receipt-submit');
    expect(record).toHaveBeenCalledWith(
      'c1',
      { lines: [{ consignmentLineId: 'cl1', acceptedQuantity: 3, damagedQuantity: 0, rejectedQuantity: 0, missingQuantity: 1 }], notes: 'one short' },
      expect.any(String),
    );
    expect(done).toHaveBeenCalledWith(receipt);
  });

  it('"Accept all remaining" fills the accepted quantity and clears the others', () => {
    const record = vi.fn((_id: string, _body: unknown, _key: string) => of(receipt));
    const { type, click } = setup(record);
    type('receipt-damaged-0', '1');
    click('receipt-accept-all');
    click('receipt-submit');
    expect(record.mock.calls[0][1]).toEqual({ lines: [{ consignmentLineId: 'cl1', acceptedQuantity: 5, damagedQuantity: 0, rejectedQuantity: 0, missingQuantity: 0 }] });
  });

  it('maps a server 400 onto the row and the banner; keeps the key on a network error, rotates it after a 400', () => {
    const keys: string[] = [];
    const responses = [
      () => throwError(() => new HttpErrorResponse({ status: 0 })),
      () => throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { 'lines[0].acceptedQuantity': ['Exceeds dispatched.'], status: ['Not in transit.'] } } })),
      () => of(receipt),
    ];
    const record = vi.fn((_id: string, _body: unknown, key: string) => {
      keys.push(key);
      return responses[keys.length - 1]();
    });
    const { el, type, click } = setup(record);
    type('receipt-accepted-0', '2');
    click('receipt-submit');
    click('receipt-submit');
    expect(keys[1]).toBe(keys[0]);
    expect(el.querySelector('[data-testid="receipt-row-error-0"]')?.textContent).toContain('Exceeds dispatched.');
    expect(el.querySelector('[data-testid="receipt-error"]')?.textContent).toContain('Not in transit.');
    click('receipt-submit');
    expect(keys[2]).not.toBe(keys[1]);
  });

  it('does not validate or submit until the unit decimals are known (submit disabled while loading)', () => {
    const units$ = new Subject<unknown[]>();
    const record = vi.fn((_id: string, _body: unknown, _key: string) => of(receipt));
    const { fixture, el, component, type, click } = setup(record, undefined, undefined, units$);
    expect(component.unitsReady()).toBe(false);
    expect((el.querySelector('[data-testid="receipt-submit"] button') as HTMLButtonElement).disabled).toBe(true);
    type('receipt-accepted-0', '1.5'); // would be refused for a whole-number unit, but must not be judged with the 3-decimal default
    click('receipt-submit');
    expect(record).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="receipt-row-error-0"]')).toBeNull();

    units$.next([BOX]);
    fixture.detectChanges();
    expect(component.unitsReady()).toBe(true);
    expect((el.querySelector('[data-testid="receipt-submit"] button') as HTMLButtonElement).disabled).toBe(false);
    click('receipt-submit');
    expect(el.querySelector('[data-testid="receipt-row-error-0"]')?.textContent).toMatch(/whole numbers/i);
    expect(record).not.toHaveBeenCalled();
  });

  it('stays blocked and says so when the units lookup fails', () => {
    const units$ = new Subject<unknown[]>();
    const { fixture, el, component } = setup(vi.fn(), undefined, undefined, units$);
    units$.error(new Error('x'));
    fixture.detectChanges();
    expect(component.unitsReady()).toBe(false);
    expect(el.querySelector('[data-testid="units-error"]')).not.toBeNull();
  });
});
