import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import { consignment, consignmentLine, shipment } from '../shipment-fixtures';
import { ShipmentsService } from '../shipments.service';
import { ConsignmentLoadDialog } from './consignment-load-dialog';

const BOX = { id: 'box', unitCode: 'box', displayName: 'Box', decimalPlaces: 0, conversionFactorToBase: 10, isBaseUnit: false, isActive: true };

function setup(load: ReturnType<typeof vi.fn>, submission?: IdempotentSubmission) {
  TestBed.configureTestingModule({
    imports: [ConsignmentLoadDialog],
    providers: [
      { provide: ShipmentsService, useValue: { load } },
      { provide: ProductUnitsLookup, useValue: { units: () => of([BOX]) } },
    ],
  });
  const fixture = TestBed.createComponent(ConsignmentLoadDialog);
  fixture.componentRef.setInput('shipmentId', 's1');
  fixture.componentRef.setInput('consignment', consignment({ lines: [consignmentLine(), consignmentLine({ id: 'cl2', status: 'Cancelled' })] }));
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

describe('ConsignmentLoadDialog', () => {
  it('lists only active lines and defaults to everything allocated', () => {
    const { component } = setup(vi.fn());
    expect(component.rows()).toHaveLength(1);
    expect(component.rows()[0].loadedQty).toBe(5);
  });

  it('refuses a loaded quantity above the allocation without calling the API', () => {
    const load = vi.fn();
    const { el, type, click } = setup(load);
    type('load-qty-0', '6');
    click('load-submit');
    expect(load).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="load-row-error-0"]')?.textContent).toContain('allocated');
  });

  it('posts the loaded quantities with an idempotency key and emits the new shipment', () => {
    const load = vi.fn(() => of(shipment('Loading')));
    const { component, type, click } = setup(load);
    const done = vi.fn();
    component.done.subscribe(done);
    type('load-qty-0', '3');
    click('load-submit');
    expect(load).toHaveBeenCalledWith('s1', 'c1', { lines: [{ consignmentLineId: 'cl1', loadedQuantity: 3 }] }, expect.any(String));
    expect(done).toHaveBeenCalled();
  });

  it('keeps the key for an identical retry after a network failure', () => {
    const keys: string[] = [];
    const load = vi.fn((_s: string, _c: string, _b: unknown, key: string) => {
      keys.push(key);
      return throwError(() => new HttpErrorResponse({ status: 0 }));
    });
    const { click } = setup(load);
    click('load-submit');
    click('load-submit');
    expect(keys[1]).toBe(keys[0]);
  });

  it('maps a server row error onto the line', () => {
    const bad = vi.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { 'lines[0].loadedQuantity': ['Cannot load more than was allocated (5 box).'] } } })));
    const second = setup(bad, new IdempotentSubmission());
    second.click('load-submit');
    expect(second.el.querySelector('[data-testid="load-row-error-0"]')?.textContent).toContain('Cannot load more');
  });
});
