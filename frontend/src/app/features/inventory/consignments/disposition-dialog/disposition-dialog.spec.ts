import { TestBed } from '@angular/core/testing';
import { NEVER, of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import type { ConsignmentDetail, ConsignmentReceipt } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';
import { DispositionDialog } from './disposition-dialog';

const loc = (id: string) => ({ id, code: id.toUpperCase(), name: id, type: 'BranchStore' });
const consignment = (): ConsignmentDetail => ({
  id: 'c1', consignmentNo: 'CON-1', status: 'Received', direction: 'incoming', shipmentId: null, shipmentNo: 'SHP-1', shipmentStatus: 'Delivered',
  transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, transportReference: null, plannedDispatchDate: null,
  source: loc('store'), destination: loc('van'), destinationBranch: { id: 'b', code: 'B', name: 'B' }, leg: null, loadedAt: null, dispatchedAt: '2026-11-16T10:00:00Z',
  receiptCount: 1, dispositionCount: 0, hasOpenDiscrepancy: true,
  lines: [{
    id: 'l1', transferNo: 'TRF-1', productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box', enteredQty: 5,
    allocatedBaseQty: 50, loadedBaseQty: 50, loadedQty: 5, dispatchedBaseQty: 50, acceptedBaseQty: 20, damagedBaseQty: 30, rejectedBaseQty: 0, missingBaseQty: 0, postedBaseQty: 20,
    conversionFactorToBase: 10, dispositionedDamagedBaseQty: 0, dispositionedRejectedBaseQty: 0, dispositionedMissingBaseQty: 0, openDiscrepancyBaseQty: 30,
  }],
});
const posted = [{
  id: 'r1', status: 'Posted',
  lines: [{ consignmentLineId: 'l1', damagedBaseQty: 20, rejectedBaseQty: 0, missingBaseQty: 0 }],
}] as unknown as ConsignmentReceipt[];

function setup(record = vi.fn(), units$: unknown = of([{ id: 'box', decimalPlaces: 0 }])) {
  TestBed.configureTestingModule({
    imports: [DispositionDialog],
    providers: [
      { provide: ConsignmentsService, useValue: { recordDisposition: record } },
      { provide: ProductUnitsLookup, useValue: { units: () => units$ } },
    ],
  });
  const fixture = TestBed.createComponent(DispositionDialog);
  fixture.componentRef.setInput('consignment', consignment());
  fixture.componentRef.setInput('receipts', posted);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const type = (id: string, value: string) => {
    const e = el.querySelector(`[data-testid="${id}"]`) as HTMLInputElement;
    e.value = value;
    e.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const submit = () => {
    (el.querySelector('[data-testid="disposition-submit"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
  };
  return { fixture, component: fixture.componentInstance, el, type, submit };
}
const text = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`)?.textContent;

describe('DispositionDialog', () => {
  it('offers only the posted open quantity (20 pc, not the 30 recorded) in the line unit and says only posted quantity can be resolved', () => {
    const { component, el } = setup();
    expect(component.rows()).toHaveLength(1);
    expect(text(el, 'disposition-open-0')).toContain('2 box');
    expect(text(el, 'disposition-note')).toMatch(/only quantity from posted receipts/i);
  });

  it('rejects a quantity above the open bucket quantity and does not call the server', () => {
    const record = vi.fn();
    const { el, type, submit } = setup(record);
    type('disposition-qty-0', '3');
    type('disposition-reason', 'broken');
    submit();
    expect(text(el, 'disposition-row-error-0')).toMatch(/More than the 2 box damaged/);
    expect(record).not.toHaveBeenCalled();
  });

  it('rejects fractional quantities for a whole-number unit', () => {
    const { el, type, submit } = setup();
    type('disposition-qty-0', '1.5');
    type('disposition-reason', 'broken');
    submit();
    expect(text(el, 'disposition-row-error-0')).toBe('Whole numbers only.');
  });

  it('requires a reason', () => {
    const record = vi.fn();
    const { el, type, submit } = setup(record);
    type('disposition-qty-0', '1');
    submit();
    expect(text(el, 'disposition-error')).toContain('Enter a reason.');
    expect(record).not.toHaveBeenCalled();
  });

  it('submits action + quantity in the line unit with an idempotency key', () => {
    const record = vi.fn(() => of({ id: 'd1' }));
    const { fixture, component, el, type, submit } = setup(record);
    const done = vi.fn();
    component.done.subscribe(done);
    (el.querySelector('[data-testid="disposition-action-0"]') as HTMLSelectElement).value = 'ReturnToSource';
    (el.querySelector('[data-testid="disposition-action-0"]') as HTMLSelectElement).dispatchEvent(new Event('change'));
    type('disposition-qty-0', '2');
    type('disposition-reason', ' broken ');
    fixture.detectChanges();
    submit();
    expect(record).toHaveBeenCalledWith('c1', { reason: 'broken', lines: [{ consignmentLineId: 'l1', bucket: 'Damaged', action: 'ReturnToSource', quantity: 2 }] }, expect.any(String));
    expect(done).toHaveBeenCalled();
  });

  it('waits for the unit lookup before it validates or submits', () => {
    const record = vi.fn();
    const { component, type, submit } = setup(record, NEVER);
    expect(component.unitsReady()).toBe(false);
    type('disposition-qty-0', '1');
    type('disposition-reason', 'x');
    submit();
    expect(record).not.toHaveBeenCalled();
  });

  it('maps a server line error onto the row', () => {
    const err = new HttpErrorResponse({ status: 400, error: { title: 'x', errors: { 'lines[0].quantity': ['Only 1 pc is posted and open.'] } } });
    const { el, type, submit } = setup(vi.fn(() => throwError(() => err)));
    type('disposition-qty-0', '1');
    type('disposition-reason', 'broken');
    submit();
    expect(text(el, 'disposition-row-error-0')).toBe('Only 1 pc is posted and open.');
  });
});
